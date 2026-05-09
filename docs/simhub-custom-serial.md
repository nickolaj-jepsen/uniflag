# SimHub — custom serial integration

SimHub has two integration paths for "I have a USB device, push data to it":

1. **"Custom serial devices"** plugin — the host PC speaks a protocol
   defined by the device. The plugin sends ASCII strings built from
   NCalc/JavaScript formulas referencing SimHub properties.
2. **"Custom Arduino" path** — the host PC speaks SimHub's fixed framing
   protocol, the device runs SimHub's Arduino firmware skeleton with
   `SHCustomProtocol.h` overridden.

uniflag uses path (1): the firmware defines its own ASCII line protocol
and SimHub formats updates with an NCalc formula. This avoids embedding
SimHub's Arduino framing and is straightforward to drive from any
USB-CDC stack.

## Plugin: "Custom serial devices"

Wiki: <https://github.com/SHWotever/SimHub/wiki/Custom-serial-devices>

### Configuration UI fields

- **Serial port** — the USB CDC device.
- **Baud rate** — anything sensible. USB CDC is virtual so the rate is symbolic;
  pick e.g. `115200`.
- **DTR / RTS** — flow-control lines, leave defaulted unless the firmware uses them.
- **Log incoming data** — debug toggle, useful while bringing a device up.
- **Auto-reconnect on error** — recommended on.

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
that's wrong (or no longer true) in the SimHub 9.x line: the bytes on
the wire are exactly what the formula evaluates to. The formula must
include the terminator explicitly, e.g. `+ '\r\n'`.

So:

- Encoding: ASCII (everything is built via string concatenation from NCalc).
- Frame terminator: **none added by SimHub** — append `'\r\n'` (or
  `'\n'`) in the formula.
- "Empty message" (formula evaluates to `""`) → not sent.
- "Changes only" → message only sent when its formula's value actually changed.

### Receiving side guarantees

- The device gets the literal output of the formula — nothing more,
  nothing less. Frame splitting is the device's responsibility.
- Up to 10 Hz max in the free build → at most 100 ms per update on
  free, faster on paid.
- The plugin only **sends**; it can show what the device sent back
  (echo), but it cannot use device responses as inputs. Bidirectional
  exchange requires either a SimHub plugin or the Custom Arduino path.

## Authoring formulas in NCalc

SimHub uses **NCalc**. Property references are wrapped in `[]`. Strings
concatenate with `+`. The relevant primitives:

- `if(cond, then, else)` — ternary, returns one branch's value
- `isnull(x)` / `isnull(x, fallback)`
- `format(value, '0.0')` — number formatting
- `padleft(s, n, '0')` — pad string left
- `replace(s, 'a', 'b')` — string replace
- `blink(period_ms)` / `blink(period_ms_on, period_ms_off)` — boolean alternator
- `changed(x)`, `isincreasing(x)`, `isdecreasing(x)` — state-change helpers

Bad property paths return null; null silently breaks `+`-concatenation
(the whole formula evaluates to an empty string and SimHub drops it).
Wrap risky references in `isnull(x, fallback)` and verify property
names with **Available properties → Show game specific properties
('rawdata')**.

The catalogue of flag-related properties is in
[`simhub-flag-properties.md`](./simhub-flag-properties.md).

## Plugin: "Custom Arduino" — for reference

Wiki: <https://github.com/SHWotever/SimHub/wiki/Custom-Arduino-Hardware-Support>

The Arduino firmware path, summarised for completeness (uniflag does
not use it; many SimHub forum posts assume it):

- The device vendors the SimHub Arduino skeleton and overrides
  `SHCustomProtocol.h`.
- The host sends frames using SimHub's framing (`FlowSerial...`) with
  helpers like `FlowSerialReadStringUntil(';')` /
  `FlowSerialReadStringUntil('\n')`.
- Three callbacks: `setup()`, `read()`, `loop()` (and `idle()`,
  time-critical).
- The host expects a bidirectional handshake; the firmware identifies
  itself.

This is more rigid and assumes an Arduino-style firmware — pick the
custom-serial path instead if you control the firmware framing.

## Profile file format

SimHub stores Custom Serial Device profiles as `.shsds` JSON files
under `PluginsData\CustomSerialDevices\`. The format is **not officially
documented** and changes between SimHub versions, so anything checked
into source control should be treated as an opaque export — author it
through the GUI on a Windows install and re-export after edits, rather
than hand-editing the JSON.

## Sources

- [Custom serial devices wiki](https://github.com/SHWotever/SimHub/wiki/Custom-serial-devices)
- [Custom Arduino Hardware Support wiki](https://github.com/SHWotever/SimHub/wiki/Custom-Arduino-Hardware-Support)
- [NCalc scripting wiki](https://github.com/SHWotever/SimHub/wiki/NCalc-scripting)
- [SimHub manual](https://manual.simhubdash.com)
- Worked Arduino example: [SHCustomProtocol forum thread](https://www.simhubdash.com/community-2/simhub-support/shcustomprotocol/)
