using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BerseriaAP.DataTool;

/// <summary>
/// Generates data/game_data.json: the single source of truth shared by the C# plugin and the apworld.
/// Inputs: the game's archives (binary tables) + raw dumps captured from the live game (data/raw).
/// </summary>
internal static class Gen
{
    public const long BaseId = 7_700_000;
    // Location id ranges (offset from BaseId)
    const long LocChest = 0, LocKatz = 10_000, LocSparkle = 20_000, LocBoss = 30_000;
    // Item id ranges (offset from BaseId)
    const long ItemKey = 1_000, ItemGald = 2_000, ItemGame = 100_000;

    // Story regions reachable from the start (prologue + prison): never locked.
    static readonly HashSet<string> StartRegions = new() { "TTN", "AVL", "SNM" };
    // Event-only maps (ships, cutscene sets): not real regions.
    static readonly HashSet<string> EventOnly = new() { "MVC", "NMR", "SHP", "BAN", "RDP", "CLE", "ZZZ" };
    // Regions whose access timing is post-game/special: locations there only ever hold filler.
    static readonly HashSet<string> Excluded = new() { "EX2", "EXI", "EXT", "ARN", "CUR" };
    // Names missing from the location string table (taken from their area strings).
    static readonly Dictionary<string, string> NameOverride = new()
    {
        ["RBN"] = "Coastal Cave", ["CMK"] = "Earthpulse", ["KRC"] = "Cauldron", ["ARN"] = "Class 4 Administrative Zone",
    };
    // Galds offered as generic filler
    static readonly int[] GaldBundles = { 100, 250, 500, 1000, 2000, 5000 };

    record ItemInfo(string Label, uint Id, string Name, int Cat, int Rank);

