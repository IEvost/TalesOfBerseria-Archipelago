using System.Diagnostics;
using System.Reflection;
using System.Text;
using BerseriaAP.Archipelago;
using BerseriaAP.Native;
using BerseriaAP.Util;

namespace BerseriaAP.Game;

/// <summary>
/// Game-thread controller. Runs at Lua safe points (~10 Hz) and is the only code that touches game state.
///  - keeps the Lua runtime (ap_runtime.lua) loaded
///  - forwards location checks, boss/goal progress to Archipelago
///  - delivers received items using a counter stored inside the save file
///  - pushes region locks and scouted texts into the runtime
/// </summary>
internal sealed class Randomizer
{
    const int RuntimeVersion = 1;
    readonly GameData _data;
    readonly ApClient _ap;
    readonly Stopwatch _sw = Stopwatch.StartNew();
    long _lastTick, _lastLoadAttempt = -10_000;
    bool _runtimeReady;
    string _runtimeSource = "";

    // Sync state (re-pushed whenever it changes or the runtime is reloaded)
    bool _pushedActive;
    string _pushedLocks = "\u0000";
    int _pushedScoutCount = -1;
    bool _needResync;
    long _scenario = -1;
    bool _goalSent;
    bool _seedWarned;
    readonly HashSet<long> _sentLocal = new();

    public Randomizer(GameData data, ApClient ap)
    {
        _data = data;
        _ap = ap;
        _ap.OnConnected += () => { _needResync = true; _pushedScoutCount = -1; _pushedLocks = "\u0000"; _goalSent = false; };
        _runtimeSource = LoadRuntimeSource();
        LuaBridge.OnSafePoint += Tick;
    }

    static string LoadRuntimeSource()
    {
        // Dev override: a loose ap_runtime.lua next to the plugin wins over the embedded copy.
        var loose = Path.Combine(Plugin.Dir, "ap_runtime.lua");
        if (File.Exists(loose)) return File.ReadAllText(loose);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("BerseriaAP.ap_runtime.lua")!;
        return new StreamReader(s).ReadToEnd();
    }

    void Tick()
    {
        long now = _sw.ElapsedMilliseconds;
        if (now - _lastTick < 100) return;
        _lastTick = now;

        if (!EnsureRuntime(now)) return;

        // 1) Events from the game
        var (ok, events) = LuaBridge.Execute("return BERSERIA_AP.Poll()", "=ap_poll");
        if (!ok) { SafeLog.Error("Poll failed: " + events); _runtimeReady = false; return; }
        if (events.Length > 0) HandleEvents(events);

        // 2) State the runtime needs
        PushState();
        while (_ap.DeathQueue.TryDequeue(out var death))
            LuaBridge.Execute($"BERSERIA_AP.ReceiveDeath({Lua.Str(death)}) return 'ok'", "=ap_death");

        // 3) Item delivery and resync (only while connected & in normal field gameplay with a save loaded)
        if (_ap.Connected)
        {
            var (okF, free) = LuaBridge.Execute("return BERSERIA_AP.CanReceive()", "=ap_free");
            if (!okF || free != "true") return;
            if (_needResync) Resync();
            DeliverItems();
        }
    }

    bool EnsureRuntime(long now)
    {
        if (_runtimeReady)
        {
            // Detect a fresh Lua state (e.g. after returning to title) cheaply.
            var (ok, v) = LuaBridge.Execute("local a=rawget(_G,'BERSERIA_AP') return a and a.version or 0", "=ap_ver");
            if (ok && v == RuntimeVersion.ToString()) return true;
            _runtimeReady = false;
        }
        if (now - _lastLoadAttempt < 2000) return false;
        _lastLoadAttempt = now;
        // Game modules must exist (not the case on the boot/title screens).
        var (rdyOk, ready) = LuaBridge.Execute("return rawget(_G,'TO13') ~= nil and TO13.Database ~= nil and TO13.Database.TreasureBox ~= nil", "=ap_ready");
        if (!rdyOk || ready != "true") return false;
        var (rok, rres) = LuaBridge.Execute(_runtimeSource, "=ap_runtime");
        if (!rok) { SafeLog.Warn("Runtime load deferred: " + rres); return false; }
        SafeLog.Info("Lua runtime: " + rres);
        if (Plugin.DebugMode) LuaBridge.Execute("BERSERIA_AP.debug=true return 'ok'", "=ap_dbg");
        _runtimeReady = true;
        _pushedActive = false; _pushedLocks = "\u0000"; _pushedScoutCount = -1; _needResync = true;
        return true;
    }

