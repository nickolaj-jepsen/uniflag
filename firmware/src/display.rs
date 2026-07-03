//! Cosmic Unicorn display driver.
//!
//! The Cosmic Unicorn is **not** a HUB75 panel. It uses a custom column
//! shift-register topology with a 4-to-16 row decoder; see
//! `docs/cosmic-unicorn-hardware.md` for full details. The driver here
//! mirrors `pimoroni-pico/libraries/cosmic_unicorn/`:
//!
//! - A single PIO state machine on `PIO0` consumes a fixed-size bitstream
//!   and shifts it out to the panel's column drivers, latching once per
//!   scan row and pulsing OE for a configurable number of ticks (BCM
//!   greyscale).
//! - A self-chaining DMA pair feeds the bitstream into the PIO TX FIFO
//!   forever — once started, the panel refreshes itself with zero CPU
//!   overhead. The CPU only touches the bitstream to update pixels.
//!
//! Two bitstreams are kept in SRAM: the `front` is the one DMA is currently
//! reading, and the `back` is what `set_pixel` / `fill` write into. The
//! renderer paints a complete frame into `back`, then calls `present()`,
//! which atomically retargets the DMA chain at the just-painted buffer and
//! waits for the swap to take effect before returning. Without this, a
//! mid-paint refresh sees a partially-updated bitstream and individual LEDs
//! flicker — most visibly on full-panel changes like brightness adjustments.
//!
//! Bitstream layout, per scan row (16 rows) × BCD frame (14 frames):
//!
//! | Offset  | Bytes | Meaning                                       |
//! |---------|-------|-----------------------------------------------|
//! | 0       | 1     | pixel count − 1 (= 63)                        |
//! | 1       | 1     | row select bits 0..3 in the low nibble        |
//! | 2..66   | 64    | 64 pixel bytes (`xxxxx_bgr`)                  |
//! | 66..68  | 2     | dword-alignment padding                       |
//! | 68..72  | 4     | 32-bit BCD tick count (LE)                    |
//!
//! 16 × 14 × 72 = **16128 bytes** total.
//!
//! BCM (binary code modulation): for each pixel + colour, the gamma-
//! corrected 14-bit intensity is split bit-wise across 14 BCD frames.
//! Frame *n* contributes weight 2ⁿ; the per-frame tick count doubles each
//! frame. The PIO holds OE high for `(1 << frame)` clock cycles per frame.

use core::sync::atomic::{AtomicU32, Ordering};

use embassy_rp::gpio::{Level, Output};
use embassy_rp::pac;
use embassy_rp::peripherals::{
    DMA_CH0, DMA_CH1, PIN_13, PIN_14, PIN_15, PIN_16, PIN_17, PIN_18, PIN_19, PIN_20, PIO0,
};
use embassy_rp::pio::{
    Common, Config, Direction, FifoJoin, InterruptHandler, Pio, ShiftConfig, ShiftDirection,
    StateMachine,
};
use embassy_rp::Peri;
use embassy_time::Duration;
use static_cell::ConstStaticCell;

// =============================================================================
// Public constants
// =============================================================================

pub const WIDTH: usize = 32;
pub const HEIGHT: usize = 32;

// =============================================================================
// Bitstream shape (private)
// =============================================================================

const ROW_COUNT: usize = 16;
const BCD_FRAME_COUNT: usize = 14;
const BCD_FRAME_BYTES: usize = 72;
const ROW_BYTES: usize = BCD_FRAME_COUNT * BCD_FRAME_BYTES;
const BITSTREAM_LENGTH: usize = ROW_COUNT * ROW_BYTES;
const PIXEL_COUNT_PER_SCAN: u8 = 64;

// DMA channel assignments. Must match the `Peri` indices we receive in
// `Display::new`. Both the chain setup and `present()` reach for them.
const CH_DATA: usize = 0;
const CH_CTRL: usize = 1;

// =============================================================================
// Storage: bitstream + DMA-readable pointer (both static)
// =============================================================================

/// 4-byte aligned wrapper around the bitstream array. The DMA performs
/// word-sized transfers from this address; bare `[u8; N]` only guarantees
/// 1-byte alignment, which produces silent garbage. Upstream Pimoroni does
/// the same with `alignas(4)`.
#[repr(align(4))]
struct Bitstream([u8; BITSTREAM_LENGTH]);

