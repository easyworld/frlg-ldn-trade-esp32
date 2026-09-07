using System.Security.Cryptography;

namespace Frlg.Trade.Core;

public sealed class Simulator : IDisposable
{
    public TradeEngine Engine { get; }
    public PiaConnection Connection { get; }
    public ReliableLink Reliable { get; } = new();
    private readonly PiaCrypto crypto;
    private readonly Action<byte[], string> send;
    private readonly string ours, host;
    private readonly Dictionary<int, int> packetIds = [];
    private readonly HashSet<int> seen = [], kInflight = [];
    private readonly Queue<int> seenOrder = [];
    private readonly HashSet<uint> ackedTimes = [];
    private readonly Queue<uint> timeOrder = [];
    private readonly Queue<(uint Sequence, uint Time)> pendingK = [];
    private readonly Queue<byte[]> ni = Rfu.GameData();
    private byte[]? niAck, emittedAck;
    private bool accepted, opened, connectSent, uni, niDone, ackOwed, seated, exiting;
    private int credits, tag, tick, lastAck = -100, heldCount, heldKey;
    private ulong nonce = Bin.B64(RandomNumberGenerator.GetBytes(8)) | 1;
    private uint timestamp = 0x362e, kSequence;
    private readonly byte[] connectId = RandomNumberGenerator.GetBytes(2);
    public bool HostDisconnected { get; private set; }
    public int ReceivedPackets { get; private set; }
    public int DecryptFailures { get; private set; }
    public int SentPackets { get; private set; }
    public event Action<string>? Log;
    public Simulator(byte[] ssid, byte[] ourMac, byte[] hostMac, string ourIp, string hostIp, TradeEngine engine, Action<byte[], string> send)
    {
        crypto = new(ssid); ours = ourIp; host = hostIp; this.send = send; Engine = engine;
        Connection = new(ourMac, hostMac, ours); connectId[0] |= 1;
    }
    public void Receive(byte[] data, string source)
    {
        if (source != host || data.Length < 29 || !data.AsSpan(0, 4).SequenceEqual(Bin.Hex("32ab9864"))) return;
        byte[] plaintext;
        try { plaintext = crypto.Decrypt(data, source); }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { DecryptFailures++; return; }
        // Learn station identifiers only after authenticating the packet.
        if (Connection.HostId == 0 && Bin.B16(data, 8) != 0) Connection.HostId = Bin.B16(data, 8);
        int padding = data[5] >> 4, footer = data[12];
        if (padding + footer > plaintext.Length) throw new InvalidDataException("Invalid Pia footer");
        plaintext = plaintext[..(plaintext.Length - padding - footer)];
        byte[] app = (data[5] & 1) != 0 ? crypto.Decompress(plaintext) : plaintext;
        foreach (var m in PiaCrypto.Messages(app))
        {
            if (m.Protocol != 10) { Connection.Feed(m, tick); continue; }
            byte[] p = m.Payload;
            if (p.Length < 8 || Bin.B16(p, 1) > p.Length - 8 || p[7] != 0) throw new InvalidDataException("Invalid reliable frame");
            int seq = Bin.B16(p, 3); var inner = p[8..(8 + Bin.B16(p, 1))];
            if ((p[0] & 1) == 0) { Reliable.Acknowledge(inner, tick * (1000.0 / 59.727)); continue; }
            ackOwed = true;
            if (seen.Add(seq))
            {
                seenOrder.Enqueue(seq); while (seenOrder.Count > 4096) seen.Remove(seenOrder.Dequeue());
                if (inner.Length >= 4 && inner[0] == 0x57) FeedGba(inner);
            }
            Reliable.Receive(seq);
        }
        ReceivedPackets++;
    }
    private void FeedGba(byte[] p)
    {
        if (p.Length != Bin.U16(p, 2) + 4) throw new InvalidDataException("Invalid emulator frame");
        if (p[1] == 0x41) { accepted = true; Log?.Invoke("Host accepted RFU connection"); return; }
        if (p[1] == 0x44) { HostDisconnected = true; return; }
        if (p[1] != 0x54 || p.Length < 9) return;
        uint time = Bin.U32(p, 4);
        if (ackedTimes.Add(time))
        {
            timeOrder.Enqueue(time); while (timeOrder.Count > 8192) ackedTimes.Remove(timeOrder.Dequeue());
            pendingK.Enqueue((++kSequence, time)); while (pendingK.Count > 32) pendingK.Dequeue();
        }
        credits = Math.Min(credits + 1, 2);
        int length = p[8]; var slots = new List<byte[]>();
        if (length > 1)
        {
            if (length < 3 || 12 + length > p.Length) throw new InvalidDataException("Invalid parent LLSF");
            int f = p[12] | p[13] << 8 | p[14] << 16, state = (f >> 14) & 15;
            if (state == 4)
            {
                uni = true;
                for (int o = 15; o + 14 <= 12 + length; o += 14) slots.Add(p[o..(o + 14)]);
            }
            else if (((f >> 13) & 1) == 0)
            {
                if (state == 2 && length > 3 && p[15] != 5) throw new ConnectionException($"Host rejected RFU join: {p[15]}");
                if (state is 1 or 2 or 3) niAck = Rfu.Ni(state, (f >> 11) & 3, (f >> 9) & 3, 1, []);
            }
        }
        Engine.Feed(slots);
    }
    private void SendMessages(IReadOnlyList<PiaMessage> messages, int dst, int src, bool compress = false, bool footer = true, bool establishing = false, int? packet = null, int? footerId = null)
    {
        if (messages.Count == 0) return;
        var body = Bin.Join(messages.Select(PiaCrypto.Message).ToArray());
        bool zipped = compress || body.Length >= 62; if (zipped) body = crypto.Compress(body);
        if (footer) { var id = new byte[2]; Bin.WB16(id, 0, footerId ?? dst); body = Bin.Join(body, id); }
        int padding = (-body.Length) & 15; body = Bin.Pad(body, body.Length + padding, 255);
        int pid = packet ?? packetIds.GetValueOrDefault(dst, 1);
        if (!packet.HasValue) packetIds[dst] = pid == 65535 ? 1 : pid + 1;
        byte[] datagram = crypto.Encrypt(body, ours, dst, src, pid, nonce++, (padding << 4) | (zipped ? 1 : 0) | (establishing ? 2 : 0), footer ? 2 : 0);
        if (nonce == 0) nonce = 1;
        send(datagram, host); SentPackets++;
    }
    private void Batch(List<ReliablePacket> frames)
    {
        foreach (var batch in frames.Chunk(9)) SendMessages(batch.Select(p => new PiaMessage(10, Reliable.Wrap(p), p.Flags == 0 ? 0x40 : null)).ToArray(), Connection.HostId, Connection.OurId);
    }
    private byte[]? NextGba()
    {
        if (!niDone)
        {
            if (ni.TryDequeue(out var slot)) return Rfu.Wrap(slot, timestamp++);
            if (niAck != null && (emittedAck == null || !niAck.AsSpan().SequenceEqual(emittedAck)))
            { emittedAck = niAck; return Rfu.Wrap(niAck, timestamp++); }
            if (!uni) return null;
            niDone = true; Log?.Invoke("RFU NI complete");
        }
        int[] words = Engine.Tick();
        if (words[0] == 0 && Engine.Established && Engine.HostInSeat && Engine.InSeatPhase)
        {
            heldCount = (heldCount + 1) & 255; words = Rfu.Words(0xbe00, (heldCount << 8) | (heldKey == 0 ? 17 : heldKey)); heldKey = 0;
        }
        var command = new byte[16]; Bin.W16(command, 0, 0x100e);
        if (words[0] != 0) { words[0] |= tag << 5; tag = (tag + 1) & 7; for (int i = 0; i < 7; i++) Bin.W16(command, 2 + i * 2, words[i]); }
        return Rfu.Wrap(command, timestamp++);
    }
    public void Tick()
    {
        tick++;
        while (Connection.Rtt.TryDequeue(out double rtt)) Reliable.AddRtt(rtt);
        Connection.Tick(tick);
        foreach (var p in Connection.Outbox) SendMessages([p.Message], p.Dst, p.Src, p.Compress, p.Footer, p.Establishing, p.Packet, p.FooterId);
        Connection.Outbox.Clear();
        if (!Connection.Connected) return;
        double now = tick * (1000.0 / 59.727);
        if (!opened)
        {
            var metadata = Bin.Join(Bin.Hex("4a002a005801004c656166477265656e5f65"), new byte[28]);
            Batch([Reliable.Queue(metadata, 15, now)]); opened = true; return;
        }
        if (!connectSent) { Batch([Reliable.Queue(Bin.Join(Bin.Hex("57430200"), connectId), 7, now)]); connectSent = true; return; }
        var batch = Reliable.Retransmit(now, accepted && !Engine.InSeatPhase ? 1 : 2);
        kInflight.IntersectWith(Reliable.Pending.Keys);
        int middle = 0;
        while (pendingK.Count > 0 && Reliable.Pending.Count < 6 && middle < 3 && kInflight.Count < 3)
        {
            var k = pendingK.Dequeue(); var packet = Reliable.Queue(Rfu.Ack(k.Sequence, ++middle, k.Time), 7, now);
            kInflight.Add(packet.Sequence); batch.Add(packet);
        }
        if (accepted)
        {
            if (Reliable.Pending.Count < 6 && credits > 0)
            { var p = NextGba(); if (p != null) { credits--; batch.Add(Reliable.Queue(p, 7, now)); } }
            else Engine.PollSendDone();
        }
        if (tick - lastAck >= 2 && (ackOwed || Reliable.HasGap))
        { batch.Add(new(0xfff0, 0, Reliable.Ack())); ackOwed = false; lastAck = tick; }
        Batch(batch);
        if (!seated && Engine.Established && Engine.HostReady && Engine.InSeatPhase)
        { seated = true; Engine.Sit(); heldKey = 22; Log?.Invoke("Taking the right seat"); }
        if (Engine.HostExiting && !exiting) { exiting = true; heldKey = 23; Log?.Invoke("Responding to host exit"); }
    }
    public void Dispose() => crypto.Dispose();
}
