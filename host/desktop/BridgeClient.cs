using System.IO;
using System.Text.Json;
using Frlg.Trade.Core;

namespace Frlg.Trade.Desktop;

public sealed class BridgeClient
{
    private CancellationTokenSource? cancellation;
    private Task? completion;
    public event Action<JsonElement>? Message;
    public event Action<int>? Exited;
    public bool Running { get; private set; }
    public Task StartAsync(string port, string?[] party, int selected)
    {
        if (Running) throw new InvalidOperationException("连接正在运行。");
        var snapshot = party.Select(p => p == null ? null : Convert.FromHexString(p)).ToArray();
        cancellation = new(); Running = true;
        string run = Path.Combine(Paths.Local, "runs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-native");
        completion = RunAsync(port, snapshot, selected, run, cancellation.Token);
        return Task.CompletedTask;
    }
    private void Emit(object message) => Message?.Invoke(JsonSerializer.SerializeToElement(message));
    private async Task RunAsync(string port, byte[]?[] party, int selected, string run, CancellationToken cancel)
    {
        int code = 0;
        try { await Task.Factory.StartNew(() => new TradeSession(Emit).Run(port, party, selected, run, cancel), cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { code = 1; Emit(new { @event = "error", message = error.Message }); }
        finally
        {
            Running = false; cancellation?.Dispose(); cancellation = null;
            Emit(new { @event = "disconnected" }); Exited?.Invoke(code);
        }
    }
    public async Task StopAsync()
    {
        cancellation?.Cancel();
        if (completion != null) await completion;
    }
}
