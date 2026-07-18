using System.Runtime.InteropServices;

namespace DcsWatcherV2.Services;

public static class WindowsPowerAwakeService
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    public static void KeepSystemAwake(LogService? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
        if (result == 0)
        {
            log?.Warning("Failed to request Windows keep-awake execution state.", "Power");
            return;
        }

        log?.Info("Windows keep-awake execution state enabled while Watcher is running.", "Power");
    }

    public static void ClearKeepAwake(LogService? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = SetThreadExecutionState(ES_CONTINUOUS);
        if (result == 0)
        {
            log?.Warning("Failed to clear Windows keep-awake execution state.", "Power");
            return;
        }

        log?.Info("Windows keep-awake execution state cleared.", "Power");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
