# SimHub — flag properties reference

SimHub exposes flag state through two layers:

1. **Unified properties** (`DataCorePlugin.GameData.Flag_*`) — same name across every
   sim that supports the flag concept. This is the layer to prefer.
2. **Game-specific raw data** (`DataCorePlugin.GameRawData.*`) — direct from the sim's
   shared memory / API. Use as a fallback for flags the unified layer doesn't surface,
   or when behaviour differs per sim.

To see raw-data properties in SimHub's "Available properties" picker you have to tick
**Show game specific properties ('rawdata')**.

## Unified `GameData.Flag_*` set

These properties exist across all supported sims; they're booleans that resolve to
the closest equivalent in each game's flag system.

| Property                                  | Meaning |
|-------------------------------------------|---------|
| `DataCorePlugin.GameData.Flag_Yellow`     | Yellow / caution (general) |
| `DataCorePlugin.GameData.Flag_Blue`       | Blue (faster car approaching) |
| `DataCorePlugin.GameData.Flag_Black`      | Black (penalty / stop-and-go) |
| `DataCorePlugin.GameData.Flag_White`      | White (final lap, or slow car ahead in some sims) |
| `DataCorePlugin.GameData.Flag_Checkered`  | Checkered (race finished) |
| `DataCorePlugin.GameData.Flag_Green`      | Green (race start / restart) — see caveats below |
| `DataCorePlugin.GameData.Flag_Orange`     | Orange / mechanical-failure black-and-orange |
| `DataCorePlugin.GameData.Flag_Penalty`    | Penalty-status flag |

Caveats:

