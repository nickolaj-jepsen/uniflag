# uniflag v2 wire protocol

> **Status: FROZEN (M6).** The packet set is complete as of M6; the byte
> vectors under `testdata/proto/` are the frozen conformance fixtures
> that both the Rust and C# codecs must round-trip. Layouts here are
> implemented in `proto/src/{crc,cobs,packet}.rs`; this document, the
> code, and the vectors must agree — any wire-visible change bumps
> `PROTOCOL_VERSION`.

## Transport

USB CDC-ACM (virtual serial). Baud is ignored (native USB). Device
identity: VID `0x1209`, PID `0x0001` (pid.codes **test PID** — the
registered PID `0xF1A6` replaces it once granted; see
[v2-tracking.md](v2-tracking.md)).

## Framing

```text
wire   := cobs(raw) 0x00          -- one trailing 0x00 delimiter per packet
raw    := type payload crc16      -- crc16 little-endian, over type + payload
type   := u8
```

- **COBS** (Cheshire & Baker, Listing 1 — the canonical
  always-terminate-with-a-group-header form; see `proto/src/cobs.rs`
  for the Wikipedia-variant warning). Encoded bytes never contain
  `0x00`; the single `0x00` after each packet is the frame delimiter.
- **CRC-16/CCITT-FALSE** (poly `0x1021`, init `0xFFFF`, no reflection,
  xorout 0, check value `0x29B1`), computed over the raw bytes (type +
  payload) *before* COBS, appended little-endian.
- **Resync contract:** any malformed COBS, bad CRC, unknown type, or
  oversized accumulation is dropped silently; the receiver realigns at
  the next `0x00`. Receivers must treat empty delimiter-to-delimiter
  spans as no-ops (back-to-back delimiters are legal).
- **Forward compat:** unknown packet types are ignored (valid CRC or
  not); never an error.

## Packet types

| type | direction | name | payload |
|------|-----------|------|---------|
| 0x01 | host→device | Hello | `[protocol_version: u8]` — exactly 1 B |
| 0x02 | host→device | Frame | 3072 B RGB888, row-major from top-left, 3 B/pixel |
| 0x03 | host→device | Brightness | `[value: u8]` — exactly 1 B |
| 0x81 | device→host | HelloAck | `[protocol_version: u8][width: u8][height: u8][fw_version: ASCII…]` — ≥ 3 B |
| 0x82 | device→host | ButtonEvent | `[button: u8][kind: u8]` — exactly 2 B |

Host→device types have the high bit clear; device→host set.
`PROTOCOL_VERSION = 1` (carried in both Hello and HelloAck).

Sizes: max raw packet (Frame) = 3075 B; max on-the-wire packet
incl. delimiter = **3089 B** (`packet::MAX_WIRE_LEN`, sizes the
firmware RX accumulator).

## Payload layouts

All multi-byte values are little-endian (today the only multi-byte field
in the protocol is the framing-layer CRC-16; every payload field below is
a single byte or a byte string). A known packet type whose payload length
doesn't match its layout is **dropped by the receiver**
(`packet::Error::BadLength` in `proto`) — the same silent-drop posture as
a bad CRC. Unknown packet *types* are the opposite: always passed through
parsing and ignored, never an error (forward compat).

### Hello (`0x01`, host→device) — exactly 1 byte

| offset | size | field | notes |
|--------|------|-------|-------|
| 0 | 1 | `protocol_version` | the host's `PROTOCOL_VERSION` |

### Frame (`0x02`, host→device) — exactly 3072 bytes

RGB888, row-major from the top-left, 3 bytes per pixel. With `(x, y)`
0-indexed, `x` growing right and `y` growing down:

| offset | size | field |
|--------|------|-------|
| `(y*32 + x)*3 + 0` | 1 | red, pixel `(x, y)` |
| `(y*32 + x)*3 + 1` | 1 | green, pixel `(x, y)` |
| `(y*32 + x)*3 + 2` | 1 | blue, pixel `(x, y)` |

### Brightness (`0x03`, host→device) — exactly 1 byte

| offset | size | field | notes |
|--------|------|-------|-------|
| 0 | 1 | `value` | display brightness multiplier `0..=255`; the device applies it to each channel **pre-gamma** as `(c * (value + 1)) >> 8` |

### HelloAck (`0x81`, device→host) — minimum 3 bytes