    void HandleEvents(string events)
    {
        var checks = new List<long>();
        foreach (var line in events.Split('\n'))
        {
            var p = line.Split('\t');
            switch (p[0])
            {
                case "CHECK" when p.Length > 1:
                    if (_data.LocationByKey.TryGetValue(p[1], out var loc)) checks.Add(loc.id);
                    else SafeLog.Warn("Unknown location key " + p[1]);
                    break;
                case "SCENARIO" when p.Length > 1 && long.TryParse(p[1], out var sc):
                    // Changes on story progress and on loading a save: re-validate the save and resync checks.
                    if (sc != _scenario) { _needResync = true; _seedWarned = false; }
                    OnScenario(sc, checks);
                    break;
                case "BLOCKED":
                    SafeLog.Info($"Blocked entry to locked region {(p.Length > 1 ? p[1] : "?")}");
                    break;
                case "ERROR":
                    SafeLog.Error("Runtime: " + line);
                    break;
                case "DEBUG":
                    SafeLog.Info("Runtime debug: " + line.Replace('\t', ' '));
                    break;
                case "DEATH":
                    _ap.SendDeath($"{_ap.PlayerName(_ap.Slot)}'s party was wiped out.");
                    break;
                case "DEATHLINK_APPLIED":
                    SafeLog.Info("DeathLink: party knocked out.");
                    break;
            }
        }
        if (checks.Count > 0)
        {
            foreach (var c in checks) _sentLocal.Add(c);
            _ap.SendChecks(checks);
        }
    }

    void OnScenario(long sc, List<long> checks)
    {
        _scenario = sc;
        foreach (var b in _data.Bosses)
            if (sc > b.scenario) checks.Add(b.id);
        if (sc > _data.goal_scenario && _ap.Connected && !_goalSent)
        {
            _goalSent = true;
            _ap.SendGoal();
            Notify("Archipelago: Goal complete! Congratulations!");
        }
    }

