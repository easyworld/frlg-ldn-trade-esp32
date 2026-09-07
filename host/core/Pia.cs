using System.Net;
using System.Security.Cryptography;
using ZstdSharp;

namespace Frlg.Trade.Core;

public sealed record PiaMessage(int Protocol, byte[] Payload, int? Flags = null);
public sealed record ReliablePacket(int Sequence, int Flags, byte[] Payload);

public sealed class ReliableLink
{
    public sealed class Entry(int flags, byte[] data, double time)
    { public int Flags = flags, Resends; public byte[] Data = data; public double Time = time; public bool Acked; }
    public int Next = 0xfff0, Low = 0xfff0, ReceiveNext = 0xfff0;
    public Dictionary<int, Entry> Pending { get; } = [];
    private readonly HashSet<int> outOfOrder = [];
    private readonly Queue<double> samples = [];
    private int gap = -1, gapCount;
    public int SendLow => Pending.Count == 0 ? Next : Low;
    public bool HasGap => outOfOrder.Count != 0;
    public void AddRtt(double ms) { if (ms >= 0) { samples.Enqueue(ms); while (samples.Count > 7) samples.Dequeue(); } }
    public double Rto
    {
        get
        {
            if (samples.Count == 0) return 200;
            var sorted = samples.Order().ToArray(); double median = sorted[sorted.Length / 2];
            return Math.Min(670, 33 + 1.4 * median + 4 * samples.Average(v => Math.Abs(v - median)));
        }
    }
    public ReliablePacket Queue(byte[] data, int flags, double time)
    { int seq = Next; Next = (Next + 1) & 65535; Pending.Add(seq, new(flags, data, time)); return new(seq, flags, data); }
    public void Acknowledge(byte[] data, double time)
    {
        if (data.Length < 20) return;
        int ack = Bin.B16(data, 2); bool any = data.AsSpan(4, 16).IndexOfAnyExcept((byte)0) >= 0;
        foreach (var (seq, entry) in Pending)
        {
            int bit = (seq - ack - 1) & 65535;
            bool arrived = Bin.Less(seq, ack) || (bit < 128 && (data[4 + (bit >> 3)] & (1 << (bit & 7))) != 0);
            if (arrived && !entry.Acked) { if (entry.Resends == 0) AddRtt(time - entry.Time); entry.Acked = true; }
        }
        while (Pending.TryGetValue(Low, out var first) && first.Acked) { Pending.Remove(Low); Low = (Low + 1) & 65535; }
        if (any && Pending.TryGetValue(ack, out var missing) && !missing.Acked)
        { if (gap == ack) gapCount++; else { gap = ack; gapCount = 1; } }
        else { gap = -1; gapCount = 0; }
    }
    public List<ReliablePacket> Retransmit(double time, int limit)
    {
        var result = new List<ReliablePacket>(); double rto = Rto;
        foreach (var pair in Pending.OrderBy(e => (e.Key - Low) & 65535))
        {
            var e = pair.Value; if (e.Acked) continue;
            if (!(pair.Key == gap && e.Resends == 0 && gapCount >= 3) && time - e.Time < rto) break;
            e.Time = time; e.Resends++; result.Add(new(pair.Key, e.Flags, e.Data));
            if (result.Count >= limit) break;
        }
        return result;
    }
    public void Receive(int seq)
    {
        if (seq == ReceiveNext) { ReceiveNext = (ReceiveNext + 1) & 65535; while (outOfOrder.Remove(ReceiveNext)) ReceiveNext = (ReceiveNext + 1) & 65535; }
        else if (Bin.Less(ReceiveNext, seq) && ((seq - ReceiveNext) & 65535) < 4096) outOfOrder.Add(seq);
    }
    public byte[] Ack()
    {
        var b = new byte[20]; b[1] = 1; Bin.WB16(b, 2, ReceiveNext);
        foreach (int seq in outOfOrder) { int bit = (seq - ReceiveNext - 1) & 65535; if (bit < 128) b[4 + (bit >> 3)] |= (byte)(1 << (bit & 7)); }
        return b;
    }
    public byte[] Wrap(ReliablePacket p)
    {
        var b = new byte[8 + p.Payload.Length]; b[0] = (byte)p.Flags;
        Bin.WB16(b, 1, p.Payload.Length); Bin.WB16(b, 3, p.Sequence); Bin.WB16(b, 5, SendLow); p.Payload.CopyTo(b, 8); return b;
    }
}

