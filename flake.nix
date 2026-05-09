{
  description = "uniflag — sim-racing flag display on a Pimoroni Cosmic Unicorn (RP2040, Embassy)";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
    fenix = {
      url = "github:nix-community/fenix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs = { self, nixpkgs, flake-utils, fenix }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs { inherit system; };

        # Embedded toolchain: stock nixpkgs rustc doesn't ship rust-std for
        # thumbv6m-none-eabi, so pull cargo+rustc+thumbv6m std from fenix.
        embeddedToolchain = with fenix.packages.${system}; combine [
          stable.cargo
          stable.rustc
          targets.thumbv6m-none-eabi.stable.rust-std
        ];

        embeddedRustPlatform = pkgs.makeRustPlatform {
          cargo = embeddedToolchain;
          rustc = embeddedToolchain;
        };

        sim = pkgs.rustPlatform.buildRustPackage {
          pname = "uniflag-sim";
          version = "0.1.0";
          src = ./.;
          cargoLock.lockFile = ./Cargo.lock;

          cargoBuildFlags = [ "-p" "uniflag-sim" ];
          cargoTestFlags  = [ "-p" "uniflag-sim" "-p" "proto" ];

          # serialport-rs needs libudev on Linux.
          nativeBuildInputs = [ pkgs.pkg-config ];
          buildInputs       = [ pkgs.udev ];

          meta = {
            description = "Host-side simulator that pretends to be SimHub";
            license     = pkgs.lib.licenses.gpl2Plus;
            mainProgram  = "uniflag-sim";
            platforms   = pkgs.lib.platforms.linux;
          };
        };

        firmware = embeddedRustPlatform.buildRustPackage {
          pname = "uniflag-firmware";
          version = "0.1.0";
          src = ./.;
          cargoLock.lockFile = ./Cargo.lock;

          doCheck   = false;
          auditable = false;

          nativeBuildInputs = [ pkgs.elf2uf2-rs ];

          buildPhase = ''
            runHook preBuild
            cargo build \
              --offline \
              --frozen \
              --release \
              -p uniflag-firmware \
              --target thumbv6m-none-eabi \
              -j $NIX_BUILD_CORES
            runHook postBuild
          '';

          installPhase = ''
            runHook preInstall
            mkdir -p $out/bin
            cp target/thumbv6m-none-eabi/release/uniflag $out/bin/uniflag.elf
            elf2uf2-rs $out/bin/uniflag.elf $out/bin/uniflag.uf2
            runHook postInstall
          '';

          meta = {
            description = "uniflag firmware for the original Pimoroni Cosmic Unicorn (RP2040, Embassy)";
            license     = pkgs.lib.licenses.gpl2Plus;
            platforms   = pkgs.lib.platforms.all;
          };
        };
      in {
        packages = {
          inherit sim firmware;
          default = sim;
        };

        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            # Rust toolchain (rustup honors rust-toolchain.toml in repo root)
            rustup

            # Embedded tooling
            elf2uf2-rs

            # Host build deps for serialport-rs
            pkg-config
            udev
            systemd

            # Convenience
            cargo-binutils
            picocom
            just
          ];

          shellHook = ''
            export PKG_CONFIG_PATH="${pkgs.udev.dev}/lib/pkgconfig:$PKG_CONFIG_PATH"
            echo "uniflag devShell ready."
            echo "  just --list           → available recipes"
            echo "  just build            → build firmware UF2"
            echo "  just sim              → run the SimHub simulator"
          '';
        };
      });
}
