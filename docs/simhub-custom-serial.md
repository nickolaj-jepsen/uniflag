# SimHub — custom serial integration

SimHub has two integration paths for "I have a USB device, push data to it":

1. **"Custom serial devices"** plugin — host PC speaks our protocol, fully defined by us.
   The plugin sends ASCII strings built from NCalc/JavaScript formulas referencing
   SimHub properties.
2. **"Custom Arduino" path** — host PC speaks a fixed framing protocol, the device runs
   the SimHub Arduino firmware skeleton with `SHCustomProtocol.h` overridden.

For uniflag we want **(1) custom serial device**: simpler protocol, no need to embed
SimHub's Arduino firmware framing, easier to author from a Rust embassy USB-CDC stack.

## Plugin: "Custom serial devices"

Wiki: <https://github.com/SHWotever/SimHub/wiki/Custom-serial-devices>

### Configuration UI fields

- **Serial port** — the USB CDC device.
- **Baud rate** — anything sensible. USB CDC is virtual so the rate is symbolic;
  pick e.g. `115200`.
- **DTR / RTS** — flow-control lines, leave defaulted unless we deliberately use them.
- **Log incoming data** — debug toggle, keep on while bringing it up.
- **Auto-reconnect on error** — yes.

### Messages

Three categories of message, each a formula:

| Hook            | When it's sent | Notes |
|-----------------|----------------|-------|
| **Hello**       | immediately after the port opens | Send any one-time init / "draw splash" command. |
| **Goodbye**     | immediately before the port closes | Won't fire on hard disconnect. |
| **Update**      | on a configurable cadence (or "changes only") | Repeats. Up to 10 Hz on the free build, higher with paid. |

You can have many update messages; each has its own enable, cadence, and formula.

### Wire format

The plugin does **not** add any framing — including no automatic line
terminator. The wiki has historically claimed `\n` is auto-appended, but
that's wrong (or no longer true) in the SimHub 9.x line we tested
against: the bytes on the wire are exactly what the formula evaluates to.
The formula must include the terminator explicitly, e.g. `+ '\r\n'`.

So:

- Encoding: ASCII (everything is built via string concatenation from NCalc)
- Frame terminator: **none added by SimHub** — append `'\r\n'` (or `'\n'`)
  yourself in the formula
- "Empty message" (formula evaluates to `""`) → not sent
- "Changes only" → message only sent when its formula's value actually changed

### Receiving side guarantees

- We get the literal output of the formula — nothing more, nothing less.
  The firmware splits on `\n`, so the formula must end with `'\r\n'` (or
  `'\n'`) for any line to be processed.
- Up to 10 Hz max in the free build → at most 100 ms per update on free, faster on paid.
- The plugin only **sends**; it can show what the device sent back (echo), but it
  cannot use device responses as inputs. If we want bidirectional, we'd need either
  a SimHub plugin or to use SimHub's Custom Arduino path instead.

## Designing the wire protocol

Some constraints to keep the firmware simple:

- Length-delimited or terminator-delimited frames are both fine; the natural choice is
  the auto-`\n` already at the end.
- ASCII-only is convenient with NCalc but inefficient for binary blobs (e.g. raw RGB
  images). For a flag display we don't need a framebuffer over the wire — just a flag
  state — so ASCII is plenty.

### Suggested format

A single semicolon-separated record per update tick. Example:

```
F=Y;B=0;G=N;S=racing;C=N;Z=\n
```

Field meanings:

| Field | Values | Meaning |
|-------|--------|---------|
| `F`   | `N` (none) / `Y` / `B` / `K` (black) / `W` / `C` (checkered) / `G` (green) / `O` (orange) / `P` (penalty) | Active flag |
| `B`   | `0` / `1` / `2` | Wave level (none / single-waved / double-waved) — drives the per-flag effect intensity |
| `G`   | `N`/`Y` | Green flag pulse (one-shot, e.g. on green start) |
| `S`   | `replay` / `racing` / `paused` / `pre-race` / `post-race` | Session state |
| `C`   | `N` / `V` (VSC) / `S` (Safety Car) | Caution state, orthogonal to `F` — coexists with any flag |
| `Z`   | (empty) / `1` / `2` / `3` / `12` / `13` / `23` / `123` | Sector-yellow mask, ascending unique digits |

