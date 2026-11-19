"""
Archipelago world for Tales of Berseria Remastered.

All game facts (locations, items, regions, story order) come from game_data.json, which is generated
from the game files by BerseriaAP.DataTool (C#). The same file is embedded in the BepInEx plugin, so
ids always match between the generator and the game client.
"""
import json
import pkgutil
from collections.abc import Mapping
from typing import Any

from BaseClasses import Item, ItemClassification, Location, LocationProgressType, Region, Tutorial
from worlds.AutoWorld import WebWorld, World

from .options import BerseriaOptions

GAME = "Tales of Berseria Remastered"
DATA: dict = json.loads(pkgutil.get_data(__name__, "game_data.json").decode("utf-8"))

REGIONS: list[dict] = DATA["regions"]
REGION_BY_CODE: dict[str, dict] = {r["code"]: r for r in REGIONS}
STORY: list[dict] = sorted((r for r in REGIONS if r["story"]), key=lambda r: r["order"])
LOCATIONS: list[dict] = DATA["locations"]
ITEMS: list[dict] = DATA["items"]
ITEM_BY_NAME: dict[str, dict] = {i["name"]: i for i in ITEMS}
GAME_ITEM_NAME: dict[int, str] = {i["game_id"]: i["name"] for i in ITEMS if i["kind"] == "game"}
KEY_NAME_BY_REGION: dict[str, str] = {i["region"]: i["name"] for i in ITEMS if i["kind"] == "key"}
GALD_BUNDLES: list[tuple[int, str]] = sorted((i["count"], i["name"]) for i in ITEMS if i["kind"] == "gald")
CONSUMABLES = [n for n in ("Apple Gel", "Peach Gel", "Grape Gel", "Life Bottle") if n in ITEM_BY_NAME]
FINAL_BOSS_SCENARIO: int = DATA["goal_scenario"]

CLASSIFICATION = {
    "progression": ItemClassification.progression,
    "useful": ItemClassification.useful,
    "filler": ItemClassification.filler,
}


def scenario_of(event: str) -> int:
    """MEP_AAA_BBB_CCC... -> AAA*1e6 + BBB*1e3 + CCC (same encoding as the game's scenario flag)."""
    parts = event.split("_")
    return int(parts[1]) * 1_000_000 + int(parts[2]) * 1_000 + int(parts[3])


def gald_bundle(amount: int) -> str:
    """Largest gald bundle not exceeding the amount (at least the smallest one)."""
    best = GALD_BUNDLES[0][1]
    for value, name in GALD_BUNDLES:
        if value <= amount:
            best = name
    return best


class BerseriaItem(Item):
    game = GAME


class BerseriaLocation(Location):
    game = GAME


class BerseriaWeb(WebWorld):
    theme = "ocean"
    tutorials = [Tutorial(
        "Multiworld Setup Guide",
        "How to install the BepInEx mod and connect Tales of Berseria Remastered to Archipelago.",
        "English",
        "setup_en.md",
        "setup/en",
        ["BerseriaAP"],
    )]


