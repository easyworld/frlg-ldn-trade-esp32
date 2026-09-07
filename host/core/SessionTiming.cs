using System.Runtime.InteropServices;

namespace Frlg.Trade.Core;

internal sealed class SessionTiming : IDisposable
{
    private readonly bool enabled = OperatingSystem.IsWindows() && timeBeginPeriod(1) == 0;
    // Windows otherwise rounds short sleeps toward 15.6ms, halving the RFU tick rate.
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    public void Dispose() { if (enabled) timeEndPeriod(1); }
}
