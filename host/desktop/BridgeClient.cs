using System.IO;
using System.Text.Json;
using Frlg.Trade.Core;

namespace Frlg.Trade.Desktop;

public sealed class BridgeClient
{
    private readonly object selectionGate = new();
    private CancellationTokenSource? cancellation;
    private Task? completion;
    private bool[] activeSlots = new bool[6];
    private int selectedSlot, lockedSlot = -1;
    public event Action<JsonElement>? Message;
    public event Action<int>? Exited;
    public bool Running { get; private set; }
    public Task StartAsync(string port, string?[] party, int selected)
    {
        if (Running) throw new InvalidOperationException("连接正在运行。");
        var snapshot = party.Select(p => p == null ? null : Convert.FromHexString(p)).ToArray();
        cancellation = new();
        lock (selectionGate)
        {
            activeSlots = snapshot.Select(p => p != null).ToArray();
            selectedSlot = selected; lockedSlot = -1; Running = true;
        }
        string run = Path.Combine(Paths.Local, "runs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-native");
        completion = RunAsync(port, snapshot, selected, run, cancellation.Token);
        return Task.CompletedTask;
    }
    public bool IsOfferLocked { get { lock (selectionGate) return lockedSlot >= 0; } }
    public bool IsActiveSlot(int slot) { lock (selectionGate) return slot is >= 0 and < 6 && activeSlots[slot]; }
    public bool TrySelectSlot(int slot)
    {
        lock (selectionGate)
        {
            if (!Running || lockedSlot >= 0 || slot is < 0 or > 5 || !activeSlots[slot]) return false;
            selectedSlot = slot; return true;
        }
    }
    private int ReadAndLockSlot()
    {
        lock (selectionGate) { lockedSlot = selectedSlot; return selectedSlot; }
    }
    private void SetLockedSlot(int? slot)
    {
        lock (selectionGate) { lockedSlot = slot ?? -1; if (slot.HasValue) selectedSlot = slot.Value; }
    }
    private void Emit(object message) => Message?.Invoke(JsonSerializer.SerializeToElement(message));
    private async Task RunAsync(string port, byte[]?[] party, int selected, string run, CancellationToken cancel)
    {
        int code = 0;
        try { await Task.Factory.StartNew(() => new TradeSession(Emit).Run(port, party, selected, run, cancel, ReadAndLockSlot, SetLockedSlot), cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { code = 1; Emit(new { @event = "error", message = error.Message }); }
        finally
        {
            lock (selectionGate) { lockedSlot = -1; activeSlots = new bool[6]; Running = false; }
            cancellation?.Dispose(); cancellation = null;
            Emit(new { @event = "disconnected" }); Exited?.Invoke(code);
        }
    }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (completion != null) await completion;
    }
}