    public static int Run(string[] a)
    {
        string streamingAssets = a[0], rawDir = a[1], outPath = a[2];
        var files = new GameFiles(streamingAssets);

        // ---------- raw dumps ----------
        var items = new Dictionary<uint, ItemInfo>();
        foreach (var line in File.ReadLines(Path.Combine(rawDir, "items_dump.txt")))
        {
            if (!line.StartsWith("{")) continue;
            var j = JsonNode.Parse(line)!;
            var it = new ItemInfo((string)j["label"]!, (uint)(long)j["id"]!, (string)j["name"]!, (int)j["cat"]!, (int)j["rank"]!);
            items[it.Id] = it;
        }
        var tbRec = new Dictionary<uint, JsonNode>();
        var cbRec = new Dictionary<uint, JsonNode>();
        foreach (var line in File.ReadLines(Path.Combine(rawDir, "boxes_dump.txt")))
        {
            if (!(line.StartsWith("TB {") || line.StartsWith("CB {"))) continue;
            var j = JsonNode.Parse(line[3..])!;
            var id = (uint)(long)j["Id"]!;
            if (line.StartsWith("TB ")) tbRec[id] = j; else if (line.StartsWith("CB ")) cbRec[id] = j;
        }
        var regionName = new Dictionary<string, (string Full, string Nick)>();
        var mapName = new Dictionary<string, string>();
        foreach (var line in File.ReadLines(Path.Combine(rawDir, "names_dump.txt")))
        {
            var p = line.Split('\t');
            if (p[0] == "LOC" && p.Length >= 4) regionName[p[1]] = (p[2], p[3]);
            else if (p[0] == "MAP" && p.Length >= 3) mapName[p[1]] = p[2];
        }

        // ---------- story order from event table ----------
        var ev = new Table(files.Read("EVENTPARAMETERDATA.DAT"));
        var eventMap = new List<(string Ev, string Map)>();
        {
            var (o, c) = ev.Sections[3];
            for (long i = 0; i < c * 2; i++)
            {
                long r = o + i * 40;
                if (r + 0x18 > ev.Data.Length) break;
                var e = ev.TryRelString(r + 8); var m = ev.TryRelString(r + 0x10);
                if (e != null && m != null && e.StartsWith("MEP_") && m.StartsWith("MAP_")) eventMap.Add((e, m));
            }
            eventMap.Sort((x, y) => string.CompareOrdinal(x.Ev, y.Ev));
        }
        var storyOrder = new List<string>();
        foreach (var (e, m) in eventMap)
        {
            var code = m.Substring(4, 3);
            if (!EventOnly.Contains(code) && !storyOrder.Contains(code)) storyOrder.Add(code);
        }

        // ---------- boss battles (event battles using boss music) ----------
        var bosses = new List<string>();
        {
            var (o, c) = ev.Sections[4];
            string? cur = null;
            for (long f = 0; f < c * 64; f += 8)
            {
                var w = ev.TryRelString(o + f);
                if (w == null) continue;
                if (w.StartsWith("MEP_")) cur = w;
                if (w.Contains("BTL_BOS") && cur != null && !bosses.Contains(cur)) bosses.Add(cur);
            }
        }

        // ---------- placement (map per record) from binary tables ----------
        var tbMap = new Table(files.Read("TREASUREBOXDATA.DAT")).PlacedRecords(96).ToDictionary(r => r.Id, r => r.Map);
        var cbMap = new Table(files.Read("CATBOXDATA.DAT")).PlacedRecords(80).ToDictionary(r => r.Id, r => r.Map);
        var tpTable = new Table(files.Read("TREASUREPOINTDATA.DAT"));
        var tpMap = tpTable.PlacedRecords(96).ToDictionary(r => r.Id, r => r.Map);

        string Code(string map) => map.Length >= 7 ? map.Substring(4, 3) : "???";
        string Nick(string code)
        {
            if (NameOverride.TryGetValue(code, out var o)) return o;
            var n = regionName.TryGetValue(code, out var v) && v.Nick != "" ? v.Nick : code;
            int dash = n.IndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? n[..dash] : n; // "Loegres - Jewel of the Empire" -> "Loegres"
        }

        // ---------- region adjacency (map links) ----------
        var links = new Dictionary<string, HashSet<string>>();
        {
            var ml = new Table(files.Read("MAPLINKDATA.DAT"));
            var (o, c) = ml.Sections[0];
            for (long i = 0; i < c; i++)
            {
                var codes = new HashSet<string>();
                for (int f = 0; f < 168; f += 8)
                {
                    var s = ml.TryRelString(o + i * 168 + f);
                    if (s != null && s.StartsWith("MAP_") && s.Length >= 7) codes.Add(s.Substring(4, 3));
                }
                foreach (var x in codes)
                    foreach (var y in codes)
                        if (x != y) { if (!links.ContainsKey(x)) links[x] = new(); links[x].Add(y); }
            }
        }
        // Side regions attach (for logic) to their latest story-order neighbour, searching through
        // other side regions if needed. Conservative: never assumes earlier access than certain.
        string? Attach(string code)
        {
            var seen = new HashSet<string> { code };
            var frontier = new List<string> { code };
            while (frontier.Count > 0)
            {
                var story = frontier.SelectMany(f => links.GetValueOrDefault(f) ?? new()).Where(storyOrder.Contains).ToList();
                if (story.Count > 0) return story.OrderBy(storyOrder.IndexOf).Last();
                frontier = frontier.SelectMany(f => links.GetValueOrDefault(f) ?? new()).Where(seen.Add).ToList();
            }
            return null;
        }

        // ---------- regions ----------
        var regionsUsed = new HashSet<string>(tbMap.Values.Concat(cbMap.Values).Concat(tpMap.Values).Select(Code));
        var regions = new JsonArray();
        int order = 0;
        foreach (var code in storyOrder)
        {
            regions.Add(new JsonObject
            {
                ["code"] = code, ["name"] = Nick(code), ["order"] = order++,
                ["start"] = StartRegions.Contains(code), ["story"] = true,
                ["first_event"] = eventMap.First(x => Code(x.Map) == code).Ev,
            });
        }
        foreach (var code in regionsUsed.Where(c => !storyOrder.Contains(c) && !EventOnly.Contains(c)).OrderBy(c => c))
        {
            var att = Attach(code);
            regions.Add(new JsonObject
            {
                ["code"] = code, ["name"] = Nick(code), ["order"] = order++, ["start"] = false, ["story"] = false,
                ["attach"] = att, ["excluded"] = Excluded.Contains(code) || att == null,
            });
        }

        // ---------- locations ----------
        var locations = new JsonArray();
        var usedNames = new HashSet<string>();
        string Unique(string n) { var b = n; for (int k = 2; !usedNames.Add(n); k++) n = $"{b} ({k})"; return n; }
        var perMapCount = new Dictionary<string, int>();
        string Place(string map, string what)
        {
            var key = map + "|" + what;
            perMapCount[key] = perMapCount.GetValueOrDefault(key) + 1;
            var mn = mapName.TryGetValue(map, out var m) && m != "" ? m : map;
            return Unique($"{Nick(Code(map))} - {mn} {what} {perMapCount[key]}");
        }

        foreach (var (id, j) in tbRec.OrderBy(kv => kv.Key))
        {
            if (!(bool)j["Release"]! || !tbMap.TryGetValue(id, out var map) || EventOnly.Contains(Code(map))) continue;
            uint item = (uint)(long)j["Item"]!;
            locations.Add(new JsonObject
            {
                ["id"] = BaseId + LocChest + id, ["key"] = $"TB:{id}", ["type"] = "chest",
                ["name"] = Place(map, "Chest"), ["region"] = Code(map), ["map"] = map,
                ["vanilla_item"] = item, ["vanilla_count"] = (int)j["Count"]!,
                ["rank"] = (int)j["Rank"]!, ["level"] = (int)j["Level"]!,
            });
        }
        foreach (var (id, j) in cbRec.OrderBy(kv => kv.Key))
        {
            if (!(bool)j["Release"]! || !cbMap.TryGetValue(id, out var map) || EventOnly.Contains(Code(map))) continue;
            locations.Add(new JsonObject
            {
                ["id"] = BaseId + LocKatz + id, ["key"] = $"CB:{id}", ["type"] = "katz",
                ["name"] = Place(map, "Katz Box"), ["region"] = Code(map), ["map"] = map,
                ["souls"] = (int)j["Spirit"]!,
            });
        }
        foreach (var (id, map) in tpMap.OrderBy(kv => kv.Key))
        {
            if (EventOnly.Contains(Code(map))) continue;
            locations.Add(new JsonObject
            {
                ["id"] = BaseId + LocSparkle + id, ["key"] = $"TP:{id}", ["type"] = "sparkle",
                ["name"] = Place(map, "Sparkle"), ["region"] = Code(map), ["map"] = map,
            });
        }
        // Bosses: region = map of the closest story event at or before the battle.
        int bi = 0;
        foreach (var e in bosses)
        {
            var prev = eventMap.LastOrDefault(x => string.CompareOrdinal(x.Ev, e) <= 0);
            var code = prev.Map != null ? Code(prev.Map) : storyOrder[0];
            while (EventOnly.Contains(code) || !storyOrder.Contains(code))
            {
                // walk back to a real region
                var idx = eventMap.FindLastIndex(x => string.CompareOrdinal(x.Ev, prev.Ev) < 0);
                if (idx < 0) { code = storyOrder[0]; break; }
                prev = eventMap[idx]; code = Code(prev.Map);
            }
            bi++;
            locations.Add(new JsonObject
            {
                ["id"] = BaseId + LocBoss + bi, ["key"] = $"BOSS:{e}", ["type"] = "boss",
                ["name"] = Unique($"Story Battle {bi:D2} - {Nick(code)}"), ["region"] = code,
                ["scenario"] = ScenarioNumber(e),
            });
        }

        // ---------- items ----------
        var itemArr = new JsonArray();
        int ki = 0;
        foreach (var r in regions)
        {
            if ((bool)r!["start"]!) continue;
            var code = (string)r["code"]!;
            if (!(bool)r["story"]!) continue; // optional regions are not gated
            itemArr.Add(new JsonObject
            {
                ["id"] = BaseId + ItemKey + ki++, ["name"] = $"Region Key: {(string)r["name"]!}",
                ["kind"] = "key", ["region"] = code, ["class"] = "progression",
            });
        }
        foreach (var g in GaldBundles)
            itemArr.Add(new JsonObject { ["id"] = BaseId + ItemGald + g, ["name"] = $"{g} Gald", ["kind"] = "gald", ["count"] = g, ["class"] = "filler" });
        // Every game item that appears as fixed chest content (plus common consumables) becomes an AP item.
        var gameItemIds = new SortedSet<uint>(tbRec.Values.Select(j => (uint)(long)j["Item"]!).Where(x => x > 2 && items.ContainsKey(x)));
        foreach (var label in new[] { "ITEMNO_APPLE_GUMMI", "ITEMNO_ORANGE_GUMMI", "ITEMNO_LEMON_GUMMI", "ITEMNO_LIFE_BOTTLE" })
        {
            var hit = items.Values.FirstOrDefault(x => x.Label == label);
            if (hit != null) gameItemIds.Add(hit.Id);
        }
        var nameCount = new Dictionary<string, int>();
        foreach (var gid in gameItemIds)
        {
            var it = items[gid];
            bool equip = it.Cat is 3 or 4 or 5 or 6 or 7;
            var nm = it.Name;
            if (nameCount.TryGetValue(nm, out var n)) nm = $"{nm} ({it.Label})";
            nameCount[it.Name] = n + 1;
            itemArr.Add(new JsonObject
            {
                ["id"] = BaseId + ItemGame + (gid >> 24) * 10_000 + (gid & 0xFFFF), ["name"] = nm,
                ["kind"] = "game", ["game_id"] = gid, ["label"] = it.Label, ["category"] = it.Cat,
                ["class"] = equip ? "useful" : "filler",
            });
        }

        var root = new JsonObject
        {
            ["game"] = "Tales of Berseria Remastered",
            ["base_id"] = BaseId,
            ["goal_event"] = "MEP_210_150_012",
            ["goal_scenario"] = ScenarioNumber("MEP_210_150_012"),
            ["regions"] = regions,
            ["locations"] = locations,
            ["items"] = itemArr,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        int Count(string t) => locations.Count(l => (string)l!["type"]! == t);
        Console.WriteLine($"Regions: {regions.Count} ({storyOrder.Count} story)  Locations: chests {Count("chest")}, katz {Count("katz")}, " +
                          $"sparkles {Count("sparkle")}, bosses {Count("boss")}  Items: {itemArr.Count}");
        Console.WriteLine("Story order: " + string.Join(" ", storyOrder));
        return 0;
    }

    /// <summary>MEP_AAA_BBB_CCC[_x] -> AAA*1e6 + BBB*1e3 + CCC (matches TO13.Scenario.GetScenarioFlag()).</summary>
    public static long ScenarioNumber(string ev)
    {
        var m = Regex.Match(ev, @"MEP_(\d{3})_(\d{3})_(\d{3})");
        return m.Success ? long.Parse(m.Groups[1].Value) * 1_000_000 + long.Parse(m.Groups[2].Value) * 1_000 + long.Parse(m.Groups[3].Value) : 0;
    }
}