impl core::ops::Deref for Bitstream {
    type Target = [u8; BITSTREAM_LENGTH];
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}
impl core::ops::DerefMut for Bitstream {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}

// The bitstreams live in BSS; DMA reads them directly. ConstStaticCell
// ensures each can be `take()`n exactly once. Two buffers for double
// buffering: at any moment one is being scanned out by DMA (the *front*),
// the other is the *back* that the CPU paints into.
static BITSTREAM_A: ConstStaticCell<Bitstream> =
    ConstStaticCell::new(Bitstream([0u8; BITSTREAM_LENGTH]));
static BITSTREAM_B: ConstStaticCell<Bitstream> =
    ConstStaticCell::new(Bitstream([0u8; BITSTREAM_LENGTH]));

// Stable storage for the bitstream's address. The control DMA channel reads
// this u32 once per restart and writes it back into the data DMA channel's
// read_addr_trig — that's how we get "infinite repeat" out of a finite
// transfer length.
static BITSTREAM_PTR: AtomicU32 = AtomicU32::new(0);

// =============================================================================
// PIO interrupt binding
// =============================================================================

embassy_rp::bind_interrupts!(pub struct PioIrqs {
    PIO0_IRQ_0 => InterruptHandler<PIO0>;
});

// =============================================================================
// Pin grouping
// =============================================================================

pub struct DisplayPins {
    pub column_clock: Peri<'static, PIN_13>,
    pub column_data: Peri<'static, PIN_14>,
    pub column_latch: Peri<'static, PIN_15>,
    pub column_blank: Peri<'static, PIN_16>,
    pub row_bit_0: Peri<'static, PIN_17>,
    pub row_bit_1: Peri<'static, PIN_18>,
    pub row_bit_2: Peri<'static, PIN_19>,
    pub row_bit_3: Peri<'static, PIN_20>,
}

// =============================================================================
// Display driver
// =============================================================================

pub struct Display {
    /// Buffer the CPU paints into. After a frame is finished, `present()`
    /// swaps `back` and `front` and republishes `BITSTREAM_PTR`.
    back: &'static mut Bitstream,
    /// Buffer DMA is currently reading from. Held only so the static
    /// lifetime is consumed; we don't read it through this reference.
    front: &'static mut Bitstream,
    /// 0..=256. Applied as `(channel * brightness) >> 8` before gamma.
    /// 256 == passthrough at full intensity.
    brightness: u16,
    // Keep the SM alive so it doesn't get dropped (which would stop it).
    _sm: StateMachine<'static, PIO0, 0>,
    _common: Common<'static, PIO0>,
}

