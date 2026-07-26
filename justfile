set shell := ["bash", "-cu"]
set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

elf := "target/thumbv6m-none-eabi/release/uniflag"
uf2 := "target/uniflag.uf2"

# Override on Windows with `$env:UNIFLAG_SERIAL = 'COM5'` etc. before running just.
serial := if os_family() == "windows" {
    env_var_or_default('UNIFLAG_SERIAL', 'COM3')
} else {
    "/dev/ttyACM0"
}
mount := if os_family() == "windows" {
    env_var_or_default('UNIFLAG_MOUNT', 'D:\')
} else {
    "/run/media/" + env_var_or_default('USER', '') + "/RPI-RP2"
}
# SimHub install (or extracted-installer dir) providing the plugin's reference DLLs.
simhub_dir := env_var_or_default('UNIFLAG_SIMHUB_DIR', 'C:\Program Files (x86)\SimHub')

default:
    @just --list

# --- One-command gate --------------------------------------------------

# Run every leg available on this machine. `just test` is NOT everything:
# it covers the Rust host crates only. This adds the cross-platform C#
# leg and, on Windows with SimHub present, the plugin leg.
#
# Legs that can't run here are reported as SKIPPED and the recipe still
# exits 0 — a silent omission reads as a pass, which is how "it's green"
# gets reported for a suite that never ran.
[doc("Run every test leg available on this machine; skips are loud.")]
check: fmt-check clippy test _check-cs
    @echo ""
    @echo "check: done (any SKIPPED legs above did not run)"

[unix]
_check-cs:
    #!/usr/bin/env bash
    set -eu
    if command -v dotnet >/dev/null 2>&1; then
        just core-test
    else
        echo "SKIPPED: core tests (dotnet not on PATH)"
    fi
    echo "SKIPPED: plugin tests (needs Windows + a SimHub install)"

[windows]
_check-cs:
    @$d = $null; try { $d = Get-Command dotnet -ErrorAction Stop } catch {}; if ($d) { just core-test } else { Write-Host "SKIPPED: core tests (dotnet not on PATH)" }
    @if (Test-Path '{{simhub_dir}}\SimHub.Plugins.dll') { just plugin-test } else { Write-Host "SKIPPED: plugin tests (no SimHub reference DLLs at {{simhub_dir}}; set UNIFLAG_SIMHUB_DIR)" }

# What's installed, what isn't, and what to do about it. Run this first
# when a recipe fails for reasons that look like the environment.
#
# The report lives in `uniflag-cli doctor` (cli/src/doctor.rs): a recipe
# that has to work on both bash and PowerShell gets written twice and the
# two copies drift. This recipe owns only the platform-flavoured paths.
[doc("Report which toolchains, SimHub DLLs and devices are present.")]
doctor:
    cargo run --quiet -p uniflag-cli -- doctor --serial '{{serial}}' --mount '{{mount}}' --simhub-dir '{{simhub_dir}}'

# Format the entire workspace (firmware included).
fmt:
    cargo fmt --all

[doc("Formatting gate (CI runs this).")]
fmt-check:
    cargo fmt --all -- --check

# Mirror CI: clippy on host crates, then on firmware (different target).
clippy:
    cargo clippy -p proto -p screens -p uniflag-cli --all-targets -- -D warnings
    cargo clippy --all-targets --manifest-path firmware/Cargo.toml --target thumbv6m-none-eabi -- -D warnings

# Tests on host crates only (the firmware binary is no_std, `test = false`
# — `screens` is the firmware's paint code, split out so it can be tested).
[doc("Rust host-crate tests. NOT everything -- see `just check`.")]
test:
    cargo test -p proto -p screens -p uniflag-cli --all-targets

# The cross-platform C# leg: renderer + wire codec, no SimHub, no Windows.
# Targets the csproj rather than the .sln on purpose — the solution also
# contains net48 projects, which only build on Windows.
[doc("Cross-platform C# tests: renderer + wire codec, no SimHub.")]
core-test:
    dotnet test plugin/tests-core/Uniflag.Core.Tests.csproj -c Release

# Build the firmware ELF (release).
build:
    cargo build --release --manifest-path firmware/Cargo.toml --target thumbv6m-none-eabi

# Convert the firmware ELF to a UF2 image.
img: build
    elf2uf2-rs {{elf}} {{uf2}}

# Wait for RPI-RP2 BOOTSEL to mount (hold BOOTSEL while plugging USB).
[unix]
wait:
    @echo "Waiting for {{mount}} ..."
    @until mountpoint -q {{mount}} 2>/dev/null && [ -w {{mount}} ]; do sleep 0.5; done
    @echo "RPI-RP2 mounted."

# Wait for RPI-RP2 BOOTSEL to mount (hold BOOTSEL while plugging USB).
[windows]
wait:
    @echo "Waiting for {{mount}} ..."
    while (-not (Test-Path '{{mount}}')) { Start-Sleep -Milliseconds 500 }
    @echo "RPI-RP2 mounted."

# Copy the UF2 onto the mounted RPI-RP2; board reboots into the new firmware.
[unix]
copy:
    cp {{uf2}} {{mount}}/
    @echo "Flashed. Board should reboot into the new firmware."

# Copy the UF2 onto the mounted RPI-RP2; board reboots into the new firmware.
[windows]
copy:
    Copy-Item '{{uf2}}' '{{mount}}'
    @echo "Flashed. Board should reboot into the new firmware."

# build → img → wait → copy → chmod-serial
flash: img wait copy chmod-serial

[unix]
chmod-serial:
    @echo "Waiting for {{serial}} ..."
    @until [ -e {{serial}} ]; do sleep 0.5; done
    sudo chmod 666 {{serial}}

# Windows has no permission step; just wait for the COM port to appear.
[windows]
chmod-serial:
    @echo "Waiting for {{serial}} ..."
    while (-not ([System.IO.Ports.SerialPort]::GetPortNames() -contains '{{serial}}')) { Start-Sleep -Milliseconds 500 }

# Run the bring-up CLI against the device (streams a test pattern by default).
cli *ARGS: chmod-serial
    cargo run --release -p uniflag-cli -- --port {{serial}} {{ARGS}}

# Serve the overlay test page over http:// (for contexts that refuse file://).
overlay-serve:
    python -m http.server 8000 --directory overlay

# --- Looking at frames -------------------------------------------------
# The panel is a picture. These render what the C# renderer actually paints,
# with no SimHub, no hardware and no game — on any OS. Output: target/frames/.

# List the scenario catalogue (names + sample frames + descriptions).
frames-list:
    dotnet run --project plugin/tools -c Release -- list

# Render one scenario to PNG. Extra args pass through: --frame N, --scale N,
# --out PATH, --rgb.
[doc("Render one scenario to PNG (target/frames/).")]
frames SCENARIO *ARGS:
    dotnet run --project plugin/tools -c Release -- render {{SCENARIO}} {{ARGS}}

# The whole catalogue as one labelled, self-contained contact-sheet.html.
# This is the visual review the painters are checked by.
[doc("Render the whole catalogue to one labelled HTML contact sheet.")]
frames-sheet *ARGS:
    dotnet run --project plugin/tools -c Release -- sheet {{ARGS}}

# One frame as terminal half-blocks — takes a scenario name or a .rgb path.
frames-ansi TARGET *ARGS:
    dotnet run --project plugin/tools -c Release -- ansi {{TARGET}} {{ARGS}}

# Regenerate the regenerable golden fixtures (testdata/proto byte vectors).
# Only ever run this deliberately, in a reviewed commit — the proto vectors
# are the bytes both the Rust and C# suites are checked against, so a regen
# that changes anything IS a protocol change.
#
# This is the whole of it: the renderer has no byte corpus. Visual work is
# reviewed by eye through `just frames-sheet`, not by regenerating fixtures.
[doc("Regenerate testdata/proto byte vectors. Reviewed commits only.")]
golden-regen:
    cargo test -p proto --test golden_vectors -- --ignored regen

# Build the SimHub plugin (override the SimHub location with $env:UNIFLAG_SIMHUB_DIR).
[windows]
plugin-build:
    dotnet build plugin/UniflagPlugin.sln -c Release "-p:SimHubDir={{simhub_dir}}"

# Run the SimHub plugin test suite.
[windows]
plugin-test:
    dotnet test plugin/UniflagPlugin.sln -c Release "-p:SimHubDir={{simhub_dir}}"

# Pack the overlay dash into a double-click .simhubdash (gitignored build
# output) for import testing. `just package` and the CI release job run the
# same generator; this recipe is just the standalone loop.
[windows]
[doc("Pack the overlay dash into a double-click .simhubdash.")]
simhubdash:
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File packaging\make-simhubdash.ps1 -DashFolder "overlay/dash/Uniflag Overlay" -OutFile "overlay/dash/Uniflag Overlay.simhubdash"

# Assemble the release zip locally: plugin DLL + firmware UF2 + overlay dash +
# INSTALL.md, identical layout (and only-one-DLL audit) to the CI release job.
# Output: target/uniflag-<git describe>.zip (or uniflag-dev.zip if dirty).
[windows]
[doc("Assemble the release zip locally (Windows).")]
package: plugin-build img
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File packaging\package.ps1
