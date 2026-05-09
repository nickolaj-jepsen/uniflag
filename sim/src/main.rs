//! uniflag-sim — host-side simulator that pretends to be SimHub.
//!
//! Three modes:
//!
//! - **interactive** (default): hotkey-driven, mutates a `proto::State` and
//!   re-emits it whenever it changes (plus a 1 s heartbeat).
//! - **scripted** (`--script PATH`): replays a `<delay-ms> <line>` text file.
//! - **demo** (`--demo`): cycles through every flag value, 2 s each.
//!
//! Output goes to a serial port (`--port PATH`) or to stdout (`--no-port`).

use std::fs;
use std::io::{self, Write};
use std::path::PathBuf;
use std::time::{Duration, Instant};

use anyhow::{bail, Context, Result};
use clap::{Parser, ValueEnum};
use crossterm::event::{self, Event, KeyCode, KeyEvent, KeyEventKind, KeyModifiers};
use crossterm::terminal;
use proto::{Flag, Session, State, WaveLevel, MAX_LINE_LEN};

const DEFAULT_PORT: &str = "/dev/ttyACM0";
const DEFAULT_BAUD: u32 = 115_200;
const HEARTBEAT: Duration = Duration::from_secs(1);
const DEMO_STEP: Duration = Duration::from_secs(2);

#[derive(Parser, Debug)]
#[command(version, about = "SimHub simulator for the uniflag firmware")]
struct Cli {
    /// Serial device the firmware is enumerated as.
    #[arg(long, default_value = DEFAULT_PORT)]
    port: PathBuf,

    /// Don't open the serial device — print outgoing lines to stdout instead.
    #[arg(long)]
    no_port: bool,

    /// Run a scripted scenario file instead of interactive mode.
    #[arg(long, conflicts_with = "demo")]
    script: Option<PathBuf>,

    /// Cycle through every flag forever, 2 s per flag.
    #[arg(long)]
    demo: bool,

    /// Override baud rate (USB CDC ignores this but some tools care).
    #[arg(long, default_value_t = DEFAULT_BAUD)]
    baud: u32,

    /// Override which mode to run (overrides --script / --demo).
    #[arg(long, value_enum)]
    mode: Option<Mode>,
}

#[derive(Copy, Clone, Debug, ValueEnum)]
enum Mode {
    Interactive,
    Scripted,
    Demo,
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    let mode = cli.mode.unwrap_or_else(|| {
        if cli.demo {
            Mode::Demo
        } else if cli.script.is_some() {
            Mode::Scripted
        } else {
            Mode::Interactive
        }
    });

    let mut sink = if cli.no_port {
        Sink::Stdout(io::stdout())
    } else {
        let port = serialport::new(cli.port.to_string_lossy(), cli.baud)
            .timeout(Duration::from_millis(50))
            .open()
            .with_context(|| format!("opening {}", cli.port.display()))?;
        eprintln!("uniflag-sim → {} @ {} baud", cli.port.display(), cli.baud);
        Sink::Serial(port)
    };

    match mode {
        Mode::Interactive => run_interactive(&mut sink),
        Mode::Scripted => {
            let path = cli.script.as_deref().context("--script PATH required")?;
            run_scripted(&mut sink, path)
        }
        Mode::Demo => run_demo(&mut sink),
    }
}

// =============================================================================
// Sink: writes a line of bytes to either a serial port or stdout.
// =============================================================================

enum Sink {
    Serial(Box<dyn serialport::SerialPort>),
    Stdout(io::Stdout),
}

impl Sink {
    fn send(&mut self, line: &[u8]) -> Result<()> {
        match self {
            Sink::Serial(p) => p.write_all(line).context("write serial")?,
            Sink::Stdout(o) => o.write_all(line).context("write stdout")?,
        }
        if let Sink::Stdout(o) = self {
            o.flush().ok();
        }
        Ok(())
    }
}

fn send_state(sink: &mut Sink, state: &State) -> Result<()> {
    let mut buf = [0u8; MAX_LINE_LEN];
    let n = state.format(&mut buf);
    sink.send(&buf[..n])
}

// =============================================================================
// Interactive mode
// =============================================================================

const HOTKEY_LEGEND: &str = "\
hotkeys:
  n  none           space  cycle wave (none/single/double)
  y  yellow         ,      wave: none      (static)
  b  blue           .      wave: single    (single-waved)
  k  black          /      wave: double    (double-waved)
  w  white          p      toggle pit
  r  red            1      session: pre-race
  g  green          2      session: racing
  c  chequered      3      session: paused
  o  orange         4      session: post-race
                    5      session: replay
                    h      this help
                    q      quit
