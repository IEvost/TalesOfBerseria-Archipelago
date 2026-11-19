using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace BerseriaAP.Util;

/// <summary>
/// Non-blocking logger. Writing to the Windows console blocks the calling thread while the user has
/// text selected in it (QuickEdit), which would freeze the game thread. All log calls from game hooks
/// go through this queue and are written by a background thread.
/// </summary>
internal static class SafeLog
{
    static readonly BlockingCollection<(LogLevel, string)> _queue = new(4096);

    public static void Init(ManualLogSource log)
    {
        DisableQuickEdit();
        new Thread(() =>
        {
            foreach (var (lvl, msg) in _queue.GetConsumingEnumerable())
                log.Log(lvl, msg);
        }) { IsBackground = true, Name = "BerseriaAP log" }.Start();
    }

    public static void Info(string m) => _queue.TryAdd((LogLevel.Info, m));
    public static void Debug(string m) => _queue.TryAdd((LogLevel.Debug, m));
    public static void Warn(string m) => _queue.TryAdd((LogLevel.Warning, m));
    public static void Error(string m) => _queue.TryAdd((LogLevel.Error, m));

    [DllImport("kernel32")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32")] static extern bool SetConsoleMode(IntPtr h, uint mode);

    static void DisableQuickEdit()
    {
        const int STD_INPUT_HANDLE = -10; const uint QUICK_EDIT = 0x40, EXTENDED_FLAGS = 0x80;
        var h = GetStdHandle(STD_INPUT_HANDLE);
        if (GetConsoleMode(h, out var mode)) SetConsoleMode(h, (mode & ~QUICK_EDIT) | EXTENDED_FLAGS);
    }
}
