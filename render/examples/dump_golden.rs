//! Golden-frame dumper (v2-plan M3 step 2).
//!
//! Renders every entry of [`uniflag_render::scenarios::ALL`] through
//! [`uniflag_render::effects::paint`] into a raw RGB888 byte buffer and
//! writes the results into the top-level `testdata/frames/` directory:
//!
//! - one `<name>.rgb` per scenario — exactly `32 * 32 * 3 = 3072` raw
//!   bytes, row-major, `(y * 32 + x) * 3`, R then G then B;
//! - `manifest.json` — the ledger the C# golden-parity suite iterates:
//!   an array of `{ name, file, state, frame, flag_age, connected }`
//!   objects in table order, enum values spelled as the PascalCase Rust
//!   variant names, sectors as an array of ints (1..=3).
//!
//! Deterministic by construction: inputs come only from the pinned
//! scenario table (no wall clock, no randomness), output order is table
//! order, JSON is hand-emitted with LF line endings and 2-space indent.
//! Stale files in the output directory are removed first so scenario
//! renames don't leave orphaned frames behind.
//!
//! Run via `cargo run -p uniflag-render --example dump_golden` (wrapped
//! by `just golden-regen`); regeneration only happens in deliberate,
//! reviewed commits — never as a test side effect.

use std::error::Error;
use std::fs;
use std::path::{Path, PathBuf};

use uniflag_render::scenarios::{Scenario, ALL};
use uniflag_render::{effects, Rgb, Surface, HEIGHT, WIDTH};

const FRAME_BYTES: usize = WIDTH * HEIGHT * 3;

/// Minimal [`Surface`] over a raw RGB888 framebuffer. Mirrors the
/// `Surface` contract: out-of-range writes are silently ignored, pixel
/// `(x, y)` lands at byte offset `(y * WIDTH + x) * 3`.
struct RawSurface {
    bytes: [u8; FRAME_BYTES],
}

impl RawSurface {
    const fn new() -> Self {
        Self {
            bytes: [0; FRAME_BYTES],
        }
    }
}

impl Surface for RawSurface {
    fn set_pixel(&mut self, x: i32, y: i32, (r, g, b): Rgb) {
        if x < 0 || y < 0 || x >= WIDTH as i32 || y >= HEIGHT as i32 {
            return;
        }
        let i = (y as usize * WIDTH + x as usize) * 3;
        self.bytes[i] = r;
        self.bytes[i + 1] = g;
        self.bytes[i + 2] = b;
    }
}

fn main() -> Result<(), Box<dyn Error>> {
    let dir = output_dir();
    fs::create_dir_all(&dir)?;
    remove_stale_files(&dir)?;

    let mut entries = Vec::with_capacity(ALL.len());
    for scenario in ALL {
        validate_name(scenario, &entries)?;

        let mut surface = RawSurface::new();
        effects::paint(
            &mut surface,
            &scenario.state,
            scenario.frame,
            scenario.flag_age,
            scenario.connected,
        );
        fs::write(dir.join(format!("{}.rgb", scenario.name)), surface.bytes)?;

        entries.push(manifest_entry(scenario));
    }

    let manifest = format!("[\n{}\n]\n", entries.join(",\n"));
    fs::write(dir.join("manifest.json"), manifest)?;

    println!(
        "wrote {} frames + manifest.json to {}",
        ALL.len(),
        dir.display()
    );
    Ok(())
}

/// `<crate root>/../testdata/frames` — top-level so the C# test project
/// reaches it by relative path without entering a Rust crate.
fn output_dir() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("testdata")
        .join("frames")
}

/// Delete every regular file already in the output directory so renamed
/// or removed scenarios can't leave orphaned `.rgb` frames behind.
fn remove_stale_files(dir: &Path) -> Result<(), Box<dyn Error>> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        if entry.file_type()?.is_file() {
            fs::remove_file(entry.path())?;
        }
    }
    Ok(())
}

/// Scenario names double as filenames and are embedded verbatim in the
/// JSON manifest — enforce the `lowercase_snake` convention (which also
/// guarantees no JSON escaping is ever needed) and reject duplicates,
/// which would silently overwrite a frame.
fn validate_name(scenario: &Scenario, prior_entries: &[String]) -> Result<(), Box<dyn Error>> {
    let name = scenario.name;
    let well_formed = !name.is_empty()
        && name
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '_');
    if !well_formed {
        return Err(format!("scenario name {name:?} is not lowercase_snake").into());
    }
    let needle = format!("\"name\": \"{name}\"");
    if prior_entries.iter().any(|e| e.contains(&needle)) {
        return Err(format!("duplicate scenario name {name:?}").into());
    }
    Ok(())
}

/// One manifest object, 2-space indented, no trailing newline. Enum
/// values use `{:?}` — for field-less enums the derived `Debug` prints
/// exactly the PascalCase Rust variant name.
fn manifest_entry(scenario: &Scenario) -> String {
    let state = &scenario.state;
    let sectors = (1..=3u8)
        .filter(|&s| state.sectors.contains(s))
        .map(|s| s.to_string())
        .collect::<Vec<_>>()
        .join(", ");
    format!(
        concat!(
            "  {{\n",
            "    \"name\": \"{name}\",\n",
            "    \"file\": \"{name}.rgb\",\n",
            "    \"state\": {{\n",
            "      \"flag\": \"{flag:?}\",\n",
            "      \"wave\": \"{wave:?}\",\n",
            "      \"session\": \"{session:?}\",\n",
            "      \"caution\": \"{caution:?}\",\n",
            "      \"sectors\": [{sectors}]\n",
            "    }},\n",
            "    \"frame\": {frame},\n",
            "    \"flag_age\": {flag_age},\n",
            "    \"connected\": {connected}\n",
            "  }}",
        ),
        name = scenario.name,
        flag = state.flag,
        wave = state.wave,
        session = state.session,
        caution = state.caution,
        sectors = sectors,
        frame = scenario.frame,
        flag_age = scenario.flag_age,
        connected = scenario.connected,
    )
}
