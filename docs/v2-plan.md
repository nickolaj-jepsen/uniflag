# uniflag v2 — Implementation Plan

The v2 rework: a C# SimHub plugin owns all state and rendering (game adapters, flag
precedence, effects, brightness policy); the RP2040 becomes a framebuffer device
speaking a binary COBS/CRC-16 protocol; a browser/DashStudio-overlay "virtual panel"
makes the plugin useful with no hardware at all. Design decisions are final and
documented in the repo history and `docs/` as milestones land — this plan sequences
the work; it does not re-litigate the design.

Structure: an incremental milestone ladder with risk spikes grafted in front
(pid.codes day-1 clock, web-overlay fps spike, 30 fps CDC streaming spike) and the
cross-language contract artifacts woven through (golden byte-vectors incl. negative
cases, golden-frame manifest, CLI loopback-decode, staged no-debugger bring-up,
grep-sweep retirement checklist).

Standing rules:

- The ASCII codec and `render/` crate stay alive until M11; **all Rust CI legs
  (`just fmt-check`, `just clippy` both legs, `just test`) are green at the end of
  every milestone.**
- The effects **code** (`render/src/effects.rs` + the insta snapshots) is the
  authoritative spec for the C# port — *not* the `runtime.rs` module doc-comment,
  which is stale in at least three places (blue does cloth-wave + sweep, not
  breathing; orange rotates at 90/24/12 frames = 1.5 s / 400 ms / 200 ms, not
  1 s / 250 ms / 133 ms; checkered scrolls ~7.5 px/s, not 30 px/s). The doc-comment
  is authoritative only for the precedence ladder and design intent.

---

## Milestones

### M1 — Plugin skeleton, Windows CI leg, external clocks

**Depends on:** nothing. **Goal:** a loadable (empty) SimHub plugin, the C#
build/test harness, an isolated Windows CI job, and both unbounded external clocks
(pid.codes, licensing) started on day 1.

1. Verify the working tree is clean and branch from a committed `main` (all later
   firmware work, including the M2b spike, must branch from a reconciled baseline).
2. File the **pid.codes registration PR** immediately (VID 0x1209, citing
   github.com/nickolaj-jepsen/uniflag). Record the requested PID and the interim
   test PID `0x1209:0x0001` in a tracking issue; establish the PID as a
   single-source-of-truth constant cited by `firmware/src/main.rs` (currently
   line ~104) and, later, the plugin discovery filter. **Define the fallback window
   now** (e.g. 6 weeks from PR filing): if the PID is still pending at release
   time, v2.0 ships on the test PID with the swap as documented follow-up — an
   explicit decision at M12, never a silent slip.
3. Make the **conscious licensing decision** on GPLv3 plugin code linking
   proprietary `SimHub.Plugins.dll`; record the rationale in the README/tracking
   issue. Do not block on it.
4. Create `plugin/` **outside the cargo workspace**: `plugin/UniflagPlugin.sln`,
   `plugin/src/UniflagPlugin.csproj` (net48, WPF),
   `plugin/tests/UniflagPlugin.Tests.csproj`. Reference `SimHub.Plugins.dll` etc.
   via MSBuild property `$(SimHubDir)` defaulting to
   `C:\Program Files (x86)\SimHub` (DLLs are proprietary — never committed).
5. Implement the minimal IPlugin + WPF settings-tab stub (device-status
   placeholder, disabled brightness slider, empty 32×32 preview control) by
   reverse-engineering open-source SimHub plugins. Start
   `docs/simhub-plugin-api.md` (the API is undocumented — this doc is a
   deliverable). Pin one reference SimHub version; it also keys the CI cache.
6. Extend `.gitignore`: `plugin/**/bin/`, `plugin/**/obj/`, `.vs/`, `*.user`, the
   local SimHub-DLL extraction dir, `dist/`, `*.zip`.
7. justfile: `[windows]` recipes `plugin-build` (msbuild Release
   `/p:SimHubDir=...`) and `plugin-test` (vstest/dotnet test).
8. `.github/workflows/ci.yml`: add an **isolated, non-gating** `windows-latest`
   job (no `needs:`, not in any required-checks group): `actions/cache` on the
   pinned SimHub installer, download on miss, extract reference DLLs (innoextract,
   scripted fallback to silent `/VERYSILENT` install), msbuild, run the
   placeholder test, upload the plugin DLL artifact. The three ubuntu Rust jobs
   are untouched.

**Verification:** SimHub on the dev box lists the plugin and renders the tab
(screenshot in the tracking issue). CI green twice consecutively, second run
showing an installer cache hit. A deliberately broken Windows job on a test branch
leaves the Rust jobs green and mergeable. pid.codes PR URL recorded.
`just fmt-check / clippy / test` untouched and green.

---

### M2 — Risk spikes: web-overlay fps (M2a) + 30 fps CDC streaming (M2b)

