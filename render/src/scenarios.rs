//! Single-source table of named render scenarios (v2-plan M3, step 1).
//!
//! Each [`Scenario`] pins one `(State, frame, flag_age, connected)` tuple
//! that [`effects::paint`](crate::effects::paint) must render
//! deterministically. The table is the shared ledger for:
//!
//! - the table-driven insta snapshot suite in
//!   `render/tests/golden_scenarios.rs` (one named `.snap` per entry), and
//! - (M3 step 2) the golden-frame dumper that exports raw RGB888 frames +
//!   a JSON manifest into `testdata/frames/` for the C# renderer port.
//!
//! The first 19 entries reproduce exactly the tuples pinned by the
//! pre-existing snapshot tests in `render/tests/effects.rs` (same names,
//! same values — do not drift them). The remaining entries backfill the
//! coverage gaps listed in `docs/v2-plan.md` M3 step 1. Entry names double
//! as snapshot identifiers and, later, golden-frame filenames: keep them
//! unique, `lowercase_snake`, and treat the table as append-only.
//!
//! # Timing derivations
//!
//! All frame choices below are derived from the integer math in
//! [`crate::anim`] — no magic numbers:
//!
//! **`strobe_60(frame, hz)`** — `period = 60 / hz`,
//! `on = (period * 6 + 5) / 10` (≈60 % duty, rounded); the strobe is in
//! its on-phase iff `frame % period < on`:
//!
//! | hz | period | on  | on-phase          | off-phase          |
//! |----|--------|-----|-------------------|--------------------|
//! | 2  | 30     | 18  | `frame % 30 < 18` | `frame % 30 >= 18` |
//! | 3  | 20     | 12  | `frame % 20 < 12` | `frame % 20 >= 12` |
//! | 4  | 15     | 9   | `frame % 15 < 9`  | `frame % 15 >= 9`  |
//! | 5  | 12     | 7   | `frame % 12 < 7`  | `frame % 12 >= 7`  |
//!
//! Off-phase frames are picked to *discriminate* the rate where possible:
//! e.g. frame 20 is off at 2 Hz (20 % 30 = 20 ≥ 18) but would be on at
//! 4 Hz (20 % 15 = 5 < 9), so a dark panel proves the 2 Hz leg was taken.
//!
//! **`breathe(frame, period)`** — LUT index `p = (frame % period) * 256 /
//! period` into `SIN_U8`, which peaks (≈255) at `p == 64` and troughs
//! (≈1) at `p == 192`. For the race-idle period of 240 frames:
//! peak at `frame = 64 * 240 / 256 = 60`, trough at
//! `frame = 192 * 240 / 256 = 180` (both divisions exact).
//!
//! **Orange rotation** — static-wave period is 90 frames/step,
//! `step = (frame / 90) % 4`, so step *k* first appears at `frame = 90k`.
//!
//! **Yellow double-wave triangles** — `PERIOD = 15`,
//! `upper_on = (frame % 15) < 7`: frame 0 lights the upper-left triangle,
//! frame 8 (8 ≥ 7) lights the lower-right one.
//!
//! **Blue sweep band** — under double-wave `sweep_step_frames = 1`, so
//! `sweep_pos = frame % 32` and the bright band covers columns
//! `pos ..= pos + 3`: frame 8 puts it at columns 8–11, clear of both edges.
//!
//! **Red onset flash** — `ONSET_FRAMES = 4`; for `flag_age < 4` the fill is
//! `(255, gb, gb)` with `gb = 255 - flag_age * 255 / 4`: age 0 → pure
//! white (255,255,255), age 1 → (255,192,192), age 2 → (255,128,128),
//! age 3 → (255,64,64) — a white flash decaying towards red.
//!
//! **Green onset sweep** — `SWEEP_FRAMES = 30`; `flag_age >= 30` renders
//! the settled flag, so strobe scenarios use `flag_age = 100` to stay
//! clear of the sweep.
//!
//! **Ready orb fallback** — `ORB_DURATION_FRAMES = 300`; `flag_age >= 300`
//! falls back to the race-idle alive marker.

use proto::{Caution, Flag, SectorMask, Session, State, WaveLevel};

/// One pinned rendering: everything `effects::paint` needs, plus a stable
/// name used for the snapshot / golden-frame identity.
#[derive(Copy, Clone, Debug)]
pub struct Scenario {
    pub name: &'static str,
    pub state: State,
    pub frame: u32,
    pub flag_age: u32,
    pub connected: bool,
}

