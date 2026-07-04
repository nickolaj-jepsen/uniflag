//! uniflag v2 firmware — a binary-protocol framebuffer device.
//!
//! The host (SimHub plugin or `uniflag-cli`) owns all state and rendering;
//! this firmware receives complete RGB888 frames over USB CDC and puts
//! them on the panel. Wire contract: `docs/protocol.md` (COBS framing,
//! CRC-16, Hello/HelloAck handshake, 30 fps stream-as-heartbeat).
//!
//! Topology — three spawned tasks plus three futures joined in `main`:
//!
//! - **`runtime_task`** (`runtime.rs`) owns the [`Display`]: blits streamed
//!   frames, applies host brightness, and paints the local fallback / test
//!   screens (`screens.rs`).
//! - **`buttons::run`** polls the three panel buttons and queues
//!   `ButtonEvent`s for the TX future (long presses also toggle the local
//!   test screen).
//! - **`watchdog_feed`** feeds the RP2040 watchdog every 2 s (8 s timeout);
//!   the panic handler relies on it — on panic we spin, the feed stops,
//!   and the chip resets within `WATCHDOG_TIMEOUT`.
//! - **`run_usb`**, **`cdc_rx_loop`**, and **`cdc_tx_loop`** are awaited in
//!   `main` via `join3` rather than spawned as tasks, dodging the `'static`
//!   lifetime gymnastics on `Receiver` / `Sender` / `UsbDevice`.
//!
//! Data plumbing:
//! - RX dispatch: `Frame` → static two-slot zero-copy channel to the
//!   runtime; `Brightness` → [`BRIGHTNESS_SIGNAL`]; `Hello` → a `HelloAck`
//!   queued on [`TX_CHANNEL`]. Malformed input (bad COBS, bad CRC, wrong
//!   length, oversized accumulation) is dropped silently and the stream
//!   realigns at the next `0x00` delimiter; unknown packet types are
//!   ignored (forward compat).
//! - TX: [`TX_CHANNEL`] carries `HelloAck` + `ButtonEvent` to the CDC
//!   sender. ButtonEvent queueing is gated on [`BUTTON_REPORTING`] so
//!   presses made with no live host never flush ahead of a later HelloAck.

#![no_std]
#![no_main]

mod buttons;
mod display;
mod runtime;
mod screens;

use core::sync::atomic::{AtomicBool, Ordering};

use embassy_executor::Spawner;
use embassy_futures::join::join3;
use embassy_rp::peripherals::USB;
use embassy_rp::usb::Driver;
use embassy_rp::watchdog::Watchdog;
use embassy_sync::blocking_mutex::raw::CriticalSectionRawMutex;
use embassy_sync::channel::Channel;
use embassy_sync::signal::Signal;
use embassy_sync::zerocopy_channel;
use embassy_time::{Duration, Timer};
use embassy_usb::class::cdc_acm::{CdcAcmClass, Receiver, Sender, State as CdcState};
use embassy_usb::driver::EndpointError;
use embassy_usb::Builder;
use heapless::Vec as HVec;
use static_cell::StaticCell;

use proto::cobs;
use proto::packet::{self, Packet, Parsed};

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    // No reporting channel; the watchdog resets us within WATCHDOG_TIMEOUT.
    loop {
        cortex_m::asm::wfi();
    }
}

use display::{Display, DisplayPins, PioIrqs};

embassy_rp::bind_interrupts!(struct UsbIrqs {
    USBCTRL_IRQ => embassy_rp::usb::InterruptHandler<USB>;
});

/// One decoded `Frame` payload: RGB888, row-major from the top-left.
pub type FrameBuf = [u8; packet::FRAME_PAYLOAD_LEN];

/// Two slots: the RX loop fills one while the runtime blits the other.
/// Zero-copy — a `Signal<FrameBuf>` would memcpy 3 KB per hop.
static FRAME_SLOTS: StaticCell<[FrameBuf; 2]> = StaticCell::new();
static FRAME_CHANNEL: StaticCell<
    zerocopy_channel::Channel<'static, CriticalSectionRawMutex, FrameBuf>,
> = StaticCell::new();

/// Latest host-commanded brightness (`Brightness` packet payload).
/// A `Signal`, not a queue — only the newest value matters.
static BRIGHTNESS_SIGNAL: Signal<CriticalSectionRawMutex, u8> = Signal::new();