| offset | size | field | notes |
|--------|------|-------|-------|
| 0 | 1 | `protocol_version` | the device's `PROTOCOL_VERSION` |
| 1 | 1 | `width` | panel width in pixels (32) |
| 2 | 1 | `height` | panel height in pixels (32) |
| 3 | remainder | `fw_version` | firmware version string: ASCII, no NUL terminator, may be empty. No wire-level length cap; the Rust *encoder* caps it at `packet::MAX_FW_VERSION_LEN` (32 B) — parsers must accept any length the framing allows |

### ButtonEvent (`0x82`, device→host) — exactly 2 bytes

| offset | size | field | notes |
|--------|------|-------|-------|
| 0 | 1 | `button` | 0 = GPIO 21 (brightness up), 1 = GPIO 26 (brightness down), 2 = GPIO 27 (sleep) |
| 1 | 1 | `kind` | 0 = short press (classified on release), 1 = long press. A long press never *also* fires a short press |

Only the 2-byte length is frozen; the `button`/`kind` id spaces are open.
Receivers must ignore unassigned values rather than reject the packet, so
future firmware buttons don't break older hosts.

## Handshake

```text
host                                        device
  ── Hello { protocol_version } ──────────→        on every (re)connect
  ←─ HelloAck { proto, w, h, fw_version } ─
  ── Brightness { value } ────────────────→        re-sent on every (re)connect
  ── Frame, Frame, … (30 fps) ────────────→        stream-as-heartbeat
  ←─ ButtonEvent ───────────────────────────       any time after HelloAck
```

- On every connect and reconnect the host opens with `Hello`, carrying
  its `PROTOCOL_VERSION`.
- The device replies `HelloAck` with its own protocol version, panel
  dimensions, and firmware version.
- The plugin validates **protocol-version equality**. On mismatch it
  refuses to drive the device and surfaces the mismatch to the user
  (refuse-with-message — no silent fallback, no best-effort mode).
- `Brightness` is re-sent by the host on every (re)connect; the device
  never assumes a value survives a reconnect.
- After `HelloAck`, `ButtonEvent` packets flow device→host at any time.

## Stream-as-heartbeat

The host streams Frames continuously at 30 fps whether or not content
changed; there are no frame acks. The stream doubles as the liveness
signal: after ~1.5 s without a decodable Frame the device drops to its
local idle/fallback screen (M8 implements; the constant mirrors v1's
`CONNECT_TIMEOUT`).

## M2b spike measurements (2026-07-03, dev box → Cosmic Unicorn)

Setup: `uniflag-sim --stream` (30 fps, monotonic-deadline pacing, one
whole encoded frame per `write`) → Windows 11 USB FS → spike firmware
(`spike/m2b-cdc-streaming` branch: COBS accumulator + zerocopy
double-buffer + `set_pixel` blit + `present`).

- Visual: scrolling gradient + 1 px/frame cursor confirmed smooth at
  30 fps; no tearing, no colour-order errors, no visible latency
  growth. *(maintainer-verified)*
- 12-minute soak: **21,600 frames in 720.000 s = exactly 30.000 fps;
  0 late writes, 0 ns max pacing slip, 0 reconnects.** The Windows
  write path never back-pressured; CDC bulk throughput has large
  headroom over the ~93 KB/s the stream needs.
- Garbage-injection resync (device kept powered throughout — with a
  bus-powered panel a cable yank is a power cycle and proves nothing
  about the decoder):
  - 1,500 non-zero delimiter-less bytes injected (stale partial frame
    in the accumulator), then a fresh 600-frame stream: clean, 0
    reconnects (a firmware crash would surface as a watchdog reset and
    re-enumeration). The merged garbage+frame packet dies at the CRC;
    everything after renders.
  - 5,000 non-zero bytes (> the 3,089 B accumulator → forced overflow
    path, drop-until-delimiter), then 600 frames: clean, 0 reconnects.
- Cable yank / host reconnect (Windows stale-COM-handle path): **not
  live-tested in M2b** — the CLI's reopen-retry loop exists and
  open-failure handling was exercised (port contention with SimHub),
  but no mid-stream physical yank was performed. Deliberately deferred
  to the M8 hardware checklist, which repeats the yank/replug soak on
  the hardened firmware, and to M9 (risk #17) where the .NET
  reconnect path is the one that ships.
- **Verdict for M6/M8: GO.** 30 fps whole-frame writes over CDC are
  comfortably sustainable on this stack; 0x00-delimiter resync
  recovers from partial frames and accumulator overflow without a
  power cycle. Heartbeat timing (~1.5 s) and the HelloAck/ButtonEvent
  TX path remain M8 scope.

Known non-goals of the spike: Hello/HelloAck, Brightness, ButtonEvent,
idle fallback, and the CDC TX path — all M6/M8 scope.
