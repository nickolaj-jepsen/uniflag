# SimHub setup

The `uniflag` firmware doesn't need a custom SimHub plugin. It uses
SimHub's built-in **Custom Serial Devices** plugin, which sends
formula-driven ASCII strings over USB-CDC. You author one device profile
on the SimHub side that emits the wire format the firmware expects.

> Tested with SimHub **9.x** (the modern release line). Older versions
> may not expose every flag property listed below.

## Wire format

The firmware accepts one line per update, terminated by `\n`
(SimHub appends this automatically). Fields are semicolon-separated
`key=value` pairs:

```
F=Y;B=1;P=0;S=racing
```

| Field | Values                                                           | Meaning                                |
|-------|------------------------------------------------------------------|----------------------------------------|
| `F`   | `N` `Y` `B` `K` `W` `R` `G` `C` `O`                              | Active flag                            |
| `B`   | `0` `1` `2`                                                      | Wave level (none / single-waved / double-waved) |
| `P`   | `0` `1`                                                          | In-pit indicator                       |
| `S`   | `pre-race` `racing` `paused` `post-race` `replay` `unknown`      | Session state                          |
| `C`   | `N` `V` `S`                                                      | Caution: none / Virtual Safety Car / Safety Car |
| `Z`   | (empty) `1` `2` `3` `12` `13` `23` `123`                         | Sector-yellow mask (ascending unique digits) |

`B=0` is a static / displayed flag. `B=1` is single-waved (the marshal is
actively signalling — local caution, faster car approaching, etc.); the
firmware renders this with a 2 Hz strobe (or breathing pulse for blue).
`B=2` is double-waved, signalling a more serious incident; the renderer
treats it as a more urgent variant — typically a 4 Hz strobe. Sims that
don't distinguish single from double should map any "waved" state to
`B=1`.

`C=` and `Z=` are orthogonal to `F=`. Caution renderings (VSC / SC)
fill the panel and override the per-flag base for everything except a
red flag, which always wins. The sector mask paints a bottom-edge
indicator strip, suppressed only under red. `F=N;Z=2` is the canonical
"yellow ahead in S2, clear at your location" warning.

Unknown keys are silently ignored, so adding new fields later won't
break older firmware. See `proto/src/lib.rs` for the canonical
definition and unit tests.

## Option A — import the shipped profile (TODO: not yet authored)

Once we have a working profile exported to `simhub/uniflag.json`, the
import flow will be:

1. Open SimHub.
2. **Settings → General → Properties cache → Open data folder**.
3. Navigate to `PluginsData\CustomSerialDevices\`.
4. Drop `uniflag.json` into that folder.
5. Restart SimHub.
6. **Available add-ons → Additional plugins → Custom serial devices**:
   tick the new entry, pick the COM port the device enumerates as,
   and apply.

> **Note**: SimHub stores Custom Serial Device profiles as JSON, but
> the format is **not officially documented** and changes between
> SimHub versions. We treat the file as an opaque export — author it
> through the GUI on a Windows install, then commit the result. Don't
> hand-edit it.

## Option B — author it manually (current path)

Until the JSON is exported, set the profile up by hand. Steps:

1. **Available add-ons → Additional plugins → Custom serial devices →
   Enable**, then in the same screen, add a new device.
2. **General**:
   - **Name**: `uniflag`
   - **Serial port**: pick the COM port (Windows) or `/dev/ttyACM*`
     (Linux SimHub) the device enumerates as. The board self-identifies
     as `uniflag / uniflag` (VID `0x1209`, PID `0x0001`).
   - **Baud rate**: `115200` (USB CDC ignores this — any value works).
   - **DTR / RTS**: leave default.
   - **Auto-reconnect on error**: enable.
3. **Update messages → Add message**:
   - **Name**: `state`
   - **Trigger**: every 200 ms (5 Hz). Higher rates are wasted on a flag
     display; the free SimHub tier caps at 10 Hz anyway.
   - **Formula type**: NCalc.
   - **Formula**: paste the block below.

```ncalc
'F=' +
if([DataCorePlugin.GameData.Flag_Yellow],   'Y',
if([DataCorePlugin.GameData.Flag_Blue],     'B',
if([DataCorePlugin.GameData.Flag_Black],    'K',
if([DataCorePlugin.GameData.Flag_White],    'W',
if([DataCorePlugin.GameData.Flag_Checkered],'C',
if([DataCorePlugin.GameData.Flag_Green],    'G',
if([DataCorePlugin.GameData.Flag_Orange],   'O',
                                            'N')))))))
+ ';B=' + if([DataCorePlugin.GameData.Flag_Yellow], '1', '0')
+ ';P=' + if([DataCorePlugin.GameData.IsInPitLane], '1', '0')
+ ';S=' + if(isnull([DataCorePlugin.GameData.SessionTypeName]), 'unknown',
          if([DataCorePlugin.GameData.SessionTypeName] = 'Race', 'racing',
          if([DataCorePlugin.GameData.SessionTypeName] = 'Practice', 'pre-race',
          if([DataCorePlugin.GameData.SessionTypeName] = 'Qualifying', 'pre-race',
          'unknown'))))
