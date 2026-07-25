// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// Brightness policy matrix, pinned against the retired v1 controller
// (render/src/brightness.rs::BrightnessController) as the behaviour spec:
// STEP=12 saturating steps, SLEEP=6, the remembered-awake level, and the
// wake-to-at-least-STEP rule. Long presses and unassigned button/kind ids
// must never move the policy (docs/protocol.md §ButtonEvent: open id
// spaces; long press drives the firmware's local test screen).

using System.Collections.Generic;
using System.Threading;
using Uniflag.Device;
using Uniflag.Protocol;
using Xunit;

namespace Uniflag.Tests
{
    public class BrightnessPolicyTests
    {
        private const byte Up = (byte)Button.BrightnessUp;
        private const byte Down = (byte)Button.BrightnessDown;
        private const byte Sleep = (byte)Button.Sleep;
        private const byte Short = (byte)PressKind.Short;
        private const byte Long = (byte)PressKind.Long;

        [Fact]
        public void ConstantsMatchTheV1Controller()
        {
            // render/src/brightness.rs: STEP = 12, SLEEP = 6. Changing these
            // changes the user-visible button feel — a deliberate decision,
            // not a drive-by.
            Assert.Equal(12, BrightnessPolicy.Step);
            Assert.Equal(6, BrightnessPolicy.SleepValue);
        }

        [Fact]
        public void UpStepsByTwelveAndSaturatesAt255()
        {
            var policy = new BrightnessPolicy(80);
            policy.ApplyButtonEvent(Up, Short);
            Assert.Equal(92, policy.Current);
            policy.ApplyButtonEvent(Up, Short);
            Assert.Equal(104, policy.Current);

            var high = new BrightnessPolicy(250);
            high.ApplyButtonEvent(Up, Short);
            Assert.Equal(255, high.Current); // saturating_add
            high.ApplyButtonEvent(Up, Short);
            Assert.Equal(255, high.Current);
        }

        [Fact]
        public void DownStepsByTwelveAndSaturatesAtZero()
        {
            var policy = new BrightnessPolicy(80);
            policy.ApplyButtonEvent(Down, Short);
            Assert.Equal(68, policy.Current);

            var low = new BrightnessPolicy(10);
            low.ApplyButtonEvent(Down, Short);
            Assert.Equal(0, low.Current); // saturating_sub
            low.ApplyButtonEvent(Down, Short);
            Assert.Equal(0, low.Current);
        }

        [Fact]
        public void SleepTogglesBetweenSixAndTheRememberedAwakeLevel()
        {
            var policy = new BrightnessPolicy(80);
            policy.ApplyButtonEvent(Sleep, Short);
            Assert.Equal(BrightnessPolicy.SleepValue, policy.Current);
            policy.ApplyButtonEvent(Sleep, Short);
            Assert.Equal(80, policy.Current); // wake restores last_awake
        }

        [Fact]
        public void WakeFromSleepClampsToAtLeastOneStep()
        {
            // v1 rule: current <= SLEEP counts as sleeping, and waking goes
            // to max(last_awake, STEP) so the toggle never appears to do
            // nothing when last_awake was very low.
            var policy = new BrightnessPolicy(80);
            policy.SetDirect(4); // slider drags it below the sleep level
            policy.ApplyButtonEvent(Sleep, Short);
            Assert.Equal(BrightnessPolicy.Step, policy.Current);
        }

        [Fact]
        public void UpFromSleepStepsAndBecomesTheNewAwakeLevel()
        {
            // Mirrors apply() in brightness.rs: Up always records
            // last_awake = result, even when starting from the sleep value.
            var policy = new BrightnessPolicy(80);
            policy.ApplyButtonEvent(Sleep, Short); // 6, last_awake 80
            policy.ApplyButtonEvent(Up, Short);    // 18, last_awake 18
            Assert.Equal(18, policy.Current);
            policy.ApplyButtonEvent(Sleep, Short); // dim again
            Assert.Equal(BrightnessPolicy.SleepValue, policy.Current);
            policy.ApplyButtonEvent(Sleep, Short); // wake to the NEW level
            Assert.Equal(18, policy.Current);
        }

        [Fact]
        public void DownToZeroThenSleepToggleWakesToStep()
        {
            var policy = new BrightnessPolicy(BrightnessPolicy.Step);
            policy.ApplyButtonEvent(Down, Short); // 0, last_awake 0
            Assert.Equal(0, policy.Current);
            policy.ApplyButtonEvent(Sleep, Short); // sleeping (0 <= 6) → wake
            Assert.Equal(BrightnessPolicy.Step, policy.Current);
        }

        [Fact]
        public void SetDirectUpdatesTheRememberedAwakeLevel()
        {
            var policy = new BrightnessPolicy(80);
            policy.SetDirect(200);
            policy.ApplyButtonEvent(Sleep, Short);
            Assert.Equal(BrightnessPolicy.SleepValue, policy.Current);
            policy.ApplyButtonEvent(Sleep, Short);
            Assert.Equal(200, policy.Current); // wakes to the slider value
        }

