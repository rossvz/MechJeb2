using System;
using KSP.UI.Screens;
using UnityEngine;
using System.Collections.Generic;

/*
 * Space-Shuttle PEG launches for RSS/RO
 */

namespace MuMech
{
    public class MechJebModuleAscentPEG : MechJebModuleAscentBase
    {
        public MechJebModuleAscentPEG(MechJebCore core) : base(core) { }

        /* default pitch program here works decently at SLT of about 1.4 */
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble pitchStartTime = new EditableDouble(10);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble pitchRate = new EditableDouble(0.75);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble pitchEndTime = new EditableDouble(55);
        [Persistent(pass = (int)(Pass.Global))]
        public EditableDoubleMult desiredApoapsis = new EditableDoubleMult(0, 1000);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public bool pitchEndToggle = false;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public bool pegAfterStageToggle = false;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public bool pegCoast = false;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public bool pegManualAzimuthToggle = false;
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble pegManualAzimuth = new EditableDouble(0);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble pegAfterStage = new EditableDouble(0);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableDouble coastSecs = new EditableDouble(0.00);
        [Persistent(pass = (int)(Pass.Type | Pass.Global))]
        public EditableInt coastAfterStage = new EditableInt(-1);

        /* this deliberately does not persist, it is for emergencies only */
        public EditableDouble pitchBias = new EditableDouble(0);

        private MechJebModulePEGController peg { get { return core.GetComputerModule<MechJebModulePEGController>(); } }

        public override void OnModuleEnabled()
        {
            mode = AscentMode.VERTICAL_ASCENT;
            peg.enabled = true;
        }

        public override void OnModuleDisabled()
        {
            peg.enabled = false;
        }

        private enum AscentMode { VERTICAL_ASCENT, INITIATE_TURN, GRAVITY_TURN, PEG, EXIT };
        private AscentMode mode;

        public override bool DriveAscent(FlightCtrlState s)
        {
            setTarget();
            peg.AssertStart();
            switch (mode)
            {
                case AscentMode.VERTICAL_ASCENT:
                    DriveVerticalAscent(s);
                    break;

                case AscentMode.INITIATE_TURN:
                    DriveInitiateTurn(s);
                    break;

                case AscentMode.GRAVITY_TURN:
                    DriveGravityTurn(s);
                    break;

                case AscentMode.PEG:
                    DrivePEG(s);
                    break;
            }

            return (mode != AscentMode.EXIT);
        }

        private void setTarget()
        {
            if (pegCoast)
                peg.SetCoast(coastSecs, coastAfterStage);
            else
                peg.SetCoast(-1, 0);

            if ( core.target.NormalTargetExists )
            {
                peg.TargetPeInsertMatchOrbitPlane(autopilot.desiredOrbitAltitude, desiredApoapsis, core.target.TargetOrbit);
                // fix the desired inclination display for the user
                autopilot.desiredInclination = Math.Acos(-Vector3d.Dot(-Planetarium.up, peg.iy)) * UtilMath.Rad2Deg;
            }
            else
            {
                peg.TargetPeInsertMatchInc(autopilot.desiredOrbitAltitude, desiredApoapsis, autopilot.desiredInclination, autopilot.launchLANDifference);
            }
        }

        private void attitudeToPEG(double pitch, bool maybeManualAz = false)
        {
            /* FIXME: use srfvel heading if peg is bad */
            double heading;

            if (maybeManualAz && pegManualAzimuthToggle)
                heading = pegManualAzimuth;
            else
                // FIXME: should remember the launch lat + lng and figure a great circle path from the launch site
                heading = peg.heading;

            attitudeTo(pitch, heading);
        }

        private void DriveVerticalAscent(FlightCtrlState s)
        {

            //during the vertical ascent we just thrust straight up at max throttle
            attitudeToPEG(90, true);
            if (autopilot.autoThrottle) core.thrust.targetThrottle = 1.0F;

            core.attitude.AxisControl(!vessel.Landed, !vessel.Landed, !vessel.Landed && vesselState.altitudeBottom > 50);

            if (!vessel.LiftedOff() || vessel.Landed) {
                status = "Awaiting liftoff";
            }
            else
            {
                if (autopilot.MET > pitchStartTime)
                {
                    mode = AscentMode.INITIATE_TURN;
                    return;
                }
                double dt = pitchStartTime - autopilot.MET;
                status = String.Format("Vertical ascent {0:F2} s", dt);
            }
        }

        private void DriveInitiateTurn(FlightCtrlState s)
        {
            double dt = autopilot.MET - pitchStartTime;
            double theta = dt * pitchRate;
            double pitch = 90 - theta + pitchBias;

            if (pitchEndToggle)
            {
                if ( autopilot.MET > pitchEndTime )
                {
                    mode = AscentMode.GRAVITY_TURN;
                    return;
                }
                status = String.Format("Pitch program {0:F2} s", pitchEndTime - pitchStartTime - dt);
            } else {
                if ( pitch < peg.pitch )
                {
                    mode = AscentMode.PEG;
                    return;
                }
                status = String.Format("Pitch program {0:F2} °", pitch - peg.pitch);
            }

            attitudeToPEG(pitch, true);
        }

        private void DriveGravityTurn(FlightCtrlState s)
        {
            double pitch = Math.Min(Math.Min(90, srfvelPitch() + pitchBias), vesselState.vesselPitch);

            if (pitchEndToggle && autopilot.MET < pitchEndTime)
            {
                // this can happen when users update the endtime box
                mode = AscentMode.INITIATE_TURN;
                return;
            }

            if ( pegAfterStageToggle )
            {
                if ( StageManager.CurrentStage < pegAfterStage )
                {
                    mode = AscentMode.PEG;
                    return;
                }
            }
            else if ( pitch < peg.pitch && peg.isStable() )
            {
                mode = AscentMode.PEG;
                return;
            }

            status = "Unguided Gravity Turn";
            attitudeToPEG(pitch, true);
        }

        private void DrivePEG(FlightCtrlState s)
        {
            if (peg.status == PegStatus.FINISHED)
            {
                mode = AscentMode.EXIT;
                return;
            }

            if (peg.isStable())
                status = "Stable PEG Guidance";
            else
                status = "WARNING: Unstable Guidance";

            attitudeToPEG(peg.pitch);
        }
    }
}
