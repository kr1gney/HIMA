using System;

namespace AscentPilot
{
    internal static class AscentGuidance
    {
        internal enum ResumeState { Liftoff, PoweredAscent, Coast, Circularize, RecoverDescent, AlreadyOrbiting }
        internal struct RecoveryCommand { internal double Up, Side; }

        internal static double PeriapsisWarpStopLead(double burnLead, double warpSpeed, double frameSeconds)
        {
            return Math.Max(0, burnLead) + 30.0 + 2.0 * Math.Max(1, warpSpeed) * Math.Max(.02, frameSeconds);
        }
        internal static bool LowerOrbitComplete(double apoapsis, double periapsis, double targetAltitude, double safeAltitude)
        {
            double tolerance = Math.Max(2000.0, targetAltitude * .05);
            return periapsis >= Math.Max(safeAltitude, targetAltitude - tolerance) && apoapsis <= targetAltitude + tolerance;
        }

        internal static bool ShouldLowerOrbit(bool grounded, bool bound, double periapsisRadius, double targetRadius, double targetAltitude)
        {
            return !grounded && bound && periapsisRadius > targetRadius + Math.Max(200, targetAltitude * .005);
        }
        internal static double TransferSpeedAtApoapsis(double mu, double radius, double targetRadius)
        {
            return Math.Sqrt(mu * (2.0 / radius - 2.0 / (radius + targetRadius)));
        }

        internal static ResumeState ChooseResume(bool grounded, double verticalSpeed, double altitude,
            double targetAltitude, bool hasOrbit, double apoapsis, double periapsis, double eccentricity)
        {
            double tolerance = Math.Max(1000.0, targetAltitude * 0.025);
            if (hasOrbit && periapsis >= targetAltitude - tolerance && apoapsis <= targetAltitude + tolerance && eccentricity < 0.005)
                return ResumeState.AlreadyOrbiting;
            if (grounded) return ResumeState.Liftoff;
            if (verticalSpeed < -1.0) return ResumeState.RecoverDescent;
            if (hasOrbit && apoapsis >= targetAltitude)
                return verticalSpeed <= 1.0 ? ResumeState.Circularize : ResumeState.Coast;
            if (altitude >= targetAltitude - tolerance && verticalSpeed >= 0)
                return ResumeState.Circularize;
            return ResumeState.PoweredAscent;
        }

        internal static RecoveryCommand RecoveryAcceleration(double maxAcceleration, double effectiveGravity,
            double targetRadius, double radius, double radialSpeed)
        {
            double maxAccel = Math.Max(0, maxAcceleration);
            double desiredClimb = Math.Max(30.0, Math.Min(250.0, Math.Max(0, targetRadius - radius) * 0.03));
            double up = Math.Max(maxAccel * 0.6, effectiveGravity + (desiredClimb - radialSpeed) * 0.25);
            up = Math.Max(0, Math.Min(maxAccel, up));
            return new RecoveryCommand { Up = up, Side = Math.Sqrt(Math.Max(0, maxAccel * maxAccel - up * up)) * 0.85 };
        }

        internal static double LimitBelowTarget(double demand, double radius, double targetRadius, double radialSpeed)
        {
            return radius < targetRadius && radialSpeed >= 0 ? Math.Max(0, demand) : demand;
        }
        internal static double AtmosphericTilt(double verticalSpeed, double horizontalSpeed, double densityFraction)
        {
            double density = Math.Max(0, Math.Min(1, densityFraction));
            double maxTilt = 12.0 + 28.0 * (1.0 - density);
            double progradeTilt = Math.Atan2(Math.Abs(horizontalSpeed), Math.Max(20.0, verticalSpeed)) * 180.0 / Math.PI;
            return Math.Max(3.0, Math.Min(maxTilt, progradeTilt + 3.0));
        }

        internal static double CoastApoapsis(double px, double py, double vx, double vy,
            double planetRadius, double mu, double atmosphereHeight, double dragFactor, Func<double, double> density)
        {
            const double dt = 0.25;
            for (int i = 0; i < 1200; i++)
            {
                double r = Math.Sqrt(px * px + py * py);
                if (r <= 0) return double.NaN;
                double radialSpeed = (px * vx + py * vy) / r;
                if (r - planetRadius >= atmosphereHeight || radialSpeed <= 0) break;
                double speed = Math.Sqrt(vx * vx + vy * vy);
                double drag = Math.Max(0, dragFactor) * Math.Max(0, density(r - planetRadius)) * speed;
                double gravity = mu / (r * r * r);
                vx += (-gravity * px - drag * vx) * dt;
                vy += (-gravity * py - drag * vy) * dt;
                px += vx * dt;
                py += vy * dt;
            }
            double radius = Math.Sqrt(px * px + py * py);
            if (radius <= 0 || mu <= 0) return double.NaN;
            double energy = (vx * vx + vy * vy) * 0.5 - mu / radius;
            if (energy >= 0) return double.PositiveInfinity;
            double momentum = px * vy - py * vx;
            double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * momentum * momentum / (mu * mu)));
            return (-mu / (2 * energy)) * (1 + e) - planetRadius;
        }
    }
}
