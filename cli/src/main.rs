//! uniflag-cli — binary-protocol test-pattern streamer and the firmware's
//! diagnostic instrument.
//!
//! Four modes:
//!
//! - **stream** (default): `Hello`/`HelloAck` handshake (prints fw
//!   version, protocol version, and panel size; refuses on a protocol
//!   mismatch), then COBS-framed `Frame` packets at a
//!   monotonic-deadline-paced 30 fps. Inbound `ButtonEvent` packets are
//!   decoded and printed live.
//! - **loopback**: no serial — generates the exact stream bytes and feeds
//!   them back through proto's decode pipeline in-process; exits non-zero
//!   if any self-emitted packet fails to decode. The host-side isolation
//!   tool for silent-failure debugging.
//! - **emit**: write exactly one encoded wire packet to the sink and exit
//!   — makes golden byte-diff verification executable from the shell.
//! - **view**: show a raw RGB888 frame as terminal half-blocks or a PNG.
//!   The panel exists to display a picture; a diagnostic tool that can
//!   only count bytes can't tell you the picture was wrong.
//!
//! Binary output goes to the sink (serial port, or stdout via
//! `--no-port`); all human status and diagnostics go to stderr. The
//! loopback packet summaries are that mode's product, so they go to
//! stdout.

use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::str::FromStr;
use std::time::{Duration, Instant};

use anyhow::{bail, Context, Result};
use clap::{Args, Parser, Subcommand};
use proto::packet::{Button, PressKind, PANEL_HEIGHT, PANEL_WIDTH, PROTOCOL_VERSION};
use uniflag_cli::patterns::Pattern;
use uniflag_cli::rx::{Decoder, OwnedPacket, RxEvent};
use uniflag_cli::{pacing, view, wire};

const DEFAULT_PORT: &str = "/dev/ttyACM0";
const DEFAULT_BAUD: u32 = 115_200;
const DEFAULT_FPS: u32 = 30;
const DEFAULT_PATTERN: &str = "gradient";
const DEFAULT_LOOPBACK_FRAMES: u64 = 90;

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

    /// Don't open the serial device — write wire bytes to stdout instead.
    #[arg(long, global = true)]
    no_port: bool,

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
    /// Decode the CLI's own emitted byte stream in-process — no serial.
    Loopback(LoopbackArgs),
    /// Write exactly one encoded wire packet to the sink and exit.
    #[command(subcommand)]
    Emit(EmitPacket),
    /// Show a raw RGB888 frame — terminal half-blocks, or a PNG.
    View(ViewArgs),
}

#[derive(Args, Debug, PartialEq)]
struct ViewArgs {
    /// Raw 3072-byte RGB888 frame, or `-` to read one from stdin.
    file: PathBuf,

    /// Write a PNG here instead of drawing to the terminal.
    #[arg(long)]
    png: Option<PathBuf>,

    /// Nearest-neighbour upscale applied to the PNG.
    #[arg(long, default_value_t = 8)]
    scale: usize,
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

    /// Skip the Hello/HelloAck handshake (required with --no-port, where
    /// nothing can answer it).
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

#[derive(Args, Debug)]
struct LoopbackArgs {
    /// Test pattern for the generated Frame packets.
    #[arg(long, default_value = DEFAULT_PATTERN, value_parser = Pattern::from_str)]
    pattern: Pattern,

    /// Number of Frame packets in the generated stream.
    #[arg(long, default_value_t = DEFAULT_LOOPBACK_FRAMES)]
    frames: u64,

