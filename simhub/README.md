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
| `B`   | `0` `1`                                                          | Blink the active flag (waved-yellow)   |
| `P`   | `0` `1`                                                          | In-pit indicator                       |
| `S`   | `pre-race` `racing` `paused` `post-race` `replay` `unknown`      | Session state                          |

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
  intentional — the firmware blinks waved-yellow. If your sim
  distinguishes "static" yellow from "waved" yellow and you only want
  the waved variant to blink, replace the `B=` line with a sim-specific
  property (e.g. `[DataCorePlugin.GameRawData.Graphics.globalYellow]`
  for ACC, or a bit-test on iRacing's `SessionFlags` mask).
- The unified `Flag_Orange` property was added late; very old SimHub
  versions may not have it. If your formula errors on save, drop that
  line.
- Session-state mapping is sim-dependent. The list above covers iRacing
  / ACC / common-case Codemasters games. Add cases for your sim if
  needed, or replace with `[DataCorePlugin.GameData.SessionState]`.

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
