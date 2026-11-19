using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Configuration;
using BerseriaAP.Archipelago;
using BerseriaAP.Native;

namespace BerseriaAP;

[BepInPlugin(Guid, "Berseria Archipelago", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "berseria.archipelago";
    public const string Version = "0.1.0";
    internal new static ManualLogSource Log = null!;
    internal static string Dir = null!;
    internal static bool DebugMode { get; private set; }

    ConfigEntry<string> _server = null!, _slot = null!, _password = null!;
    ConfigEntry<bool> _autoConnect = null!, _devConsole = null!;
    internal static ApClient Ap { get; } = new();
    internal static Game.GameData Data { get; private set; } = null!;
    Game.Randomizer? _randomizer;

    public override void Load()
    {
        Log = base.Log;
        Util.SafeLog.Init(Log);
        Dir = Path.Combine(Paths.PluginPath, "BerseriaAP");
        Directory.CreateDirectory(Dir);

        _server = Config.Bind("Archipelago", "Server", "archipelago.gg:38281", "Archipelago server address (host:port).");
        _slot = Config.Bind("Archipelago", "Slot", "", "Your slot (player) name.");
        _password = Config.Bind("Archipelago", "Password", "", "Room password, if any.");
        _autoConnect = Config.Bind("Archipelago", "AutoConnect", true,
            "Connect automatically on startup. Editing and saving this file while the game runs reconnects.");
        _devConsole = Config.Bind("Debug", "DevConsole", false, "Enable the lua_in.lua/lua_out.txt developer console.");
        Data = Game.GameData.Load();
        Log.LogInfo($"Game data: {Data.locations.Count} locations, {Data.items.Count} items.");
        WatchConfig();
        _window = new Ui.ConnectWindow(
            () => new(_server.Value, _slot.Value, _password.Value),
            s => { SaveSettings(s); _ = Ap.ConnectAsync(s.Server, s.Slot, s.Password); },
            () => { Ap.Disconnect(); Ap.ChatLog.Enqueue("Disconnected."); },
            () => Ap.Status,
            () => { var l = new List<string>(); while (Ap.ChatLog.TryDequeue(out var m)) l.Add(m); return l; });
        _window.Start(showImmediately: string.IsNullOrWhiteSpace(_slot.Value));
        // The engine DLL is loaded lazily on Unity's first P/Invoke, after plugins load. Install the hooks
        // the moment it is mapped, before any of its code runs (patching later races the game thread).
        if (GameModule.Init())
        {
            Log.LogWarning($"{GameModule.DllName} was already loaded; installing hooks late.");
            OnEngineLoaded();
        }
        else if (!DllLoadWatcher.Watch(GameModule.DllName, b => { if (GameModule.Init(b)) OnEngineLoaded(); }))
            Log.LogError("Could not register DLL load notification; mod disabled.");
    }

    void OnEngineLoaded()
    {
        Log.LogInfo($"Engine base: 0x{(long)GameModule.Base:X}");
        LuaBridge.Init();
        DebugMode = _devConsole.Value;
        if (_devConsole.Value) DevConsole.Init();
        _randomizer = new Game.Randomizer(Data, Ap);
        // We may be under the loader lock here: never block. Queue networking on the thread pool
        // (Thread.Start would wait for the new thread, which itself needs the loader lock).
        if (_autoConnect.Value) ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1000); Connect(); });
    }

    void Connect()
    {
        if (string.IsNullOrWhiteSpace(_slot.Value))
        {
            Log.LogWarning($"No slot name set. Edit {Config.ConfigFilePath} ([Archipelago] Slot) and save it to connect.");
            return;
        }
        _ = Ap.ConnectAsync(_server.Value.Trim(), _slot.Value.Trim(), _password.Value);
    }

    Ui.ConnectWindow? _window;
    DateTime _ignoreConfigUntil = DateTime.MinValue;

    /// <summary>Persist settings typed into the connect window (without triggering the file watcher).</summary>
    void SaveSettings(Ui.ConnectWindow.Settings s)
    {
        _ignoreConfigUntil = DateTime.Now.AddSeconds(3);
        _server.Value = s.Server; _slot.Value = s.Slot; _password.Value = s.Password;
        Config.Save();
    }

    /// <summary>Reconnect when the user edits and saves the config file while the game is running.</summary>
    void WatchConfig()
    {
        var path = Config.ConfigFilePath;
        var w = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            { NotifyFilter = NotifyFilters.LastWrite, EnableRaisingEvents = true };
        DateTime last = DateTime.MinValue;
        w.Changed += (_, _) =>
        {
            if (DateTime.Now < _ignoreConfigUntil) return; // our own save from the connect window
            if ((DateTime.Now - last).TotalSeconds < 2) return; // debounce
            last = DateTime.Now;
            Thread.Sleep(300);
            try
            {
                string before = $"{_server.Value}|{_slot.Value}|{_password.Value}";
                Config.Reload();
                string after = $"{_server.Value}|{_slot.Value}|{_password.Value}";
                if (before != after || !Ap.Connected) { Log.LogInfo("Config changed; reconnecting."); Connect(); }
            }
            catch (Exception e) { Log.LogError(e); }
        };
        _configWatcher = w;
    }
    FileSystemWatcher? _configWatcher;
}

/// <summary>
/// Development REPL: write Lua to plugins/BerseriaAP/lua_in.lua; on the next safe point it is executed
/// in-game and the result is written to lua_out.txt (file deleted after reading).
/// </summary>
internal static class DevConsole
{
    static string _in = null!, _out = null!;
    static readonly Stopwatch _sw = Stopwatch.StartNew();
    static long _last;
    static bool _announced;

    public static void Init()
    {
        _in = Path.Combine(Plugin.Dir, "lua_in.lua");
        _out = Path.Combine(Plugin.Dir, "lua_out.txt");
        LuaBridge.OnSafePoint += Tick;
    }

    static void Tick()
    {
        if (!_announced) { _announced = true; Util.SafeLog.Info($"First Lua safe point, main state 0x{(long)LuaBridge.MainState:X}"); }
        long now = _sw.ElapsedMilliseconds;
        if (now - _last < 100) return;
        _last = now;
        if (!File.Exists(_in)) return;
        string code;
        try { code = File.ReadAllText(_in); File.Delete(_in); } catch { return; }
        var t0 = _sw.Elapsed;
        var (ok, result) = LuaBridge.Execute(code, "=devconsole");
        File.WriteAllText(_out, $"{(ok ? "OK" : "ERR")} ({(_sw.Elapsed - t0).TotalMilliseconds:F1} ms)\n{result}");
    }
}
