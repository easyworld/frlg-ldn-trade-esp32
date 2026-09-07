using System.Text;
using PKHeX.Core;

namespace Frlg.Trade.Core;

public static class Rfu
{
    public static int[] Words(int command, int value = 0, int owner = 0) => [command, value, owner, 0, 0, 0, 0];
    public static int[] Fragment(int index, byte[] data)
    { var b = Bin.Pad(data, 12); return [0x8900 | index, Bin.U16(b), Bin.U16(b, 2), Bin.U16(b, 4), Bin.U16(b, 6), Bin.U16(b, 8), Bin.U16(b, 10)]; }
    public static byte[] Ni(int state, int n, int phase, int ack, byte[] payload)
    { var b = new byte[2 + payload.Length]; Bin.W16(b, 0, (state << 10) | (ack << 9) | (n << 7) | (phase << 5) | payload.Length); payload.CopyTo(b, 2); return b; }
    public static Queue<byte[]> GameData()
    {
        var b = new byte[26]; Bin.W16(b, 0, 2); Bin.W16(b, 2, 2 | (5 << 10)); Bin.W16(b, 4, 0x8822); b[12] = 0x84;
        Name("EMU", 9).CopyTo(b, 17);
        return new([Ni(1, 1, 0, 0, Bin.Hex("010c001a000000")), Ni(2, 1, 0, 0, b[..12]), Ni(2, 1, 1, 0, b[12..24]), Ni(2, 1, 2, 0, b[24..]), Ni(3, 0, 0, 0, []), Ni(0, 1, 0, 0, [])]);
    }
    public static byte[] Name(string name, int size)
    {
        var b = new byte[size]; int n = 0;
        foreach (char c in name.Take(size - 1)) b[n++] = c switch { >= 'A' and <= 'Z' => (byte)(c - 'A' + 0xbb), >= 'a' and <= 'z' => (byte)(c - 'a' + 0xd5), >= '0' and <= '9' => (byte)(c - '0' + 0xa1), _ => (byte)0 };
        b[n] = 255; return b;
    }
    public static string ReadName(byte[] b) => StringConverter3.GetString(b, 2);
    public static byte[] PlayerBlock()
    {
        var b = new byte[200]; var magic = Bin.Pad(Encoding.ASCII.GetBytes("GameFreak inc."), 16);
        magic.CopyTo(b, 0); magic.CopyTo(b, 44);
        Bin.W16(b, 16, 0x4005); Bin.W16(b, 18, 0x8000); Bin.W32(b, 20, 0x47ed8822); Name("EMU", 8).CopyTo(b, 24);
        b[32] = b[34] = 0x11; Bin.W16(b, 42, 2); return b;
    }
    public static byte[] TrainerCard()
    { var b = new byte[100]; b[2] = 1; Bin.W16(b, 14, 0x8822); Name("EMU", 8).CopyTo(b, 48); b[56] = 5; return b; }
    public static bool IsPlayer(byte[] b) => b.Length >= 60 && b.AsSpan(0, 14).SequenceEqual("GameFreak inc."u8) && b.AsSpan(44, 14).SequenceEqual("GameFreak inc."u8);
    public static byte[] Wrap(byte[] slot, uint time)
    { var b = new byte[12 + ((slot.Length + 3) & ~3)]; b[0] = 0x57; b[1] = 0x54; Bin.W16(b, 2, b.Length - 4); Bin.W32(b, 4, time); b[9] = (byte)slot.Length; slot.CopyTo(b, 12); return b; }
    public static byte[] Ack(uint sequence, int middle, uint time)
    { var b = new byte[16]; b[0] = 0x57; b[1] = 0x4b; b[2] = 12; Bin.W32(b, 4, sequence); Bin.W32(b, 8, (uint)middle); Bin.W32(b, 12, time); return b; }
}

public sealed class BlockReceive
{
    public int Count, Last = -1;
    public uint Flags;
    public byte[] Data = [];
    public bool Receiving, Done;
    public void Init(int count)
    {
        if (count is < 1 or > 32) throw new InvalidDataException("Invalid RFU fragment count");
        if (Receiving && !Done && count == Count) return;
        Count = count; Last = -1; Flags = 0; Data = new byte[count * 12]; Receiving = true; Done = false;
    }
    public bool Add(int index, byte[] fragment)
    {
        if (!Receiving || index < 0 || index >= Count) return false;
        bool previous = Done; Last = index; Flags |= 1u << index; fragment.CopyTo(Data, index * 12);
        Done = Flags == (Count == 32 ? uint.MaxValue : (1u << Count) - 1); return Done && !previous;
    }
}
public sealed class BlockSend(byte[] bytes)
{
    public int State { get; private set; }
    public bool Done => State == 3;
    private int index, roundRobin;
    public int Count { get; } = Math.Max(1, (bytes.Length + 11) / 12);
    private int[] Fragment(int i) => Rfu.Fragment(i, bytes[(i * 12)..Math.Min(bytes.Length, (i + 1) * 12)]);
    public int[] Tick(BlockReceive ack)
    {
        if (Done) return Rfu.Words(0);
        if (State == 0) { if (ack.Receiving && ack.Count == Count) State = 1; else return Rfu.Words(0x8800, Count, 0x81); }
        if (State == 1) { var result = Fragment(index); if (++index >= Count) State = 2; return result; }
        int last = Count - 1;
        if (ack.Last == last)
        {
            if (ack.Flags == (1u << Count) - 1) { State = 3; return Rfu.Words(0); }
            var missing = Enumerable.Range(0, Count).Where(i => (ack.Flags & (1u << i)) == 0).ToArray();
            if (missing.Length > 0) { roundRobin = (roundRobin + 1) % missing.Length; return Fragment(missing[roundRobin]); }
        }
        return Fragment(last);
    }
}
public sealed class Barrier
{
    public int Mode, Count, HostCount = -1, Rounds;
    public bool Initiated;
    public bool Active => Mode != 0;
    private int sinceHost, sinceInitiate, burstFor = -1, burst;
    public void Reset() { if (Mode == 1) { Mode = 0; Initiated = false; sinceInitiate = 0; burstFor = -1; } }
    public void Initiate()
    { if (Mode == 1) return; Mode = 1; Initiated = true; HostCount = -1; sinceHost = sinceInitiate = 0; burstFor = -1; }
    public void Feed(int op, int count)
    {
        sinceHost = 0; int previous = HostCount; HostCount = count;
        if (op == 0x5f00) { Count = count; if (Mode != 2) { Mode = 2; Initiated = false; } return; }
        if (Initiated && Mode == 1)
        {
            if (count == Count) { Count++; Rounds++; Mode = 0; Initiated = false; sinceInitiate = 0; }
            return;
        }
        if (count < Count) return;
        if (Mode != 1) { Mode = 1; Initiated = false; }
        else if (previous >= 0 && previous != count) Rounds++;
        Count = count; sinceInitiate = 0;
    }
    public void Observe(bool saw)
    {
        if (Mode != 1) return;
        if (saw) { sinceHost = sinceInitiate = 0; return; }
        sinceHost++;
        if (Initiated) { if (++sinceInitiate > 120) { Mode = 0; Initiated = false; } }
        else if (sinceHost > 90) { Count++; Rounds++; Mode = 0; }
    }
    public int[]? Emit()
    {
        if (Mode == 0) return null;
        if (burstFor != Count) { burstFor = Count; burst = 0; }
        if (burst++ >= 6) return null;
        return Rfu.Words(Mode == 1 ? 0x6600 : 0x5f00, Count);
    }
}
