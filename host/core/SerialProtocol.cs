using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Frlg.Trade.Core;

public sealed record SerialFrame(byte Kind, uint Request, uint Session, byte[] Payload);

public static class SerialCodec
{
    public const int MaxFrame = 4096;
    public static byte[] Encode(SerialFrame frame)
    {
        if (frame.Payload.Length > MaxFrame - 16) throw new InvalidDataException("Serial payload too large");
        var bytes = new byte[16 + frame.Payload.Length];
        bytes[0] = 1; bytes[1] = frame.Kind; Bin.W32(bytes, 2, frame.Request); Bin.W32(bytes, 6, frame.Session);
        Bin.W16(bytes, 10, frame.Payload.Length); frame.Payload.CopyTo(bytes, 12);
        Bin.W32(bytes, bytes.Length - 4, Bin.Crc32(bytes.AsSpan(0, bytes.Length - 4)));
        var encoded = new List<byte> { 0 }; int codeAt = 0; byte code = 1;
        foreach (byte b in bytes)
        {
            if (b == 0) { encoded[codeAt] = code; codeAt = encoded.Count; encoded.Add(0); code = 1; }
            else
            {
                encoded.Add(b);
                if (++code == 255) { encoded[codeAt] = code; codeAt = encoded.Count; encoded.Add(0); code = 1; }
            }
        }
        encoded[codeAt] = code; encoded.Add(0); return encoded.ToArray();
    }
    public static SerialFrame? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > MaxFrame + 32) return null;
        Span<byte> decoded = stackalloc byte[MaxFrame]; int read = 0, count = 0;
        while (read < encoded.Length)
        {
            int code = encoded[read++];
            int needed = code - 1 + (code != 255 && read + code - 1 < encoded.Length ? 1 : 0);
            if (code == 0 || read + code - 1 > encoded.Length || count + needed > decoded.Length) return null;
            for (int i = 1; i < code; i++) decoded[count++] = encoded[read++];
            if (code != 255 && read < encoded.Length) decoded[count++] = 0;
        }
        if (count < 16 || decoded[0] != 1 || count != 16 + Bin.U16(decoded, 10) ||
            Bin.U32(decoded, count - 4) != Bin.Crc32(decoded[..(count - 4)])) return null;
        return new(decoded[1], Bin.U32(decoded, 2), Bin.U32(decoded, 6), decoded[12..(count - 4)].ToArray());
    }
}

public sealed class SerialDevice : IDisposable
{
    private readonly SerialPort port;
    private readonly CancellationToken cancel;
    private readonly List<byte> buffer = [];
    private readonly Queue<SerialFrame> events = [];
    private readonly Dictionary<uint, List<string>> responses = [];
    private readonly HashSet<uint> done = [];
    private uint sequence;
    private bool overflow;
    private bool stopping;
    public uint Session { get; private set; }
    public string Model { get; private set; } = "";
    public int BadFrames { get; private set; }
    public SerialDevice(string name, CancellationToken cancel)
    {
        this.cancel = cancel;
        port = new(name, 115200) { DtrEnable = false, RtsEnable = false, ReadTimeout = 20, WriteTimeout = 2000,
            ReadBufferSize = 65536, WriteBufferSize = 32768 };
        port.Open();
    }
    private void Write(byte[] data) => port.Write(data, 0, data.Length);
    public void Pump()
    {
        if (!stopping) cancel.ThrowIfCancellationRequested();
        int available = Math.Min(port.BytesToRead, 65536);
        if (available == 0) return;
        var data = new byte[available]; int count = port.Read(data, 0, data.Length);
        for (int i = 0; i < count; i++)
        {
            if (data[i] != 0)
            {
                if (buffer.Count < SerialCodec.MaxFrame + 32) buffer.Add(data[i]); else overflow = true;
                continue;
            }
            if (buffer.Count == 0 && !overflow) continue;
            var frame = overflow ? null : SerialCodec.Decode(buffer.ToArray()); buffer.Clear(); overflow = false;
            if (frame == null) { BadFrames++; continue; }
            if (frame.Session != Session && Session != 0) continue;
            if (frame.Kind == 2 && responses.TryGetValue(frame.Request, out var lines))
            {
                string text = Encoding.UTF8.GetString(frame.Payload);
                if (text == "LDN_DONE") done.Add(frame.Request); else lines.Add(text);
            }
            else if (frame.Kind is 3 or 5)
            {
                if (events.Count >= 1024) throw new IOException("串口接收队列已满");
                events.Enqueue(frame);
            }
        }
    }
    public List<string> Command(string command, double timeout = 3)
    {
        uint id = ++sequence; var lines = new List<string>(); responses.Add(id, lines);
        try
        {
            Write(SerialCodec.Encode(new(1, id, Session, Encoding.ASCII.GetBytes(command))));
            var watch = Stopwatch.StartNew();
            while (!done.Contains(id))
            {
                Pump();
                if (watch.Elapsed.TotalSeconds > timeout) throw new TimeoutException($"设备未响应 {command.Split(' ')[0]}");
                Thread.Sleep(1);
            }
            foreach (string line in lines)
            {
                if (line.StartsWith("LDN_ERROR ")) throw new ConnectionException(line);
                if (line.Contains("_RESULT ") && line.Split(' ')[^1] != "0") throw new ConnectionException(line);
            }
            return lines;
        }
        finally { responses.Remove(id); done.Remove(id); }
    }
    public void Handshake()
    {
        List<string>? reply = null;
        foreach (int baud in new[] { 115200, 921600 })
        {
            port.BaudRate = baud; port.DiscardInBuffer(); buffer.Clear();
            Write(Encoding.ASCII.GetBytes("\nLDN_BINARY\n")); Write([0]);
            try { reply = Command("LDN_HELLO", 2); break; } catch (TimeoutException) { }
        }
        string? hello = reply?.FirstOrDefault(l => l.StartsWith("LDN_HELLO 1 "));
        if (hello == null) throw new ConnectionException("设备未提供串口协议 v1，请先安装支持动态会话的新固件。");
        var fields = hello.Split(' ');
        if (fields.Length != 5 || new[] { "dynamic-session", "scan", "auth", "udp" }.Except(fields[3].Split(',')).Any() ||
            !int.TryParse(fields[4], out int mtu) || mtu < 1472)
            throw new ConnectionException("设备能力不满足交易要求。");
        Model = fields[2];
        if (port.BaudRate != 921600) { Command("LDN_BAUD 921600"); port.BaudRate = 921600; }
        Session = Bin.U32(RandomNumberGenerator.GetBytes(4)) | 1;
        Command($"LDN_BEGIN {Session:x8}", 8); events.Clear(); BadFrames = 0;
    }
    public void SendDatagram(byte[] data, string destination)
    {
        if (data.Length > 1472) throw new InvalidDataException("Datagram exceeds MTU");
        Write(SerialCodec.Encode(new(4, 0, Session, Bin.Join(IPAddress.Parse(destination).GetAddressBytes(), data))));
    }
    public List<SerialFrame> Drain()
    { Pump(); var list = events.ToList(); events.Clear(); return list; }
    public void Stop() { stopping = true; Command("LDN_STOP", 5); }
    public void Dispose() => port.Dispose();
}