**Depends on:** M2a → M1; M2b → M1 step 1 (clean baseline; spike branch).
**Goal:** measured, written verdicts on the two riskiest technical assumptions
before anything builds on them. M2b's proto core is production code, not throwaway.

**M2a — web overlay spike**

1. Author a static canvas test page (rough LED-dot skin, sized within the 800×600
   overlay surface cap) with a **dummy JS frame generator** — no renderer needed —
   plus on-page measured-fps and dropped-frame counters. Add a `just overlay-serve`
   recipe to serve it standalone; also host it from a throwaway HttpListener in the
   M1 skeleton with a WS endpoint pushing synthetic 3072-byte binary frames at
   30 fps (no COBS on WS).
2. Run the test matrix: (a) desktop Chrome/Edge; (b) DashStudio Web Page View,
   licensed tier; (c) **free tier — does the 10 fps dash cap throttle the embedded
   browser's render loop or only property updates?**; (d) SimHub's optional HTML
   rendering mode (confirm and document the exact break symptom).
3. Write `docs/web-overlay.md`: readings for every matrix cell, the chosen overlay
   target fps, and an explicit go/degraded verdict.

**M2b — CDC streaming spike**

1. Add to `proto/` as new modules **alongside the untouched ASCII codec**:
   `proto/src/crc.rs` (CRC-16, parameters chosen and documented),
   `proto/src/cobs.rs` (no_std encode/decode), `proto/src/packet.rs` (Frame,
   3072 B RGB888). Unit tests: all-zero payloads, 254-byte non-zero runs,
   truncated frames. Keep new modules import-clean of `State`/`Flag` etc.
2. Firmware **spike branch** (from clean main): replace the line-accumulator body
   of `cdc_rx_loop` in `firmware/src/main.rs` with a ~3.1 KB resync-on-0x00 COBS
   accumulator (Frame + header + CRC + COBS overhead ⌈n/254⌉); hand off via
   **static double-buffer + `Signal<buffer index>`** (never `Signal<[u8;3072]>`);
   minimal blit through `display.rs::set_pixel` (the coordinate remap makes raw
   bitstream writes impossible; ~330 µs/frame) then `present().await`.
3. Temporary sender subcommand in `sim/`: 30 fps moving test pattern, one whole
   encoded frame per write.
4. Soak 10+ minutes; mid-frame cable yank/replug to prove 0x00-delimiter resync;
   watch for cumulative latency and burst behaviour. Record max sustainable fps
   and jitter in a `docs/protocol.md` draft.

**Verification:** both docs contain numbers and explicit verdicts; the spike branch
demonstrably sustains smooth 30 fps and recovers from mid-frame disconnection
without a power cycle. `just test`/`clippy` green (ASCII codec untouched; new proto
modules tested).

---

### M3 — C# renderer core + golden-frame parity + WPF preview sink

**Depends on:** M1. **Goal:** all existing flag effects render in C#, byte-exact
against goldens exported from the Rust render crate, animating in the settings-tab
preview with zero hardware and zero game.

1. **Backfill snapshot gaps** in `render/tests/effects.rs` (currently 19 `.snap`
   files) before the crate retires: red onset frames 0–3, blue double-wave,
   yellow/red/green/white strobe off-phases, white waved 3/5 Hz, orange steps 2–3,
   ready-orb post-300-frame fallback, sector band 4 Hz double-wave, race-idle
   breathe extremes.
2. Add a golden-frame dumper (test-gated or example bin in `render/`) writing
   **raw 3072-byte RGB888 dumps + a JSON manifest** of every
   (State, frame, flag_age) tuple (the 19 snapshots + backfills) into top-level
   `testdata/frames/`. Raw bytes chosen over porting the MockSurface ASCII
   quantiser — stricter, and drops the `render_char` dependency. Determinism
   check: no wall-clock input anywhere. Introduce the **`just golden-regen`**
   recipe here, covering `testdata/frames/` — regeneration happens only via this
   recipe in deliberate, reviewed commits, never as a test side effect.
3. Author `docs/effects-spec.md` **from the code**: `render/src/effects.rs` and
   the snapshots are the executable spec. Capture the full parameter inventory
   (palette constants, strobe rates 2/3/4/5 Hz, wave_mult lo/hi pairs, sweep
   geometry, glyph bitmaps, sector-band geometry S1 0..=9 / S2 11..=20 /
   S3 22..=31, SIN_U8 semantics). Take only the precedence ladder and design
   intent from the `runtime.rs` doc-comment (lines 14–59), and fix its three
   stale claims (blue, orange, checkered — see standing rules) rather than
   transcribing them. Also author the **three-state idle visual spec** here
   (firmware fallback / plugin connected-idle / game live — visually distinct):
   it sits on the shared path before the tracks fork, so both M4 (connected-idle)
   and M8 (firmware fallback) implement against it.
