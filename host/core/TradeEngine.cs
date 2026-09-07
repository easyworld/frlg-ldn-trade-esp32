using PKHeX.Core;

namespace Frlg.Trade.Core;

public sealed class TradeEngine
{
    public const int Ready = 0xaabb, SetMons = 0xdddd, InitBlock = 0xbbbb, Start = 0xccdd,
        ReadyFinish = 0xabcd, ConfirmFinish = 0xdcba, Cancel = 0xeeaa, ReadyCancel = 0xbbcc,
        PlayerCancel = 0xddee, BothCancel = 0xeebb, PartnerCancel = 0xeecc;
    public int State { get; private set; }
    public Barrier Barrier { get; } = new();
    public bool Established => playerSent && HostName != null;
    public bool InSeatPhase => postCancel || !seatOver;
    public bool HostInSeat { get; private set; }
    public bool HostReady { get; private set; }
    public bool HostExiting { get; private set; }
    public bool Done { get; private set; }
    public int Commits { get; private set; }
    public string? HostName { get; private set; }
    public byte[]? Received { get; private set; }
    public event Action<byte[]?[], string>? OpponentParty;
    public event Action<byte[], int>? Committed;
    public event Action<string>? Log;
    private readonly byte[][] party;
    private readonly int offered;
    private readonly BlockReceive[] receivers = Enumerable.Range(0, 5).Select(_ => new BlockReceive()).ToArray();
    private BlockSend? sender;
    private byte[]? pending;
    private byte[] hostParty = new byte[600];
    private int sentParty, hostBlocks, settle, hostCursor = -1, animWait = -1, reselect = -1;
    private bool playerSent, cardSupplied, seatOver, menuComplete, ribbons, selected, confirmed, finishSent, pendingConfirm;
    private bool leaving, cancelled, cancelAfterSend, cancelBarrier, returnBarrier, postCancel, saveBarriers, seam;
    private int saveSettle, firstEmits, secondEmits, thirdEmits, fourthEmits, thirdGap, fourthGap, postSeat;
    private bool seated;
    public int AnimationFrames { get; set; } = 1935;
    public TradeEngine(byte[]?[] data, int selectedSlot)
    {
        if (data.Length != 6 || selectedSlot is < 0 or > 5 || data[selectedSlot] == null || data.Count(p => p != null) < 2)
            throw new InvalidDataException("队伍需要至少两只宝可梦，并选择有效槽位。");
        party = data.Select(p => p == null ? new byte[100] : ToWire(p)).ToArray(); offered = selectedSlot;
    }
    public static PK3 Parse(byte[] data)
    {
        if (data.Length is not (80 or 100)) throw new InvalidDataException("Invalid PK3 size");
        var p = new PK3(data.ToArray());
        if (!p.ChecksumValid || p.Species is 0 or > 386 || p.FlagIsBadEgg) throw new InvalidDataException("PK3 checksum or species invalid");
        if (data.Length == 80 || p.Stat_Level == 0) p.ResetPartyStats(); return p;
    }
    public static byte[] ToWire(byte[] data)
    {
        var p = Parse(data); p.HeldMailID = -1; p.RefreshChecksum();
        var wire = new byte[100]; p.WriteEncryptedDataParty(wire); return wire;
    }
    public void Sit() { if (seated) return; seated = true; postSeat = 20; }
    private void Begin(byte[] data) { receivers[1] = new(); sender = new(data); }
    public void Feed(IReadOnlyList<byte[]> slots)
    {
        var completed = new List<(int Peer, int Count, byte[] Data)>(); var requests = new List<int>(); bool barrierSeen = false;
        for (int i = 0; i < Math.Min(5, slots.Count); i++)
        {
            byte[] slot = slots[i]; if (slot.Length != 14) throw new InvalidDataException("Invalid RFU command size");
            int word = Bin.U16(slot), op = word & 0xff00, value = Bin.U16(slot, 2);
            if (op == 0xa100) requests.Add(value);
            else if (op == 0x8800) receivers[i].Init(value);
            else if (op == 0x8900 && receivers[i].Add(word & 31, slot[2..14])) completed.Add((i, receivers[i].Count, (byte[])receivers[i].Data.Clone()));
            if (!HostReady && op == 0xbe00)
            {
                if (!HostInSeat) { HostInSeat = true; Barrier.Reset(); Log?.Invoke("Host entered the room"); }
                if ((value & 255) == 22) HostReady = true;
            }
            if (op == 0xbe00 && (value & 255) == 23) HostExiting = true;
            if (i != 1 && op is 0x6600 or 0x5f00 && !barrierSeen) { Barrier.Feed(op, value); barrierSeen = true; }
        }
        Barrier.Observe(barrierSeen);
        if (saveBarriers) saveSettle = barrierSeen ? 0 : saveSettle + 1;
        bool hostBlock = completed.Any(c => c.Peer == 0);
        if (saveBarriers && (requests.Count > 0 || hostBlock)) { saveBarriers = false; Barrier.Reset(); }
        foreach (int req in requests)
        {
            if (sender is { Done: false }) continue;
            Begin(RequestBlock(req)); Log?.Invoke($"RFU request {req}");
        }
        foreach (var c in completed.Where(c => c.Peer == 0)) HostBlock(c.Count, c.Data);
        settle = requests.Count > 0 || hostBlock ? 0 : settle + 1;
    }
    private byte[] RequestBlock(int type)
    {
        if (type == 2) { cardSupplied = true; return Rfu.TrainerCard(); }
        if (type == 3) return new byte[220];
        if (type == 4) return new byte[40];
        if (HostInSeat) seatOver = true;
        if (Commits == 0 && !playerSent) { playerSent = true; return Rfu.PlayerBlock(); }
        int block = sentParty++; return block < 3 ? Bin.Join(party[block * 2], party[block * 2 + 1]) : new byte[200];
    }
    public void HostBlock(int count, byte[] data)
    {
        if (count == 9 && !menuComplete) return;
        if (count == 2) { OnCommand(Bin.U16(data), Bin.U16(data, 2)); return; }
        if (count == 4) { ribbons = true; return; }
        if (count != 17) return;
        if (HostInSeat) seatOver = true;
        if (Rfu.IsPlayer(data)) { HostName ??= Rfu.ReadName(data[24..32]); return; }
        if (hostBlocks >= 3) return;
        data.AsSpan(0, 200).CopyTo(hostParty.AsSpan(200 * hostBlocks)); hostBlocks++;
        if (hostBlocks == 3)
        {
            var parsed = Enumerable.Range(0, 6).Select(i => new PK3(hostParty[(i * 100)..((i + 1) * 100)])).ToArray();
            foreach (var p in parsed.Where(p => p.Species != 0)) if (!p.ChecksumValid) throw new InvalidDataException("Opponent PK3 checksum failed");
            OpponentParty?.Invoke(parsed.Select(p => p.Species == 0 ? null : p.Data.ToArray()).ToArray(), HostName ?? "Switch");
            if (State == 0) State = 1;
        }
    }
    public void OnCommand(int command, int cursor)
    {
        Log?.Invoke($"LINKCMD {command:x4} cursor={cursor}");
        switch (command)
        {
            case SetMons:
                if (cursor is < 0 or > 5) throw new InvalidDataException("Invalid opponent cursor");
                hostCursor = cursor;
                if (State is 1 or 2) { State = 3; if (!confirmed) { confirmed = true; pending = LinkCommand(InitBlock); } }
                break;
            case Start:
                if (!cancelled) { State = 4; animWait = AnimationFrames; }
                break;
            case ConfirmFinish:
                if (cancelled || Commits != 0) break;
                if (finishSent) Commit(); else pendingConfirm = true;
                break;
            case BothCancel:
                State = 6; cancelled = true; cancelBarrier = true; Barrier.Initiate(); break;
            case PlayerCancel:
            case PartnerCancel:
                State = 1; selected = false; reselect = 60; pending = null; cancelAfterSend = false;
                confirmed = false; cancelled = false; hostCursor = -1; break;
        }
    }
    public static byte[] LinkCommand(int command, int cursor = 0)
    { var b = new byte[20]; Bin.W16(b, 0, command); Bin.W16(b, 2, cursor); return b; }
    private void Commit()
    {
        if (hostCursor < 0 || hostBlocks != 3) throw new InvalidDataException("Trade confirmed without a complete opponent selection");
        var received = hostParty[(hostCursor * 100)..((hostCursor + 1) * 100)];
        Received = Parse(received).Data.ToArray(); party[offered] = received; Commits++;
        Committed?.Invoke(Received, offered);
        saveBarriers = true; saveSettle = 0; leaving = true;
        sentParty = hostBlocks = settle = 0; hostParty = new byte[600]; hostCursor = -1;
        selected = ribbons = finishSent = pendingConfirm = confirmed = seam = false; animWait = reselect = -1; State = 0;
    }
    private void Timers()
    {
        if (reselect >= 0) reselect--;
        if (animWait >= 0 && animWait-- == 0)
        {
            pending = LinkCommand(ReadyFinish); finishSent = true;
            if (pendingConfirm) { pendingConfirm = false; Commit(); }
        }
    }
    public void PollSendDone() { Timers(); if (sender?.State == 2) Tick(); }
    private static int[] Sustain(int count, ref int emits, ref int gap)
    {
        if (emits < 6) { emits++; gap = 0; return Rfu.Words(0x6600, count); }
        if (++gap >= 60) emits = gap = 0;
        return Rfu.Words(0);
    }
    public int[] Tick()
    {
        if (sender != null)
        {
            var words = sender.Tick(receivers[1]);
            if (sender.Done) { sender = null; if (cancelAfterSend && !Done) { cancelAfterSend = false; State = 6; leaving = true; } }
            return words;
        }
        if (cancelBarrier)
        {
            if (Barrier.Active) return Barrier.Emit() ?? Rfu.Words(0);
            cancelBarrier = false; returnBarrier = true; Barrier.Initiate();
        }
        if (returnBarrier)
        {
            if (Barrier.Active) return Barrier.Emit() ?? Rfu.Words(0);
            returnBarrier = false; Done = postCancel = true;
        }
        if (postCancel && Barrier.Active) return Barrier.Emit() ?? Rfu.Words(0);
        if (saveBarriers)
        {
            if (!Barrier.Active && saveSettle > 600) saveBarriers = false;
            else { if (!Barrier.Active) Barrier.Initiate(); return Barrier.Emit() ?? Rfu.Words(0); }
        }
        Timers();
        if (animWait >= 0 && !seam) { seam = true; Barrier.Initiate(); }
        if (!selected && State == 1 && reselect < 0 && hostBlocks >= 3 && playerSent && sentParty >= 3 && (ribbons || settle >= 600) && pending == null)
        {
            selected = menuComplete = true;
            if (leaving) { pending = LinkCommand(Cancel); cancelled = true; }
            else { State = 2; pending = LinkCommand(Ready, offered); }
        }
        if (pending != null)
        {
            var buf = pending; pending = null;
            if (Bin.U16(buf) is Cancel or ReadyCancel) cancelAfterSend = cancelled = true;
            Begin(buf); return sender!.Tick(receivers[1]);
        }
        if (Established && !HostInSeat)
        {
            if (!cardSupplied && firstEmits++ < 6) return Rfu.Words(0x6600, 0);
            if (cardSupplied && secondEmits++ < 6) return Rfu.Words(0x6600, 1);
            return Rfu.Words(0);
        }
        if (Established && seated && !seatOver)
        {
            if (postSeat > 0) { postSeat--; return Rfu.Words(0); }
            if (Barrier.HostCount < 2) return Sustain(2, ref thirdEmits, ref thirdGap);
            if (Barrier.HostCount < 3) return Sustain(3, ref fourthEmits, ref fourthGap);
            return Rfu.Words(0);
        }
        return Barrier.Emit() ?? Rfu.Words(0);
    }
}