impl Display {
    pub fn new(
        pio: Peri<'static, PIO0>,
        _dma_data: Peri<'static, DMA_CH0>,
        _dma_ctrl: Peri<'static, DMA_CH1>,
        irqs: PioIrqs,
        pins: DisplayPins,
    ) -> Self {
        // 1. Initialise both bitstreams' framing bytes (pixel count, row
        //    select, BCD ticks). Pixel data starts at zero in both — panel
        //    dark until the renderer's first paint+present.
        let front = BITSTREAM_A.take();
        let back = BITSTREAM_B.take();
        init_bitstream_framing(front);
        init_bitstream_framing(back);

        // 2. Publish the front buffer's address so the control DMA channel
        //    can find it. The wrapper struct guarantees this is 4-aligned.
        let front_addr = front.0.as_ptr() as u32;
        debug_assert_eq!(front_addr & 0b11, 0, "bitstream must be 4-byte aligned");
        debug_assert_eq!(
            back.0.as_ptr() as u32 & 0b11,
            0,
            "bitstream must be 4-byte aligned",
        );
        BITSTREAM_PTR.store(front_addr, Ordering::Relaxed);

        // 3. Bring up the column-driver chips by bit-banging their config
        //    register. Without this, the chips don't drive the LEDs at full
        //    current.
        let DisplayPins {
            column_clock,
            column_data,
            column_latch,
            column_blank,
            row_bit_0,
            row_bit_1,
            row_bit_2,
            row_bit_3,
        } = pins;

        let (column_clock, column_data, column_latch, column_blank) =
            configure_column_drivers(column_clock, column_data, column_latch, column_blank);

        // 4. Hand the eight panel pins to PIO0 SM0 and load the bitstream
        //    program.
        let pio = Pio::new(pio, irqs);
        let Pio {
            mut common,
            mut sm0,
            ..
        } = pio;

        let clock_pin = common.make_pio_pin(column_clock);
        let data_pin = common.make_pio_pin(column_data);
        let latch_pin = common.make_pio_pin(column_latch);
        let blank_pin = common.make_pio_pin(column_blank);
        let row0 = common.make_pio_pin(row_bit_0);
        let row1 = common.make_pio_pin(row_bit_1);
        let row2 = common.make_pio_pin(row_bit_2);
        let row3 = common.make_pio_pin(row_bit_3);

        let prg = pio::pio_asm!(
            ".side_set 1 opt",
            ".wrap_target",
            // Pull the pixel count - 1 (= 63) and the row select byte.
            "    out y, 8",
            "    out pins, 8",
            "pixels:",
            // Bit 0 of pixel byte (panel bit-position 0 → BLUE per upstream).
            "    out x, 1   side 0  [1]",
            "    set pins, 0b100",
            "    jmp !x endb",
            "    set pins, 0b101",
            "endb:",
            "    nop        side 1 [2]",
            // Bit 1 (GREEN).
            "    out x, 1   side 0 [1]",
            "    set pins, 0b100",
            "    jmp !x endg",
            "    set pins, 0b101",
            "endg:",
            "    nop        side 1 [2]",
            // Bit 2 (RED).
            "    out x, 1   side 0  [1]",
            "    set pins, 0b100",
            "    jmp !x endr",
            "    set pins, 0b101",
            "endr:",
            // Discard the 5 unused bits in the pixel byte.
            "    out null, 5  side 1 [2]",
            "    jmp y-- pixels",
            // 64 pixels done; discard 2 padding bytes that align us to dword.
            "    out null, 16",
            // Latch the shift register, briefly enable outputs.
            "    set pins, 0b110 [5]",
            "    set pins, 0b000",
            // BCD delay: pull tick count and busy-wait it down.
            "    out y, 32",
            "bcd_delay:",
            "    jmp y-- bcd_delay",
            // Disable outputs again, ready for the next BCD frame.
            "    set pins, 0b100",
            ".wrap",
        );

        let loaded = common.load_program(&prg.program);

        let mut cfg = Config::default();
        cfg.use_program(&loaded, &[&clock_pin]);
        cfg.set_set_pins(&[&data_pin, &latch_pin, &blank_pin]);
        cfg.set_out_pins(&[&row0, &row1, &row2, &row3]);
        cfg.shift_out = ShiftConfig {
            threshold: 32,
            direction: ShiftDirection::Right,
            auto_fill: true,
        };
        cfg.fifo_join = FifoJoin::TxOnly;
        // Default clock_divider = 1 (full system clock, 125 MHz on RP2040).
        // Upstream doesn't override; we don't either.

        sm0.set_config(&cfg);

        // Start with BLANK and ROW_BIT_* high so the panel is invisible
        // during the moment between configure and SM-enable.
        sm0.set_pins(Level::High, &[&blank_pin, &row0, &row1, &row2, &row3]);
        sm0.set_pin_dirs(
            Direction::Out,
            &[
                &clock_pin, &data_pin, &latch_pin, &blank_pin, &row0, &row1, &row2, &row3,
            ],
        );

        // 5. Configure the self-chaining DMA pair.
        setup_dma_chain(sm0.tx_fifo_ptr() as u32);

        // 6. Enable SM and start the chain by triggering the control channel.
        sm0.set_enable(true);
        start_dma_chain();

        Display {
            back,
            front,
            // Boot default: full brightness (multiplier 256 = unity),
            // deliberately. The runtime re-applies the host value before
            // every streamed blit and pins the local screens at full
            // brightness (runtime.rs), so this default only covers the
            // boot fallback paint — where full matters: the fallback's
            // sole lit pixel is already the dim literal (40, 14, 0), and
            // scaling it further would risk making "device alive, no
            // host" invisible.
            brightness: 256,
            _sm: sm0,
            _common: common,
        }
    }

    /// Set the brightness multiplier. 0..=255 (255 = full).
    /// Applied to `r/g/b` *before* gamma correction, matching the wire
    /// contract for the Brightness packet: `(c * (value + 1)) >> 8`
    /// (docs/protocol.md §Payload layouts).
    pub fn set_brightness(&mut self, value: u8) {
        // Upstream stores 0..=256 ((value+1) gives 256 at max so unity).
        self.brightness = value as u16 + 1;
    }

