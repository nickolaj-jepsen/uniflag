set shell := ["bash", "-cu"]

elf    := "target/thumbv6m-none-eabi/release/uniflag"
uf2    := "target/uniflag.uf2"
serial := "/dev/ttyACM0"
mount  := "/run/media/" + env_var('USER') + "/RPI-RP2"

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
    cd firmware && cargo clippy --all-targets -- -D warnings

# Tests on host crates only (firmware is no_std, `test = false`).
test:
    cargo test -p proto -p uniflag-sim --all-targets

# Build the firmware ELF (release).
build:
    cd firmware && cargo build --release

# Convert the firmware ELF to a UF2 image.
img: build
    elf2uf2-rs {{elf}} {{uf2}}

# Wait for RPI-RP2 BOOTSEL to mount (hold BOOTSEL while plugging USB).
wait:
    @echo "Waiting for {{mount}} ..."
    @until mountpoint -q {{mount}} 2>/dev/null && [ -w {{mount}} ]; do sleep 0.5; done
    @echo "RPI-RP2 mounted."

# Copy the UF2 onto the mounted RPI-RP2; board reboots into the new firmware.
copy:
    cp {{uf2}} {{mount}}/
    @echo "Flashed. Board should reboot into the new firmware."

# build → img → wait → copy → chmod-serial
flash: img wait copy chmod-serial

chmod-serial:
    @echo "Waiting for {{serial}} ..."
    @until [ -e {{serial}} ]; do sleep 0.5; done
    sudo chmod 666 {{serial}}

# Run the host-side simulator interactively against the device.
sim *ARGS: chmod-serial
    cargo run --release -p uniflag-sim -- --port {{serial}} {{ARGS}}

# Quick PIO/firmware sanity check via probe-rs (defmt over RTT).
run-probe:
    cd firmware && cargo run --release
