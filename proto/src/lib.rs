//! uniflag wire protocol — shared between firmware and the host-side
//! simulator.
//!
//! Lines are ASCII, semicolon-separated `key=value` fields, terminated with
//! `\n` (the parser also accepts `\r\n` and trailing whitespace). The whole
//! API is `no_std` and allocation-free.
//!
//! Example: `F=Y;B=1;P=0;S=racing\n`
//!
//! | Field | Values | Meaning |
//! |-------|--------|---------|
//! | `F`   | `N` `Y` `B` `K` `W` `R` `G` `C` `O` | flag (none/yellow/blue/black/white/red/green/chequered/orange) |
//! | `B`   | `0` `1` | blink the active flag (waved-yellow / caution) |
//! | `P`   | `0` `1` | in-pit indicator |
//! | `S`   | `pre-race` `racing` `paused` `post-race` `replay` `unknown` | session state |
//!
//! Unknown keys are silently ignored, so adding new fields is
//! backwards-compatible. Unknown values for a known key return
//! [`ParseError::BadValue`].

#![no_std]

/// Maximum length of a formatted line, including the trailing `\n`. Caller
/// must pass a buffer of at least this size to [`State::format`].
pub const MAX_LINE_LEN: usize = 32;

#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub enum Flag {
    #[default]
    None,
    Yellow,
    Blue,
    Black,
    White,
    Red,
    Green,
    Checkered,
    Orange,
}

#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub enum Session {
    PreRace,
    #[default]
    Unknown,
    Racing,
    Paused,
    PostRace,
    Replay,
}

#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub struct State {
    pub flag: Flag,
    pub blink: bool,
    pub in_pit: bool,
    pub session: Session,
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum ParseError {
    BadField,
    BadValue,
    NotAscii,
}

impl Flag {
    pub fn code(self) -> &'static str {
        match self {
            Flag::None => "N",
            Flag::Yellow => "Y",
            Flag::Blue => "B",
            Flag::Black => "K",
            Flag::White => "W",
            Flag::Red => "R",
            Flag::Green => "G",
            Flag::Checkered => "C",
            Flag::Orange => "O",
        }
    }
}

impl Session {
    pub fn code(self) -> &'static str {
        match self {
            Session::PreRace => "pre-race",
            Session::Racing => "racing",
            Session::Paused => "paused",
            Session::PostRace => "post-race",
            Session::Replay => "replay",
            Session::Unknown => "unknown",
        }
    }
}

impl State {
    /// Parse a single line. Trailing `\r`, `\n`, space, and tab are
    /// stripped. Missing fields default to [`State::default`]. Unknown
    /// fields are ignored.
    pub fn parse(line: &[u8]) -> Result<Self, ParseError> {
        if !line.is_ascii() {
            return Err(ParseError::NotAscii);
        }

        let line = trim_trailing(line);

        let mut state = State::default();
        for field in line.split(|&b| b == b';') {
            if field.is_empty() {
                continue;
            }
            let eq = field
                .iter()
                .position(|&b| b == b'=')
                .ok_or(ParseError::BadField)?;
            if eq == 0 {
                return Err(ParseError::BadField);
            }
            let key = &field[..eq];
            let val = &field[eq + 1..];
            match key {
                b"F" => state.flag = parse_flag(val)?,
                b"B" => state.blink = parse_bool(val)?,
                b"P" => state.in_pit = parse_bool(val)?,
                b"S" => state.session = parse_session(val)?,
                _ => { /* forward-compat: ignore unknown keys */ }
            }
        }

        Ok(state)
    }

    /// Format the state into `buf`, including the trailing `\n`. Returns
    /// the number of bytes written. Panics if `buf.len() < MAX_LINE_LEN`.
    pub fn format(&self, buf: &mut [u8]) -> usize {
        assert!(
            buf.len() >= MAX_LINE_LEN,
            "format buffer must be at least MAX_LINE_LEN bytes"
        );
        let mut w = SliceWriter { buf, len: 0 };
        w.put(b"F=");
        w.put(self.flag.code().as_bytes());
        w.put(b";B=");
        w.put(if self.blink { b"1" } else { b"0" });
        w.put(b";P=");
        w.put(if self.in_pit { b"1" } else { b"0" });
        w.put(b";S=");
        w.put(self.session.code().as_bytes());
        w.put(b"\n");
        w.len
    }
}

fn trim_trailing(s: &[u8]) -> &[u8] {
    let mut end = s.len();
    while end > 0 {
        let b = s[end - 1];
        if b == b'\n' || b == b'\r' || b == b' ' || b == b'\t' {
            end -= 1;
        } else {
            break;
        }
    }
    &s[..end]
}

fn parse_flag(v: &[u8]) -> Result<Flag, ParseError> {
    match v {
        b"N" => Ok(Flag::None),
        b"Y" => Ok(Flag::Yellow),
        b"B" => Ok(Flag::Blue),
        b"K" => Ok(Flag::Black),
        b"W" => Ok(Flag::White),
        b"R" => Ok(Flag::Red),
        b"G" => Ok(Flag::Green),
        b"C" => Ok(Flag::Checkered),
        b"O" => Ok(Flag::Orange),
        _ => Err(ParseError::BadValue),
    }
}