```

Notes:

- `Flag_Yellow` triggering both the `F=Y` field *and* `B=1` is
  intentional: the unified property doesn't distinguish static from
  waved, so we treat any yellow as single-waved. If your sim *does*
  distinguish, swap the `B=` line for one that maps the bits explicitly.
  For iRacing, `SessionFlags` exposes `YellowWaving` / `CautionWaving`;
  for ACC, `[DataCorePlugin.GameRawData.Graphics.flag] = 2` plus
  `[DataCorePlugin.GameRawData.Graphics.globalYellow]` distinguishes
  global vs sector yellows. The firmware understands `B=2` (double-waved)
  for situations a sim flags as more urgent — wire it up if the data is
  available, otherwise leave at `1`.
- The unified `Flag_Orange` property was added late; very old SimHub
  versions may not have it. If your formula errors on save, drop that
  line.
- Session-state mapping is sim-dependent. The list above covers iRacing
  / ACC / common-case Codemasters games. Add cases for your sim if
  needed, or replace with `[DataCorePlugin.GameData.SessionState]`.
- The basic formula above doesn't emit `C=` or `Z=`. The firmware
  tolerates their absence (defaults: `C=N`, empty `Z=`), so existing
  setups keep working. Add the extension below if you want VSC /
  Safety Car / sector indicators.

### Extended formula — caution + sector yellows

Append the following to the basic formula to drive the `C=` (caution)
and `Z=` (sector mask) fields. Pick the sim-specific block that
matches your install; if you race more than one sim, copy the relevant
block on a per-device-profile basis.

**iRacing** — bit-test the `SessionFlags` mask (see `irsdk_Flags`).
`Caution` is bit `0x4000`, `SafetyCarActive` exposes the physical SC.

```ncalc
+ ';C=' + if(([DataCorePlugin.GameRawData.Telemetry.SessionFlags] & 0x4000) > 0, 'V',
          if([DataCorePlugin.GameData.SafetyCarActive], 'S', 'N'))
+ ';Z='   // iRacing has no per-sector flag in stock telemetry
```

**ACC** — concatenate the three `globalYellow*` raw properties into a
canonical sector mask. ACC has no first-class VSC / SC concept.

```ncalc
+ ';C=N'
+ ';Z=' +
  (if([DataCorePlugin.GameRawData.Graphics.globalYellow1], '1', '') +
   if([DataCorePlugin.GameRawData.Graphics.globalYellow2], '2', '') +
   if([DataCorePlugin.GameRawData.Graphics.globalYellow3], '3', ''))
```

**rF2 / Le Mans Ultimate** — `mGamePhase` enum surfaces pace-car /
FCY phases. Verify the bit values against your install; the values
below are the common ISI ModDev mapping.

```ncalc
+ ';C=' + if([DataCorePlugin.GameRawData.Scoring.mGamePhase] = 6, 'V',
          if([DataCorePlugin.GameRawData.Scoring.mGamePhase] = 5, 'S', 'N'))
+ ';Z='   // mSectorFlag is per-corner; needs host-side aggregation (TODO)
```

If the formula errors on save, the property names may differ on your
build — check **Available properties → Show game specific properties
('rawdata')** in SimHub and adjust. Bad property paths return null in
NCalc, which silently breaks `+`-concatenation; wrap risky references
in `isnull(x, fallback)`.

4. **Apply**, then **Save**. The device should connect; its solid-flag
   LED corner of the panel changes colour as you toggle a flag in-game.

## Once it works

Export and commit:

1. **Settings → General → Properties cache → Open data folder →
   `PluginsData\CustomSerialDevices\`**.
2. Find the JSON file matching the device name (`uniflag.json`).
3. Copy it into `simhub/uniflag.json` and commit.
4. Update Option A above to remove the TODO note.

## Troubleshooting

- **Panel boot-splash visible but doesn't react to flags**: open
  SimHub's **Logs** screen and check for the device line. Common cause:
  the formula is being evaluated before a sim is connected, so all
  `[DataCorePlugin.GameData.Flag_*]` properties are null and the
  formula returns an empty string (which SimHub silently drops). Tick
  **Available properties → Show game specific properties ('rawdata')**
  to confirm the values are populating in your sim.
- **Wrong colour for a flag**: verify your sim is running the version
  SimHub claims it supports. Some sims need their telemetry plugin
  separately installed (e.g. iRacing wants the iRSDK pipe alive).
- **No device appears**: check `dmesg` (Linux) or Device Manager
  (Windows). The board enumerates as VID `0x1209`, PID `0x0001`. If
  SimHub doesn't see it, the OS isn't either.
- **Device sees data but flag is wrong**: run `uniflag-sim --port
  COMx --no-port` (or just `--no-port`) on the same host to verify the
  firmware accepts your formula's output. The simulator emits the same
  format the firmware expects, so any divergence shows up immediately.