    /// Write a pixel. Coordinates are panel-logical: `(0,0)` is the
    /// top-left corner.
    pub fn set_pixel(&mut self, x: i32, y: i32, r: u8, g: u8, b: u8) {
        if !(0..WIDTH as i32).contains(&x) || !(0..HEIGHT as i32).contains(&y) {
            return;
        }

        // Panel coordinate remap. The Cosmic Unicorn panel is wired so the
        // top half (logical y < 16) lives in columns 32..63 of the
        // bitstream's pixel area, and the bottom half in columns 0..31.
        // Both x and y are mirrored.
        let mut mx = (WIDTH as i32 - 1) - x;
        let mut my = (HEIGHT as i32 - 1) - y;
        if my < 16 {
            mx += 32;
        } else {
            my -= 16;
        }

        let r = ((r as u16) * self.brightness) >> 8;
        let g = ((g as u16) * self.brightness) >> 8;
        let b = ((b as u16) * self.brightness) >> 8;
        let mut gamma_r = GAMMA_14BIT[r as u8 as usize];
        let mut gamma_g = GAMMA_14BIT[g as u8 as usize];
        let mut gamma_b = GAMMA_14BIT[b as u8 as usize];

        // Stamp one bit per BCD frame. Bit-position layout per upstream:
        //  bit 0 → BLUE, bit 1 → GREEN, bit 2 → RED.
        for frame in 0..BCD_FRAME_COUNT {
            let off = (my as usize) * ROW_BYTES + frame * BCD_FRAME_BYTES + 2 + (mx as usize);
            let r_bit = (gamma_r & 1) as u8;
            let g_bit = (gamma_g & 1) as u8;
            let b_bit = (gamma_b & 1) as u8;
            self.back[off] = b_bit | (g_bit << 1) | (r_bit << 2);
            gamma_r >>= 1;
            gamma_g >>= 1;
            gamma_b >>= 1;
        }
    }

    /// Blit one wire-format frame into the back buffer: RGB888, row-major
    /// from the top-left, 3 bytes per pixel — exactly the `Frame` packet
    /// payload (docs/protocol.md §Payload layouts). Layered on
    /// [`Self::set_pixel`], so the brightness multiplier and gamma apply
    /// per pixel. Call [`Self::present`] afterwards to show it.
    pub fn blit_rgb888(&mut self, rgb: &[u8; WIDTH * HEIGHT * 3]) {
        for y in 0..HEIGHT {
            for x in 0..WIDTH {
                let i = (y * WIDTH + x) * 3;
                self.set_pixel(x as i32, y as i32, rgb[i], rgb[i + 1], rgb[i + 2]);
            }
        }
    }
}

impl Display {
    /// Publish the just-painted `back` buffer to DMA, swap labels, and
    /// wait until the DMA chain has actually picked up the new pointer.
    ///
    /// The control channel only re-reads `BITSTREAM_PTR` at the end of
    /// each ~3.3 ms refresh cycle (300 fps), so for up to one cycle after
    /// the pointer write, the data channel is still scanning out the
    /// buffer that's about to become our new `back`. Returning before
    /// the swap takes effect would let the next paint race that
    /// in-progress refresh — exactly the tearing this function exists to
    /// prevent. We poll the data channel's `read_addr` until it falls
    /// inside the new front's range, yielding to the executor between
    /// checks so other tasks (USB rx, buttons) keep running.
    pub async fn present(&mut self) {
        core::mem::swap(&mut self.back, &mut self.front);
        let new_front_addr = self.front.0.as_ptr() as u32;
        BITSTREAM_PTR.store(new_front_addr, Ordering::Relaxed);

        let new_front_end = new_front_addr + BITSTREAM_LENGTH as u32;
        let dma = pac::DMA;
        loop {
            let ra = dma.ch(CH_DATA).read_addr().read();
            if (new_front_addr..new_front_end).contains(&ra) {
                return;
            }
            embassy_futures::yield_now().await;
        }
    }
}

// =============================================================================
// Bitstream framing init
// =============================================================================

