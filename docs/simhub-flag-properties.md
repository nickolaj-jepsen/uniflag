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

When the unified property isn't enough — most often because we want the **localised**
yellow flag (sector 1/2/3) and not just "any yellow" — use the raw-data path.

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
| `DataCorePlugin.GameRawData.Telemetry.OnPitRoad`          | In pit lane |

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
| `DataCorePlugin.GameData.IsInPitLane`                      | Pit-lane indicator (icon overlay or side stripe) |
| `DataCorePlugin.GameData.IsInPit`                          | Stationary in pit box |
| `DataCorePlugin.GameData.SessionTypeName`                  | "Race" / "Practice" / "Qualifying" |
| `DataCorePlugin.GameData.SessionState`                     | "PreSession" / "Green" / "FullCourseYellow" / "Finished" |
| `DataCorePlugin.GameData.PlayerName` / `CarModel` / `TrackName` | Splash/idle screen content |
| `DataCorePlugin.GameData.GameRunning` / `GameInMenu`       | Whether to drive the panel at all |
| `DataCorePlugin.GameData.CurrentLap`                       | "Last lap" detection in conjunction with `TotalLaps` |

## Putting it together

For uniflag's first iteration, the formula in `simhub-custom-serial.md` uses only the
unified `GameData.Flag_*` set and emits `B=1` for any active yellow (the firmware
treats `B=1` as single-waved, `B=2` as double-waved, `B=0` as static). To upgrade
to genuine wave-level distinction:

- **iRacing**: bit-test `[DataCorePlugin.GameRawData.Telemetry.SessionFlags]` for the
  `YellowWaving` / `CautionWaving` bits to emit `B=2`; static `Yellow` / `Caution`
  bits give `B=1`. (See `irsdk_Flags` in the iRacing SDK for the bit positions.)
- **ACC**: `[DataCorePlugin.GameRawData.Graphics.flag] = 2` indicates a yellow event;
  there is no single/double-waved distinction in the SDK so leave at `B=1`.
- **rF2 / LMU**: `[DataCorePlugin.GameRawData.Scoring.mYellowFlagState]` exposes a
  numeric severity (`PendingYellow`, `Yellow`, `LastLap`, `Resume`, …) — map the
  more urgent values to `B=2`.

## Caution states (VSC / Safety Car)

The wire protocol's `C=` field carries session-wide caution, orthogonal to `F=`:
`C=N` (none), `C=V` (Virtual Safety Car / FCY), `C=S` (physical Safety Car).
Coverage by sim:

| Sim       | VSC | SC | How to detect |
|-----------|-----|----|---------------|
| iRacing   | ✓   | ✓  | `SessionFlags` bitmask for `Caution` / `CautionWaving`; `SafetyCarActive` for SC |
| F1 (Codemasters) | ✓ | ✓ | `m_safetyCarStatus` raw enum (0=none, 1=full SC, 2=VSC, 3=formation lap) |
| ACC       | —   | —  | No first-class VSC / SC concept exposed; leave `C=N` |
| rF2 / LMU | ✓   | ✓  | `mGamePhase` enum exposes pace-car / FCY phases |
| Automobilista 2 | ✓ | ✓ | `mSafetyCarStatus` raw field |

Hosts that can't distinguish VSC from SC should map any "FCY-like" state to `C=V`.

## Sector-localised yellows

The wire protocol's `Z=` field is a sector-yellow bitmask using ascending unique
digits (`Z=`, `Z=1`, `Z=23`, `Z=123`). The firmware fixes the model at three
sectors (S1/S2/S3); sims with finer granularity must aggregate host-side.

| Sim       | Property | Mapping |
|-----------|----------|---------|
| ACC       | `GameRawData.Graphics.globalYellow1/2/3` | 1:1 — concatenate active sectors into `Z=` |
| F1 (Codemasters) | `GameRawData.MarshalZones[i].ZoneFlag` | Aggregate the up-to-21 marshal zones into thirds by `ZoneStart` (0..⅓ → S1, ⅓..⅔ → S2, ⅔..1 → S3); set the bit for any third with an active yellow |
| rF2 / LMU | `GameRawData.Scoring.mSectorFlag[0..2]` | 1:1 (rF2 calls them sectors directly) |
| iRacing   | (none)   | iRacing's `SessionFlags` is global only — leave `Z=` empty |
| Assetto Corsa (vanilla) | (none) | Vanilla AC doesn't expose per-sector flags — leave `Z=` empty |

## Sources

- [SHWotever/SimHub Issue #579 — broaden unified Flag enum](https://github.com/SHWotever/SimHub/issues/579)
- [SHWotever/SimHub Issue #436 — iRacing blue flags at start](https://github.com/SHWotever/SimHub/issues/436)
- [SHWotever/SimHub Issue #388 — green flag](https://github.com/SHWotever/SimHub/issues/388)
- [Forum: ACC flags not working properly](https://www.simhubdash.com/community-2/simhub-support/acc-flags-not-working-properly-2/)
- [SimHub manual](https://manual.simhubdash.com)