/// `State::default()` spelled out as a `const` (trait `Default` isn't
/// callable in const context).
const BASE: State = State {
    flag: Flag::None,
    wave: WaveLevel::None,
    session: Session::Unknown,
    caution: Caution::None,
    sectors: SectorMask::empty(),
};

const fn flag(f: Flag) -> State {
    State { flag: f, ..BASE }
}

const fn waved(f: Flag, w: WaveLevel) -> State {
    State {
        flag: f,
        wave: w,
        ..BASE
    }
}

const fn racing_sectors(bits: u8, w: WaveLevel) -> State {
    State {
        session: Session::Racing,
        sectors: SectorMask::from_bits(bits),
        wave: w,
        ..BASE
    }
}

const fn sc(name: &'static str, state: State, frame: u32, flag_age: u32) -> Scenario {
    Scenario {
        name,
        state,
        frame,
        flag_age,
        connected: true,
    }
}

/// Every pinned scenario, in ledger order: the 19 legacy snapshot tuples
/// first, then the M3 backfills.
pub const ALL: &[Scenario] = &[
    // =====================================================================
    // Legacy tuples — exactly the 19 snapshot tests in tests/effects.rs.
    // Names match the existing `effects__<name>.snap` files.
    // =====================================================================
    sc("snap_yellow_static", flag(Flag::Yellow), 0, 100),
    sc(
        "snap_yellow_single_wave_on_phase",
        waved(Flag::Yellow, WaveLevel::Single),
        0,
        100,
    ),
    sc(
        "snap_yellow_double_wave",
        waved(Flag::Yellow, WaveLevel::Double),
        0,
        100,
    ),
    sc("snap_red_after_onset", flag(Flag::Red), 100, 100),
    sc("snap_blue_static", flag(Flag::Blue), 0, 100),
    sc(
        "snap_blue_single_wave",
        waved(Flag::Blue, WaveLevel::Single),
        0,
        100,
    ),
    // Mid-sweep: age 15 of the 30-frame onset band sweep.
    sc("snap_green_onset", flag(Flag::Green), 15, 15),
    sc("snap_green_settled", flag(Flag::Green), 100, 100),
    sc("snap_white_static", flag(Flag::White), 0, 100),
    // Frame 25 of the 100-frame breathe — near the envelope peak.
    sc("snap_black_flag_x", flag(Flag::Black), 25, 100),
    sc("snap_orange_step0", flag(Flag::Orange), 0, 100),
    // One 90-frame period in — quadrant step 1.
    sc("snap_orange_step1", flag(Flag::Orange), 90, 100),
    sc("snap_checkered", flag(Flag::Checkered), 0, 100),
    sc(
        "snap_caution_vsc",
        State {
            caution: Caution::VirtualSafetyCar,
            ..BASE
        },
        0,
        100,
    ),
    sc(
        "snap_caution_safety_car",
        State {
            caution: Caution::SafetyCar,
            ..BASE
        },
        0,
        100,
    ),
    sc(
        "snap_race_idle",
        State {
            session: Session::Racing,
            ..BASE
        },
        0,
        100,
    ),
    // Mid-breathe of the 120-frame ready-orb envelope.
    sc(
        "snap_ready_orb",
        State {
            session: Session::PreRace,
            ..BASE
        },
        30,
        30,
    ),
    sc(
        "snap_sector_band_s2",
        racing_sectors(0b010, WaveLevel::None),
        0,
        100,
    ),
    sc(
        "snap_sector_band_all_three",
        racing_sectors(0b111, WaveLevel::None),
        0,
        100,
    ),
    // =====================================================================
    // M3 backfills — the coverage gaps from docs/v2-plan.md M3 step 1.
    // =====================================================================
    //
    // --- Red onset flash, all four frames of the 4-frame envelope.
    // gb = 255 - age*255/4 → 255, 192, 128, 64 (see module doc).
    sc("red_onset_frame0", flag(Flag::Red), 0, 0),
    sc("red_onset_frame1", flag(Flag::Red), 1, 1),
    sc("red_onset_frame2", flag(Flag::Red), 2, 2),
    sc("red_onset_frame3", flag(Flag::Red), 3, 3),
    //
    // --- Blue double-wave: sweep_pos = 8 % 32 = 8 → band at cols 8..=11.
    // The ASCII quantiser renders the whole panel 'B' (both the band's
    // full brightness and the 59–100 % cloth-wave floor are above the
    // bright threshold); the raw RGB888 golden dump (M3 step 2) is what
    // pins the band position byte-exactly.
    sc(
        "blue_double_wave",
        waved(Flag::Blue, WaveLevel::Double),
        8,
        100,
    ),
    //
    // --- Strobe off-phases. Frame 20: off at 2 Hz (20 % 30 = 20 >= 18)
    // yet on at 4 Hz (20 % 15 = 5 < 9) — dark proves the 2 Hz leg.
    sc(
        "yellow_single_wave_off_phase",
        waved(Flag::Yellow, WaveLevel::Single),
        20,
        100,
    ),
    sc(
        "red_single_wave_off_phase",
        waved(Flag::Red, WaveLevel::Single),
        20,
        100,
    ),
    // Frame 10: off at 4 Hz (10 % 15 = 10 >= 9) yet on at 2 Hz (10 < 18).
    sc(
        "red_double_wave_off_phase",
        waved(Flag::Red, WaveLevel::Double),
        10,
        100,
    ),
    sc(
        "green_single_wave_off_phase",
        waved(Flag::Green, WaveLevel::Single),
        20,
        100,
    ),
    //
    // --- Yellow double-wave, opposite triangle: frame 8 → upper_on =
    // (8 % 15) < 7 = false → lower-right triangle lit.
    sc(
        "yellow_double_wave_alt_phase",
        waved(Flag::Yellow, WaveLevel::Double),
        8,
        100,
    ),
    //
    // --- White waved at both rates, each frame discriminating its rate:
    // frame 10: on at 3 Hz (10 % 20 = 10 < 12), off at 5 Hz (10 % 12 = 10 >= 7).
    sc(
        "white_single_wave_on_phase",
        waved(Flag::White, WaveLevel::Single),
        10,
        100,
    ),
    // frame 15: off at 3 Hz (15 % 20 = 15 >= 12), on at 5 Hz (15 % 12 = 3 < 7).
    sc(
        "white_single_wave_off_phase",
        waved(Flag::White, WaveLevel::Single),
        15,
        100,
    ),
    // frame 13: on at 5 Hz (13 % 12 = 1 < 7), off at 3 Hz (13 % 20 = 13 >= 12).
    sc(
        "white_double_wave_on_phase",
        waved(Flag::White, WaveLevel::Double),
        13,
        100,
    ),
    // frame 8: off at 5 Hz (8 % 12 = 8 >= 7), on at 3 Hz (8 % 20 = 8 < 12).
    sc(
        "white_double_wave_off_phase",
        waved(Flag::White, WaveLevel::Double),
        8,
        100,
    ),
    //
    // --- Orange rotation steps 2 and 3: step = (frame / 90) % 4.
    sc("orange_step2", flag(Flag::Orange), 180, 100),
    sc("orange_step3", flag(Flag::Orange), 270, 100),
    //
    // --- Ready orb after the 300-frame fallback: flag_age >= 300 renders
    // the race-idle alive marker instead of the breathing ring.
    sc(
        "ready_orb_fallback_after_300",
        State {
            session: Session::PreRace,
            ..BASE
        },
        360,
        360,
    ),
    //
    // --- Sector band at 4 Hz under double-wave. Frame 20: on at 4 Hz,
    // off at 2 Hz — lit segments prove the double-wave rate switch.
    sc(
        "sector_band_double_wave_on_phase",
        racing_sectors(0b010, WaveLevel::Double),
        20,
        100,
    ),
    // Frame 10: off at 4 Hz, on at 2 Hz — dark active segment (dim
    // inactive segments stay) proves the same from the other side.
    sc(
        "sector_band_double_wave_off_phase",
        racing_sectors(0b010, WaveLevel::Double),
        10,
        100,
    ),
    //
    // --- Race-idle breathe extremes (period 240): peak at frame 60
    // (LUT index 64), trough at frame 180 (LUT index 192). The pulse dot
    // spans (4,4,4)..(14,14,14) — below the ASCII quantiser's dim
    // threshold, so both snapshots read as '.' corners; the raw RGB888
    // golden dump (M3 step 2) is what distinguishes them byte-exactly.
    sc(
        "race_idle_breathe_peak",
        State {
            session: Session::Racing,
            ..BASE
        },
        60,
        100,
    ),
    sc(
        "race_idle_breathe_trough",
        State {
            session: Session::Racing,
            ..BASE
        },
        180,
        100,
    ),
];
