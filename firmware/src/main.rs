#![no_std]
#![no_main]

mod buttons;
mod display;
mod render;

use defmt_rtt as _;
use embassy_executor::Spawner;
use embassy_futures::join::join;
use embassy_rp::peripherals::USB;
use embassy_rp::usb::Driver;
use embassy_rp::watchdog::{ResetReason, Watchdog};
use embassy_sync::blocking_mutex::raw::CriticalSectionRawMutex;
use embassy_sync::signal::Signal;
use embassy_time::{Duration, Timer};
use embassy_usb::class::cdc_acm::{CdcAcmClass, Receiver, State as CdcState};
use embassy_usb::driver::EndpointError;
use embassy_usb::Builder;
use heapless::Vec as HVec;
use panic_probe as _;
use static_cell::StaticCell;

use buttons::BrightnessChannel;
use display::{Display, DisplayPins, PioIrqs};

embassy_rp::bind_interrupts!(struct UsbIrqs {
    USBCTRL_IRQ => embassy_rp::usb::InterruptHandler<USB>;
});

/// Latest decoded state from the host. Updated whenever the CDC RX path
/// successfully parses a line; observed by the render task.
static STATE_SIGNAL: Signal<CriticalSectionRawMutex, proto::State> = Signal::new();

/// Brightness-button events from the buttons task to the render task.
static BRIGHTNESS_CHAN: BrightnessChannel = BrightnessChannel::new();

/// Maximum line length we'll accept from the host. proto::MAX_LINE_LEN is
/// the *formatted* length; allow a bit of slack for non-canonical input.
const MAX_LINE_BYTES: usize = 64;

/// Watchdog timeout. Just under the RP2040 ~8.39 s ceiling (errata E1
/// limits the load value to 0xFFFFFF / 2 µs).
const WATCHDOG_TIMEOUT: Duration = Duration::from_secs(8);
/// Feed cadence. 4× margin against the timeout — comfortable headroom
/// for blocking work that briefly stalls the executor (flash erases,
/// long PIO operations).
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
    match watchdog.reset_reason() {
        Some(ResetReason::TimedOut) => defmt::warn!("boot: watchdog timeout reset"),
        Some(ResetReason::Forced) => defmt::warn!("boot: forced reset"),
        None => defmt::info!("boot: clean (cold or BOOTSEL)"),
    }
    // Don't reset while halted under probe-rs.
    watchdog.pause_on_debug(true);
    watchdog.start(WATCHDOG_TIMEOUT);

    defmt::info!("uniflag boot");

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

    let mut config = embassy_usb::Config::new(0x1209, 0x0001);
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
    let (_sender, receiver) = class.split();
    let usb = builder.build();

    // ------------------------------------------------------------------
    // Spawn workers
    // ------------------------------------------------------------------
    spawner.spawn(render_task(display).expect("spawn render task"));
    spawner.spawn(
        buttons::run(p.PIN_21, p.PIN_26, p.PIN_27, &BRIGHTNESS_CHAN).expect("spawn buttons task"),
    );
    spawner.spawn(watchdog_feed(watchdog).expect("spawn watchdog feed task"));

    // The remaining two futures borrow `'static` resources but aren't tasks
    // (they're awaited here in `main`'s top-level `join`). Doing it this way
    // avoids the lifetime gymnastics of moving `Receiver<'static, _>` and
    // `UsbDevice<'static, _>` into `#[embassy_executor::task]` functions —
    // which works, but adds noise.
    let usb_fut = run_usb(usb);
    let rx_fut = cdc_rx_loop(receiver);
    join(usb_fut, rx_fut).await;
}

// =============================================================================
// USB / CDC tasks
// =============================================================================

async fn run_usb(mut usb: embassy_usb::UsbDevice<'static, Driver<'static, USB>>) {
    usb.run().await;
}

async fn cdc_rx_loop(mut rx: Receiver<'static, Driver<'static, USB>>) -> ! {
    let mut accum: HVec<u8, MAX_LINE_BYTES> = HVec::new();
    let mut buf = [0u8; 64];

    loop {
        rx.wait_connection().await;
        defmt::info!("usb: host connected");

        loop {
            match rx.read_packet(&mut buf).await {
                Ok(n) => {
                    for &byte in &buf[..n] {
                        if byte == b'\n' {
                            handle_line(&accum);
                            accum.clear();
                        } else if byte == b'\r' {
                            // ignore — `proto::State::parse` also tolerates these
                        } else if accum.push(byte).is_err() {
                            // line too long; drop it and resync at next \n
                            defmt::warn!("usb: line buffer overflow, dropping");
                            accum.clear();
                        }
                    }
                }
                Err(EndpointError::BufferOverflow) => {
                    defmt::warn!("usb: endpoint overflow");
                }
                Err(EndpointError::Disabled) => {
                    defmt::info!("usb: endpoint disabled (host disconnect?)");
                    accum.clear();
                    break;
                }
            }
        }
    }
}

fn handle_line(line: &[u8]) {
    match proto::State::parse(line) {
        Ok(state) => {
            STATE_SIGNAL.signal(state);
        }
        Err(e) => {
            defmt::warn!("usb: parse error: {:?}", defmt::Debug2Format(&e));
        }
    }
}

// =============================================================================
// Render task
// =============================================================================

#[embassy_executor::task]
async fn render_task(display: Display) -> ! {
    render::run(display, &STATE_SIGNAL, &BRIGHTNESS_CHAN).await
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
