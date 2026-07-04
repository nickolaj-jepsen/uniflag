# v2 external clocks (M1)

Tracks the two unbounded external clocks started in M1 of
[v2-plan.md](v2-plan.md). (Written as a doc so it lives with the code;
paste into a GitHub issue if issue-tracking is preferred.)

## Maintainer verification checklist (written 2026-07-04, end of the autonomous M9–M12 run)

All twelve milestones are code-complete on `v2` (through commit
`f3b7c33`); everything machine-verifiable is verified (CI green, 396
plugin tests + 79 Rust tests, the plugin hardware test passed against
the real panel on COM5, `just package` audited). What remains needs
eyes, hands, or a public action. Already done on the dev box: the
2.0.0 `UniflagPlugin.dll` is deployed into SimHub, the "Uniflag
Overlay" dash folder is copied into `DashTemplates`, and the old
ASCII Custom Serial device is confirmed **disabled** (delete it via
the GUI when convenient: Custom serial devices → remove "Custom
Serial device"). The panel still runs the fw built as 0.1.0 —
protocol-compatible (both v1), but reflash `target/uniflag.uf2` for a
matching 2.0.0 HelloAck when convenient.

1. **Release dry-run** (the one blocked-for-the-agent step —
   publishing a prerelease is a public action):
   `git tag v2.0.0-rc1 && git push origin v2.0.0-rc1`, then check the
   Actions run and that the release page shows exactly one asset
   (`uniflag-v2.0.0-rc1.zip`, marked prerelease). Unzip and eyeball:
   `UniflagPlugin.dll` + `uniflag.uf2` + `Uniflag Overlay/` +
   `INSTALL.md`, nothing else.
2. **One SimHub session covers most of it** (panel plugged in):
   - Uniflag tab: device auto-discovered, status shows port +
     firmware/protocol version + 32×32; brightness slider dims the
     panel; device buttons step it and the value survives a SimHub
     restart; USB yank → tab shows disconnected + panel drops to the
     amber fallback ≤ ~1.5 s; replug → auto-reconnect.
   - No game running: panel and preview show **connected-idle** (dim
     blue breathing pair, bottom-centre) — this is the §7b visual
     sign-off. §7a (amber corner blink) was already seen live during
     M8 bring-up. On approval say so — the PROPOSED markers in
     docs/effects-spec.md §7 get removed.
   - Tick **Cycle test states**: the tour now includes the 8 penalty
     states (slowdown severities, meatball, DT/SG, furled) plus the 6
     iRacing-extension states (start-lights Ready/Set/Go, debris, and
     the incident-warning frame alone and over yellow) — this is the
     visual review the `testdata/frames-plugin/` corpus is pending on.
     Rejections are cheap: the corpus regenerates via `just golden-regen`.
   - Browser at `http://127.0.0.1:8972/` mirrors the preview; from
     another LAN machine the same URL must be **unreachable**.
   - DashStudio: the "Uniflag Overlay" dash imports and renders
     in-game (never enable SimHub's HTML rendering mode — issue
     #1494).
   - Any sim replay: flags on panel + preview + overlay
     simultaneously. iRacing specifically: repair → meatball, furled
     → warning accent, caution → SC board (the M10 live check); plus
     the new refinements — a standing/rolling start → the light gantry
     (Ready→Set→Go), a debris flag → the striped board, and nearing
     the incident limit → the blinking red frame. **The incident-limit
     read is the one path not confirmed against a live session** (the
     `SessionDataDict` nesting for `WeekendInfo:WeekendOptions:Incident
     Limit` is researched, not live-verified — see docs/simhub-flag-
     properties.md); confirm it fires here and tweak the dictionary walk
     in `GameDataExtractor` if the shape differs.
3. **Clean-machine walkthrough** (M12 verification): on a fresh PC,
   zip → INSTALL.md → flags on panel, unaided.
4. **pid.codes** (unchanged): the `uniflag-f1a6` branch is staged in
   the scratchpad clone; file when ready. v2.0 ships on the test PID
   by explicit M12 decision (see protocol.md); swap sites enumerated
   there.
5. Optional, deferred with rationale in protocol.md: the forced-panic
   watchdog-recovery flash test.

## pid.codes registration

- Requested PID: **0x1209:0xF1A6** ("FLAG" in hexspeak; verified free
  in-tree and across all open PRs on 2026-07-03; verified-free fallbacks
  if raced: 5AFE, FA61, D15C, CA85).
- Interim/current: test PID **0x1209:0x0001**
  (`firmware/src/main.rs:104`). The shared test PID must not ship on
  redistributed devices — this registration replaces it.
- PR: *(link once filed — branch `uniflag-f1a6` with both registry
  files is committed in the maintainer's fork clone, ready to push.)*
- **Fallback window: 12 weeks from PR filing.** Calibration: pid.codes
  merges in batches every ~6–11 weeks (recent batches 2025-12-19/23,
  2026-02-05/06, 2026-02-11/12, 2026-04-28/29; median wait ~4 weeks,
  max observed 84 days). If still pending at release time, v2.0 ships
  on the test PID with the swap as a documented follow-up — an explicit
  M12 decision, never a silent slip.
- After merge (live at https://pid.codes/1209/F1A6/): swap the shared
  PID constant in firmware + plugin discovery filter + docs; keep
  matching 1209:0001 in the plugin's transition filter until fielded
  devices are reflashed (dual-PID filter, collapsed at M12).

## Licensing decision (recorded and implemented)

The v2 SimHub plugin links the proprietary `SimHub.Plugins.dll` /
`GameReaderCommon.dll`. These are not GPLv3 "System Libraries" (SimHub
is an application, not a Major Component), and the plugin is
specifically designed to require them, so a bare-GPLv3 plugin binary
would carry a Corresponding Source obligation that no redistributor or
fork could satisfy — even though our releases never contain SimHub's
DLLs.

**Decision: keep GPL-3.0-or-later repo-wide and grant a GPLv3
section 7 additional permission** — the FSF GPL-FAQ template, which is
the SPDX-listed `GPL-3.0-linking-exception` (the pattern GNU Wget uses
for OpenSSL) — granted now, while the sole copyright holder can still
do so unilaterally; once external contributions land in `plugin/`,
adding it would need every contributor's consent. Scoped in practice to
`plugin/` (nothing else links SimHub). The MIT-for-plugin alternative
was rejected to keep plugin improvements copyleft in an ecosystem of
paid closed-source plugins.

Recorded in: [LICENSE-EXCEPTION](../LICENSE-EXCEPTION), the README
`## License` section, and
`SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception`
headers on `plugin/` sources.

Survey note: no existing GPL-licensed SimHub plugin on GitHub handles
this (stock GPL-3.0, LGPL, MIT, or no license — all ignore the linking
question).