    void PushState()
    {
        bool active = _ap.Connected;
        var sb = new StringBuilder();
        if (active != _pushedActive)
        {
            sb.Append($"BERSERIA_AP.active={(active ? "true" : "false")} ");
            sb.Append($"BERSERIA_AP.sparkles={(_ap.SlotFlag("sparklesanity") ? "true" : "false")} ");
            sb.Append($"BERSERIA_AP.deathlink={(_ap.DeathLinkEnabled ? "true" : "false")} ");
            _pushedActive = active;
            if (active) Notify($"Archipelago: {_ap.Status}");
        }
        // Region locks
        // While disconnected keep the last known locks (no free roaming by going offline).
        string locks = active ? (_ap.SlotFlag("region_keys") ? ComputeLocks() : "")
                              : (_pushedLocks == "\u0000" ? "" : _pushedLocks);
        if (locks != _pushedLocks)
        {
            if (locks.Length == 0) sb.Append("BERSERIA_AP.locked=nil ");
            else
            {
                sb.Append("BERSERIA_AP.locked={");
                foreach (var c in locks.Split(',')) sb.Append($"[{Lua.Str(c)}]=true,");
                sb.Append("} BERSERIA_AP.lockText={");
                foreach (var c in locks.Split(','))
                {
                    var name = _data.regions.FirstOrDefault(r => r.code == c)?.name ?? c;
                    sb.Append($"[{Lua.Str(c)}]={Lua.Str($"{name} is locked. You need \"Region Key: {name}\" from Archipelago.")},");
                }
                sb.Append("} ");
            }
            _pushedLocks = locks;
        }
        // Scouted texts (sent once after connecting)
        if (active && _ap.ScoutText.Count != _pushedScoutCount)
        {
            sb.Append("local s=BERSERIA_AP.scout ");
            foreach (var (id, text) in _ap.ScoutText)
                if (_data.LocationById.TryGetValue(id, out var loc)) sb.Append($"s[{Lua.Str(loc.key)}]={Lua.Str(text)} ");
            _pushedScoutCount = _ap.ScoutText.Count;
        }
        if (sb.Length == 0) return;
        var (ok, err) = LuaBridge.Execute(sb + " return 'ok'", "=ap_state");
        if (!ok) SafeLog.Error("PushState: " + err);
    }

    string ComputeLocks()
    {
        var unlocked = new HashSet<string>();
        foreach (var id in _ap.ReceivedItemIds())
            if (_data.ItemById.TryGetValue(id, out var it) && it.kind == "key" && it.region != null) unlocked.Add(it.region);
        return string.Join(",", _data.LockableRegions.Where(r => !unlocked.Contains(r)).OrderBy(r => r));
    }

    int SeedHash()
    {
        unchecked
        {
            int h = 17;
            foreach (var ch in _ap.SeedName + "|" + _ap.Slot) h = h * 31 + ch;
            return h == 0 ? 1 : h;
        }
    }

    /// <summary>On connect / runtime reload: report everything this save already opened.</summary>
    void Resync()
    {
        var (ok, list) = LuaBridge.Execute("return BERSERIA_AP.OpenedList()", "=ap_resync");
        if (!ok) { SafeLog.Warn("Resync failed: " + list); return; }
        _needResync = false;
        if (!SaveBelongsToSeed()) return;
        var ids = new List<long>();
        foreach (var key in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (_data.LocationByKey.TryGetValue(key, out var loc)) ids.Add(loc.id);
        if (_scenario > 0) OnScenario(_scenario, ids);
        _ap.SendChecks(ids);
    }

    bool SaveBelongsToSeed()
    {
        var (ok, state) = LuaBridge.Execute("local c,h=BERSERIA_AP.GetSaveState() return c..','..h", "=ap_save");
        if (!ok) return false;
        var parts = state.Split(',');
        long count = long.Parse(parts[0]); long hash = long.Parse(parts[1]);
        int mine = SeedHash();
        if (hash == 0 && count == 0)
        {
            LuaBridge.Execute($"BERSERIA_AP.SetSaveState(0,{mine}) return 'ok'", "=ap_save");
            SafeLog.Info("This save is now bound to the current Archipelago seed.");
            return true;
        }
        if (hash != mine)
        {
            if (!_seedWarned) { _seedWarned = true; Notify("Archipelago: this save belongs to a different seed/slot. Items will not be delivered."); }
            return false;
        }
        return true;
    }

    void DeliverItems()
    {
        if (!SaveBelongsToSeed()) return;
        var (ok, state) = LuaBridge.Execute("local c=BERSERIA_AP.GetSaveState() return c", "=ap_save");
        if (!ok || !int.TryParse(state, out int count)) return;
        var item = _ap.ReceivedAt(count);
        if (item == null) return;

        string give = "true";
        string? text = null;
        bool fromSelf = item.Player.Slot == _ap.Slot && item.LocationId >= 0;
        if (_data.ItemById.TryGetValue(item.ItemId, out var it))
        {
            switch (it.kind)
            {
                case "gald": give = $"BERSERIA_AP.Give('gald',1,{it.count})"; break;
                case "game": give = $"BERSERIA_AP.Give('game',{it.game_id},1)"; break;
                case "key": give = "true"; break; // virtual: unlocks come from the server item list
            }
        }
        else SafeLog.Warn($"Unknown item id {item.ItemId}");

        if (!fromSelf) text = $"Received {_ap.ItemName(item)} from {item.Player.Name}!";
        else if (it?.kind == "key") text = $"Unlocked: {it.name}";

        var (gok, gres) = LuaBridge.Execute(
            $"local ok,err={give} if ok==false then return 'FAIL:'..tostring(err) end " +
            $"BERSERIA_AP.SetSaveState({count + 1}) " +
            (text != null ? $"BERSERIA_AP.Notify({Lua.Str(text)}) " : "") + "return 'ok'", "=ap_give");
        if (!gok || gres != "ok") SafeLog.Error($"Delivering item #{count} failed: {gres}");
        else SafeLog.Info($"Delivered #{count}: {_ap.ItemName(item)}");
    }

    void Notify(string text)
    {
        if (_runtimeReady) LuaBridge.Execute($"BERSERIA_AP.Notify({Lua.Str(text)}) return 'ok'", "=ap_notify");
    }
}

internal static class Lua
{
    /// <summary>Lua string literal with escapes.</summary>
    public static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default: sb.Append(ch); break;
            }
        }
        return sb.Append('"').ToString();
    }
}