/// Device→host packets awaiting the CDC TX future.
pub enum TxEvent {
    /// Reply to a `Hello`. The payload is constant for a given build, so
    /// the event carries nothing; `cdc_tx_loop` assembles the packet.
    HelloAck,
    /// Debounced button press, classified on release
    /// (`proto::packet::Button` / `PressKind` byte values).
    Button { button: u8, kind: u8 },
}

/// TX queue. HelloAck is queued at most once per received Hello and
/// button events are human-paced, so 8 slots is generous.
pub type TxChannel = Channel<CriticalSectionRawMutex, TxEvent, 8>;
static TX_CHANNEL: TxChannel = Channel::new();

/// Gates ButtonEvent queueing on a live, handshaken host. Armed by a
/// `Hello` (right after its `HelloAck` is queued, so any event stays
/// behind the ack in the FIFO); disarmed when the host goes away — USB
/// endpoint disable here, or stream silence past `CONNECT_TIMEOUT` in
/// `runtime.rs`. Without the gate, presses made with nobody reading
/// (`Sender::wait_connection` fires at USB enumeration, not COM open)
/// would queue up and flush ahead of the next session's HelloAck,
/// violating docs/protocol.md §Handshake, which sequences ButtonEvent
/// strictly after HelloAck.
pub static BUTTON_REPORTING: AtomicBool = AtomicBool::new(false);

/// Long-press notifications from the buttons task to the runtime (each
/// message toggles the local test screen).
pub type TestToggleChannel = Channel<CriticalSectionRawMutex, (), 4>;
static TEST_TOGGLE: TestToggleChannel = Channel::new();

/// Firmware version reported in `HelloAck`. The encoder caps the tail at
/// `MAX_FW_VERSION_LEN`; assert at compile time so a future version bump
/// can't silently truncate or break the ack.
const FW_VERSION: &str = env!("CARGO_PKG_VERSION");
const _: () = assert!(FW_VERSION.len() <= packet::MAX_FW_VERSION_LEN);

/// Watchdog timeout. Just under the RP2040 ~8.39 s ceiling (errata E1
/// limits the load value to 0xFFFFFF / 2 µs).
const WATCHDOG_TIMEOUT: Duration = Duration::from_secs(8);
/// Feed cadence. 4× margin against the timeout — comfortable headroom
/// for blocking work that briefly stalls the executor.
const WATCHDOG_FEED_INTERVAL: Duration = Duration::from_secs(2);

#[embassy_executor::main]
async fn main(spawner: Spawner) {
    let p = embassy_rp::init(Default::default());

    // ------------------------------------------------------------------
    // Watchdog
    // ------------------------------------------------------------------
    // Set up first so a hang anywhere downstream (display init, USB
    // bring-up, the runtime itself) eventually triggers a chip reset
    // instead of a permanently-frozen panel. The feed task is spawned
    // further down once the executor is up.
    let mut watchdog = Watchdog::new(p.WATCHDOG);
    watchdog.start(WATCHDOG_TIMEOUT);

    // ------------------------------------------------------------------
    // Display
    // ------------------------------------------------------------------
    let display = Display::new(
        p.PIO0,
        p.DMA_CH0,
        p.DMA_CH1,
        PioIrqs,
        DisplayPins {
            column_clock: p.PIN_13,
            column_data: p.PIN_14,
            column_latch: p.PIN_15,
            column_blank: p.PIN_16,
            row_bit_0: p.PIN_17,
            row_bit_1: p.PIN_18,
            row_bit_2: p.PIN_19,
            row_bit_3: p.PIN_20,
        },
    );

    // ------------------------------------------------------------------
    // USB device + CDC ACM
    // ------------------------------------------------------------------
    let driver = Driver::new(p.USB, UsbIrqs);

    let mut config = embassy_usb::Config::new(packet::USB_VID, packet::USB_PID);
    config.manufacturer = Some("uniflag");
    config.product = Some("uniflag");
    config.serial_number = Some("0001");
    config.max_power = 100;
    config.max_packet_size_0 = 64;

    static CONFIG_DESC: StaticCell<[u8; 256]> = StaticCell::new();
    static BOS_DESC: StaticCell<[u8; 256]> = StaticCell::new();
    static MSOS_DESC: StaticCell<[u8; 256]> = StaticCell::new();
    static CONTROL_BUF: StaticCell<[u8; 64]> = StaticCell::new();
    static CDC_STATE: StaticCell<CdcState> = StaticCell::new();

    let config_desc = CONFIG_DESC.init([0; 256]);
    let bos_desc = BOS_DESC.init([0; 256]);
    let msos_desc = MSOS_DESC.init([0; 256]);
    let control_buf = CONTROL_BUF.init([0; 64]);
    let cdc_state = CDC_STATE.init(CdcState::new());

    let mut builder = Builder::new(
        driver,
        config,
        config_desc,
        bos_desc,
        msos_desc,
        control_buf,
    );

    let class = CdcAcmClass::new(&mut builder, cdc_state, 64);
    let (sender, receiver) = class.split();
    let usb = builder.build();

    // ------------------------------------------------------------------
    // Frame double-buffer channel + workers
    // ------------------------------------------------------------------
    let slots = FRAME_SLOTS.init([[0; packet::FRAME_PAYLOAD_LEN]; 2]);
    let channel = FRAME_CHANNEL.init(zerocopy_channel::Channel::new(slots));
    let (frame_tx, frame_rx) = channel.split();

    spawner.spawn(runtime_task(display, frame_rx).expect("spawn runtime task"));
    spawner.spawn(
        buttons::run(p.PIN_21, p.PIN_26, p.PIN_27, &TX_CHANNEL, &TEST_TOGGLE)
            .expect("spawn buttons task"),
    );
    spawner.spawn(watchdog_feed(watchdog).expect("spawn watchdog feed task"));

    // The remaining three futures borrow `'static` resources but aren't
    // tasks (they're awaited here in `main`'s top-level `join3`). Doing it
    // this way avoids the lifetime gymnastics of moving `Receiver` /
    // `Sender` / `UsbDevice` into `#[embassy_executor::task]` functions —
    // which works, but adds noise.
    let usb_fut = run_usb(usb);
    let rx_fut = cdc_rx_loop(receiver, frame_tx);
    let tx_fut = cdc_tx_loop(sender, &TX_CHANNEL);
    join3(usb_fut, rx_fut, tx_fut).await;
}

