# uniflag v2 wire protocol

> **Status: DRAFT (M2b).** The packet set freezes in M6; this document
> currently covers the framing layers and the `Frame` packet proven by
> the M2b streaming spike, plus the spike's measured results. Layouts
> here are implemented in `proto/src/{crc,cobs,packet}.rs` — the code
> and its tests are authoritative until the M6 freeze adds golden
> vectors.

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
| 0x01 | host→device | Hello | *(M6)* |
| 0x02 | host→device | Frame | 3072 B RGB888, row-major from top-left, 3 B/pixel |
| 0x03 | host→device | Brightness | *(M6)* |
| 0x81 | device→host | HelloAck | *(M6: fw version, protocol version, panel W×H)* |
| 0x82 | device→host | ButtonEvent | *(M6)* |

Host→device types have the high bit clear; device→host set.
`PROTOCOL_VERSION = 1` (carried in HelloAck from M6).

Sizes: max raw packet (Frame) = 3075 B; max on-the-wire packet
incl. delimiter = **3089 B** (`packet::MAX_WIRE_LEN`, sizes the
firmware RX accumulator).

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
