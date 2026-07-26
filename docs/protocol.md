# uniflag v2 wire protocol

> **Status: settled for now, not set in stone.** This is a prerelease
> hobby project and the protocol will keep moving. Nothing here is sacred
> — it just costs a version bump to change, because the firmware and the
> plugin have to agree about what a byte means, and they ship separately.
>
> So when you change something wire-visible: bump `PROTOCOL_VERSION`,
> update both codecs and this document, and regenerate `testdata/proto/`,
> all in the same commit. Those vectors are what the two codecs get
> checked against; the layouts here live in
> `proto/src/{crc,cobs,packet}.rs`.

## Transport

USB CDC-ACM (virtual serial). Baud is ignored (native USB). Device
identity: VID `0x1209`, PID `0x0001`.

`0x0001` is the pid.codes **shared test PID**, which must not ship on
redistributed devices; `0x1209:0xF1A6` is requested and pending. When it
is granted, swap `USB_PID` in `proto/src/packet.rs`, its C# mirror in
`plugin/core/Device/DeviceDiscovery.cs`, this section, and
`simhub/README.md`. There is no flag day: the plugin's discovery filter
accepts both PIDs, so fielded devices keep working until they are
reflashed and the filter collapses to the registered PID.

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

Only the 2-byte length is fixed; the `button`/`kind` id spaces are open.
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
- **Hosts must tolerate device→host packets arriving *before* the
  `HelloAck`** and skip them (drop, do not error). The device gates
  ButtonEvent reporting on a completed handshake and clears its TX queue
  on each `Hello`, but it cannot observe COM open/close: a press landing
  inside the ~1.5 s silence window can leave one packet wedged in the
  in-flight write and deliver it to the next session ahead of the ack.
  `uniflag-cli` already discards pre-ack packets; any other host must
  do the same.

## Stream-as-heartbeat

The host streams Frames continuously at 30 fps whether or not content
changed; there are no frame acks. The stream doubles as the liveness
signal: after ~1.5 s without a decodable Frame the device drops to its
local idle/fallback screen.

30 fps whole-frame writes are comfortably sustainable on this stack: a
30-minute hardware soak held 30.000 fps exactly with no skipped
deadlines, link errors or watchdog resets, and CDC bulk throughput has
large headroom over the ~93 KB/s the stream needs. Delimiter resync was
exercised against both a stale partial frame and a forced accumulator
overflow without a power cycle.
