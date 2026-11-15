using System;

namespace RecoveryPilot
{
    // A geometric and time allowance for rotating a tall vehicle upright.
    internal static class LandingStability
    {
        internal struct Plan
        {
            internal double IgnitionMargin;
            internal double UprightStart;
            internal double UprightHold;
            internal double MaxTiltDegrees;
        }

        private static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));

        internal static Plan Compute(double height, double down, double sideways, double gravity,
            double tiltDegrees, double angularSpeed, double span, double bottomNow, double bottomUpright)
        {
            height = Math.Max(0, height); down = Math.Max(0, down); gravity = Math.Max(0, gravity);
            double tilt = Math.Abs(tiltDegrees), spin = Math.Abs(angularSpeed);
            span = Clamp(span, 0, 400);
            double extraBottom = Clamp(bottomUpright - bottomNow, 0, 250);
            double risk = Clamp(Math.Max(tilt / 45.0, Math.Abs(sideways) / 25.0), 0, 1);
            double alignTime = Clamp(.7 + tilt / 45.0 + spin / 35.0, .7, 6);
            double gravityTime = Math.Min(2, alignTime);
            double travelMargin = Clamp(down * alignTime + .5 * gravity * gravityTime * gravityTime, 0, 150);
            double margin = extraBottom + risk * travelMargin;
            double hold = Clamp(20 + span * .45 + down * 1.5 + spin * .25, 25, 180);
            double start = Clamp(100 + span * 1.5 + down * 5 + spin * 2, 150, 800);
            start = Math.Max(start, hold + 30);
            double fraction = Clamp((height - hold) / (start - hold), 0, 1);
            double limit = height <= hold ? 0 : 65 * fraction;
            return new Plan { IgnitionMargin = margin, UprightStart = start, UprightHold = hold, MaxTiltDegrees = limit };
        }

        internal static double FinalTilt(double limitedTilt, bool burning, double height, double hold, double signedSide)
        {
            if (!burning || height > hold || Math.Abs(signedSide) <= .75) return limitedTilt;
            double cap = 3 + 5 * Clamp(height / 20, 0, 1);
            double amount = Math.Min(cap, Math.Abs(signedSide) * .8);
            return -Math.Sign(signedSide) * amount;
        }
    }
}
