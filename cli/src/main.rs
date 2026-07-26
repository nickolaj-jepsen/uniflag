//! uniflag-cli — binary-protocol test-pattern streamer and the firmware's
//! diagnostic instrument.
//!
//! Three modes:
//!
//! - **stream** (default): `Hello`/`HelloAck` handshake (prints fw
//!   version, protocol version, and panel size; refuses on a protocol
//!   mismatch), then COBS-framed `Frame` packets at a
//!   monotonic-deadline-paced 30 fps. Inbound `ButtonEvent` packets are
//!   decoded and printed live.
//! - **doctor**: report the toolchains, SimHub assemblies and devices
//!   present on this machine (`just doctor` is a thin wrapper).
//! - **gate**: run a command only if its toolchain requirement is met,
//!   else print a loud `SKIPPED` line (`just check`'s leg selection).
//!
//! Binary output goes to the serial port; all human status and
//! diagnostics go to stderr, except the doctor report, which *is* its
//! mode's product and goes to stdout.

mod doctor;
mod pacing;
mod patterns;
mod rx;
mod wire;

use std::io;
use std::path::PathBuf;
use std::str::FromStr;
use std::time::{Duration, Instant};

use anyhow::{bail, Context, Result};
use clap::{Args, Parser, Subcommand};
use proto::packet::{Button, PressKind, PROTOCOL_VERSION};

use crate::patterns::Pattern;
use crate::rx::{Decoder, OwnedPacket, RxEvent};

const DEFAULT_PORT: &str = "/dev/ttyACM0";
const DEFAULT_BAUD: u32 = 115_200;
const DEFAULT_FPS: u32 = 30;
const DEFAULT_PATTERN: &str = "gradient";

/// How long the stream mode waits for `HelloAck` before giving up.
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(2);
/// Serial timeout while streaming: frame writes are ~3 KB bursts and a
/// briefly backpressuring device shouldn't kill the session.
const STREAM_TIMEOUT: Duration = Duration::from_secs(1);
/// Short serial read timeout during handshake so the loop rechecks its deadline often.
const HANDSHAKE_POLL: Duration = Duration::from_millis(100);
/// Cadence of the periodic stream status line on stderr.
const REPORT_EVERY: Duration = Duration::from_secs(5);

#[derive(Parser, Debug)]
#[command(
    version,
    about = "Binary-protocol test-pattern streamer for the uniflag firmware"
)]
struct Cli {
    /// Serial device the firmware is enumerated as.
    #[arg(long, global = true, default_value = DEFAULT_PORT)]
    port: PathBuf,

    /// Override baud rate (USB CDC ignores this but some tools care).
    #[arg(long, global = true, default_value_t = DEFAULT_BAUD)]
    baud: u32,

    #[command(subcommand)]
    command: Option<Command>,
}

#[derive(Subcommand, Debug)]
enum Command {
    /// Stream Frame packets at a paced rate (the default subcommand).
    Stream(StreamArgs),
    /// Report the toolchains, SimHub assemblies and devices present here.
    Doctor(DoctorArgs),
    /// Run a command if a requirement is met, else print a loud SKIPPED
    /// line and exit 0. `just check`'s leg selection lives here for the
    /// same reason doctor does: a probe written in both bash and
    /// PowerShell drifts.
    Gate(GateArgs),
}

/// The justfile owns these defaults and passes them in.
#[derive(Args, Debug, PartialEq)]
struct DoctorArgs {
    /// Serial device the firmware is expected to enumerate as.
    #[arg(long, default_value = DEFAULT_PORT)]
    serial: String,

    /// BOOTSEL mass-storage mount point.
    #[arg(long, default_value = "/run/media/RPI-RP2")]
    mount: String,

    /// SimHub install providing the plugin's reference DLLs.
    #[arg(long, default_value = "C:\\Program Files (x86)\\SimHub")]
    simhub_dir: String,
}

#[derive(Args, Debug, PartialEq)]
struct GateArgs {
    /// What the gated command needs to be able to run.
    #[arg(value_enum)]
    requirement: Requirement,

    /// SimHub install providing the plugin's reference DLLs (simhub only).
    #[arg(long, default_value = "C:\\Program Files (x86)\\SimHub")]
    simhub_dir: String,

    /// The command to run when the requirement is met (after `--`).
    #[arg(last = true, required = true)]
    command: Vec<String>,
}