// =============================================================================
// USB / CDC futures
// =============================================================================

async fn run_usb(mut usb: embassy_usb::UsbDevice<'static, Driver<'static, USB>>) {
    usb.run().await;
}

/// Accumulate COBS-framed bytes; on each `0x00` delimiter decode, verify,
/// and dispatch. Any malformed packet, CRC failure, oversized
/// accumulation, or mid-frame disconnect ends with the accumulator
/// cleared and the stream realigned at the next delimiter — that is the
/// whole resync story.
async fn cdc_rx_loop(
    mut rx: Receiver<'static, Driver<'static, USB>>,
    mut frames: zerocopy_channel::Sender<'static, CriticalSectionRawMutex, FrameBuf>,
) -> ! {
    // Encoded packet without its delimiter; MAX_WIRE_LEN includes the
    // delimiter byte so this has one byte of slack.
    let mut accum: HVec<u8, { packet::MAX_WIRE_LEN }> = HVec::new();
    let mut raw = [0u8; packet::MAX_RAW_LEN];
    let mut overflow = false;
    let mut buf = [0u8; 64];

    loop {
        rx.wait_connection().await;

        loop {
            match rx.read_packet(&mut buf).await {
                Ok(n) => {
                    for &byte in &buf[..n] {
                        if byte == 0x00 {
                            // Back-to-back delimiters are legal no-ops.
                            if !overflow && !accum.is_empty() {
                                dispatch(&accum, &mut raw, &mut frames).await;
                            }
                            accum.clear();
                            overflow = false;
                        } else if !overflow && accum.push(byte).is_err() {
                            // Oversized: drop everything until the next
                            // delimiter realigns us.
                            accum.clear();
                            overflow = true;
                        }
                    }
                }
                Err(EndpointError::BufferOverflow) => {}
                Err(EndpointError::Disabled) => {
                    accum.clear();
                    overflow = false;
                    // USB reset/detach ends the session; the next host
                    // re-arms with its Hello.
                    BUTTON_REPORTING.store(false, Ordering::Relaxed);
                    break;
                }
            }
        }
    }
}

