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

default:
    @just --list

# Format the entire workspace (firmware included).
fmt:
    cargo fmt --all

fmt-check:
    cargo fmt --all -- --check

# Mirror CI: clippy on host crates, then on firmware (different target).
clippy:
    cargo clippy -p proto -p uniflag-sim --all-targets -- -D warnings
    cargo clippy --all-targets --manifest-path firmware/Cargo.toml -- -D warnings

# Tests on host crates only (firmware is no_std, `test = false`).
test:
    cargo test -p proto -p uniflag-sim --all-targets

# Build the firmware ELF (release).
build:
    cargo build --release --manifest-path firmware/Cargo.toml

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

# Run the host-side simulator interactively against the device.
sim *ARGS: chmod-serial
    cargo run --release -p uniflag-sim -- --port {{serial}} {{ARGS}}