#[derive(clap::ValueEnum, Clone, Copy, Debug, PartialEq)]
enum Requirement {
    /// A dotnet SDK on PATH (the cross-platform C# leg).
    Dotnet,
    /// Windows plus SimHub's reference DLLs (the net48 plugin leg).
    Simhub,
}

#[derive(Args, Debug, PartialEq)]
struct StreamArgs {
    /// Test pattern: solid:<named-or-hex-color>, gradient, moving-pixel,
    /// checkerboard, or brightness-sweep.
    #[arg(long, default_value = DEFAULT_PATTERN, value_parser = Pattern::from_str)]
    pattern: Pattern,

    /// Frame rate.
    #[arg(long, default_value_t = DEFAULT_FPS)]
    fps: u32,

    /// Send a Brightness packet with this value after each (re)connect.
    #[arg(long)]
    brightness: Option<u8>,

    /// Skip the Hello/HelloAck handshake, and with it the
    /// protocol-version check.
    #[arg(long)]
    no_handshake: bool,

    /// Stop after this many seconds (default: run until interrupted).
    #[arg(long)]
    seconds: Option<u64>,
}

impl Default for StreamArgs {
    /// Must match the clap defaults above (pinned by a unit test) so a
    /// bare `uniflag-cli` equals `uniflag-cli stream`.
    fn default() -> Self {
        StreamArgs {
            pattern: Pattern::Gradient,
            fps: DEFAULT_FPS,
            brightness: None,
            no_handshake: false,
            seconds: None,
        }
    }
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    match &cli.command {
        Some(Command::Doctor(args)) => {
            doctor::report(&doctor::Targets {
                serial: &args.serial,
                mount: &args.mount,
                simhub_dir: &args.simhub_dir,
            });
            Ok(())
        }
        Some(Command::Gate(args)) => run_gate(args),
        Some(Command::Stream(args)) => run_stream(&cli, args),
        None => run_stream(&cli, &StreamArgs::default()),
    }
}

/// Skips exit 0 with a loud line (a silent omission reads as a pass);
/// a met requirement makes this transparent to the gated command's
/// success or failure.
fn run_gate(args: &GateArgs) -> Result<()> {
    let unmet: Option<String> = match args.requirement {
        Requirement::Dotnet => (!doctor::have_dotnet()).then(|| "dotnet not on PATH".to_string()),
        Requirement::Simhub => {
            if !cfg!(windows) {
                Some("the net48 plugin leg is Windows-only".to_string())
            } else if doctor::missing_simhub_dlls(&args.simhub_dir).is_empty() {
                None
            } else {
                Some(format!(
                    "no SimHub reference DLLs at {}; set UNIFLAG_SIMHUB_DIR",
                    args.simhub_dir
                ))
            }
        }
    };
    let display = args.command.join(" ");
    if let Some(reason) = unmet {
        println!("SKIPPED: {display} ({reason})");
        return Ok(());
    }
    let status = std::process::Command::new(&args.command[0])
        .args(&args.command[1..])
        .status()
        .with_context(|| format!("could not run {display}"))?;
    if !status.success() {
        bail!("{display} failed");
    }
    Ok(())
}

fn open_port(cli: &Cli, timeout: Duration) -> Result<Box<dyn serialport::SerialPort>> {
    let port = serialport::new(cli.port.to_string_lossy(), cli.baud)
        .timeout(timeout)
        .open()
        .with_context(|| format!("opening {}", cli.port.display()))?;
    eprintln!("uniflag-cli → {} @ {} baud", cli.port.display(), cli.baud);
    Ok(port)
}

/// Block until the serial device is openable again (replug after a yank,
/// re-enumeration after a watchdog reset).
fn reopen_port(cli: &Cli, timeout: Duration) -> Box<dyn serialport::SerialPort> {
    loop {
        std::thread::sleep(Duration::from_millis(500));
        match serialport::new(cli.port.to_string_lossy(), cli.baud)
            .timeout(timeout)
            .open()
        {
            Ok(port) => {
                eprintln!("reconnected to {}", cli.port.display());
                return port;
            }
            Err(_) => continue,
        }
    }
}

/// How a whole streaming session ended.
enum SessionOutcome {
    /// `--seconds` elapsed; the run is complete.
    Finished,
    /// The serial link failed mid-stream; reopen and start a new session.
    LinkLost(anyhow::Error),
}

