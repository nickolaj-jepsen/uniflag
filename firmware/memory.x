/* Memory layout for the original Pimoroni Cosmic Unicorn (Pico W aboard).
 * The Pico W ships with a 2 MB / 16 Mbit external QSPI flash (W25Q16JV).
 *
 * The last 4 KB sector is carved out of FLASH so firmware/src/storage.rs
 * can use it for persistent settings (brightness). The linker stops the
 * firmware short of that sector; storage::* targets the carved region
 * directly via the flash driver. */

MEMORY {
    BOOT2 : ORIGIN = 0x10000000, LENGTH = 0x100
    FLASH : ORIGIN = 0x10000100, LENGTH = 2048K - 0x100 - 0x1000
    RAM   : ORIGIN = 0x20000000, LENGTH = 264K
}

EXTERN(BOOT2_FIRMWARE)

SECTIONS {
    /* The RP2040 ROM bootloader runs the first 256 bytes of flash as a
       second-stage that configures XIP. embassy-rp ships a default. */
    .boot2 ORIGIN(BOOT2) :
    {
        KEEP(*(.boot2));
    } > BOOT2
} INSERT BEFORE .text;
