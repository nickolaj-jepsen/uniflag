//! Host snapshot tests for `uniflag_render::effects::paint`.
//!
//! Each test paints a representative `(State, frame, flag_age)` into a
//! `MockSurface` and compares the ASCII rendering against the committed
//! snapshot. Run `cargo insta review` to update intentionally-changed
//! snapshots.

mod common;

use common::MockSurface;
use insta::assert_snapshot;
use proto::{Caution, Flag, SectorMask, Session, State, WaveLevel};
use uniflag_render::effects::paint;
use uniflag_render::HEIGHT;

// =============================================================================
// Sanity / precedence tests (inline asserts, no snapshot)
// =============================================================================

#[test]
fn disconnected_blanks_panel() {
    let mut s = MockSurface::new();
    paint(&mut s, &State::default(), 0, 0, false);
    assert_eq!(
        s.count_lit(),
        0,
        "panel should be entirely black when host is silent"
    );
}

#[test]
fn red_overrides_caution() {
    let st = State {
        flag: Flag::Red,
        caution: Caution::SafetyCar,
        ..State::default()
    };
    let mut s = MockSurface::new();
    // After the 4-frame onset, red is solid red — most pixels should
    // have a dominant red channel. The caution-board border (which would
    // dominate at this point if precedence were wrong) is yellow.
    paint(&mut s, &st, 100, 100, true);
    assert!(
        s.count_redish() > 900,
        "red flag should fill the panel after onset (got {} red-dominant pixels)",
        s.count_redish()
    );
}

#[test]
fn sector_band_suppressed_under_red() {
    let st = State {
        flag: Flag::Red,
        sectors: SectorMask::from_bits(0b111),
        ..State::default()
    };
    let mut s = MockSurface::new();
    paint(&mut s, &st, 100, 100, true);
    // Bottom two rows should be red (red wins, sector overlay suppressed).
    // SECTOR_DIM = (40, 30, 0) is yellow-ish — assert no yellow-dominant
    // pixels in the band.
    let y_band_start = HEIGHT as i32 - 2;
    for y in y_band_start..HEIGHT as i32 {
        for x in 0..32 {
            let (r, g, b) = s.pixel(x, y);
            assert!(
                r > g && r > b,
                "pixel ({x},{y}) under red+sector mask should still be red, got ({r},{g},{b})"
            );
        }
    }
}

#[test]
fn sector_band_present_without_red() {
    let st = State {
        flag: Flag::None,
        session: Session::Racing,
        sectors: SectorMask::from_bits(0b010), // S2 only
        ..State::default()
    };
    let mut s = MockSurface::new();
    // Frame 0: strobe is in on-phase; S2 segment should be lit yellow.
    paint(&mut s, &st, 0, 0, true);
    let mid_band_y = HEIGHT as i32 - 1;
    let (r, g, b) = s.pixel(15, mid_band_y);
    assert!(r > 0 && g > 0 && b == 0, "S2 segment should be yellow-ish");
}

// =============================================================================
// Snapshot tests — one representative frame per flag/state
// =============================================================================

fn snap(state: State, frame: u32, flag_age: u32) -> String {
    let mut s = MockSurface::new();
    paint(&mut s, &state, frame, flag_age, true);
    s.render_text()
}

#[test]
fn snap_yellow_static() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Yellow,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_yellow_single_wave_on_phase() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Yellow,
            wave: WaveLevel::Single,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_yellow_double_wave() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Yellow,
            wave: WaveLevel::Double,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_red_after_onset() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Red,
            ..State::default()
        },
        100,
        100,
    ));
}

#[test]
fn snap_blue_static() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Blue,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_blue_single_wave() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Blue,
            wave: WaveLevel::Single,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_green_onset() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Green,
            ..State::default()
        },
        15,
        15, // mid-sweep
    ));
}

#[test]
fn snap_green_settled() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Green,
            ..State::default()
        },
        100,
        100, // post-sweep
    ));
}

#[test]
fn snap_white_static() {
    assert_snapshot!(snap(
        State {
            flag: Flag::White,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_black_flag_x() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Black,
            ..State::default()
        },
        25, // near peak of breathe envelope
        100,
    ));
}

#[test]
fn snap_orange_step0() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Orange,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_orange_step1() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Orange,
            ..State::default()
        },
        90, // one period in
        100,
    ));
}

#[test]
fn snap_checkered() {
    assert_snapshot!(snap(
        State {
            flag: Flag::Checkered,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_caution_vsc() {
    assert_snapshot!(snap(
        State {
            flag: Flag::None,
            caution: Caution::VirtualSafetyCar,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_caution_safety_car() {
    assert_snapshot!(snap(
        State {
            flag: Flag::None,
            caution: Caution::SafetyCar,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_race_idle() {
    assert_snapshot!(snap(
        State {
            flag: Flag::None,
            session: Session::Racing,
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_ready_orb() {
    assert_snapshot!(snap(
        State {
            flag: Flag::None,
            session: Session::PreRace,
            ..State::default()
        },
        30, // mid-breathe
        30,
    ));
}

#[test]
fn snap_sector_band_s2() {
    assert_snapshot!(snap(
        State {
            flag: Flag::None,
            session: Session::Racing,
            sectors: SectorMask::from_bits(0b010),
            ..State::default()
        },
        0,
        100,
    ));
}

#[test]
fn snap_sector_band_all_three() {
    assert_snapshot!(snap(
        State {
            flag: Flag::None,
            session: Session::Racing,
            sectors: SectorMask::from_bits(0b111),
            ..State::default()
        },
        0,
        100,
    ));
}
