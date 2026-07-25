// SPDX-License-Identifier: GPL-3.0-or-later WITH GPL-3.0-linking-exception
//
// The Grammar scenario catalogue: a curated tour of the signal vocabulary
// (docs/flag-grammar.md §6-§7), each entry a script — a sequence of
// (frame, state) steps replayed through the compositor and envelope from
// frame 0 — plus the frame that best discriminates the signal.
//
// Because the envelope makes rendering a function of state HISTORY, a
// scenario cannot be reduced to a single state; the script is the unit.
// Sample frames are chosen deliberately: strobe on/off phases, breathe
// peaks and mid-points, sweep positions, flash blend weights, fade depths.
//
// This is dev tooling, not a fixture — it pins nothing. It drives the frame
// viewer and the GrammarSmokeTests whole-scenario replay. It outlived the
// byte corpus it was written for (docs/flag-grammar.md §11) because the
// curation is the valuable part: these are the states worth looking at.

using System.Collections.Generic;
using Uniflag.Rendering;
using Uniflag.Rendering.Grammar;
using Caution = Uniflag.Rendering.Grammar.Caution;
using SectorSet = Uniflag.Rendering.SectorSet;
using Session = Uniflag.Rendering.Session;

namespace Uniflag.Tools
{
    public static class ScenarioCatalogue
    {
        public delegate void StateMutator(ref SignalState s);

        public sealed class Step
        {
            public Step(uint frame, SignalState state)
            {
                Frame = frame;
                State = state;
            }

            public uint Frame { get; }
            public SignalState State { get; }
        }

        public sealed class Scenario
        {
            public Scenario(string name, string description, uint sampleFrame, Step[] steps)
            {
                Name = name;
                Description = description;
                SampleFrame = sampleFrame;
                Steps = steps;
            }

            public string Name { get; }
            public string Description { get; }
            public uint SampleFrame { get; }
            public Step[] Steps { get; }

            /// <summary>The state in force at <paramref name="frame"/>.</summary>
            public SignalState StateAt(uint frame)
            {
                SignalState current = Steps[0].State;
                foreach (Step step in Steps)
                {
                    if (step.Frame <= frame)
                    {
                        current = step.State;
                    }
                }
                return current;
            }
        }

        private static Step St(uint frame, StateMutator mutate)
        {
            var s = SignalState.Default;
            mutate(ref s);
            return new Step(frame, s);
        }

        private static Scenario Sc(string name, string description, uint sampleFrame, params Step[] steps) =>
            new Scenario(name, description, sampleFrame, steps);