fn run_stream(cli: &Cli, args: &StreamArgs) -> Result<()> {
    let fps = args.fps.max(1);
    eprintln!(
        "streaming {:?} at {fps} fps (Ctrl-C to stop){}",
        args.pattern,
        args.seconds
            .map(|s| format!(", for {s}s"))
            .unwrap_or_default()
    );

    let run_started = Instant::now();
    let mut port = open_port(cli, STREAM_TIMEOUT)?;
    loop {
        // Handshake refusals (protocol mismatch, no HelloAck) propagate as
        // errors — deliberate stops, not link flaps, so never retried.
        start_session(port.as_mut(), args)?;
        match stream_frames(port.as_mut(), args, fps, run_started)? {
            SessionOutcome::Finished => return Ok(()),
            SessionOutcome::LinkLost(err) => {
                eprintln!("link lost ({err:#}); waiting for {}", cli.port.display());
                port = reopen_port(cli, STREAM_TIMEOUT);
            }
        }
    }
}

/// Per-(re)connect setup: the Hello/HelloAck handshake, then the optional
/// Brightness re-send (the device never assumes a value survives a
/// reconnect).
fn start_session(port: &mut dyn serialport::SerialPort, args: &StreamArgs) -> Result<()> {
    if !args.no_handshake {
        handshake(port)?;
        port.set_timeout(STREAM_TIMEOUT).ok();
    }
    if let Some(value) = args.brightness {
        port.write_all(&wire::brightness(value)?)
            .context("send Brightness")?;
        eprintln!("sent Brightness {{ value: {value} }}");
    }
    Ok(())
}

/// Send `Hello`, wait for `HelloAck`, print what the device reported, and
/// refuse to continue on a protocol-version mismatch.
fn handshake(port: &mut dyn serialport::SerialPort) -> Result<()> {
    port.set_timeout(HANDSHAKE_POLL).ok();
    port.write_all(&wire::hello()?).context("send Hello")?;

    let deadline = Instant::now() + HANDSHAKE_TIMEOUT;
    let mut decoder = Decoder::default();
    let mut buf = [0u8; 512];
    while Instant::now() < deadline {
        let n = match port.read(&mut buf) {
            Ok(n) => n,
            Err(err) if err.kind() == io::ErrorKind::TimedOut => continue,
            Err(err) => return Err(err).context("read during handshake"),
        };
        for event in decoder.feed(&buf[..n]) {
            let RxEvent::Packet(OwnedPacket::HelloAck {
                protocol_version,
                width,
                height,
                fw_version,
            }) = event
            else {
                continue; // pre-ack noise; the decoder already resynced
            };
            eprintln!(
                "device: fw {}, protocol v{protocol_version}, panel {width}x{height}",
                String::from_utf8_lossy(&fw_version)
            );
            if protocol_version != PROTOCOL_VERSION {
                bail!(
                    "protocol version mismatch: host v{PROTOCOL_VERSION}, device \
                     v{protocol_version} — refusing to stream (update the firmware or \
                     rebuild the CLI so the versions agree)"
                );
            }
            return Ok(());
        }
    }
    bail!(
        "no HelloAck within {HANDSHAKE_TIMEOUT:?} — is v2 firmware flashed and is this \
         the right port? (--no-handshake skips the check)"
    );
}

/// The paced frame loop for one session. Frame `n`'s deadline is
/// `epoch + n/fps` on a monotonic clock; when the loop falls behind it
/// skips missed deadlines (see [`pacing`]) so the cadence stays honest
/// for the device's stream-as-heartbeat timeout. A failed serial write is
/// a lost link: the session ends and the caller reopens the port.
fn stream_frames(
    port: &mut dyn serialport::SerialPort,
    args: &StreamArgs,
    fps: u32,
    run_started: Instant,
) -> Result<SessionOutcome> {
    let epoch = Instant::now();
    let mut decoder = Decoder::default();
    let mut index: u64 = 0;
    let mut sent: u64 = 0;
    let mut skipped: u64 = 0;
    let mut last_report = epoch;

    loop {
        if let Some(seconds) = args.seconds {
            if run_started.elapsed() >= Duration::from_secs(seconds) {
                eprintln!(
                    "done: sent={sent} skipped={skipped} in {:?}",
                    epoch.elapsed()
                );
                return Ok(SessionOutcome::Finished);
            }
        }

        // The sweep's Brightness rides just ahead of its frame.
        if let Some(value) = args.pattern.brightness_for_frame(index) {
            if let Err(err) = port.write_all(&wire::brightness(value)?) {
                return Ok(SessionOutcome::LinkLost(err.into()));
            }
        }
        if let Err(err) = port.write_all(&wire::frame(args.pattern, index)?) {
            return Ok(SessionOutcome::LinkLost(err.into()));
        }
        sent += 1;

        if let Err(err) = poll_inbound(port, &mut decoder) {
            return Ok(SessionOutcome::LinkLost(err));
        }

        if last_report.elapsed() >= REPORT_EVERY {
            let elapsed = epoch.elapsed().as_secs_f64();
            eprintln!(
                "t={elapsed:>7.1}s sent={sent} skipped={skipped} avg_fps={:.2}",
                sent as f64 / elapsed
            );
            last_report = Instant::now();
        }

        let next = pacing::next_frame_index(index, epoch.elapsed().as_nanos(), fps);
        skipped += next - index - 1;
        let deadline = pacing::deadline_nanos(next, fps);
        let now = epoch.elapsed().as_nanos();
        if deadline > now {
            std::thread::sleep(Duration::from_nanos((deadline - now) as u64));
        }
        index = next;
    }
}