4. Port the anim primitives **bit-exactly in C# integer math** (golden parity
   re-imposes the no-float constraint): the 256-entry Bhaskara-I SIN_U8 LUT
   values, `strobe_60`'s `(period*6+5)/10` duty rounding, `breathe`, `wave_mult`
   phase `(16x + 8y + 4*frame) mod 256` with wrapping arithmetic, `scale_rgb`
   `(c*(m+1))>>8`. Port `div_euclid`/`rem_euclid` explicitly — C# `%` differs on
   negatives (checkered scroll, blue wraparound sweep).
5. Port the paint-target abstraction (out-of-range `set_pixel` **silently
   ignored** — the green sweep starts at x = −4) and every effect per the spec
   checklist; implement the precedence ladder (disconnected > red > VSC > SC >
   per-flag > session idle; sector band last unless red) as its **own separately
   unit-tested layer**.
6. Implement the **one-renderer→N-sinks** interface consuming finished 3072-byte
   RGB888 frames. Renderer ticks a 60 fps internal frame counter; sinks sample
   it — never rebase the animation math to 30 fps (that would halve every strobe
   rate).
7. First sink: WPF preview (32×32 WriteableBitmap) in the settings tab, plus a
   debug state-cycler to drive the renderer standalone.
8. C# golden-frame tests: iterate the `testdata/frames/` manifest, byte-exact
   compare all 3072 bytes; port the 4 inline precedence asserts from
   `render/tests/effects.rs` as unit tests.

**Verification:** `just plugin-test` and the Windows CI job pass the golden suite
byte-exact; `just test` still green (render crate extended, not removed); the
settings tab preview cycles every effect with no device and no game.

---

### M4 — Generic SimHub adapter + state model + connected-idle

**Depends on:** M3. **Goal:** real SimHub telemetry drives rendered flags via the
layered-adapter architecture; connected-idle implemented per the M3 spec.

