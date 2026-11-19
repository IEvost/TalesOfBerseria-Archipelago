from dataclasses import dataclass

from Options import Choice, DeathLink, DefaultOnToggle, PerGameCommonOptions, Range, Toggle


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


class DeathLinkMode(Choice):
    """
    How a received DeathLink is applied (only with DeathLink on). The game has no deaths outside battle.
    immediate: as soon as you can move, a battle starts on the spot and your party is knocked out.
    next_battle: the death waits until your next battle begins, then knocks out your party.
    """
    display_name = "DeathLink Mode"
    option_immediate = 0
    option_next_battle = 1
    default = 0


@dataclass
class BerseriaOptions(PerGameCommonOptions):
    region_keys: RegionKeys
    sparklesanity: Sparklesanity
    starting_region_keys: StartingRegionKeys
    death_link: DeathLink
    death_link_mode: DeathLinkMode
