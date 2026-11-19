from dataclasses import dataclass

from Options import DefaultOnToggle, PerGameCommonOptions, Range, Toggle


class RegionKeys(DefaultOnToggle):
    """
    Each region of the world (after the prologue) is locked until you receive its "Region Key".
    The game refuses map transitions into locked regions, so story progress depends on keys
    found by you and the other players. Turning this off makes the world filler-only.
    """
    display_name = "Region Keys"


class Sparklesanity(Toggle):
    """
    The first pickup of every sparkle point (the shiny spots on the field) becomes a check.
    Adds about 4,300 locations, mostly filled with consumables and gald.
    """
    display_name = "Sparklesanity"


class StartingRegionKeys(Range):
    """
    Start with this many Region Keys, taken in story order (the first regions after the prologue).
    Reduces the time spent waiting for keys at the beginning of a seed.
    """
    display_name = "Starting Region Keys"
    range_start = 0
    range_end = 10
    default = 0


@dataclass
class BerseriaOptions(PerGameCommonOptions):
    region_keys: RegionKeys
    sparklesanity: Sparklesanity
    starting_region_keys: StartingRegionKeys