fn parse_bool(v: &[u8]) -> Result<bool, ParseError> {
    match v {
        b"0" => Ok(false),
        b"1" => Ok(true),
        _ => Err(ParseError::BadValue),
    }
}

fn parse_session(v: &[u8]) -> Result<Session, ParseError> {
    match v {
        b"pre-race" => Ok(Session::PreRace),
        b"racing" => Ok(Session::Racing),
        b"paused" => Ok(Session::Paused),
        b"post-race" => Ok(Session::PostRace),
        b"replay" => Ok(Session::Replay),
        b"unknown" => Ok(Session::Unknown),
        _ => Err(ParseError::BadValue),
    }
}

struct SliceWriter<'a> {
    buf: &'a mut [u8],
    len: usize,
}

impl SliceWriter<'_> {
    fn put(&mut self, s: &[u8]) {
        let end = self.len + s.len();
        self.buf[self.len..end].copy_from_slice(s);
        self.len = end;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ALL_FLAGS: [Flag; 9] = [
        Flag::None,
        Flag::Yellow,
        Flag::Blue,
        Flag::Black,
        Flag::White,
        Flag::Red,
        Flag::Green,
        Flag::Checkered,
        Flag::Orange,
    ];
    const ALL_SESSIONS: [Session; 6] = [
        Session::PreRace,
        Session::Racing,
        Session::Paused,
        Session::PostRace,
        Session::Replay,
        Session::Unknown,
    ];

    #[test]
    fn parse_canonical_line() {
        let s = State::parse(b"F=Y;B=1;P=0;S=racing").unwrap();
        assert_eq!(
            s,
            State {
                flag: Flag::Yellow,
                blink: true,
                in_pit: false,
                session: Session::Racing,
            }
        );
    }

    #[test]
    fn parse_with_crlf_and_trailing_whitespace() {
        let s = State::parse(b"F=N;B=0;P=0;S=pre-race \r\n").unwrap();
        assert_eq!(s.flag, Flag::None);
        assert_eq!(s.session, Session::PreRace);
    }

    #[test]
    fn parse_unknown_field_is_ignored() {
        let s = State::parse(b"F=Y;X=hello;B=1").unwrap();
        assert_eq!(s.flag, Flag::Yellow);
        assert!(s.blink);
    }

    #[test]
    fn parse_empty_input_yields_default() {
        let s = State::parse(b"").unwrap();
        assert_eq!(s, State::default());
    }

    #[test]
    fn parse_consecutive_separators() {
        let s = State::parse(b";F=Y;;B=1;").unwrap();
        assert_eq!(s.flag, Flag::Yellow);
        assert!(s.blink);
    }

    #[test]
    fn parse_bad_field_returns_err() {
        assert_eq!(State::parse(b"abc"), Err(ParseError::BadField));
        assert_eq!(State::parse(b"=val"), Err(ParseError::BadField));
    }

    #[test]
    fn parse_bad_value_returns_err() {
        assert_eq!(State::parse(b"F=Z"), Err(ParseError::BadValue));
        assert_eq!(State::parse(b"B=2"), Err(ParseError::BadValue));
        assert_eq!(State::parse(b"S=foo"), Err(ParseError::BadValue));
    }

    #[test]
    fn parse_non_ascii_returns_err() {
        assert_eq!(State::parse(b"F=\xc3\xa9"), Err(ParseError::NotAscii));
    }

    #[test]
    fn format_ends_with_newline() {
        let mut buf = [0u8; MAX_LINE_LEN];
        let n = State::default().format(&mut buf);
        assert_eq!(buf[n - 1], b'\n');
    }

    #[test]
    fn format_within_max_len() {
        let mut buf = [0u8; MAX_LINE_LEN];
        // Pick the longest possible state — chequered + post-race.
        let s = State {
            flag: Flag::Checkered,
            blink: true,
            in_pit: true,
            session: Session::PostRace,
        };
        let n = s.format(&mut buf);
        assert!(n <= MAX_LINE_LEN);
    }

    #[test]
    fn round_trip_default() {
        let mut buf = [0u8; MAX_LINE_LEN];
        let s = State::default();
        let n = s.format(&mut buf);
        assert_eq!(State::parse(&buf[..n]).unwrap(), s);
    }

    #[test]
    fn round_trip_every_flag() {
        for &flag in &ALL_FLAGS {
            let s = State {
                flag,
                blink: true,
                in_pit: true,
                session: Session::Racing,
            };
            let mut buf = [0u8; MAX_LINE_LEN];
            let n = s.format(&mut buf);
            assert_eq!(State::parse(&buf[..n]).unwrap(), s, "flag {:?}", flag);
        }
    }

    #[test]
    fn round_trip_every_session() {
        for &session in &ALL_SESSIONS {
            let s = State {
                flag: Flag::Yellow,
                blink: false,
                in_pit: false,
                session,
            };
            let mut buf = [0u8; MAX_LINE_LEN];
            let n = s.format(&mut buf);
            assert_eq!(State::parse(&buf[..n]).unwrap(), s, "session {:?}", session);
        }
    }

    #[test]
    fn flag_codes_are_unique() {
        let mut codes = [""; 9];
        for (i, f) in ALL_FLAGS.iter().enumerate() {
            codes[i] = f.code();
        }
        codes.sort_unstable();
        for w in codes.windows(2) {
            assert_ne!(w[0], w[1]);
        }
    }
}
