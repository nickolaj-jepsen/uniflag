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
| `DataCorePlugin.GameRawData.Telemetry.PlayerCarTowTime`   | Tow-truck recovery state (proxy for damage) |

The `SessionFlags` bits are documented by iRacing as `irsdk_Flags` — `Checkered`,
`White`, `Green`, `Yellow`, `Red`, `Blue`, `Debris`, `Crossed`, `YellowWaving`,
`OneLapToGreen`, `GreenHeld`, `TenToGo`, `FiveToGo`, `RandomWaving`, `Caution`,
`CautionWaving`, plus per-driver flags like `Black`, `Disqualify`, `Servicible`,
`Furled`, `Repair`. SimHub usually surfaces these via the unified `Flag_*` props but
the bitmask is there if we need precise behaviour (e.g. distinguishing displayed
yellow vs waved yellow).

### rFactor 2 / Le Mans Ultimate

Both expose a per-corner `Sectors` flag colour and a per-vehicle penalty status. The
unified `Flag_*` properties cover the common cases. For richer info:

| Property                                                       | Meaning |
|----------------------------------------------------------------|---------|
| `DataCorePlugin.GameRawData.Scoring.mYellowFlagState`          | Global yellow state |
| `DataCorePlugin.GameRawData.Scoring.mSectorFlag[0..2]`         | Per-sector flag |
| `DataCorePlugin.GameRawData.Telemetry.mPenalties`              | Active penalties |

(Names may differ slightly between rF2 and LMU — verify in the in-app properties
picker once we have a running install.)

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
| iRacing          | ✓   | ✓  | `SessionFlags` bitmask for `Caution` / `CautionWaving`; `SafetyCarActive` for SC |
| F1 (Codemasters) | ✓   | ✓  | `m_safetyCarStatus` raw enum (0=none, 1=full SC, 2=VSC, 3=formation lap)    |
| ACC              | —   | —  | No first-class VSC / SC concept exposed.                                    |
| rF2 / LMU        | ✓   | ✓  | `mGamePhase` enum exposes pace-car / FCY phases.                            |
| Automobilista 2  | ✓   | ✓  | `mSafetyCarStatus` raw field.                                               |

## Sector-localised yellows by sim

| Sim                     | Property                                | Notes                                                               |
|-------------------------|-----------------------------------------|---------------------------------------------------------------------|
| ACC                     | `GameRawData.Graphics.globalYellow1/2/3`| One boolean per sector.                                             |
| F1 (Codemasters)        | `GameRawData.MarshalZones[i].ZoneFlag`  | Up to 21 marshal zones with `ZoneStart` ∈ [0, 1]; aggregate to thirds host-side. |
| rF2 / LMU               | `GameRawData.Scoring.mSectorFlag[0..2]` | One value per sector.                                               |
| iRacing                 | (none)                                  | `SessionFlags` is global only.                                      |
| Assetto Corsa (vanilla) | (none)                                  | No per-sector flags exposed.                                        |

## Sources

- [SHWotever/SimHub Issue #579 — broaden unified Flag enum](https://github.com/SHWotever/SimHub/issues/579)
- [SHWotever/SimHub Issue #436 — iRacing blue flags at start](https://github.com/SHWotever/SimHub/issues/436)
- [SHWotever/SimHub Issue #388 — green flag](https://github.com/SHWotever/SimHub/issues/388)
- [Forum: ACC flags not working properly](https://www.simhubdash.com/community-2/simhub-support/acc-flags-not-working-properly-2/)
- [SimHub manual](https://manual.simhubdash.com)