/// Decode and dispatch one delimiter-to-delimiter span. Every failure
/// mode (COBS, CRC, wrong length for a known type) is a silent drop;
/// unknown packet types are ignored (forward compat) — exactly the
/// receiver posture `docs/protocol.md` §Framing specifies.
async fn dispatch(
    encoded: &[u8],
    raw: &mut [u8; packet::MAX_RAW_LEN],
    frames: &mut zerocopy_channel::Sender<'static, CriticalSectionRawMutex, FrameBuf>,
) {
    let Ok(raw_len) = cobs::decode(encoded, raw) else {
        return;
    };
    let Ok(parsed) = packet::parse_packet(&raw[..raw_len]) else {
        return;
    };
    match parsed {
        Parsed::Known(Packet::Hello { .. }) => {
            // A Hello opens a fresh session: drop anything queued for a
            // previous one so stale ButtonEvents can't flush ahead of
            // this session's HelloAck (docs/protocol.md §Handshake).
            // Dropping a not-yet-sent HelloAck is fine too — its payload
            // is identical for every Hello of a given build.
            TX_CHANNEL.clear();
            // Always ack, whatever version the host claims — version
            // policy (refuse-with-message on mismatch) is the host's job.
            TX_CHANNEL.send(TxEvent::HelloAck).await;
            // Arm button reporting only now that the ack is queued: the
            // FIFO keeps any subsequent ButtonEvent behind it.
            BUTTON_REPORTING.store(true, Ordering::Relaxed);
        }
        Parsed::Known(Packet::Frame { pixels }) => {
            let slot = frames.send().await;
            slot.copy_from_slice(pixels);
            frames.send_done();
        }
        Parsed::Known(Packet::Brightness { value }) => {
            BRIGHTNESS_SIGNAL.signal(value);
        }
        // Device→host types arriving inbound are protocol misuse; drop.
        Parsed::Known(Packet::HelloAck { .. } | Packet::ButtonEvent { .. }) => {}
        // Unassigned type bytes: ignore, never an error.
        Parsed::Unknown { .. } => {}
    }
}

/// Drain device→host packets ([`TxEvent`]) into the CDC sender. On
/// disconnect the current packet is abandoned and we go back to waiting;
/// a lost HelloAck is fine because the host re-opens with Hello on every
/// (re)connect.
async fn cdc_tx_loop(
    mut tx: Sender<'static, Driver<'static, USB>>,
    events: &'static TxChannel,
) -> ! {
    // Largest device→host packet is a HelloAck: 1 type + 3 header +
    // ≤ MAX_FW_VERSION_LEN payload + 2 CRC = 38 raw bytes, ≤ 40 on the
    // wire with COBS + delimiter. 64 gives slack either way.
    let mut scratch = [0u8; 64];
    let mut wire = [0u8; 64];

    loop {
        tx.wait_connection().await;

        'connected: loop {
            let pkt = match events.receive().await {
                TxEvent::HelloAck => Packet::HelloAck {
                    protocol_version: packet::PROTOCOL_VERSION,
                    width: display::WIDTH as u8,
                    height: display::HEIGHT as u8,
                    fw_version: FW_VERSION.as_bytes(),
                },
                TxEvent::Button { button, kind } => Packet::ButtonEvent { button, kind },
            };
            // Can't fail for the packets above (fw_version length is
            // compile-time checked); drop rather than unwrap if it
            // somehow does.
            let Ok(wire_len) = pkt.encode(&mut scratch, &mut wire) else {
                continue;
            };
            // Chunked to the CDC packet size. Today's packets fit one
            // chunk; the loop keeps this correct if one ever doesn't.
            let mut sent = 0;
            while sent < wire_len {
                let chunk = (wire_len - sent).min(64);
                match tx.write_packet(&wire[sent..sent + chunk]).await {
                    Ok(()) => sent += chunk,
                    Err(EndpointError::BufferOverflow) | Err(EndpointError::Disabled) => {
                        break 'connected;
                    }
                }
            }
        }
    }
}

// =============================================================================
// Runtime task
// =============================================================================

#[embassy_executor::task]
async fn runtime_task(
    display: Display,
    frames: zerocopy_channel::Receiver<'static, CriticalSectionRawMutex, FrameBuf>,
) -> ! {
    runtime::run(display, frames, &BRIGHTNESS_SIGNAL, &TEST_TOGGLE).await
}

// =============================================================================
// Watchdog feed task
// =============================================================================

// Passive liveness check: if the executor or any task it cooperates with
// wedges, this timer stops firing and the chip resets. Deliberately not
// gated on a "liveness signal" from other tasks — at this code size, the
// extra plumbing buys nothing.
#[embassy_executor::task]
async fn watchdog_feed(mut wd: Watchdog) -> ! {
    loop {
        Timer::after(WATCHDOG_FEED_INTERVAL).await;
        wd.feed(WATCHDOG_TIMEOUT);
    }
}
