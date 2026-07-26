{
  description = "uniflag — sim-racing flag display on a Pimoroni Cosmic Unicorn (RP2040, Embassy)";

  # A dev shell only: releases are built by the GitHub workflow, and a package
  # derivation for the firmware would need a second Rust toolchain (stock
  # nixpkgs rustc ships no thumbv6m-none-eabi std) that rustup already covers.

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };
      in {
        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            # Rust toolchain (rustup honors rust-toolchain.toml in repo root)
            rustup

            # Embedded tooling
            elf2uf2-rs

            # Host build deps for serialport-rs (udev is nixpkgs' alias for
            # systemd's udev outputs; the shellHook pins its pkgconfig path)
            pkg-config
            udev

            # Convenience
            just
          ];

          shellHook = ''
            export PKG_CONFIG_PATH="${pkgs.udev.dev}/lib/pkgconfig:$PKG_CONFIG_PATH"
            echo "uniflag devShell ready."
            echo "  just --list           → available recipes"
            echo "  just img              → build firmware UF2"
            echo "  just cli              → stream a test pattern to the device"
          '';
        };
      });
}
