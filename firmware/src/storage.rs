//! Persistent settings in the last 4 KB sector of QSPI flash.
//!
//! `firmware/memory.x` reserves the final sector by shrinking the
//! `FLASH` region — the linker won't place code there. We address it
//! directly via the embassy-rp flash driver.
//!
//! Erase + write blocks XIP for ~25 ms, so callers should debounce
//! rapid changes rather than write per-event. The watchdog feed
//! interval (`WATCHDOG_FEED_INTERVAL` in `main.rs`) is sized to
//! tolerate this.

use embassy_rp::flash::{Blocking, Flash, ERASE_SIZE};
use embassy_rp::peripherals::FLASH;

/// Total QSPI flash on the original Cosmic Unicorn (Pico W, W25Q16JV).
/// Used as the `Flash` driver's const-generic capacity, regardless of
/// how much the linker actually claims for firmware.
pub const FLASH_TOTAL_SIZE: usize = 2 * 1024 * 1024;

/// Offset of the persistence sector from flash start. The last 4 KB
/// sector — kept in sync with the `- 0x1000` reservation in memory.x.
const STORAGE_OFFSET: u32 = (FLASH_TOTAL_SIZE - ERASE_SIZE) as u32;

const MAGIC: [u8; 4] = *b"UFB1";
const RECORD_LEN: usize = 8;

pub type FlashStorage = Flash<'static, FLASH, Blocking, FLASH_TOTAL_SIZE>;

/// Read the persisted brightness, if any. Returns `None` on a virgin
/// sector (all-`0xFF`), a corrupted record, or a flash read error.
pub fn load_brightness(flash: &mut FlashStorage) -> Option<u8> {
    let mut buf = [0u8; RECORD_LEN];
    flash.blocking_read(STORAGE_OFFSET, &mut buf).ok()?;
    if buf[0..4] != MAGIC {
        return None;
    }
    let b = buf[4];
    if buf[5] != !b {
        return None;
    }
    Some(b)
}

/// Erase the persistence sector and write the brightness record.
/// Stalls XIP for ~25 ms. Errors are logged but never panicked on —
/// brightness persistence is best-effort.
pub fn save_brightness(flash: &mut FlashStorage, brightness: u8) {
    let mut buf = [0u8; RECORD_LEN];
    buf[0..4].copy_from_slice(&MAGIC);
    buf[4] = brightness;
    buf[5] = !brightness;

    if let Err(e) = flash.blocking_erase(STORAGE_OFFSET, STORAGE_OFFSET + ERASE_SIZE as u32) {
        defmt::warn!("storage: erase failed: {:?}", e);
        return;
    }
    if let Err(e) = flash.blocking_write(STORAGE_OFFSET, &buf) {
        defmt::warn!("storage: write failed: {:?}", e);
    }
}