    /// Include an initial Brightness packet, as --brightness does in
    /// stream mode.
    #[arg(long)]
    brightness: Option<u8>,
}

#[derive(Subcommand, Debug)]
enum EmitPacket {
    /// Hello carrying the host protocol version.
    Hello,
    /// Brightness set-point.
    Brightness {
        #[arg(long)]
        value: u8,
    },
    /// One Frame of a test pattern.
    Frame {
        #[arg(long, default_value = DEFAULT_PATTERN, value_parser = Pattern::from_str)]
        pattern: Pattern,
        #[arg(long, default_value_t = 0)]
        frame_index: u64,
    },
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    match &cli.command {
        Some(Command::Loopback(args)) => run_loopback(args),
        Some(Command::Emit(packet)) => run_emit(&cli, packet),
        Some(Command::View(args)) => run_view(args),
        Some(Command::Stream(args)) => run_stream(&cli, args),
        None => run_stream(&cli, &StreamArgs::default()),
    }
}

// Sink: writes wire bytes to either a serial port or stdout.
enum Sink {
    Serial(Box<dyn serialport::SerialPort>),
    Stdout(io::Stdout),
}

impl Sink {
    fn send(&mut self, bytes: &[u8]) -> Result<()> {
        match self {
            Sink::Serial(port) => port.write_all(bytes).context("write serial")?,
            Sink::Stdout(out) => {
                out.write_all(bytes).context("write stdout")?;
                out.flush().ok();
            }
        }
        Ok(())
    }
}

fn open_sink(cli: &Cli, timeout: Duration) -> Result<Sink> {
    if cli.no_port {
        return Ok(Sink::Stdout(io::stdout()));
    }
    let port = serialport::new(cli.port.to_string_lossy(), cli.baud)
        .timeout(timeout)
        .open()
        .with_context(|| format!("opening {}", cli.port.display()))?;
    eprintln!("uniflag-cli → {} @ {} baud", cli.port.display(), cli.baud);
    Ok(Sink::Serial(port))
}

/// Block until the serial device is openable again (replug after a yank,
/// re-enumeration after a watchdog reset).
fn reopen_serial(cli: &Cli, timeout: Duration) -> Sink {
    loop {
        std::thread::sleep(Duration::from_millis(500));
        match serialport::new(cli.port.to_string_lossy(), cli.baud)
            .timeout(timeout)
            .open()
        {
            Ok(port) => {
                eprintln!("reconnected to {}", cli.port.display());
                return Sink::Serial(port);
            }
            Err(_) => continue,
        }
    }
}

/// How one attempt to send on the sink ended. Serial failures are lost
/// links (the caller reopens and re-handshakes); stdout failures are
/// fatal and surface as the outer `Err`.
enum SendOutcome {
    Sent,
    Lost(anyhow::Error),
}

fn try_send(sink: &mut Sink, bytes: &[u8]) -> Result<SendOutcome> {
    match sink.send(bytes) {
        Ok(()) => Ok(SendOutcome::Sent),
        Err(err) => match sink {
            Sink::Serial(_) => Ok(SendOutcome::Lost(err)),
            Sink::Stdout(_) => Err(err),
        },
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
    if cli.no_port && !args.no_handshake {
        bail!(
            "--no-port writes to stdout, which cannot answer the handshake; \
             pass --no-handshake to stream without one"
        );
    }
    let fps = args.fps.max(1);
    eprintln!(
        "streaming {:?} at {fps} fps (Ctrl-C to stop){}",
        args.pattern,
        args.seconds
            .map(|s| format!(", for {s}s"))
            .unwrap_or_default()
    );

    let run_started = Instant::now();
    let mut sink = open_sink(cli, STREAM_TIMEOUT)?;
    loop {
        // Handshake refusals (protocol mismatch, no HelloAck) propagate as
        // errors — deliberate stops, not link flaps, so never retried.
        start_session(&mut sink, args)?;
        match stream_frames(&mut sink, args, fps, run_started)? {
            SessionOutcome::Finished => return Ok(()),
            SessionOutcome::LinkLost(err) => {
                eprintln!("link lost ({err:#}); waiting for {}", cli.port.display());
                sink = reopen_serial(cli, STREAM_TIMEOUT);
            }
        }
    }
}

/// Per-(re)connect setup: the Hello/HelloAck handshake, then the optional
/// Brightness re-send (the device never assumes a value survives a
/// reconnect).
fn start_session(sink: &mut Sink, args: &StreamArgs) -> Result<()> {
    if !args.no_handshake {
        let Sink::Serial(port) = sink else {
            bail!("handshake requires a serial port"); // unreachable: guarded in run_stream
        };
        handshake(port.as_mut())?;
        port.set_timeout(STREAM_TIMEOUT).ok();
    }
    if let Some(value) = args.brightness {
        sink.send(&wire::brightness(value)?)?;
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
/// for the device's stream-as-heartbeat timeout.
fn stream_frames(
    sink: &mut Sink,
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
            if let SendOutcome::Lost(err) = try_send(sink, &wire::brightness(value)?)? {
                return Ok(SessionOutcome::LinkLost(err));
            }
        }
        if let SendOutcome::Lost(err) = try_send(sink, &wire::frame(args.pattern, index)?)? {
            return Ok(SessionOutcome::LinkLost(err));
        }
        sent += 1;

        if let Err(err) = poll_inbound(sink, &mut decoder) {
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
fn poll_inbound(sink: &mut Sink, decoder: &mut Decoder) -> Result<()> {
    let Sink::Serial(port) = sink else {
        return Ok(());
    };
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

/// Generate the exact stream bytes (handshake Hello included — see
/// [`wire::loopback_stream`]) and decode them in-process. Every packet
/// summary goes to stdout; any decode failure exits non-zero.
fn run_loopback(args: &LoopbackArgs) -> Result<()> {
    let (bytes, expected) = wire::loopback_stream(args.pattern, args.frames, args.brightness)?;
    eprintln!(
        "loopback: {} bytes encoding {expected} packets ({:?}, {} frames)",
        bytes.len(),
        args.pattern,
        args.frames
    );

    let mut decoder = Decoder::default();
    let mut decoded = 0usize;
    let mut failures = 0usize;
    let mut stdout = io::stdout();
    // Feed in transport-sized chunks so the accumulator paths get real work.
    for chunk in bytes.chunks(1024) {
        for event in decoder.feed(chunk) {
            match event {
                RxEvent::Packet(packet) => {
                    decoded += 1;
                    writeln!(stdout, "{decoded:>5}  {}", packet.summary())
                        .context("write stdout")?;
                }
                RxEvent::Unknown { ty } => {
                    failures += 1;
                    eprintln!("loopback: self-emitted packet decoded as unknown type {ty:#04x}");
                }
                RxEvent::Dropped(reason) => {
                    failures += 1;
                    eprintln!("loopback: self-emitted packet dropped ({reason:?})");
                }
            }
        }
    }

    if failures > 0 || decoded != expected {
        bail!(
            "loopback FAILED: {expected} packets emitted, {decoded} decoded, \
             {failures} failures — the TX bytes and the decode pipeline disagree"
        );
    }
    eprintln!("loopback OK: all {decoded} packets decoded");
    Ok(())
}

fn run_emit(cli: &Cli, packet: &EmitPacket) -> Result<()> {
    let (bytes, what) = match packet {
        EmitPacket::Hello => (wire::hello()?, "Hello".to_string()),
        EmitPacket::Brightness { value } => (
            wire::brightness(*value)?,
            format!("Brightness {{ value: {value} }}"),
        ),
        EmitPacket::Frame {
            pattern,
            frame_index,
        } => (
            wire::frame(*pattern, *frame_index)?,
            format!("Frame {{ {pattern:?}, frame_index: {frame_index} }}"),
        ),
    };
    let mut sink = open_sink(cli, STREAM_TIMEOUT)?;
    sink.send(&bytes)?;
    eprintln!("emitted {what} ({} wire bytes)", bytes.len());
    Ok(())
}

fn run_view(args: &ViewArgs) -> Result<()> {
    let raw = if args.file.as_os_str() == "-" {
        let mut buf = Vec::new();
        io::stdin()
            .read_to_end(&mut buf)
            .context("reading a frame from stdin")?;
        buf
    } else {
        std::fs::read(&args.file).with_context(|| format!("reading {}", args.file.display()))?
    };

    if raw.len() != view::FRAME_LEN {
        bail!(
            "expected a {}-byte RGB888 frame, got {} bytes",
            view::FRAME_LEN,
            raw.len()
        );
    }

    match &args.png {
        Some(path) => {
            let encoded = view::png(&raw, PANEL_WIDTH, PANEL_HEIGHT, args.scale);
            std::fs::write(path, &encoded)
                .with_context(|| format!("writing {}", path.display()))?;
            eprintln!(
                "wrote {} ({} bytes, {}x upscale)",
                path.display(),
                encoded.len(),
                args.scale
            );
        }
        None => print!("{}", view::ansi(&raw, PANEL_WIDTH, PANEL_HEIGHT)),
    }
    Ok(())
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
    fn emit_subcommands_parse() {
        let cli = Cli::try_parse_from(["uniflag-cli", "emit", "brightness", "--value", "200"])
            .expect("parse");
        assert!(matches!(
            cli.command,
            Some(Command::Emit(EmitPacket::Brightness { value: 200 }))
        ));
        let cli = Cli::try_parse_from([
            "uniflag-cli",
            "emit",
            "frame",
            "--pattern",
            "moving-pixel",
            "--frame-index",
            "42",
        ])
        .expect("parse");
        assert!(matches!(
            cli.command,
            Some(Command::Emit(EmitPacket::Frame {
                pattern: Pattern::MovingPixel,
                frame_index: 42,
            }))
        ));
    }
}