public sealed class PiaCrypto(byte[] ssid) : IDisposable
{
    private readonly byte[] key = Bin.Ecb(Bin.Hex("83ca7fab734c34633b10183526c1e85b"), ssid, true);
    private readonly uint netId = Bin.Crc32(ssid.AsSpan(1, 15));
    private readonly Compressor compressor = new(4);
    private readonly Decompressor decompressor = new();
    private byte[] Nonce(string ip, byte[] nonce)
    { var b = new byte[12]; Bin.WB32(b, 0, netId ^ Bin.B32(IPAddress.Parse(ip).GetAddressBytes())); nonce.CopyTo(b, 4); return b; }
    public byte[] Decrypt(byte[] datagram, string source)
    {
        if (datagram.Length < 29 || !datagram.AsSpan(0, 4).SequenceEqual(Bin.Hex("32ab9864"))) throw new InvalidDataException("Invalid Pia header");
        return Bin.Gcm(key, Nonce(source, datagram[13..21]), Bin.Join(datagram[29..], datagram[21..29]), [], false, 8);
    }
    public byte[] Encrypt(byte[] body, string source, int dst, int src, int packet, ulong nonce, int flags, int footer)
    {
        var h = new byte[21]; Bin.Hex("32ab9864").CopyTo(h, 0); h[4] = 0x90; h[5] = (byte)flags;
        Bin.WB16(h, 6, dst); Bin.WB16(h, 8, src); Bin.WB16(h, 10, packet); h[12] = (byte)footer; Bin.WB64(h, 13, nonce);
        var data = Bin.Gcm(key, Nonce(source, h[13..21]), body, [], true, 8);
        return Bin.Join(h, data[^8..], data[..^8]);
    }
    public byte[] Compress(byte[] b)
    {
        var compressed = compressor.Wrap(b).ToArray();
        int fhd = compressed[4], fcsFlag = fhd >> 6;
        if ((fhd & 7) != 0) return compressed;
        int start = (fhd & 32) != 0 ? 5 + new[] { 1, 2, 4, 8 }[fcsFlag] : 6 + new[] { 0, 2, 4, 8 }[fcsFlag];
        if ((fhd & 32) == 0 && compressed[5] > 0x18) return compressed;
        return Bin.Join(Bin.Hex("28b52ffd0018"), compressed[start..]);
    }
    public byte[] Decompress(byte[] data)
    {
        if (data.Length < 4 || Bin.U32(data) != 0xfd2fb528) return data;
        // Pia may append footer/padding after the zstd frame. The stream stops at frame completion.
        using var stream = new DecompressionStream(new MemoryStream(data));
        using var output = new MemoryStream(); var chunk = new byte[4096];
        while (true)
        {
            int n = stream.Read(chunk); if (n == 0) break;
            output.Write(chunk, 0, n); if (output.Length > 16384) throw new InvalidDataException("Pia decompression limit");
        }
        return output.ToArray();
    }
    public void Dispose() { compressor.Dispose(); decompressor.Dispose(); CryptographicOperations.ZeroMemory(key); }
    public static byte[] Message(PiaMessage m)
    {
        var b = new byte[(m.Flags.HasValue ? 5 : 4) + m.Payload.Length]; int o = 0;
        b[o++] = (byte)(m.Flags.HasValue ? 7 : 6); if (m.Flags.HasValue) b[o++] = (byte)m.Flags.Value;
        Bin.WB16(b, o, m.Payload.Length); o += 2; b[o++] = (byte)m.Protocol; m.Payload.CopyTo(b, o); return b;
    }
    public static List<PiaMessage> Messages(byte[] data)
    {
        var result = new List<PiaMessage>(); int i = 0, size = -1, proto = -1, flags = 0;
        while (i < data.Length)
        {
            int bits = data[i++]; if ((bits & 0xf0) != 0 || (bits == 0 && size < 0)) break;
            if ((bits & 1) != 0) { if (i >= data.Length) break; flags = data[i++]; }
            if ((bits & 2) != 0) { if (i + 2 > data.Length) break; size = Bin.B16(data, i); i += 2; }
            if ((bits & 4) != 0) { if (i >= data.Length) break; proto = data[i++]; }
            if ((bits & 8) != 0) { if (i >= data.Length) break; i++; }
            if (size < 0 || proto < 0 || i + size > data.Length) break;
            result.Add(new(proto, data[i..(i + size)], flags)); i += size;
        }
        return result;
    }
}