/// Drain and report whatever inbound bytes are waiting, without ever
/// blocking the pacing loop.
fn poll_inbound(port: &mut dyn serialport::SerialPort, decoder: &mut Decoder) -> Result<()> {
    loop {
        let available = port.bytes_to_read().context("bytes_to_read")?;
        if available == 0 {
            return Ok(());
        }
        let mut buf = vec![0u8; (available as usize).min(4096)];
        let n = port.read(&mut buf).context("read inbound")?;
        for event in decoder.feed(&buf[..n]) {
            report_inbound(&event);
        }
    }
}

fn report_inbound(event: &RxEvent) {
    match event {
        RxEvent::Packet(OwnedPacket::ButtonEvent { button, kind }) => {
            eprintln!("button: {}", describe_button_event(*button, *kind));
        }
        RxEvent::Packet(OwnedPacket::HelloAck { .. }) => {
            eprintln!("unexpected HelloAck mid-stream (device rebooted?)");
        }
        RxEvent::Packet(_) => {} // host->device types echoed back; ignore
        RxEvent::Unknown { .. } => {} // forward compat: silent
        RxEvent::Dropped(reason) => {
            eprintln!("rx: dropped a segment ({reason:?})");
        }
    }
}

/// Human name for a ButtonEvent, falling back to raw ids for unassigned
/// values (the id space is open — never reject).
fn describe_button_event(button: u8, kind: u8) -> String {
    let button_name = match Button::from_byte(button) {
        Some(Button::BrightnessUp) => "brightness-up (GPIO 21)".to_string(),
        Some(Button::BrightnessDown) => "brightness-down (GPIO 26)".to_string(),
        Some(Button::Sleep) => "sleep (GPIO 27)".to_string(),
        None => format!("button {button}"),
    };
    let kind_name = match PressKind::from_byte(kind) {
        Some(PressKind::Short) => "short press".to_string(),
        Some(PressKind::Long) => "long press".to_string(),
        None => format!("press kind {kind}"),
    };
    format!("{button_name}, {kind_name}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cli_definition_is_consistent() {
        use clap::CommandFactory;
        Cli::command().debug_assert();
    }

    #[test]
    fn bare_invocation_equals_the_stream_subcommand_defaults() {
        // `uniflag-cli` (no subcommand) runs StreamArgs::default(); pin it
        // to what `uniflag-cli stream` parses so the two can't drift.
        let cli = Cli::try_parse_from(["uniflag-cli", "stream"]).expect("parse");
        let Some(Command::Stream(parsed)) = cli.command else {
            panic!("expected the stream subcommand");
        };
        assert_eq!(parsed, StreamArgs::default());
    }

    #[test]
    fn doctor_takes_its_lookup_paths_from_the_caller() {
        let cli = Cli::try_parse_from([
            "uniflag-cli",
            "doctor",
            "--serial",
            "COM5",
            "--mount",
            "D:\\",
            "--simhub-dir",
            "C:\\SimHub",
        ])
        .expect("parse");
        let Some(Command::Doctor(args)) = cli.command else {
            panic!("expected the doctor subcommand");
        };
        assert_eq!(args.serial, "COM5");
        assert_eq!(args.mount, "D:\\");
        assert_eq!(args.simhub_dir, "C:\\SimHub");
    }
}
