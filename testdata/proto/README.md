# Protocol golden vectors

Cross-language byte vectors for the uniflag v2 binary protocol. These are
the bytes the Rust and C# codecs are **both** checked against:

- Rust: `proto/tests/golden_vectors.rs`, `cli/tests/rx_decode.rs`,
  `cli/tests/golden_conformance.rs`
- C#: `plugin/tests-core/ProtoConformanceTests.cs`

Neither side generates its own fixtures. The prose specification is
[`docs/protocol.md`](../../docs/protocol.md); this file describes what
each vector is *for*.

Regenerate only via `just golden-regen`, and only on purpose: a regen that
moves bytes is a protocol change, and a protocol change bumps
`PROTOCOL_VERSION` and updates both codecs and the prose in the same
commit.

This README and `.gitattributes` are hand-written — `golden-regen` writes
only the vector files.

## Reading the files

| Extension | How to read it |
|---|---|
| `.raw` | Type byte, then payload, then CRC-16/CCITT-FALSE (poly `0x1021`, init `0xFFFF`, no reflection, xorout 0) computed over type+payload and appended little-endian. |
| `.wire` | COBS-encoded `.raw` followed by a single `0x00` delimiter. Canonical Cheshire–Baker (Listing 1) encoding, meaning the encoder always terminates with a group header. |
| `.stream` | A raw byte stream: split on `0x00` delimiters, run every non-empty segment through the decode pipeline, drop segments that fail. |

Negative vectors: strip the single trailing `0x00`, then run the remaining
bytes through the decode pipeline (COBS decode → CRC check → typed parse).
It must fail with the error class named below.

## Positive vectors

Each has a `.raw` and a `.wire`.

| Vector | Type | Purpose |
|---|---|---|
| `hello` | `Hello` (`0x01`) | Host→device handshake open carrying the protocol version. |
| `hello_ack` | `HelloAck` (`0x81`) | Device→host handshake reply: protocol version, 32×32 panel, `fw_version` `2.0.0-test`. |
| `brightness` | `Brightness` (`0x03`) | Brightness set-point 200 (`0xC8`). |
| `button_event_short` | `ButtonEvent` (`0x82`) | Device→host short press of button 0 (brightness up). |
| `button_event_long` | `ButtonEvent` (`0x82`) | Device→host long press of button 2 (sleep). |
| `frame` | `Frame` (`0x02`) | Full 32×32 RGB888 frame — see below. |

### The `frame` payload

Deterministic and piecewise, so the framing layer sees every case that
matters. Byte `i` is:

| Range | Value |
|---|---|
| `i < 256` | `0` if `i % 8 == 0`, else `i` |
| `i < 512` | `0xFF` |
| `i < 1024` | `(i * 7) % 256` |
| `i < 1342` | `(i % 253) + 1` |
| otherwise | `i % 256` |

That yields zero bytes, a 257-byte `0xFF` run (byte 255 is already `0xFF`,
then 256..=511), and non-zero runs longer than 254 bytes — so COBS group
handling is exercised end to end.

## Boundary vector

`cobs_boundary_254` (`.raw` + `.wire`) — the vector that catches a
mis-ported COBS encoder.

Its raw form ends in exactly 254 non-zero bytes immediately after a zero
byte, so a canonical encoder emits a full `0xFF` group followed by a
trailing `0x01` group header before the delimiter. **The Wikipedia
`cobsEncode` example omits that trailing `0x01`**; an encoder ported from
it produces a wire one byte shorter and fails the byte-exact comparison.
Both forms decode to the same raw packet, which is exactly why only the
byte comparison catches it.

Type byte `0x7E` is unassigned, so receivers parse this as an unknown
packet and ignore it (forward compat) rather than erroring.

Re-derivable as `[0x7E, 0x51, tweak, 0x00, 0x01, 0x02, .., 0xFC, crc_lo,
crc_hi]`, where `tweak` is the smallest value in `0x00..=0xFF` for which
neither CRC-16 byte of the whole preceding sequence is zero. In the
committed file `tweak = 0x00`, giving 252 ascending pattern bytes + 2 CRC
bytes = 254 zero-free trailing bytes after the `0x00` at raw offset 3.

## Negative vectors

Error classes are the cross-language contract; each implementation maps
them onto its own error type (Rust `cobs::Error::Malformed`,
`packet::Error::BadCrc`, `packet::Error::BadLength`; C#
`ProtocolException`).

| Vector | Error class | Purpose |
|---|---|---|
| `bad_crc` | `bad_crc` | Brightness packet whose payload byte was corrupted after the CRC was computed. COBS decodes cleanly; the CRC check must reject. |
| `truncated` | `cobs_malformed` | COBS group header promises 4 data bytes but only 2 are present before the delimiter. |
| `embedded_zero_garbage` | `cobs_malformed` | A `0x00` inside COBS group data, which a valid encoder never produces. |
| `wrong_length_known_type` | `bad_length` | Valid CRC, known type `Frame` (`0x02`), but a 5-byte payload instead of 3072. The typed layer must reject. |

## Resync vector

`resync.stream` — garbage bytes containing no delimiter, then a lone
`0x00`, then `hello.wire` and `brightness.wire` verbatim.

A decoder walking the stream must drop the garbage segment and recover
exactly two packets, in order: `Hello`, then `Brightness`.