- **Green flag** is patchy: not every sim emits a green-flag pulse, and SimHub's
  unified property reflects whatever the sim provides. Some users derive their own
  "green" from `SessionState` transitions instead. See
  [SHWotever/SimHub#388](https://github.com/SHWotever/SimHub/issues/388) and
  [#436](https://github.com/SHWotever/SimHub/issues/436).
- **`Flag_Orange`, `Flag_Penalty`** were added later (issue
  [#579](https://github.com/SHWotever/SimHub/issues/579) tracked broadening the unified
  flag list past the original blue/black/white/yellow/checkered five). Make sure the
  user's SimHub is a recent build before relying on them.
- **Typed model vs property bag** (verified by reflection against
  `GameReaderCommon.dll`, SimHub 9.x): on the typed `StatusDataBase` class that a C#
  plugin reads in `DataUpdate`, the unified flags are **`int` 0/1, not `bool`**
  (`Flag_Yellow`, `Flag_Blue`, `Flag_Black`, `Flag_White`, `Flag_Checkered`,
  `Flag_Green`, `Flag_Orange`, plus a `Flag_Name` string). `Flag_Penalty` is **not** a
  typed member on that build — it only exists (if at all) in SimHub's name-based
  property bag that NCalc formulas resolve against. Plugin code should stick to the
  seven typed flags.

## Per-sim raw-data fallbacks

When the unified property isn't enough — most often when a **localised**
yellow (sector 1/2/3) is needed and not just "any yellow" — fall back
to raw data.

### ACC (Assetto Corsa Competizione)

Verified from [Custom serial devices forum threads](https://www.simhubdash.com/community-2/simhub-support/acc-flags-not-working-properly-2/)
and issue [#579](https://github.com/SHWotever/SimHub/issues/579):

| Property                                                | Meaning |
|---------------------------------------------------------|---------|
| `DataCorePlugin.GameRawData.Graphics.globalYellow`      | Any yellow on track |
| `DataCorePlugin.GameRawData.Graphics.globalYellow1`     | Yellow in sector 1 |
| `DataCorePlugin.GameRawData.Graphics.globalYellow2`     | Yellow in sector 2 |
| `DataCorePlugin.GameRawData.Graphics.globalYellow3`     | Yellow in sector 3 |
| `DataCorePlugin.GameRawData.Graphics.flag`              | Numeric flag enum (see ACC SDK) |

ACC's `flag` enum values: `ACC_NO_FLAG`, `ACC_BLUE_FLAG`, `ACC_YELLOW_FLAG`,
`ACC_BLACK_FLAG`, `ACC_WHITE_FLAG`, `ACC_CHECKERED_FLAG`, `ACC_PENALTY_FLAG`.

### iRacing

iRacing exposes a bitmask `SessionFlags` that contains every active flag at once.
Useful raw fields:

| Property                                                  | Meaning |
|-----------------------------------------------------------|---------|
| `DataCorePlugin.GameRawData.Telemetry.SessionFlags`       | Bitmask of all current flags |
| `DataCorePlugin.GameRawData.Telemetry.SessionFlagsDetails.Is<flag>` | Per-bit booleans (NCalc convenience — see below) |
| `DataCorePlugin.GameRawData.Telemetry.UnderPaceCar`       | Pace car on track (typed getter: `CarIdxTrackSurface[0] == 3`; car index 0 is always the pace car) |
| `DataCorePlugin.GameRawData.Telemetry.PlayerCarTowTime`   | Tow-truck recovery state (proxy for damage) |
| `DataCorePlugin.GameRawData.Telemetry.PlayerCarMyIncidentCount` | Player's incident count this session — drives the incident-limit warning. A dictionary key with **no typed getter** on `iRacingSDK.Telemetry` (verified). |
| `SessionData.WeekendInfo.WeekendOptions.IncidentLimit` (raw session-info) | Session incident limit ("unlimited" or a number). **Not in SimHub's typed model** — see the incident-limit note below. |

**`irsdk_Flags` bit values — VERIFIED at M10** against the `iRacingSDK.dll`
that ships *inside* SimHub 9.11.21 (the assembly SimHub's own iRacing reader
consumes; enum `iRacingSDK.SessionFlags`), not transcribed from prose — this
doc once carried an inverted `mGamePhase` claim, so bits get verified against
executable artifacts now:

| Bit | Name | Bit | Name |
|------------|----------------|------------|----------------|
| `0x00000001` | checkered    | `0x00002000` | randomWaving |
| `0x00000002` | white        | `0x00004000` | **caution** |
| `0x00000004` | green        | `0x00008000` | cautionWaving |
| `0x00000008` | yellow       | `0x00010000` | black (per-driver) |
| `0x00000010` | red          | `0x00020000` | disqualify |
| `0x00000020` | blue         | `0x00040000` | servicible |
| `0x00000040` | debris       | `0x00080000` | furled (warning) |
| `0x00000080` | crossed      | `0x00100000` | repair (meatball) |
| `0x00000100` | yellowWaving | `0x10000000` | startHidden |
| `0x00000200` | oneLapToGreen| `0x20000000` | startReady |
| `0x00000400` | greenHeld    | `0x40000000` | startSet |
| `0x00000800` | tenToGo      | `0x80000000` | startGo |
| `0x00001000` | fiveToGo     |              |              |

**How SimHub's unified layer derives `Flag_*` for iRacing** (IL-verified at
M10 against `IRacingReader.IRacingManager.GD_Flag_*` in `ICarsReader.dll`):
`Flag_Yellow` ⇐ `yellow | yellowWaving | caution | cautionWaving` (so a
full-course caution *also* reads as unified yellow); `Flag_Orange` ⇐
`repair` (the meatball!); `Flag_Black` ⇐ `black` only (disqualify is
dropped); `Flag_Blue` ⇐ `blue && !green` — **not** a 1:1 mirror: a set
`green` bit suppresses unified blue (SimHub's deliberate handling of the
spurious start-window blues from issue
[#436](https://github.com/SHWotever/SimHub/issues/436));
`Flag_White/Green/Checkered` mirror their single bits (`greenHeld` is
dropped). The unified layer never surfaces `red`, `furled`, `disqualify`
or the start lights.

`SessionFlagsDetails` is a SimHub NCalc-side extension: the iRacing reader
attaches a `GameReaderCommon.EnumExposer<SessionFlags>` under
`Telemetry.SessionFlagsDetails`, exposing one boolean per enum value named
`Is<value>` with the *lowercase* bit name — e.g.
`[DataCorePlugin.GameRawData.Telemetry.SessionFlagsDetails.Isfurled]`.
Third-party plugins (e.g. ATSR Hub) drive their iRacing "slow down" alerts
from exactly that `Isfurled` property, with client-side state on top.

Caution detection: bit-test `Caution` (**`0x4000`**) in NCalc as
`([...SessionFlags] & 0x4000) > 0`. **Correction (M10):** the v1-era claim
that iRacing exposes a **`SafetyCarActive`** property was wrong — a binary
sweep of every assembly in SimHub 9.11.21 finds the `SafetyCarActive` name
in `RfactorReader.dll` **only** (it's an rFactor-family property bag entry).
For iRacing, detect the physical pace car via `UnderPaceCar` /
`CarIdxTrackSurface[0]`, or treat the `caution`/`cautionWaving` bits as
"pace car deployed" (iRacing's full-course caution *is* a pace car; the sim
has no VSC concept).

**Penalty telemetry limits (verified at M10):** iRacing exports **no graded
slow-down meter** (the on-screen SLOW DOWN bar isn't in telemetry — the
closest signal is the `furled` bit) and **no drive-through vs stop-and-go
distinction** (only the single `black` bit). Don't invent either from
prose; the plugin's penalty model keeps those dimensions defaulted for
iRacing (see "iRacing adapter mapping" below).

### rFactor 2 / Le Mans Ultimate

Both expose a per-corner `Sectors` flag colour and a per-vehicle penalty status. The
unified `Flag_*` properties cover the common cases. For richer info:

| Property                                                       | Meaning |
|----------------------------------------------------------------|---------|
| `DataCorePlugin.GameRawData.Scoring.mYellowFlagState`          | Global yellow state |
| `DataCorePlugin.GameRawData.Scoring.mSectorFlag[0..2]`         | Per-sector flag |
| `DataCorePlugin.GameRawData.Telemetry.mPenalties`              | Active penalties |
| `DataCorePlugin.GameRawData.Scoring.mGamePhase`                | Session phase enum — **5 = Green flag, 6 = Full Course Yellow / Safety Car** |

The `mGamePhase` values above follow the ISI InternalsPlugin / rF2 shared-memory
`GamePhase` enum (`GreenFlag = 5`, `FullCourseYellow = 6`). Beware: the v1
serial-profile research (and the v1 profile in `simhub/README.md`) had these two
**inverted** — don't copy that mapping. There is no phase value for "safety car
deployed"; phase 6 covers both an FCY and an SC, so distinguish them via
`mYellowFlagState` / the pace-car fields, not `mGamePhase` alone. Mods can deviate,
so **verify against your install** in the in-app properties picker. (Other property
names may also differ slightly between rF2 and LMU.)

### Assetto Corsa (vanilla)

`DataCorePlugin.GameRawData.Graphics.flag` (same field as ACC, reused). Vanilla AC's
flag system is simpler than ACC's — the unified `Flag_*` properties usually suffice.

### Automobilista 2

Exposes `DataCorePlugin.GameRawData.mFlagColour` and `mFlagReason` enums. Coverage is
similar to rF2.

### F1 (Codemasters / EA)

Codemasters games provide both a session-wide flag and per-marshal-zone flags via UDP
telemetry; SimHub surfaces them as `DataCorePlugin.GameRawData.MarshalZones[i].ZoneFlag`.
The unified `Flag_*` set picks the most relevant one for the player.

## Adjacent properties worth pulling

Not flags, but useful for the display anyway:

| Property                                                   | Use |
|------------------------------------------------------------|-----|
| `DataCorePlugin.GameData.SessionTypeName`                  | "Race" / "Practice" / "Qualifying" |
| `DataCorePlugin.GameData.SessionState`                     | "PreSession" / "Green" / "FullCourseYellow" / "Finished" |
| `DataCorePlugin.GameData.PlayerName` / `CarModel` / `TrackName` | Splash/idle screen content |
| `DataCorePlugin.GameData.GameRunning` / `GameInMenu`       | Whether to drive the panel at all |
| `DataCorePlugin.GameData.CurrentLap`                       | "Last lap" detection in conjunction with `TotalLaps` |

## Wave-level distinction (single vs double waved)

The unified `Flag_Yellow` / `Flag_Blue` properties are booleans — they
don't distinguish a static / displayed flag from a single- or
double-waved flag. To recover that, fall back to raw data:

- **iRacing**: bit-test `[DataCorePlugin.GameRawData.Telemetry.SessionFlags]`.
  `YellowWaving` / `CautionWaving` bits indicate "waved"; static
  `Yellow` / `Caution` bits indicate displayed only. (See `irsdk_Flags`
  in the iRacing SDK for the bit positions.)
- **ACC**: `[DataCorePlugin.GameRawData.Graphics.flag] = 2` indicates a
  yellow event; the SDK has no single/double-waved distinction.
- **rF2 / LMU**: `[DataCorePlugin.GameRawData.Scoring.mYellowFlagState]`
  exposes a numeric severity (`PendingYellow`, `Yellow`, `LastLap`,
  `Resume`, …).

## Caution states (VSC / Safety Car) by sim

| Sim              | VSC | SC | How to detect                                                               |
|------------------|-----|----|-----------------------------------------------------------------------------|
| iRacing          | —   | ✓  | `SessionFlags` bitmask for `Caution` / `CautionWaving` (a full-course caution is a deployed pace car; no VSC concept); pace car on track via `UnderPaceCar`. (`SafetyCarActive` is an rFactor-family property, not iRacing — corrected at M10.) |
| F1 (Codemasters) | ✓   | ✓  | `m_safetyCarStatus` raw enum (0=none, 1=full SC, 2=VSC, 3=formation lap)    |
| ACC              | —   | —  | No first-class VSC / SC concept exposed.                                    |
| rF2 / LMU        | ✓   | ✓  | `mGamePhase` = 6 covers both FCY and a deployed SC (5 = green flag); tell them apart via `mYellowFlagState` / pace-car fields — verify per install. |
| Automobilista 2  | ✓   | ✓  | `mSafetyCarStatus` raw field.                                               |

## Sector-localised yellows by sim

| Sim                     | Property                                | Notes                                                               |
|-------------------------|-----------------------------------------|---------------------------------------------------------------------|
| ACC                     | `GameRawData.Graphics.globalYellow1/2/3`| One boolean per sector.                                             |
| F1 (Codemasters)        | `GameRawData.MarshalZones[i].ZoneFlag`  | Up to 21 marshal zones with `ZoneStart` ∈ [0, 1]; aggregate to thirds host-side. |
| rF2 / LMU               | `GameRawData.Scoring.mSectorFlag[0..2]` | One value per sector.                                               |
| iRacing                 | (none)                                  | `SessionFlags` is global only.                                      |
| Assetto Corsa (vanilla) | (none)                                  | No per-sector flags exposed.                                        |

## Generic adapter mapping (plugin)

How the plugin's generic adapter (`plugin/src/Adapters/GenericAdapter.cs`) maps
the unified layer to the Grammar `SignalState` (docs/flag-grammar.md §9-§10).
Doc and code state the same contract — change them together.

- **Inputs**: the seven typed unified flags off `StatusDataBase` (int 0/1, see
  above), `SessionTypeName`, and `GameData.GamePaused`. No raw data — per-sim
  refinements (tiers, VSC/SC/FCY, sector yellows, notices) layer on via
  game-keyed adapters that run after the generic one and override its result.
- **Track-flag priority** when several are set:
  **Yellow > Blue > White > Checkered > Green.** The unified black and orange
  flags are *not* in this ladder — they map to the orthogonal
  `BlackFlag`/`Meatball` dimensions so the compositor's demotion rule
  (docs/flag-grammar.md §5) can keep them visible under a winning track flag.
  The unified layer never surfaces a red flag, so the generic adapter never
  emits one either.
- **Tier heuristic**: the unified booleans can't distinguish a displayed from a
  waved flag, so a yellow always enters at **Tier 1 (Alert)** — the
  marshal-is-waving guess; every other flag enters at Tier 0 (Ambient). Same
  tradeoff the retired v1 formula made, re-expressed on the tier ladder.
- **Session mapping** from `SessionTypeName` (ordinal case-insensitive substring
  matching): `GamePaused` → *Paused* outright; null/empty → *Unknown*; names
  containing `practice`, `qualif`, `test`, `warmup`, `hotlap`, `hotstint` or
  `superpole` → *PreRace* (covers iRacing's "Offline Testing" / "Lone Qualify" /
  "Open Practice", ACC's "HOTSTINT" / "SUPERPOLE", Codemasters' "Practice n" /
  "Qualifying n"); otherwise names containing `race` → *Racing*; anything else →
  *Unknown*. Pre-race keywords are checked before `race` so a combined name can't
  misroute.
- **Caution / sectors / notices**: always defaults from the generic adapter —
  first-class VSC/SC/FCY, sector-local yellows, start sequences and penalty
  notices only exist in per-sim raw data (see the tables above).
- **No-game predicate**: the plugin shows its connected-idle marker (instead of
  running the adapter) unless `GameRunning && !GameInMenu && NewData != null`.
  A paused game still counts as live — it renders as the *Paused* session, not as
  connected-idle.

## iRacing adapter mapping (plugin)

How the iRacing raw-telemetry refiner (`plugin/src/Adapters/IRacingAdapter.cs`)
layers over the generic result, emitting the Grammar `SignalState`
(docs/flag-grammar.md §10). It matches `GameData.GameName == "IRacing"`
(ordinal case-insensitive; the code that also names the `PluginsData\IRacing`
settings folder) and only ever *refines* — if the raw SessionFlags mask is
unavailable for a tick, the generic result stands untouched. Doc and code state
the same contract — change them together.

- **Red** (`0x10`) → `TrackFlag.Red` at Tier 2 (raw-only; the unified layer
  never carries red).
- **Caution** (`0x4000 | 0x8000`) → the **SC board** (`Caution.SafetyCar`).
  iRacing's full-course caution is a deployed pace car and the sim has no VSC,
  so the SC board is always the right board. The compositor rides it on a
  yellow field.
- **Yellow tier refinement**: when the generic flag is yellow,
  `cautionWaving` → Tier 2, `yellowWaving | caution` → Tier 1, displayed-only
  → Tier 0 — raw kills the generic "yellow is always Alert" guess. Blue and
  green firm up to Tier 1 (a blue shown to you and a start/restart both want
  the attention pulse).
- **Disqualify** (`0x20000`) → the orthogonal black-family order with
  `BlackDetail.Disqualified` — never discarded, whatever the track flag; the
  compositor's demotion rule keeps it visible. `greenHeld` (`0x400`) is
  deliberately **not** mapped to green: iRacing raises it while the starter
  still holds the green *furled* (start/restart imminent), so surfacing it as
  a green flag made the panel jump the start. It folds into the gantry's Set
  phase instead; the panel goes green only when the `green` bit itself flies.
- **Penalties**: `repair` (`0x100000`) → the meatball dimension (the unified
  layer maps the same bit to `Flag_Orange`, which the generic adapter already
  routes there — the meatball renders as its true form, a black field with
  the orange disc); `furled` (`0x80000`) → the furled warning frame.
  **DT-vs-SG stays defaulted** — no iRacing telemetry source exists (see the
  penalty-telemetry-limits note above), so a bare `black` bit stays a bare
  black flag.
- **Start sequence** (`SignalState.StartPhase`): `startGo` (`0x80000000`) →
  Go, `startSet` (`0x40000000`) or `greenHeld` (`0x400`, furled green in the
  starter's hand) → Set, `startReady` (`0x20000000`) or rolling-start
  `oneLapToGreen` (`0x200`) → Ready; else Off (`startHidden` included).
  Rendered as the five-light gantry board; under the compositor it can ride
  any field, and at Go the green flag takes the field naturally. iRacing
  exposes no light counts, so `StartLightsLit` stays 0 (= all five at Set).
- **Countdown notices**: `tenToGo` (`0x800`) → `CountdownLaps = 10`,
  `fiveToGo` (`0x1000`) → 5 — the numeric notice boards.
- **Debris**: `debris` (`0x40`) → `TrackFlag.Debris`, only when no other
  track flag won (lowest track state; the real signal is the striped surface
  flag, rendered as a field). The unified layer never surfaces this bit.
- **Incident warning** (`SignalState.IncidentWarning`): fires when
  `PlayerCarMyIncidentCount` ≥ session `IncidentLimit − IncidentWarnMargin`
  (margin 4 ≈ one hard incident). Rendered as the red warning frame
  (suppressed under takeovers and DQ). Independent of the SessionFlags mask,
  so a mask-less tick still warns. See the incident-limit note below for the
  source.
- **Not touched**: checkered/white/green/black are single bits the unified
  layer already carries 1:1; blue stands as the unified layer derives it —
  `blue && !green`, so on a simultaneous blue+green tick the panel shows
  green. The adapter deliberately does **not** restore blue from raw during
  that overlap: SimHub suppresses it on purpose (issue
  [#436](https://github.com/SHWotever/SimHub/issues/436), spurious blues
  around starts), and green is the flag that matters in that window; session
  mapping stays generic. `crossed` (halfway) remains unmapped — a future
  candidate, deliberately not guessed at.

**Incident-limit source (researched, not live-verified).** iRacing's incident
limit lives in the session-info YAML at
`WeekendInfo:WeekendOptions:IncidentLimit`, not in telemetry. Reflection over
the `iRacingSDK.dll` shipped inside SimHub 9.11.21 shows its **typed**
`SessionData._WeekendOptions` model maps only a subset of that section
(`NumStarters`, `StandingStart`, `HardcoreLevel`, …) and **omits
`IncidentLimit`** — so the typed path does not exist. The extractor instead
reads the raw parsed tree exposed as `DataSampleEx.SessionDataDict`
(`Dictionary<string, object>` — reachable through the BCL interface, no
proprietary reference), walking `WeekendInfo → WeekendOptions → IncidentLimit`.
Every layer is guarded (a wrong nesting/key/type degrades to "no limit", never
throws); `"unlimited"` and any non-numeric value count as no finite limit. The
**dictionary** nesting/casing is researched, not confirmed against a live
session — verify (and tweak if needed) in a running race.

## Property-layer gotchas

- **Bad property paths return null in NCalc**, and null silently breaks
  string concatenation — the whole formula evaluates to an empty string and the
  serial line is silently dropped rather than erroring. Wrap risky references in
  `isnull(x, fallback)`. This applies to any name-based property access (raw-data
  paths that differ per install are the usual culprits), not just flags.

## Sources

- [SHWotever/SimHub Issue #579 — broaden unified Flag enum](https://github.com/SHWotever/SimHub/issues/579)
- [SHWotever/SimHub Issue #436 — iRacing blue flags at start](https://github.com/SHWotever/SimHub/issues/436)
- [SHWotever/SimHub Issue #388 — green flag](https://github.com/SHWotever/SimHub/issues/388)
- [Forum: ACC flags not working properly](https://www.simhubdash.com/community-2/simhub-support/acc-flags-not-working-properly-2/)
- [SimHub manual](https://manual.simhubdash.com)