class BerseriaWorld(World):
    """
    Tales of Berseria Remastered: follow Velvet's quest for vengeance across Midgand.
    Treasure chests, Katz boxes, story battles (and optionally sparkle points) are checks;
    regions of the world are unlocked by Region Keys found throughout the multiworld.
    """
    game = GAME
    web = BerseriaWeb()
    options_dataclass = BerseriaOptions
    options: BerseriaOptions
    origin_region_name = "Menu"

    item_name_to_id = {i["name"]: i["id"] for i in ITEMS}
    location_name_to_id = {l["name"]: l["id"] for l in LOCATIONS}
    item_name_groups = {"Region Keys": set(KEY_NAME_BY_REGION.values())}

    def _included(self, loc: dict) -> bool:
        if loc["type"] == "sparkle":
            return bool(self.options.sparklesanity)
        return True

    @staticmethod
    def _logical_region(loc: dict) -> str:
        """
        Story battles happen at a fixed point of the story, possibly while revisiting an early region.
        They are logically placed in the latest story region already reached at that point.
        """
        if loc["type"] != "boss":
            return loc["region"]
        reached = [r for r in STORY if scenario_of(r["first_event"]) <= loc["scenario"]]
        return reached[-1]["code"] if reached else STORY[0]["code"]

    # ------------------------------------------------------------------ regions
    def create_regions(self) -> None:
        menu = Region("Menu", self.player, self.multiworld)
        self.multiworld.regions.append(menu)
        regions: dict[str, Region] = {}
        for r in REGIONS:
            reg = Region(r["name"], self.player, self.multiworld)
            regions[r["code"]] = reg
            self.multiworld.regions.append(reg)

        keys_on = bool(self.options.region_keys)
        player = self.player

        # Story chain: each story region is entered from the previous one and needs its own key.
        prev = menu
        for r in STORY:
            reg = regions[r["code"]]
            key = KEY_NAME_BY_REGION.get(r["code"])
            if r["start"] or not keys_on or key is None:
                prev.connect(reg, f"{prev.name} -> {reg.name}")
            else:
                prev.connect(reg, f"{prev.name} -> {reg.name}", lambda state, k=key: state.has(k, player))
            prev = reg
        last_story = prev

        # Side regions hang off their (latest) adjacent story region; post-game/special ones off the end.
        for r in REGIONS:
            if r["story"]:
                continue
            parent = regions.get(r.get("attach") or "", last_story) if not r.get("excluded") else last_story
            parent.connect(regions[r["code"]], f"{parent.name} -> {r['name']}")

        # Locations
        for loc in LOCATIONS:
            if not self._included(loc):
                continue
            reg = regions[self._logical_region(loc)]
            l = BerseriaLocation(player, loc["name"], loc["id"], reg)
            if REGION_BY_CODE[loc["region"]].get("excluded"):
                l.progress_type = LocationProgressType.EXCLUDED
            reg.locations.append(l)

        # Goal: defeating the final boss (an event in the final story region).
        final_region = regions[STORY[-1]["code"]]
        victory = BerseriaLocation(player, "Defeat the Final Boss", None, final_region)
        victory.place_locked_item(BerseriaItem("Victory", ItemClassification.progression, None, player))
        final_region.locations.append(victory)
        self.multiworld.completion_condition[player] = lambda state: state.has("Victory", player)

    # ------------------------------------------------------------------ items
    def create_item(self, name: str) -> BerseriaItem:
        data = ITEM_BY_NAME[name]
        return BerseriaItem(name, CLASSIFICATION[data["class"]], data["id"], self.player)

    def get_filler_item_name(self) -> str:
        return self.random.choice(CONSUMABLES + [gald_bundle(500)])

    def _vanilla_item(self, loc: dict) -> str:
        if loc["type"] == "chest":
            gid = loc.get("vanilla_item", 0)
            if gid in GAME_ITEM_NAME:
                return GAME_ITEM_NAME[gid]
            if gid == 1:  # gald
                return gald_bundle(loc.get("vanilla_count", 100))
            return gald_bundle(max(100, loc.get("level", 1) * 50))  # random-loot chests and recipes
        if loc["type"] == "boss":
            return gald_bundle(1000)
        return self.get_filler_item_name()

    def create_items(self) -> None:
        included = [l for l in LOCATIONS if self._included(l)]
        pool: list[str] = []
        keys: list[str] = []
        if self.options.region_keys:
            story_keys = [KEY_NAME_BY_REGION[r["code"]] for r in STORY if r["code"] in KEY_NAME_BY_REGION]
            n_start = min(int(self.options.starting_region_keys), len(story_keys))
            for k in story_keys[:n_start]:
                self.multiworld.push_precollected(self.create_item(k))
            keys = story_keys[n_start:]

        fillers = [self._vanilla_item(l) for l in included]
        # Make room for the keys by dropping the least valuable fillers first.
        fillers.sort(key=lambda n: (ITEM_BY_NAME[n]["class"] != "filler", ITEM_BY_NAME[n].get("count", 10**6)))
        fillers = fillers[len(keys):]
        pool = keys + fillers
        self.multiworld.itempool += [self.create_item(n) for n in pool]

    # ------------------------------------------------------------------ client
    def fill_slot_data(self) -> Mapping[str, Any]:
        return {
            "region_keys": int(bool(self.options.region_keys)),
            "sparklesanity": int(bool(self.options.sparklesanity)),
            "death_link": int(bool(self.options.death_link)),
            "death_link_mode": int(self.options.death_link_mode.value),
            "goal_scenario": FINAL_BOSS_SCENARIO,
            "data_base_id": DATA["base_id"],
        }