        [Fact]
        public void LongPressesNeverMoveThePolicy()
        {
            // The firmware uses long presses for its local test/version
            // screen; the host policy must ignore them entirely.
            var policy = new BrightnessPolicy(80);
            int events = 0;
            policy.Changed += _ => events++;
            policy.ApplyButtonEvent(Up, Long);
            policy.ApplyButtonEvent(Down, Long);
            policy.ApplyButtonEvent(Sleep, Long);
            Assert.Equal(80, policy.Current);
            Assert.Equal(0, events);
        }

        [Fact]
        public void UnassignedButtonAndKindIdsAreIgnored()
        {
            var policy = new BrightnessPolicy(80);
            int events = 0;
            policy.Changed += _ => events++;
            policy.ApplyButtonEvent(7, Short); // unassigned button
            policy.ApplyButtonEvent(Up, 9);    // unassigned kind
            Assert.Equal(80, policy.Current);
            Assert.Equal(0, events);
        }

        [Fact]
        public void ChangedFiresOncePerEffectiveChangeWithTheNewValue()
        {
            var policy = new BrightnessPolicy(80);
            var seen = new List<byte>();
            policy.Changed += v => seen.Add(v);

            policy.ApplyButtonEvent(Up, Short);   // 92
            policy.SetDirect(92);                 // no-op: same value
            policy.SetDirect(10);                 // 10
            policy.ApplyButtonEvent(Sleep, Short); // 10 > 6 → 6
            Assert.Equal(new List<byte> { 92, 10, 6 }, seen);
        }

        [Fact]
        public void SaturatedStepDoesNotFireChanged()
        {
            var policy = new BrightnessPolicy(255);
            int events = 0;
            policy.Changed += _ => events++;
            policy.ApplyButtonEvent(Up, Short); // 255 → 255: no change
            Assert.Equal(0, events);
        }

        [Fact]
        public void RacingChangesCannotDeliverAStaleValueLast()
        {
            // Ordering pin: Changed is raised while holding the state lock,
            // so delivery order always matches mutation order. Before that
            // guarantee, a thread preempted between mutating _current and
            // raising could deliver its (now stale) value AFTER a newer
            // change had already been delivered — pinning both subscribers
            // (the device TX slot and Settings.Brightness) to a value that
            // no longer matches Current, a divergence that survived a SimHub
            // restart. The blocking handler below forces exactly that
            // preemption window deterministically.
            var policy = new BrightnessPolicy(80);
            byte lastDelivered = 0;
            var deliveries = new List<byte>();
            using (var blockedRaiseEntered = new ManualResetEventSlim(false))
            using (var releaseBlockedRaise = new ManualResetEventSlim(false))
            {
                policy.Changed += v =>
                {
                    if (v == 100)
                    {
                        blockedRaiseEntered.Set();
                        releaseBlockedRaise.Wait(5000);
                    }
                    lock (deliveries)
                    {
                        lastDelivered = v; // what a real subscriber keeps
                        deliveries.Add(v);
                    }
                };

                var slow = new Thread(() => policy.SetDirect(100));
                slow.Start();
                Assert.True(
                    blockedRaiseEntered.Wait(5000), "the first delivery should be in flight");

                var fast = new Thread(() => policy.SetDirect(50));
                fast.Start();
                // The ordering contract: a second change cannot complete
                // (and deliver) while an earlier delivery is in flight.
                Assert.False(
                    fast.Join(200), "the second change must wait for the in-flight delivery");

                releaseBlockedRaise.Set();
                Assert.True(slow.Join(5000), "the blocked change should finish once released");
                Assert.True(fast.Join(5000), "the racing change should finish once released");
            }

            Assert.Equal(new List<byte> { 100, 50 }, deliveries);
            Assert.Equal(50, policy.Current);
            // The last delivery carries the final state — subscribers never
            // end pinned to a stale value.
            Assert.Equal(policy.Current, lastDelivered);
        }

        [Fact]
        public void ReplaySequenceMatchesTheV1Semantics()
        {
            // A longer tour hand-evaluated against BrightnessController's
            // apply(): seed 30 → Down 18 → Down 6 → (asleep at exactly
            // SLEEP) Sleep wakes to max(6, 12) = 12 → Up 24 → Sleep dims to
            // 6 remembering 24 → Sleep wakes to 24.
            var policy = new BrightnessPolicy(30);
            var expected = new byte[] { 18, 6, 12, 24, 6, 24 };
            var steps = new[]
            {
                new byte[] { Down, Short },
                new byte[] { Down, Short },
                new byte[] { Sleep, Short },
                new byte[] { Up, Short },
                new byte[] { Sleep, Short },
                new byte[] { Sleep, Short },
            };
            for (int i = 0; i < steps.Length; i++)
            {
                policy.ApplyButtonEvent(steps[i][0], steps[i][1]);
                Assert.Equal(expected[i], policy.Current);
            }
        }
    }
}
