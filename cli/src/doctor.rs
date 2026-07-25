//! Environment report: what's installed, what isn't, and what to do
//! about it.
//!
//! Lives here rather than in the justfile because a recipe that must work
//! on both bash and PowerShell gets written twice, and the two copies
//! drift.

use std::path::Path;
use std::process::Command;

use proto::packet::{USB_PID, USB_VID};

/// The registered pid.codes PID, accepted alongside the test PID during
/// the transition (docs/protocol.md §Transport).
const REGISTERED_PID: u16 = 0xF1A6;

const SIMHUB_DLLS: [&str; 3] = ["SimHub.Plugins.dll", "GameReaderCommon.dll", "log4net.dll"];

/// The justfile owns these defaults, so the platform flavouring lives in
/// exactly one place.
pub struct Targets<'a> {
    pub serial: &'a str,
    pub mount: &'a str,
    pub simhub_dir: &'a str,
}

/// Never fails: a doctor that dies on a broken environment is no use.
pub fn report(targets: &Targets<'_>) {
    println!("uniflag doctor\n");

    println!("Rust:");
    tool("cargo", &["--version"], "install rustup");
    tool("rustc", &["--version"], "install rustup");
    thumbv6m();
    tool(
        "elf2uf2-rs",
        &["--version"],
        "cargo install elf2uf2-rs (or use 'nix develop')",
    );

    println!("\nC#:");
    dotnet();
    simhub(targets.simhub_dir);

    println!("\nDevice:");
    serial_ports(targets.serial);
    mount(targets.mount);
}

fn ok(name: &str, detail: &str) {
    println!("  ok      {name:<14} {detail}");
}

fn miss(name: &str, fix: &str) {
    println!("  MISSING {name:<14} {fix}");
}

fn tool(name: &str, args: &[&str], fix: &str) {
    match run(name, args) {
        Some(output) => ok(name, output.lines().next().unwrap_or("installed").trim()),
        None => miss(name, fix),
    }
}

fn thumbv6m() {
    let installed = run("rustup", &["target", "list", "--installed"])
        .is_some_and(|out| out.lines().any(|line| line.trim() == "thumbv6m-none-eabi"));
    if installed {
        ok("thumbv6m", "firmware target installed");
    } else {
        miss("thumbv6m", "rustup target add thumbv6m-none-eabi");
    }
}

fn dotnet() {
    match run("dotnet", &["--list-sdks"]) {
        Some(output) => {
            let versions: Vec<&str> = output
                .lines()
                .filter_map(|line| line.split_whitespace().next())
                .collect();
            ok("dotnet", &format!("SDKs: {}", versions.join(" ")));
        }
        None => miss(
            "dotnet",
            "install the .NET SDK — needed for just core-test and the frame viewer",
        ),
    }
}

fn simhub(dir: &str) {
    let missing: Vec<&str> = SIMHUB_DLLS
        .iter()
        .copied()
        .filter(|dll| !Path::new(dir).join(dll).exists())
        .collect();
    if missing.is_empty() {
        ok("SimHub", dir);
    } else {
        miss(
            "SimHub",
            &format!(
                "{dir} is missing {} — set UNIFLAG_SIMHUB_DIR (the net48 plugin leg is Windows-only)",
                missing.join(", ")
            ),
        );
    }
}

fn serial_ports(configured: &str) {
    let ports = match serialport::available_ports() {
        Ok(ports) => ports,
        Err(err) => {
            println!("  serial ports : enumeration failed ({err})");
            return;
        }
    };
    if ports.is_empty() {
        println!("  serial ports : none detected (configured: {configured})");
        return;
    }
    println!("  serial ports :");
    for port in &ports {
        let note = match &port.port_type {
            serialport::SerialPortType::UsbPort(usb) if is_panel(usb.vid, usb.pid) => {
                " <- uniflag panel".to_string()
            }
            serialport::SerialPortType::UsbPort(usb) => {
                format!(" (USB {:04x}:{:04x})", usb.vid, usb.pid)
            }
            _ => String::new(),
        };
        let configured_note = if port.port_name == configured {
            "  [configured]"
        } else {
            ""
        };
        println!("      {}{note}{configured_note}", port.port_name);
    }
    if !ports.iter().any(|p| p.port_name == configured) {
        println!("      (configured {configured} is not present — override with UNIFLAG_SERIAL)");
    }
}

fn is_panel(vid: u16, pid: u16) -> bool {
    vid == USB_VID && (pid == USB_PID || pid == REGISTERED_PID)
}

fn mount(path: &str) {
    if Path::new(path).exists() {
        println!("  BOOTSEL      : {path} (mounted)");
    } else {
        println!("  BOOTSEL      : {path} (not mounted — hold BOOTSEL while plugging in)");
    }
}

/// Capture a command's stdout, or None if it can't be run or fails.
fn run(program: &str, args: &[&str]) -> Option<String> {
    let output = Command::new(program).args(args).output().ok()?;
    if !output.status.success() {
        return None;
    }
    Some(String::from_utf8_lossy(&output.stdout).into_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn panel_identity_accepts_both_pids_only_under_our_vid() {
        assert!(is_panel(USB_VID, USB_PID));
        assert!(is_panel(USB_VID, REGISTERED_PID));
        assert!(!is_panel(USB_VID, 0x1234));
        assert!(!is_panel(0x0483, USB_PID));
    }

    #[test]
    fn missing_programs_report_none_rather_than_panicking() {
        assert_eq!(run("uniflag-no-such-program-exists", &["--version"]), None);
    }

    #[test]
    fn report_runs_against_paths_that_do_not_exist() {
        // The whole point is to survive a broken environment.
        report(&Targets {
            serial: "/dev/does-not-exist",
            mount: "/does-not-exist",
            simhub_dir: "/does-not-exist",
        });
    }
}