public sealed record ConnectionPacket(PiaMessage Message, int Dst, int Src, bool Compress = false, bool Footer = true, bool Establishing = false, int? Packet = null, int? FooterId = null);
public sealed class PiaConnection(byte[] ourMac, byte[] hostMac, string ourIp)
{
    public int OurId = 0xc493, HostId;
    public bool Connected => state == 2;
    private int state, lastRtt = -100;
    private byte[] remoteMac = hostMac, random = RandomNumberGenerator.GetBytes(4);
    private byte[]? template;
    private ulong systemTime = 0x10000;
    private readonly Dictionary<ulong, int> pending = [];
    public Queue<double> Rtt { get; } = [];
    public List<ConnectionPacket> Outbox { get; } = [];
    public byte[] Join()
    {
        using var m = new MemoryStream();
        m.Write(Bin.Hex("00060100030505010a030d070f000058")); m.Write(random); m.Write(ourMac); m.Write(new byte[2]);
        var id = new byte[2]; Bin.WB16(id, 0, OurId); m.Write(id); m.Write(new byte[34]); m.Write(remoteMac); m.Write(new byte[2]);
        Bin.WB16(id, 0, HostId); m.Write(id); m.Write([1, 1, 0]); m.Write(IPAddress.Parse(ourIp).GetAddressBytes()); m.Write([0x30, 0x39]);
        m.Write(Bin.Hex("000000000000000100000000000000000000000301454d55")); return m.ToArray();
    }
    public void Feed(PiaMessage message, int tick)
    {
        var p = message.Payload;
        if (message.Protocol == 1 && p.Length >= 4)
        {
            if (p[1] == 0x11 && state == 0 && p.Length >= 16)
            {
                remoteMac = p[10..16]; if (HostId == 0) HostId = Bin.B16(p, 8);
                Outbox.Add(new(new(1, Bin.Join(Bin.Hex("01120000"), p[4..8])), 0, 0, Footer: false, Establishing: true, Packet: 0));
                Outbox.Add(new(new(13, Join()), 0, OurId, true, false, true, 0));
            }
            else if (p[1] == 0x50 && p.Length >= 8)
                Outbox.Add(new(new(1, Bin.Join(Bin.Hex("01510000"), p[4..8])), 0, OurId, Footer: false, Establishing: true));
        }
        else if (message.Protocol == 13 && p.Length > 0)
        {
            if (p[0] == 5 && HostId != 0 && state != 2)
                Outbox.Add(new(new(13, Bin.Join([6], ourMac, new byte[7], [1])), HostId, OurId));
            if (state == 0) state = 1;
        }
        else if (message.Protocol is 3 or 10)
        {
            if (state == 1) state = 2;
            if (message.Protocol == 3 && state != 0 && p.Length >= 16 && HostId != 0)
            {
                if (p[0] == 0)
                {
                    template = Bin.Pad(p, 21); var response = (byte[])template.Clone(); response[0] = 1;
                    Outbox.Add(new(new(3, response), 1, OurId, FooterId: HostId));
                }
                else if (p[0] == 1 && pending.Remove(Bin.U64(p, 8), out int sent)) Rtt.Enqueue((tick - sent) * (1000.0 / 59.727));
            }
        }
    }
    public void Tick(int tick)
    {
        if (!Connected || template == null || tick - lastRtt < 10) return;
        lastRtt = tick; systemTime++;
        var p = (byte[])template.Clone(); p[0] = 0; Bin.W64(p, 8, systemTime);
        pending[systemTime] = tick;
        if (pending.Count > 64) foreach (var key in pending.OrderBy(k => k.Value).Take(32).Select(k => k.Key).ToArray()) pending.Remove(key);
        Outbox.Add(new(new(3, p), 1, OurId, FooterId: HostId));
    }
}
