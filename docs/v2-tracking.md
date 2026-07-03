# v2 external clocks (M1)

Tracks the two unbounded external clocks started in M1 of
[v2-plan.md](v2-plan.md). (Written as a doc so it lives with the code;
paste into a GitHub issue if issue-tracking is preferred.)

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
