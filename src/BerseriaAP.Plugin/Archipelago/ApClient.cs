using System.Collections.Concurrent;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using BerseriaAP.Util;

namespace BerseriaAP.Archipelago;

/// <summary>
/// Thin, thread-safe wrapper around the Archipelago session.
/// Network callbacks run on background threads; everything the game thread needs is exposed
/// through snapshots and queues so no game code ever runs off the game thread.
/// </summary>
internal sealed class ApClient
{
    public const string GameName = "Tales of Berseria Remastered";

    ArchipelagoSession? _session;
    public bool Connected => _session?.Socket.Connected == true && SlotData != null;
    string _status = "Not connected";
    /// <summary>Human-readable connection status; every change is also posted to the message log.</summary>
    public string Status
    {
        get => _status;
        private set { _status = value; ChatLog.Enqueue("[AP] " + value); }
    }
    public Dictionary<string, object> ? SlotData { get; private set; }
    public int Slot { get; private set; }
    public string SeedName { get; private set; } = "";

    /// <summary>Scouted contents of our locations: location id -> display text.</summary>
    public ConcurrentDictionary<long, string> ScoutText { get; } = new();
    public ConcurrentQueue<string> ChatLog { get; } = new();
    readonly object _itemsLock = new();
    readonly List<ItemInfo> _received = new();

    public int ReceivedCount { get { lock (_itemsLock) return _received.Count; } }
    public ItemInfo? ReceivedAt(int i) { lock (_itemsLock) return i < _received.Count ? _received[i] : null; }
    public List<long> ReceivedItemIds() { lock (_itemsLock) return _received.Select(x => x.ItemId).ToList(); }

    public event Action? OnConnected;

    public async Task ConnectAsync(string server, string slot, string password)
    {
        try
        {
            Disconnect();
            Status = $"Connecting to {server}...";
            var session = ArchipelagoSessionFactory.CreateSession(server);
            session.Items.ItemReceived += helper =>
            {
                // Drain everything the helper has; keep the full ordered list.
                while (helper.Any())
                {
                    var item = helper.DequeueItem();
                    lock (_itemsLock) _received.Add(item);
                }
            };
            session.MessageLog.OnMessageReceived += msg => ChatLog.Enqueue(msg.ToString());
            session.Socket.SocketClosed += reason => Status = $"Disconnected: {reason}";

            var result = await Task.Run(() => session.TryConnectAndLogin(GameName, slot, ItemsHandlingFlags.AllItems,
                new Version(0, 6, 0), tags: Array.Empty<string>(), password: string.IsNullOrEmpty(password) ? null : password));
            if (result is LoginFailure fail)
            {
                Status = "Connection failed: " + string.Join("; ", fail.Errors);
                SafeLog.Error(Status);
                return;
            }
            var ok = (LoginSuccessful)result;
            _session = session;
            Slot = ok.Slot;
            SeedName = session.RoomState.Seed;
            SlotData = ok.SlotData;
            Status = $"Connected as {slot}";
            SafeLog.Info($"{Status} (slot {Slot}, seed {SeedName})");
            await ScoutAllAsync();
            if (SlotFlag("death_link"))
            {
                _deathLink = session.CreateDeathLinkService();
                _deathLink.OnDeathLinkReceived += d =>
                {
                    var who = d.Source ?? "someone";
                    DeathQueue.Enqueue(string.IsNullOrEmpty(d.Cause) ? $"DeathLink: {who} died." : $"DeathLink: {d.Cause}");
                };
                _deathLink.EnableDeathLink();
                SafeLog.Info("DeathLink enabled.");
            }
            OnConnected?.Invoke();
        }
        catch (Exception e)
        {
            Status = "Connection error: " + e.Message;
            SafeLog.Error(e.ToString());
        }
    }

    public void Disconnect()
    {
        try { _session?.Socket.DisconnectAsync().Wait(2000); } catch { }
        _session = null;
        SlotData = null;
        _deathLink = null;
        lock (_itemsLock) _received.Clear();
        ScoutText.Clear();
    }

    async Task ScoutAllAsync()
    {
        var s = _session!;
        var ids = s.Locations.AllLocations.ToArray();
        var scouted = await s.Locations.ScoutLocationsAsync(false, ids);
        foreach (var (id, info) in scouted)
        {
            ScoutText[id] = info.Player.Slot == Slot
                ? $"Found {info.ItemDisplayName}!"
                : $"Found {info.Player.Name}'s {info.ItemDisplayName}!";
        }
        SafeLog.Info($"Scouted {scouted.Count} locations.");
    }

    public void SendChecks(IEnumerable<long> ids)
    {
        var s = _session;
        if (s == null) return;
        var arr = ids.Where(id => s.Locations.AllMissingLocations.Contains(id)).ToArray();
        if (arr.Length == 0) return;
        s.Locations.CompleteLocationChecksAsync(arr);
        SafeLog.Info($"Sent {arr.Length} check(s): {string.Join(",", arr)}");
    }

    public bool IsChecked(long id) => _session?.Locations.AllLocationsChecked.Contains(id) == true;

    DeathLinkService? _deathLink;
    /// <summary>Incoming deaths (display text), consumed on the game thread.</summary>
    public ConcurrentQueue<string> DeathQueue { get; } = new();
    public bool DeathLinkEnabled => _deathLink != null;

    public void SendDeath(string cause)
    {
        if (_deathLink == null || _session == null) return;
        _deathLink.SendDeathLink(new DeathLink(_session.Players.GetPlayerAlias(Slot), cause));
        SafeLog.Info("DeathLink sent: " + cause);
    }

    public void SendGoal()
    {
        var s = _session;
        if (s == null) return;
        s.Socket.SendPacketAsync(new StatusUpdatePacket { Status = ArchipelagoClientState.ClientGoal });
        SafeLog.Info("Goal reached!");
    }

    public string PlayerName(int slot) => _session?.Players.GetPlayerAlias(slot) ?? $"Player {slot}";
    public string ItemName(ItemInfo i) => i.ItemDisplayName ?? $"Item {i.ItemId}";

    public bool SlotFlag(string key) =>
        SlotData != null && SlotData.TryGetValue(key, out var v) && v != null && Convert.ToInt64(v) != 0;
}
