// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Host-side brightness policy (docs/v2-plan.md M9 step 4). The retired v1
// firmware controller — render/src/brightness.rs::BrightnessController — is
// the behaviour spec: STEP/SLEEP constants, saturating steps, the
// remembered-awake level, and the wake-to-at-least-STEP rule are ported
// 1:1. What does NOT carry over is the flash-persistence debounce
// (SAVE_DEBOUNCE_MS): in v2 the value lives in SimHub's settings store,
// where a property write costs nothing — SimHub saves the file at End.

using System;
using Uniflag.Protocol;

namespace Uniflag.Device
{
    /// <summary>
    /// Owns the current panel brightness on the host. Inputs: the settings
    /// tab slider (<see cref="SetDirect"/>, UI thread) and device
    /// ButtonEvents (<see cref="ApplyButtonEvent"/>, RX thread) — the state
    /// is lock-protected so both may call concurrently. Every effective
    /// change raises <see cref="Changed"/> exactly once; subscribers persist
    /// the value into <see cref="UniflagSettings"/> and queue one Brightness
    /// packet (the connection manager's depth-one slot coalesces rapid
    /// changes, so a dragging slider never floods the wire).
    /// </summary>
    public sealed class BrightnessPolicy
    {
        /// <summary>
        /// Step per brightness-up/down short press — ~5 % of the 0..=255
        /// range. Mirrors <c>BrightnessController::STEP</c>
        /// (render/src/brightness.rs).
        /// </summary>
        public const byte Step = 12;

        /// <summary>
        /// The dim level the sleep button drops to when awake. Mirrors
        /// <c>BrightnessController::SLEEP</c> (render/src/brightness.rs).
        /// </summary>
        public const byte SleepValue = 6;

        private readonly object _gate = new object();
        private byte _current;

        // What to restore to when waking from sleep. Updated on every
        // awake-state change (step or slider) so the wake level always
        // tracks the user's last preferred bright value — mirrors
        // BrightnessController::last_awake.
        private byte _lastAwake;

        /// <summary>
        /// Raised after every effective change with the new value, on
        /// whichever thread applied the change (UI thread for the slider,
        /// device RX thread for buttons). Raised while still holding the
        /// internal state lock so delivery order always matches mutation
        /// order — the last delivery a subscriber sees is guaranteed to
        /// carry the final <see cref="Current"/> value even when slider and
        /// button inputs race (the device TX slot and the persisted setting
        /// both converge on the true state). Handlers must therefore be
        /// thread-safe, fast, and non-blocking, and must never wait on
        /// another thread that could call into this policy.
        /// </summary>
        public event Action<byte> Changed;

        /// <summary>Seed with the persisted value from SimHub settings.</summary>
        public BrightnessPolicy(byte initial)
        {
            _current = initial;
            _lastAwake = initial;
        }

        /// <summary>Currently applied brightness, 0..=255.</summary>
        public byte Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        /// <summary>
        /// Slider path: set the value directly. Like a step, this is an
        /// awake-state change, so it also becomes the remembered wake level
        /// (a subsequent sleep-toggle from a slider value ≤
        /// <see cref="SleepValue"/> wakes to at least <see cref="Step"/>,
        /// per the v1 wake rule).
        /// </summary>
        public void SetDirect(byte value)
        {
            lock (_gate)
            {
                _lastAwake = value;
                bool changed = value != _current;
                _current = value;
                if (changed)
                {
                    // Raised under the lock: see the Changed doc — delivery
                    // order must match mutation order or racing inputs can
                    // pin subscribers to a stale value.
                    RaiseChanged(value);
                }
            }
        }

        /// <summary>
        /// Device ButtonEvent path. Only assigned buttons with a
        /// <b>short</b> press drive the policy: long presses belong to the
        /// firmware's local test/version screen and must not also step
        /// brightness here; unassigned button/kind ids are ignored (the id
        /// spaces are open — docs/protocol.md §ButtonEvent).
        /// </summary>
        public void ApplyButtonEvent(byte button, byte kind)
        {
            if (ProtocolIds.PressKindFromByte(kind) != PressKind.Short)
            {
                return;
            }
            Button? assigned = ProtocolIds.ButtonFromByte(button);
            if (assigned == null)
            {
                return;
            }

            lock (_gate)
            {
                byte next;
                switch (assigned.Value)
                {
                    case Button.BrightnessUp:
                        // Saturating add; last_awake tracks the result even
                        // when saturated (mirrors apply() in brightness.rs).
                        next = (byte)Math.Min(byte.MaxValue, _current + Step);
                        _lastAwake = next;
                        break;
                    case Button.BrightnessDown:
                        next = (byte)Math.Max(byte.MinValue, _current - Step);
                        _lastAwake = next;
                        break;
                    default: // Button.Sleep
                        if (_current <= SleepValue)
                        {
                            // Already sleeping → wake to the remembered
                            // level, clamped to at least one step so the
                            // toggle never appears to do nothing.
                            next = Math.Max(_lastAwake, Step);
                        }
                        else
                        {
                            // Awake → remember this value and dim.
                            _lastAwake = _current;
                            next = SleepValue;
                        }
                        break;
                }
                bool changed = next != _current;
                _current = next;
                if (changed)
                {
                    // Raised under the lock — see SetDirect.
                    RaiseChanged(next);
                }
            }
        }

        private void RaiseChanged(byte value)
        {
            Changed?.Invoke(value);
        }
    }
}
