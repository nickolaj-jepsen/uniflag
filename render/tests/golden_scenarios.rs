//! Table-driven snapshot suite over [`uniflag_render::scenarios::ALL`].
//!
//! One insta snapshot per scenario, named by the entry's `name` via a
//! snapshot suffix (`golden_scenarios__scenario@<name>.snap`). Additive to
//! the hand-written tests in `tests/effects.rs` — the 19 legacy tuples are
//! deliberately pinned twice (there by hand, here through the table) so the
//! table can't silently drift from what the original tests froze.
//!
//! Run `cargo insta review` (or `INSTA_UPDATE=always`) to (re)generate
//! after an intentional rendering change.

mod common;

use common::MockSurface;
use uniflag_render::effects::paint;
use uniflag_render::scenarios::{Scenario, ALL};

fn render(sc: &Scenario) -> String {
    let mut s = MockSurface::new();
    paint(&mut s, &sc.state, sc.frame, sc.flag_age, sc.connected);
    s.render_text()
}

/// Every scenario name must be unique — names double as snapshot suffixes
/// (a duplicate would make two entries silently share one `.snap`).
#[test]
fn scenario_names_unique() {
    for (i, a) in ALL.iter().enumerate() {
        for b in &ALL[i + 1..] {
            assert_ne!(a.name, b.name, "duplicate scenario name {:?}", a.name);
        }
    }
}

#[test]
fn golden_scenarios() {
    for sc in ALL {
        insta::with_settings!({snapshot_suffix => sc.name}, {
            insta::assert_snapshot!("scenario", render(sc));
        });
    }
}