";

fn run_interactive(sink: &mut Sink) -> Result<()> {
    eprintln!("{HOTKEY_LEGEND}");
    eprintln!("interactive mode — press 'h' to re-show hotkeys, 'q' to quit");

    let mut state = State {
        session: Session::PreRace,
        ..State::default()
    };
    send_state(sink, &state)?;
    print_state(&state);

    terminal::enable_raw_mode().context("enable raw mode")?;
    let result = interactive_loop(sink, &mut state);
    terminal::disable_raw_mode().ok();

    // Polite goodbye: clear flag, then close.
    let bye = State {
        session: Session::PreRace,
        ..State::default()
    };
    let _ = send_state(sink, &bye);
    eprintln!("bye");
    result
}

fn interactive_loop(sink: &mut Sink, state: &mut State) -> Result<()> {
    let mut last_send = Instant::now();
    loop {
        if event::poll(HEARTBEAT)? {
            if let Event::Key(k) = event::read()? {
                if k.kind != KeyEventKind::Press {
                    continue;
                }
                let action = handle_key(k);
                match action {
                    Action::Quit => return Ok(()),
                    Action::Help => {
                        // Briefly drop raw mode so the legend prints cleanly.
                        terminal::disable_raw_mode().ok();
                        eprintln!("\n{HOTKEY_LEGEND}");
                        terminal::enable_raw_mode().ok();
                    }
                    Action::Mutate(f) => {
                        f(state);
                        send_state(sink, state)?;
                        last_send = Instant::now();
                        print_state(state);
                    }
                    Action::Ignore => {}
                }
            }
        } else if last_send.elapsed() >= HEARTBEAT {
            send_state(sink, state)?;
            last_send = Instant::now();
        }
    }
}

enum Action {
    Quit,
    Help,
    Mutate(fn(&mut State)),
    Ignore,
}

fn handle_key(k: KeyEvent) -> Action {
    if k.modifiers.contains(KeyModifiers::CONTROL) {
        if let KeyCode::Char('c') = k.code {
            return Action::Quit;
        }
    }
    match k.code {
        KeyCode::Char('q') | KeyCode::Esc => Action::Quit,
        KeyCode::Char('h') | KeyCode::Char('?') => Action::Help,
        KeyCode::Char('n') => Action::Mutate(|s| s.flag = Flag::None),
        KeyCode::Char('y') => Action::Mutate(|s| s.flag = Flag::Yellow),
        KeyCode::Char('b') => Action::Mutate(|s| s.flag = Flag::Blue),
        KeyCode::Char('k') => Action::Mutate(|s| s.flag = Flag::Black),
        KeyCode::Char('w') => Action::Mutate(|s| s.flag = Flag::White),
        KeyCode::Char('r') => Action::Mutate(|s| s.flag = Flag::Red),
        KeyCode::Char('g') => Action::Mutate(|s| s.flag = Flag::Green),
        KeyCode::Char('c') => Action::Mutate(|s| s.flag = Flag::Checkered),
        KeyCode::Char('o') => Action::Mutate(|s| s.flag = Flag::Orange),
        KeyCode::Char(' ') => Action::Mutate(|s| {
            s.wave = match s.wave {
                WaveLevel::None => WaveLevel::Single,
                WaveLevel::Single => WaveLevel::Double,
                WaveLevel::Double => WaveLevel::None,
            }
        }),
        KeyCode::Char(',') => Action::Mutate(|s| s.wave = WaveLevel::None),
        KeyCode::Char('.') => Action::Mutate(|s| s.wave = WaveLevel::Single),
        KeyCode::Char('/') => Action::Mutate(|s| s.wave = WaveLevel::Double),
        KeyCode::Char('p') => Action::Mutate(|s| s.in_pit = !s.in_pit),
        KeyCode::Char('1') => Action::Mutate(|s| s.session = Session::PreRace),
        KeyCode::Char('2') => Action::Mutate(|s| s.session = Session::Racing),
        KeyCode::Char('3') => Action::Mutate(|s| s.session = Session::Paused),
        KeyCode::Char('4') => Action::Mutate(|s| s.session = Session::PostRace),
        KeyCode::Char('5') => Action::Mutate(|s| s.session = Session::Replay),
        _ => Action::Ignore,
    }
}

