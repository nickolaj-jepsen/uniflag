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

# Format the entire workspace (firmware included).
fmt:
    cargo fmt --all

fmt-check:
    cargo fmt --all -- --check

# Mirror CI: clippy on host crates, then on firmware (different target).
clippy:
    cargo clippy -p proto -p screens -p uniflag-cli --all-targets -- -D warnings
    cargo clippy --all-targets --manifest-path firmware/Cargo.toml --target thumbv6m-none-eabi -- -D warnings

# Tests on host crates only (the firmware binary is no_std, `test = false`
# — `screens` is the firmware's paint code, split out so it can be tested).
test:
    cargo test -p proto -p screens -p uniflag-cli --all-targets

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

# Regenerate the regenerable golden fixtures (testdata/proto byte vectors).
# Only ever run this deliberately, in a reviewed commit — the proto vectors
# are the frozen wire contract both the Rust and C# suites must match
# byte-exactly, so a regen that changes anything IS a protocol change.
#
# This is the whole of it: the renderer has no byte corpus. Visual work is
# reviewed by eye through `just frames-sheet`, not by regenerating fixtures.
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
simhubdash:
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File packaging\make-simhubdash.ps1 -DashFolder "overlay/dash/Uniflag Overlay" -OutFile "overlay/dash/Uniflag Overlay.simhubdash"

# Assemble the release zip locally: plugin DLL + firmware UF2 + overlay dash +
# INSTALL.md, identical layout (and only-one-DLL audit) to the CI release job.
# Output: target/uniflag-<git describe>.zip (or uniflag-dev.zip if dirty).
[windows]
package: plugin-build img
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File packaging\package.ps1