…or whatever final taxonomy we decide on. The point is: it's text, it's terminator-
delimited, and the device can parse it with a tiny state machine.

### Authoring the formula in SimHub

SimHub uses **NCalc**. Property references are wrapped in `[]`. Strings concatenate
with `+`. The relevant primitives:

- `if(cond, then, else)` — ternary, returns one branch's value
- `isnull(x)` / `isnull(x, fallback)`
- `format(value, '0.0')` — number formatting
- `padleft(s, n, '0')` — pad string left
- `replace(s, 'a', 'b')` — string replace
- `blink(period_ms)` / `blink(period_ms_on, period_ms_off)` — boolean alternator
- `changed(x)`, `isincreasing(x)`, `isdecreasing(x)` — state-change helpers

A first-cut formula for the field-set above:

```
'F=' +
if([DataCorePlugin.GameData.Flag_Yellow],   'Y',
if([DataCorePlugin.GameData.Flag_Blue],     'B',
if([DataCorePlugin.GameData.Flag_Black],    'K',
if([DataCorePlugin.GameData.Flag_White],    'W',
if([DataCorePlugin.GameData.Flag_Checkered],'C',
if([DataCorePlugin.GameData.Flag_Green],    'G',
                                            'N'))))))
+ ';B=' + if([DataCorePlugin.GameData.Flag_Yellow], '1', '0')
+ ';S=' + isnull([DataCorePlugin.GameData.SessionTypeName], 'unknown')
+ '\r\n'
```

The trailing `'\r\n'` is **required** — SimHub does not add a line
terminator automatically. Without it the firmware accumulates bytes
indefinitely without ever passing a line to the parser, and the panel
stays in its disconnected (dark) state.

(Property names verified per [`simhub-flag-properties.md`](./simhub-flag-properties.md).)

## Plugin: "Custom Arduino" — for reference, not what we're using

Wiki: <https://github.com/SHWotever/SimHub/wiki/Custom-Arduino-Hardware-Support>

If we instead used the Arduino firmware path:

- We'd vendor the SimHub Arduino skeleton and override `SHCustomProtocol.h`.
- The host sends frames using its built-in framing (`FlowSerial...`) with helpers like
  `FlowSerialReadStringUntil(';')` / `FlowSerialReadStringUntil('\n')`.
- Three callbacks: `setup()`, `read()`, `loop()` (and `idle()`, time-critical).
- The host expects bidirectional handshake; the firmware identifies itself.

This is more rigid and assumes Arduino — wrong fit for a Rust + embassy build. We
mention it only because some SimHub forum posts assume this path.

## SimHub-side artefacts

The end-user setup steps and the canonical NCalc formula live at
[`simhub/README.md`](../simhub/README.md). That file is the source of
truth for what to type into SimHub's GUI; this doc just describes the
protocol the firmware accepts.

The pre-authored profile lives at [`simhub/uniflag.shsds`](../simhub/uniflag.shsds).
SimHub stores Custom Serial Device profiles as `.shsds` JSON; the format
is undocumented and version-fragile, so the file is treated as an opaque
export — authored through the GUI on a Windows install, then committed
verbatim. Don't hand-edit it.

## Sources

- [Custom serial devices wiki](https://github.com/SHWotever/SimHub/wiki/Custom-serial-devices)
- [Custom Arduino Hardware Support wiki](https://github.com/SHWotever/SimHub/wiki/Custom-Arduino-Hardware-Support)
- [NCalc scripting wiki](https://github.com/SHWotever/SimHub/wiki/NCalc-scripting)
- [SimHub manual](https://manual.simhubdash.com)
- Worked Arduino example: [SHCustomProtocol forum thread](https://www.simhubdash.com/community-2/simhub-support/shcustomprotocol/)