1. Define the plugin-side state model (flag/wave/session/caution/sector semantics
   migrated conceptually from proto's enums — host-only, never on the wire).
2. Implement the layered-adapter interface and the **generic adapter** reading
   DataCorePlugin `Flag_*` normalized properties per
   `docs/simhub-flag-properties.md`. **Salvage now**, before simhub/README.md is
   replaced in M11: the per-sim raw-property research (iRacing SessionFlags bit
   0x4000, ACC `globalYellow1/2/3`, rF2/LMU `mGamePhase` 5=SC 6=FCY) into that doc.
3. Implement plugin connected-idle (dim marker) vs game-live rendering per the
   three-state spec authored in M3; renderer runs whenever ANY sink is active.

**Verification:** adapter-mapping and precedence unit tests green locally and in
CI; with SimHub replaying a game session, the WPF preview shows correct live flag
effects; Rust CI legs green.

---

### M5 — Web sink production, overlay page, dash file

**Depends on:** M2a (verdict), M3 (renderer + sink interface), M4 (idle content).
**Goal:** the virtual panel works in any browser and as an in-game DashStudio
overlay — the plugin is a complete product with zero hardware.

1. Productionize the HttpListener inside the plugin: **localhost-only** binding,
   LED-dot canvas page embedded as an assembly resource, WS endpoint broadcasting
   the renderer's raw 3072-byte frames (no COBS) to all clients, lifecycle tied to
   sink activation. Fixed high port; **graceful bind-failure surfaced in the
   settings-tab status area**, never a plugin crash. Cache-bust the page with a
   version query param (Web Page View caches across plugin updates).
2. Author and commit the ready-made **DashStudio overlay dash file** embedding a
   Web Page View at the localhost URL, sized within the 800×600 cap, honoring
   M2a's measured fps verdict. Document the HTML-rendering-mode incompatibility
   prominently in user docs.
3. Keep `just overlay-serve` for page iteration outside the plugin.
4. C# test: WS connect + receive one well-formed 3072 B frame.

**Verification:** browser at the localhost URL and the committed dash file in-game
both show the live panel matching the WPF preview; from another LAN machine the URL
is **unreachable** (localhost binding proven); toggling the overlay reconnects the
WS cleanly; WS test green in `just plugin-test` and CI.

---

### M6 — Protocol freeze: full packet set + cross-language golden vectors

**Depends on:** M2b (proto core), M1 (C# test project). **Goal:** the complete
binary protocol as code, prose, and frozen fixtures that BOTH test suites
round-trip. ASCII codec still untouched (render/sim depend on it until M7/M11).

1. Complete `proto/src/packet.rs`: `Hello`, `Frame`, `Brightness` (host→device);
   `HelloAck` (firmware version, **protocol version**, panel size from 32×32),
   `ButtonEvent` (device→host). Version fields in the handshake; forward-compat
   posture carried from the ASCII parser philosophy (unknown packet types ignored;
   bad CRC dropped-and-resynced).
2. Finish `docs/protocol.md`: packet layouts, COBS framing rules, CRC-16
   parameters, handshake sequence, 30 fps stream-as-heartbeat, ~1.5 s
   silence→fallback contract, PID constants (test + requested).
3. Check in golden vectors at top-level `testdata/proto/` (C#-reachable without
   entering a Rust crate): every packet **pre-COBS (with CRC) and post-COBS
   framed**, plus **negative vectors** (bad CRC, truncated frame,
   garbage-then-resync).
4. Rust tests: exhaustive round-trips in the style of the 28 inline tests in
   `proto/src/lib.rs`, plus golden decode/encode byte-exact, in a new
   `proto/tests/` integration-test dir (picked up automatically —
   justfile runs `--all-targets`).
5. C# codec (packets + COBS + CRC-16) in `plugin/src/`; conformance tests loading
   the identical `testdata/proto/` files by relative path; wired into the Windows
   CI job and `just plugin-test`.
6. Extend `just golden-regen` to also cover `testdata/proto/`.
7. **Corruption drill:** flip one byte in a vector; confirm BOTH suites fail
   (proving both actually read the files); revert.

**Verification:** `just test` green including vector round-trips; Windows CI green
on the identical files; corruption drill recorded in the PR description.

---

### M7 — Rust bring-up CLI (sim repurpose)

**Depends on:** M6. **Goal:** `sim/` becomes `uniflag-cli`, the binary-protocol
test-pattern streamer and the firmware's only diagnostic instrument — fully
verified host-side before it touches hardware.

1. Rework `sim/src/main.rs` keeping the clap skeleton, serial/stdout `Sink` split,
   and serialport handling: emit COBS-framed Frame packets at 30 fps with
   **monotonic-deadline pacing** (not sleep-per-frame — heartbeat semantics depend
   on it); Hello/HelloAck handshake printing fw/protocol version and panel size;
   **add a receive path** (the old sim was TX-only) decoding and printing
   ButtonEvent packets live.
2. Add a **loopback-decode mode**: the CLI decodes its own emitted byte stream via
   proto's decoder — the host-side isolation tool for silent-failure debugging.
3. Add a **bounded emission mode** (`emit <packet>` subcommand or `--count N`)
   writing exactly one encoded packet and exiting — this is what makes byte-diff
   verification against the golden vectors executable.
4. Pattern generators: solid colors, gradients, moving pixel, checkerboard,
   brightness sweep; `--brightness N` to exercise the Brightness path.
5. Before deleting `sim/scenarios/` (stale `P=` fields confirm nothing
   wire-worthy survives), **transcribe** race-arc and caution-and-sectors
   sequences as C# adapter-timeline fixtures for M10.
6. Rename crate to `uniflag-cli` in one commit, updating every duplicated list:
   root `Cargo.toml` members/default-members, justfile clippy/test crate lists,
   justfile `sim`→`cli` recipe (keep `UNIFLAG_SERIAL`), `flake.nix` (pname,
   version, cargoBuildFlags, cargoTestFlags, mainProgram, **and the devShell
   shellHook's "just sim" hint**). `sim/Cargo.toml`: keep
   proto/clap/serialport/anyhow; drop or feature-gate crossterm.
7. **Acknowledged window:** from this commit until M8 lands, the in-repo host tool
   speaks the binary protocol while `just build`/`just img` still produce
   ASCII-line firmware — the repo's own recipes can't drive a freshly flashed
   device. Accepted: pre-M7 commits are the ASCII test path if M8 stalls; the
   device is never bricked (BOOTSEL ROM + retained watchdog).

**Verification:** `cargo test -p uniflag-cli` green; bounded-emission output diffs
clean against the golden vectors; `just fmt-check`, `just clippy` (both legs),
`just test` all green with the renamed crate.

---

### M8 — Firmware v2 rework + first end-to-end frame

**Depends on:** M6, M7, **M3** (docs/effects-spec.md incl. the three-state idle
spec — the firmware fallback look is designed against it, and runtime.rs's
doc-comment can only be deleted once its salvageable content is extracted there).
**Goal:** firmware shrinks to its v2 role and is proven on hardware by the CLI —
with zero SimHub involvement — before any plugin-device work.

1. `main.rs`: harden the M2b COBS RX loop to dispatch Hello/Frame/Brightness;
   delete `STATE_SIGNAL`, `BRIGHTNESS_CHAN`, `MAX_LINE_BYTES`, `handle_line()`,
   FlashStorage init; **retain the CDC Sender** currently discarded at line 133
   via a TX channel + a third future joined in main (mirroring the existing
   `join(usb_fut, rx_fut)` pattern — no mutex) sending HelloAck on Hello and
   ButtonEvent from the buttons channel; frames via the static double-buffer +
   notification; HelloAck from `env!("CARGO_PKG_VERSION")` + proto's
   protocol-version const + `display::WIDTH/HEIGHT`; USB PID from the shared
   constant (test PID until pid.codes lands).
2. `display.rs`: delete only the `impl uniflag_render::Surface` block (lines
   362–367); add `blit_rgb888(&[u8; 3072])` looping `set_pixel`. PIO/DMA driver,
   `present()` semantics, brightness multiplier untouched.
3. `runtime.rs`: replace wholesale (spec already preserved in
   `docs/effects-spec.md` by M3). New select loop: (a) frame notification →
   blit + `present().await` (keep paint-into-back/await-present cadence);
   (b) Brightness packet → `set_brightness` (next streamed frame repaints — no
   explicit refresh needed); (c) ~1.5 s silence (reuse the
   `CONNECT_TIMEOUT=1500 ms` pattern) → dim idle fallback; (d) long-press → test
   pattern + fw version screen.
4. New `firmware/src/screens.rs`: fill/rect/dot helpers over `set_pixel` + a
   minimal pixel font (digits, 'v', '.') — glyphs **copied** from the retiring
   render crate, never depended on. Fallback look implements the M3 three-state
   spec (visually distinct from connected-idle).
5. `buttons.rs`: extend the Debouncer to track release and hold duration;
   **classify on release** so a long press never also fires a step; emit
   ButtonEvent (id + press kind) to the CDC TX channel; long-press additionally
   toggles the local test screen. GPIO 21/26/27 only.
6. **Lockstep deletions:** `firmware/src/storage.rs` + the `- 0x1000` carve in
   `firmware/memory.x` (same commit); drop `uniflag-render` and the
   already-unreferenced `embedded-graphics` from `firmware/Cargo.toml`; keep the
   8 s watchdog / 2 s feed.
7. Sanity-check BSS totals after adding the ~3.1 KB accumulator + 2×3072 B frame
   slots (fine within 264 KB alongside the 32 KB bitstreams, verify anyway).
8. **Staged bring-up** (no debugger by convention): idle screen → handshake →
   frames. Then the full hardware checklist via `just flash` + the CLI: all
   patterns, brightness sweep, kill-CLI → fallback ≤ ~1.5 s, ButtonEvents
   printed, long-press version screen, forced-panic build recovers via watchdog,
   **30-minute soak** with a mid-stream yank/replug proving resync without power
   cycle. Record measured behaviour in `docs/protocol.md`.

**Verification:** `just clippy` (firmware leg), `just build`, `just img` green;
the complete hardware checklist passes on a real device.

---

### M9 — Plugin-device integration: discovery, USB sink, settings tab v1, brightness end-to-end

**Depends on:** M4, **M5** (web-sink status in the tab; overlay in the
verification), M8. **Goal:** SimHub drives the physical panel; settings tab v1
complete; the full brightness loop closed.

1. Discovery: enumerate serial ports, filter on the shared VID/PID constant
   (**accept both test and registered PID during transition**), Hello/HelloAck
   with protocol-version and panel-size validation, auto-reconnect with
   retry-on-open (Windows holds stale COM handles after replug), manual COM-port
   override in the tab.
2. USB sink off the shared sink interface: 30 fps monotonic-deadline-paced frame
   writer (heartbeat, no acks) — compare byte cadence against M2b/M8 soak data.
3. Complete settings tab v1: device status (port, connection state, firmware +
   protocol version, panel size from HelloAck), brightness slider, manual
   override, live preview, web-sink status (URL, client count, bind failures).
4. Brightness policy host-side: slider → Brightness packet; ButtonEvent →
   step/sleep policy using the retired `BrightnessController` semantics as
   behaviour spec (STEP=12, SLEEP=6, wake ≥ STEP); persist in SimHub settings;
   re-send Brightness on every (re)connect.
5. Stream the connected-idle marker with no game running — **all three idle
   states now live and visually distinct**.
6. Remove the old ASCII Custom Serial profile from the dev box's SimHub so it
   can't contend for the COM port (repo deletion is M11).

**Verification:** plug in → auto-discovered; tab shows connected + fw/proto
version + 32×32; panel mirrors WPF preview and web overlay live; slider dims the
panel; device buttons step brightness and the value survives a SimHub restart;
USB yank → firmware fallback ≤ ~1.5 s and tab shows disconnected; replug
auto-reconnects; manual override bypasses filtering.

---

### M10 — iRacing adapter, penalty suite, text engine

**Depends on:** M4, M9. **Goal:** v1 display-feature completeness. Spotter and pit
signals remain explicitly deferred.

1. Text engine: pixel font extending the 7×11 S/C/V glyphs (digits + letters the
   penalty suite needs) + layout helpers (centering, spacing). Migrate the VSC/SC
   caution boards onto it — the migrated boards **must bit-match the
   ported-parity goldens**; if the migration intentionally changes their
   rendering, the affected baselines are **re-homed into the C#-authored corpus**
   in a reviewed commit (with WPF visual review per the risk register) and the
   ported-parity set shrinks accordingly. Rust-side regeneration is *not* an
   option for migrated effects — the Rust dumper can only ever reproduce the old
   rendering.
2. iRacing raw-telemetry adapter layered over the generic baseline (SessionFlags
   bits incl. 0x4000=Caution, slowdown/penalty properties), overriding/enriching
   only where raw data is better. Feed findings into
   `docs/simhub-flag-properties.md` and `docs/simhub-plugin-api.md`.
3. Penalty effects: iRacing slowdown with severity, meatball (visually distinct
   from the orange quadrant effect), drive-through vs stop-go black-flag detail,
   furled black/white warning. Each gets **C#-authored golden frames in a corpus
   clearly separated from the ported-parity set** — review visually in the WPF
   preview before freezing (a wrong baseline locks in a wrong effect). Name the
   C# dumper tool that produces them and add it to `just golden-regen`.
4. Adapter integration tests replaying recorded iRacing fixtures plus the
   M7-transcribed scenario timelines through generic+iRacing layers.

**Verification:** `just plugin-test` + CI green including new goldens and adapter
tests; a live iRacing session or captured-telemetry replay shows each penalty on
panel, preview, and overlay simultaneously.

---

### M11 — Retirement + docs rewrite

**Depends on:** M3, M9, M10 (M7/M8 already landed). **Goal:** the clean break —
render crate, ASCII codec, Custom Serial artifacts all deleted with CI green
throughout.

1. **Pre-delete checklist, name-by-name:** every (State, frame, flag_age) tuple in
   the `testdata/frames/` JSON manifest has a passing C# golden test (don't gate
   on counts — the manifest is the ledger). Run `just golden-regen` one final time
   and commit. From this point the **ported-parity frame corpus is permanently
   frozen** (unregenerable once render/ is gone) — retire or repoint the
   frame-regen half of `just golden-regen` in the same change (the C#-authored
   corpus keeps its own M10 regen path).
2. Delete `render/` entirely (src, tests, the 19 .snap files, MockSurface);
   remove from `Cargo.toml` members/default-members and the justfile clippy/test
   crate lists. (`flake.nix` needs no change here — it never referenced
   uniflag-render; it was already updated in M7.)
3. Delete the ASCII codec from `proto/src/lib.rs` (State/Flag/WaveLevel/Session/
   Caution/SectorMask, parse/format, ParseError, MAX_LINE_LEN + their 28 inline
   tests). **Ordering constraint: this step must follow (or share a commit with)
   the render/ deletion in step 2 — render/ is the *last* consumer of the State
   types (effects::paint's signature), not M7/M8.**
4. Delete `simhub/uniflag.shsds` and `docs/simhub-custom-serial.md`; replace
   `simhub/README.md` with the plugin install/setup guide (salvage its
   troubleshooting structure); re-index `docs/README.md` (protocol.md,
   effects-spec.md, simhub-plugin-api.md, web-overlay.md); keep
   `cosmic-unicorn-hardware.md` and `cosmic-unicorn-pio.md` untouched.
5. Rewrite the top-level README and CLAUDE.md for the v2 architecture
   (plugin-owns-state, binary protocol, plugin/ build via `$(SimHubDir)`, new
   just recipes, PID identity).
6. **Grep sweep** (zero hits outside git history): `State::parse`,
   `MAX_LINE_LEN`, `uniflag_render`/`uniflag-render`, `shsds`, stale `P=`, and
   `0x1209:0x0001` outside the transition filter.

**Verification:** `just fmt-check && just clippy && just test && just
plugin-build && just plugin-test` green; CI fully green; grep sweep clean; a
fresh-eyes walkthrough of the docs gets from zip to flags-on-panel unaided.

---

### M12 — Release pipeline: one versioned zip + PID gate

**Depends on:** M1 (CI extraction), M9 (which transitively brings M5's dash
file), M11. **Goal:** tag-triggered release shipping a single versioned zip
(plugin DLL + firmware UF2 + dash file + install README).

1. Rework `.github/workflows/release.yml`: keep the ubuntu `just img` UF2 leg;
   add a windows plugin leg reusing the M1 cached-installer extraction (**add the
   cache restore step to this workflow too** — it has none today); join via
   upload/download-artifact into a packaging job assembling
   `uniflag-<tag>.zip` published via softprops/action-gh-release
   (`fail_on_unmatched_files` stays).
2. Add a `[windows]` `just package` recipe producing the identical zip locally.
3. **Audit** that the artifact and zip contain only the plugin DLL — never
   extracted SimHub proprietary DLLs.
4. **PID gate:** registered pid.codes PID merged in firmware, plugin filter, and
   docs; remove the dual-PID transition filter. If still pending after the window
   defined in M1, ship v2.0 on the test PID with the swap documented as
   follow-up — an explicit decision, never a silent slip.
5. Dry-run with a prerelease tag (`v2.0.0-rc1`); clean-machine install
   walkthrough (drop DLL into SimHub, flash UF2, reach a working panel +
   overlay).

**Verification:** the rc tag produces a release with exactly one zip asset; the
clean-machine walkthrough succeeds end to end.

---

## Dependency graph

```
M1 ──┬─→ M2a ──────────────┐
     ├─→ M3 ──→ M4 ──┬─→ M5 ──┐
     │    │          │        │
     │    └──────────│────────│──────────┐        (M3 → M8: effects-spec +
     ├─→ M6 ─→ M7 ─→ M8 ──────┤          │         three-state idle spec)
     │                        ├─→ M9 ─→ M10 ─→ M11 ─→ M12
M2b ─→ M6                     │                        ↑
     (spike core hardens      │                        │
      into protocol freeze)   └────────────────────────┘ (M1: CI extraction)

M3 ─→ M11  (golden corpus must supersede render/ before deletion)
```

Parallelism: after M1+M2, the C# track (M3→M4→M5) and the Rust track (M6→M7→M8)
run concurrently — with two deliberate cross-track edges: M8 reads M3's
effects-spec/idle-spec docs, and M9 needs both tracks complete (M5 + M8).

---

## Cross-cutting policies

**CI-stays-green (strangler-fig).** The ASCII codec coexists with the packet
modules inside `proto` from M2b until M11; `render/` stays a workspace member
(extended in M3, deleted in M11); all Rust CI legs must be green at the end of
every milestone. The Windows CI job is isolated and non-gating from birth (M1)
per the flaky-extraction caveat — an installer outage never reds the Rust
pipeline. The two commits that change workspace crate lists (M7 rename, M11
deletion) carry explicit full-CI verification and must update all duplication
points in the same commit: justfile clippy/test recipes, root `Cargo.toml`
members/default-members, `flake.nix` (M7 only — incl. the shellHook hint), and
CLAUDE.md.

**The cross-language contract.** Two frozen artifact sets, both top-level so the
C# test project reaches them by relative path: (1) `testdata/proto/` — golden
byte vectors for every packet, pre- and post-COBS, plus negative vectors (bad
CRC, truncated, garbage-then-resync), specified in prose by `docs/protocol.md`;
(2) `testdata/frames/` — raw 3072-byte RGB888 golden frames + JSON manifest of
(State, frame, flag_age) tuples, specified in prose by `docs/effects-spec.md`.
Rules: both Rust and C# suites consume the same files (proven by the M6
corruption drill); neither side ever generates its own fixtures; regeneration
only via `just golden-regen` (born M3 for frames, extended M6 for proto,
frame-half retired M11) in deliberate, reviewed commits. The ported-parity frame
corpus is **immutable after M11**; C#-authored penalty goldens (M10) live in a
clearly separated corpus with their own C#-side regen path.

**Versioning between plugin and firmware.** The protocol version is a const in
`proto`, mirrored as a const in the C# codec, carried in `HelloAck` alongside the
firmware version (`env!("CARGO_PKG_VERSION")`) and panel size; the plugin
validates it at handshake and surfaces mismatches in the settings tab
(refuse-with-message, not silent failure). Forward-compat posture carries over
from the ASCII protocol: unknown packet types ignored, bad CRC
dropped-and-resynced. The USB VID/PID is a single-source constant referenced by
the firmware descriptor, the plugin discovery filter, and docs; during the
pid.codes transition (M8→M12) the plugin filter accepts both test and registered
PIDs, and the release (M12) gates on collapsing to one. The release zip pairs
one plugin DLL with one firmware UF2 per tag, so version skew between the two
halves is always a user-visible, documented pairing.

**Renderer clock discipline.** The C# renderer keeps a 60 fps internal frame
counter (all ported animation math assumes it); sinks sample it — USB at 30 fps,
web at the M2a-measured rate, WPF at display cadence. Conflating renderer and
sink clocks would halve every strobe rate; this is a standing review checkpoint
for all sink code.

**Scope additions accepted deliberately** (beyond the original design, all
risk-reduction elaborations, none contradicting a decision): web-sink status in
the settings tab; dash file + install README in the release zip; the
`sim`→`uniflag-cli` rename; the licensing-decision deliverable; negative golden
vectors + corruption drill; the dual-PID transition filter and the
ship-on-test-PID release fallback (with its window fixed in M1).

---

## Risk register

| # | Risk | Mitigation | Milestone |
|---|------|-----------|-----------|
| 1 | SimHub plugin API undocumented (lifecycle, settings persistence, tab hosting, DataUpdate cadence) | Skeleton spike before any feature code; findings captured in `docs/simhub-plugin-api.md`; pin one reference SimHub version | M1 |
| 2 | CI SimHub-installer extraction flaky | Cache installer keyed by pinned version; innoextract with scripted silent-install fallback; job isolated and never gates Rust legs | M1 |
| 3 | pid.codes approval unbounded, human-gated | PR filed day 1 with a defined fallback window; PID is a one-line shared constant; dual-PID discovery filter during transition; explicit ship-on-test-PID fallback decision at release | M1 / M9 / M12 |
| 4 | GPLv3 plugin linking proprietary SimHub.Plugins.dll (licensing gray area) | Conscious recorded decision up front; DLLs never committed; release-zip audit confirms no proprietary DLLs ship | M1 / M12 |
| 5 | Free-tier 10 fps cap and/or HTML rendering mode degrade/break the Web Page View overlay; 800×600 cap | Dummy-frame spike measures all matrix cells before web-sink design; verdict + workarounds in `docs/web-overlay.md`; documented degradation, not redesign | M2a / M5 |
| 6 | 30 fps COBS streaming over CDC infeasible or jittery (latency growth, desync, Windows write batching) | Hardware spike with soak + mid-frame yank resync test before protocol freeze; whole-frame writes; findings feed renderer cadence and heartbeat timing | M2b / M8 |
| 7 | Bit-exactness drift in the C# port (integer division on negatives, `%` vs rem_euclid, rounding) | Raw-RGB888 byte-exact golden comparison; explicit div_euclid/rem_euclid ports; integer math retained in C# | M3 |
| 8 | Golden-frame corpus non-determinism (wall-clock leakage) invalidates baselines | Dumper pins exact (frame, flag_age) tuples; determinism check in M3 | M3 |
| 9 | Stale runtime.rs doc-comment transcribed as spec (blue/orange/checkered params wrong) | effects-spec.md authored from effects.rs + snapshots; doc-comment used only for precedence/intent; stale claims fixed explicitly | M3 |
| 10 | CRC-16/COBS parameter or endianness mismatch between Rust and C# | Shared checked-in vectors incl. negatives; corruption drill proves both suites read them; neither side self-generates fixtures | M6 |
| 11 | Accidental golden regeneration masking breakage | Regeneration only via explicit `just golden-regen` in reviewed commits | M3 / M6 |
| 12 | Crate rename/deletion ripples through duplicated lists (justfile, flake.nix, Cargo.toml, CI, CLAUDE.md) | Single-commit updates with full-CI verification at both list-changing points | M7 / M11 |
| 13 | No on-device logging/debugger — firmware protocol bugs look like a dead panel | CLI loopback-decode mode isolates host bugs first; staged bring-up (idle screen → handshake → frames); CLI-visible HelloAck/ButtonEvent traffic as the diagnostic channel | M7 / M8 |
| 14 | CDC Sender shared between Hello handler and button TX (deadlock/contention) | TX channel + single dedicated sender future joined in main — no mutex; validated via CLI-visible traffic | M8 |
| 15 | Long-press detection conflicting with brightness-step events | Classify press kind on release; long press never also fires a step | M8 |
| 16 | New SRAM pressure (3.1 KB accumulator + 2×3072 B frame slots atop 32 KB bitstreams) | BSS-total sanity check after the change | M8 |
| 17 | .NET serial robustness: port-in-use, reconnect storms, stale COM handles after replug | Retry-on-open in auto-reconnect; unplug/replug is an explicit verification step | M9 |
| 18 | .NET 30 fps timer drift/GC hiccups near the 1.5 s heartbeat threshold | Monotonic-deadline pacing (CLI and USB sink); compared against M2b/M8 soak cadence | M7 / M9 |
| 19 | HttpListener port conflicts / URL-ACL issues | Fixed high port working without admin; graceful bind failure surfaced in settings tab; localhost-only binding verified from a second machine | M5 |
| 20 | Web Page View caching a stale overlay page across plugin updates | Version query-param cache-busting | M5 |
| 21 | iRacing raw penalty telemetry semantics unconfirmed (slowdown severity, furled bits) | Telemetry-capture session; recorded fixtures drive adapter tests; old README research treated as starting point, not ground truth | M10 |
| 22 | Self-authored penalty goldens could freeze a wrong effect | Visual review in WPF preview before freezing; separate corpus from ported-parity goldens | M10 |
| 23 | Deleting render/ before the C# corpus fully supersedes it | Name-by-name pre-delete checklist against the manifest; final `just golden-regen` committed first; render/ deleted before (or with) the ASCII types it consumes | M11 |
| 24 | Hidden ASCII/render stragglers after retirement | Grep sweep (State::parse, MAX_LINE_LEN, uniflag_render, shsds, stale P=, test PID) + full-CI green | M11 |
| 25 | Cross-job artifact plumbing in release.yml is new to this repo; cold cache + installer outage on tag day | Prerelease rc dry-run; cache restore step added to the release workflow; retry posture acceptable for a hobby project | M12 |