fn init_bitstream_framing(bs: &mut Bitstream) {
    for row in 0..ROW_COUNT {
        for frame in 0..BCD_FRAME_COUNT {
            let off = row * ROW_BYTES + frame * BCD_FRAME_BYTES;
            bs[off] = PIXEL_COUNT_PER_SCAN - 1;
            bs[off + 1] = row as u8;
            // Pixel data (bytes 2..66) starts as zero — panel dark.
            // Padding at 66..68 stays zero.
            // BCD tick count: 1 << frame, little-endian. Doubles each frame.
            let ticks = 1u32 << frame;
            bs[off + 68..off + 72].copy_from_slice(&ticks.to_le_bytes());
        }
    }
}

// =============================================================================
// Column-driver bit-bang config
// =============================================================================

// Sends `0b1111111111001110` to each of the 12 column-driver chips. This
// loads the chip's configuration register (full output current). Done with
// raw GPIO before PIO takes over. Returns the four pin Peris back so PIO
// can claim them.
fn configure_column_drivers(
    mut column_clock: Peri<'static, PIN_13>,
    mut column_data: Peri<'static, PIN_14>,
    mut column_latch: Peri<'static, PIN_15>,
    mut column_blank: Peri<'static, PIN_16>,
) -> (
    Peri<'static, PIN_13>,
    Peri<'static, PIN_14>,
    Peri<'static, PIN_15>,
    Peri<'static, PIN_16>,
) {
    const REG1: u16 = 0b1111111111001110;

    {
        // Scoped: dropped at end so PIO can claim the pins.
        let mut clk = Output::new(column_clock.reborrow(), Level::Low);
        let mut data = Output::new(column_data.reborrow(), Level::Low);
        let mut latch = Output::new(column_latch.reborrow(), Level::Low);
        let mut blank = Output::new(column_blank.reborrow(), Level::High);

        // Settle a moment after power-up.
        embassy_time::block_for(Duration::from_millis(100));

        let pulse = Duration::from_micros(10);

        // Clock the register value into the first 11 chips.
        for _ in 0..11 {
            for i in 0..16 {
                if (REG1 & (1 << (15 - i))) != 0 {
                    data.set_high();
                } else {
                    data.set_low();
                }
                embassy_time::block_for(pulse);
                clk.set_high();
                embassy_time::block_for(pulse);
                clk.set_low();
            }
        }

        // 12th chip — pull LATCH high partway through the write.
        for i in 0..16 {
            if (REG1 & (1 << (15 - i))) != 0 {
                data.set_high();
            } else {
                data.set_low();
            }
            embassy_time::block_for(pulse);
            clk.set_high();
            embassy_time::block_for(pulse);
            clk.set_low();
            if i == 4 {
                latch.set_high();
            }
        }
        latch.set_low();

        // A brief blank pulse — upstream notes this kills a slight glow
        // left by the configuration sequence.
        blank.set_low();
        embassy_time::block_for(pulse);
        blank.set_high();
    }

    (column_clock, column_data, column_latch, column_blank)
}

// =============================================================================
// DMA chain (PAC-level — embassy-rp 0.10 has no high-level helper for this)
// =============================================================================

fn setup_dma_chain(pio_tx_fifo_addr: u32) {
    use pac::dma::regs::CtrlTrig;
    use pac::dma::vals::{DataSize, TreqSel};

    let dma = pac::DMA;

    // Data channel: BITSTREAM_LENGTH/4 words → PIO TX FIFO. read_addr will be
    // (re-)set by the control channel. Configure via al1_ctrl alias so this
    // write does NOT trigger the channel.
    dma.ch(CH_DATA).read_addr().write_value(0);
    dma.ch(CH_DATA).write_addr().write_value(pio_tx_fifo_addr);
    dma.ch(CH_DATA)
        .trans_count()
        .write_value((BITSTREAM_LENGTH / 4) as u32);

    let mut data_ctrl = CtrlTrig::default();
    data_ctrl.set_data_size(DataSize::SIZE_WORD);
    data_ctrl.set_incr_read(true);
    data_ctrl.set_incr_write(false);
    data_ctrl.set_treq_sel(TreqSel::PIO0_TX0);
    data_ctrl.set_chain_to(CH_CTRL as u8);
    data_ctrl.set_irq_quiet(true);
    data_ctrl.set_en(true);
    dma.ch(CH_DATA).al1_ctrl().write_value(data_ctrl.0);

    // Control channel: 1 word — copy bitstream pointer into the data
    // channel's PLAIN read_addr (no trigger). The data channel is then
    // restarted by the chain_to=CH_DATA below.
    //
    // Important: write to the *plain* read_addr, not al3_read_addr_trig.
    // The trig alias would atomically restart data; combined with chain_to
    // that creates a queued chain trigger that fires after data completes,
    // racing with the next ctrl-driven restart and producing visibly
    // wandering scan rows.
    dma.ch(CH_CTRL)
        .read_addr()
        .write_value(&BITSTREAM_PTR as *const _ as u32);
    dma.ch(CH_CTRL)
        .write_addr()
        .write_value(dma.ch(CH_DATA).read_addr().as_ptr() as u32);
    dma.ch(CH_CTRL).trans_count().write_value(1);

    let mut ctrl_ctrl = CtrlTrig::default();
    ctrl_ctrl.set_data_size(DataSize::SIZE_WORD);
    ctrl_ctrl.set_incr_read(false);
    ctrl_ctrl.set_incr_write(false);
    ctrl_ctrl.set_treq_sel(TreqSel::PERMANENT);
    ctrl_ctrl.set_chain_to(CH_DATA as u8);
    ctrl_ctrl.set_irq_quiet(true);
    ctrl_ctrl.set_en(true);
    dma.ch(CH_CTRL).al1_ctrl().write_value(ctrl_ctrl.0);
}