        /// <summary>The catalogue. Names are unique.</summary>
        public static readonly IReadOnlyList<Scenario> Table = new[]
        {
            Sc("yellow_t0_ambient", "settled yellow cloth wave", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Session = Session.Racing; })),
            Sc("yellow_t1_flash_white", "onset flash, solid-white opening frame", 1,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("yellow_t1_flash_blend", "onset flash mid-blend (age 5)", 5,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("yellow_t1_pulse_on", "alert pulse on-phase", 40,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("yellow_t1_pulse_off", "alert pulse off-phase (dimmed, not black)", 20,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("yellow_t2_strobe_off", "urgent strobe off-phase (black)", 13,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Urgent; s.Session = Session.Racing; })),
            Sc("red_flash_ramp", "red onset ramp (age 4)", 4,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Red; s.Session = Session.Racing; })),
            Sc("red_t2_strobe_on", "red urgent strobe on-phase", 95,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Red; s.Session = Session.Racing; })),
            Sc("red_settled_ambient", "red settled to calm cloth wave", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Red; s.Session = Session.Racing; })),
            Sc("green_onset_band_mid", "green signature sweep, band centred (age 15)", 15,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Green; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("green_settled_ambient", "green settled cloth wave", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Green; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("blue_t1_sweep", "blue alert sweep band at x=18", 100,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Blue; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("blue_settled_ambient", "blue settled, sweep stopped", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Blue; s.Tier = Tier.Alert; s.Session = Session.Racing; })),
            Sc("white_t0_settled", "white settled cloth wave", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.White; s.Session = Session.Racing; })),
            Sc("black_attention_x_on", "black flag attention pulse, X bright", 40,
                St(0, (ref SignalState s) => { s.BlackFlag = true; s.Session = Session.Racing; })),
            Sc("black_ambient_breathe_mid", "black flag settled, X breathe mid-level", 360,
                St(0, (ref SignalState s) => { s.BlackFlag = true; s.Session = Session.Racing; })),
            Sc("dq_steady_x_and_board", "disqualification: steady X + DQ board", 100,
                St(0, (ref SignalState s) => { s.BlackDetail = BlackDetail.Disqualified; s.Session = Session.Racing; })),
            Sc("meatball_ambient_disc", "meatball settled, disc breathe mid-level", 360,
                St(0, (ref SignalState s) => { s.Meatball = true; s.Session = Session.Racing; })),
            Sc("checkered_attention_scroll", "checkered attention-speed scroll", 100,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Checkered; s.Session = Session.Racing; })),
            Sc("checkered_settled_drift", "checkered settled lazy drift", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Checkered; s.Session = Session.Racing; })),
            Sc("debris_ambient_stripes", "debris stripe field", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Debris; s.Session = Session.Racing; })),
            Sc("sc_board_over_caution_yellow", "SC board on the caution-forced yellow field", 400,
                St(0, (ref SignalState s) => { s.Caution = Caution.SafetyCar; s.Session = Session.Racing; })),
            Sc("vsc_board", "VSC board on the caution-forced yellow field", 400,
                St(0, (ref SignalState s) => { s.Caution = Caution.VirtualSafetyCar; s.Session = Session.Racing; })),
            Sc("fcy_board", "FCY board on the caution-forced yellow field", 400,
                St(0, (ref SignalState s) => { s.Caution = Caution.FullCourseYellow; s.Session = Session.Racing; })),
            Sc("dt_board_over_black_field", "drive-through board over the black field", 400,
                St(0, (ref SignalState s) => { s.BlackFlag = true; s.BlackDetail = BlackDetail.DriveThrough; s.Session = Session.Racing; })),
            Sc("sg_board_over_black_field", "stop-and-go board over the black field", 400,
                St(0, (ref SignalState s) => { s.BlackFlag = true; s.BlackDetail = BlackDetail.StopAndGo; s.Session = Session.Racing; })),
            Sc("demoted_x_board_over_yellow", "bare black flag demoted to the X board under yellow", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Alert; s.BlackFlag = true; s.Session = Session.Racing; })),
            Sc("demoted_disc_board_over_black", "meatball demoted to the disc board under the black field", 400,
                St(0, (ref SignalState s) => { s.BlackFlag = true; s.Meatball = true; s.Session = Session.Racing; })),
            Sc("time_penalty_plus5_over_green", "+5 time-penalty board over a green field", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Green; s.TimePenaltySeconds = 5; s.Session = Session.Racing; })),
            Sc("countdown_10_over_white", "10-to-go countdown board over the white field", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.White; s.CountdownLaps = 10; s.Session = Session.Racing; })),
            Sc("gantry_ready_breathe_mid", "gantry standby breathe mid-level", 360,
                St(0, (ref SignalState s) => { s.StartPhase = StartPhase.Ready; })),
            Sc("gantry_set_3of5", "gantry set phase, 3 of 5 lights", 400,
                St(0, (ref SignalState s) => { s.StartPhase = StartPhase.Set; s.StartLightsLit = 3; })),
            Sc("gantry_go_lights_out", "gantry go phase, lights out", 100,
                St(0, (ref SignalState s) => { s.StartPhase = StartPhase.Go; })),
            Sc("incident_ring_settled_over_yellow", "incident ring settled dim over yellow", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.IncidentWarning = true; s.Session = Session.Racing; })),
            Sc("furled_dashes_on_over_race_idle", "furled dash ring blink on-phase over race idle", 40,
                St(0, (ref SignalState s) => { s.Furled = true; s.Session = Session.Racing; })),
            Sc("strip_sector2_settled", "sector strip: S2 active settled, others dim", 400,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Sectors = SectorSet.Empty.With(2); s.Session = Session.Racing; })),
            Sc("strip_sector13_urgent_off", "sector strip: S1+S3 active, urgent off-phase", 13,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Tier = Tier.Urgent; s.Sectors = SectorSet.Empty.With(1).With(3); s.Session = Session.Racing; })),
            Sc("fade_mid_yellow_clear", "yellow cleared, fade-out mid-way (age 7)", 407,
                St(0, (ref SignalState s) => { s.Flag = TrackFlag.Yellow; s.Session = Session.Racing; }),
                St(400, (ref SignalState s) => { s.Session = Session.Racing; })),
            Sc("race_idle_static_ticks", "race idle: four static grey ticks", 100,
                St(0, (ref SignalState s) => { s.Session = Session.Racing; })),
            Sc("session_idle_flankers_peak", "session idle: violet flankers, left peak", 60,
                St(0, (ref SignalState s) => { s.Session = Session.Unknown; })),
        };

        /// <summary>Look a scenario up by name, or null if there is no such scenario.</summary>
        public static Scenario Find(string name)
        {
            foreach (Scenario sc in Table)
            {
                if (sc.Name == name)
                {
                    return sc;
                }
            }
            return null;
        }

        /// <summary>Replay a scenario's script from frame 0 and render its sample frame.</summary>
        public static byte[] Render(Scenario sc) => Render(sc, sc.SampleFrame);

        /// <summary>
        /// Replay a scenario's script from frame 0 and render <paramref name="frame"/>.
        /// Replay always starts at 0: the envelope is a function of state history,
        /// so a frame rendered from a cold tracker is a different frame.
        /// </summary>
        public static byte[] Render(Scenario sc, uint frame)
        {
            var tracker = new EnvelopeTracker();
            var buf = new FrameBuffer();
            for (uint f = 0; f <= frame; f++)
            {
                SignalState state = sc.StateAt(f);
                var comp = Compositor.Select(state, true);
                var env = tracker.Update(comp, state, f);
                if (f == frame)
                {
                    Painter.Paint(buf, comp, env, state, f);
                }
            }
            return (byte[])buf.Pixels.Clone();
        }
    }
}