fn print_state(s: &State) {
    eprintln!(
        "  flag={:<10} wave={} pit={} session={}\r",
        format!("{:?}", s.flag),
        s.wave.code(),
        s.in_pit as u8,
        s.session.code(),
    );
}

// =============================================================================
// Scripted mode
// =============================================================================

fn run_scripted(sink: &mut Sink, path: &std::path::Path) -> Result<()> {
    let text =
        fs::read_to_string(path).with_context(|| format!("reading scenario {}", path.display()))?;
    let steps =
        parse_scenario(&text).with_context(|| format!("parsing scenario {}", path.display()))?;
    eprintln!(
        "uniflag-sim → scripted ({} steps from {})",
        steps.len(),
        path.display()
    );

    for (delay, line) in &steps {
        std::thread::sleep(*delay);
        sink.send(line.as_bytes())?;
        sink.send(b"\n")?;
        eprintln!("  +{:>5}ms  {}", delay.as_millis(), line);
    }
    Ok(())
}

fn parse_scenario(text: &str) -> Result<Vec<(Duration, String)>> {
    let mut out = Vec::new();
    for (idx, raw) in text.lines().enumerate() {
        let line = raw.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let (delay_str, payload) = line
            .split_once(char::is_whitespace)
            .with_context(|| format!("line {}: expected '<delay-ms> <payload>'", idx + 1))?;
        let delay_ms: u64 = delay_str
            .parse()
            .with_context(|| format!("line {}: bad delay '{}'", idx + 1, delay_str))?;
        let payload = payload.trim();
        if payload.is_empty() {
            bail!("line {}: empty payload", idx + 1);
        }
        out.push((Duration::from_millis(delay_ms), payload.to_string()));
    }
    Ok(out)
}

// =============================================================================
// Demo mode
// =============================================================================

fn run_demo(sink: &mut Sink) -> Result<()> {
    eprintln!("uniflag-sim → demo (Ctrl-C to stop)");
    let mut idx = 0usize;
    loop {
        let (flag, wave) = DEMO_CYCLE[idx];
        let state = State {
            flag,
            wave,
            in_pit: false,
            session: Session::Racing,
        };
        send_state(sink, &state)?;
        eprintln!("  {:?} wave={}", flag, wave.code());
        std::thread::sleep(DEMO_STEP);
        idx = (idx + 1) % DEMO_CYCLE.len();
    }
}

/// Walk every flag, and for flags whose effect changes with wave level,
/// also cycle through the three wave variants. Black, chequered, and none
/// look identical at every wave level so they only appear once.
const DEMO_CYCLE: &[(Flag, WaveLevel)] = &[
    (Flag::None, WaveLevel::None),
    (Flag::Yellow, WaveLevel::None),
    (Flag::Yellow, WaveLevel::Single),
    (Flag::Yellow, WaveLevel::Double),
    (Flag::Blue, WaveLevel::None),
    (Flag::Blue, WaveLevel::Single),
    (Flag::Blue, WaveLevel::Double),
    (Flag::Black, WaveLevel::None),
    (Flag::White, WaveLevel::None),
    (Flag::White, WaveLevel::Single),
    (Flag::White, WaveLevel::Double),
    (Flag::Red, WaveLevel::None),
    (Flag::Red, WaveLevel::Single),
    (Flag::Red, WaveLevel::Double),
    (Flag::Green, WaveLevel::None),
    (Flag::Green, WaveLevel::Single),
    (Flag::Green, WaveLevel::Double),
    (Flag::Checkered, WaveLevel::None),
    (Flag::Orange, WaveLevel::None),
    (Flag::Orange, WaveLevel::Single),
    (Flag::Orange, WaveLevel::Double),
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_scenario_skips_comments_and_blanks() {
        let text = "\
            # comment\n\
            \n\
            0   F=N;B=0;P=0;S=pre-race\n\
            500 F=Y;B=1;P=0;S=racing\n\
        ";
        let steps = parse_scenario(text).unwrap();
        assert_eq!(steps.len(), 2);
        assert_eq!(steps[0].0, Duration::from_millis(0));
        assert_eq!(steps[0].1, "F=N;B=0;P=0;S=pre-race");
        assert_eq!(steps[1].0, Duration::from_millis(500));
    }

    #[test]
    fn parse_scenario_rejects_missing_payload() {
        assert!(parse_scenario("100\n").is_err());
    }

    #[test]
    fn parse_scenario_rejects_bad_delay() {
        assert!(parse_scenario("abc F=Y\n").is_err());
    }
}