fn start_dma_chain() {
    // Trigger the control channel by writing 1 to its bit in
    // multi_chan_trigger. The control channel runs once, kicks the data
    // channel via al3_read_addr_trig, and from there the chain self-runs.
    pac::DMA
        .multi_chan_trigger()
        .write(|w| w.set_multi_chan_trigger(1 << 1));
}

// =============================================================================
// 14-bit gamma LUT (verbatim from pimoroni_common.hpp)
//
//   v = (uint16_t)(powf((float)(n) / 255.0f, 2.2) * 16383.0f + 0.5f)
// =============================================================================

const GAMMA_14BIT: [u16; 256] = [
    0, 0, 0, 1, 2, 3, 4, 6, 8, 10, 13, 16, 20, 23, 28, 32, 37, 42, 48, 54, 61, 67, 75, 82, 90, 99,
    108, 117, 127, 137, 148, 159, 170, 182, 195, 207, 221, 234, 249, 263, 278, 294, 310, 326, 343,
    361, 379, 397, 416, 435, 455, 475, 496, 517, 539, 561, 583, 607, 630, 654, 679, 704, 730, 756,
    783, 810, 838, 866, 894, 924, 953, 983, 1014, 1045, 1077, 1110, 1142, 1176, 1210, 1244, 1279,
    1314, 1350, 1387, 1424, 1461, 1499, 1538, 1577, 1617, 1657, 1698, 1739, 1781, 1823, 1866, 1910,
    1954, 1998, 2044, 2089, 2136, 2182, 2230, 2278, 2326, 2375, 2425, 2475, 2525, 2577, 2629, 2681,
    2734, 2787, 2841, 2896, 2951, 3007, 3063, 3120, 3178, 3236, 3295, 3354, 3414, 3474, 3535, 3596,
    3658, 3721, 3784, 3848, 3913, 3978, 4043, 4110, 4176, 4244, 4312, 4380, 4449, 4519, 4589, 4660,
    4732, 4804, 4876, 4950, 5024, 5098, 5173, 5249, 5325, 5402, 5479, 5557, 5636, 5715, 5795, 5876,
    5957, 6039, 6121, 6204, 6287, 6372, 6456, 6542, 6628, 6714, 6801, 6889, 6978, 7067, 7156, 7247,
    7337, 7429, 7521, 7614, 7707, 7801, 7896, 7991, 8087, 8183, 8281, 8378, 8477, 8576, 8675, 8775,
    8876, 8978, 9080, 9183, 9286, 9390, 9495, 9600, 9706, 9812, 9920, 10027, 10136, 10245, 10355,
    10465, 10576, 10688, 10800, 10913, 11027, 11141, 11256, 11371, 11487, 11604, 11721, 11840,
    11958, 12078, 12198, 12318, 12440, 12562, 12684, 12807, 12931, 13056, 13181, 13307, 13433,
    13561, 13688, 13817, 13946, 14076, 14206, 14337, 14469, 14602, 14735, 14868, 15003, 15138,
    15273, 15410, 15547, 15685, 15823, 15962, 16102, 16242, 16383,
];
