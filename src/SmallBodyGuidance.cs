using System;

namespace RecoveryPilot
{
    internal static class SmallBodyGuidance
    {
        // Thresholds use the live game-world radius and surface gravity, so
        // scaled/custom systems are classified from their actual physics.
        internal static bool NeedsRadialDescent(double radius, double surfaceGravity, double maximumTerrain)
        {
            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0 || surfaceGravity < 0 || double.IsNaN(surfaceGravity)) return false;
            double reliefRatio = double.IsNaN(maximumTerrain) || double.IsInfinity(maximumTerrain) ? 0 : Math.Max(0, maximumTerrain) / radius;
            return (radius <= 60000 && surfaceGravity <= 1.5) ||
                (radius <= 150000 && surfaceGravity <= 1.0 && reliefRatio >= .015);
        }
        internal static double RequiredClearance(double horizontal, double down, double gravity, double acceleration, double angle, double spin)
        {
            double turnTime = Math.Min(35, 4 + Math.Abs(angle) / 12 + Math.Abs(spin) / 3);
            double burnTime = Math.Max(0, horizontal - .03) / Math.Max(.1, acceleration * .6);
            double time = Math.Min(120, turnTime + burnTime + 2);
            return 30 + Math.Max(0, down) * time + .5 * Math.Max(0, gravity) * time * time;
        }
        internal static double HorizontalTarget(double orbitalRadius, double safeSurfaceRadius)
        {
            if (orbitalRadius <= 0 || safeSurfaceRadius <= 0) return .03;
            return Math.Max(.03, Math.Min(.5, .5 * safeSurfaceRadius / orbitalRadius));
        }
        internal static float Throttle(double horizontal, double target, double lateralAcceleration)
        {
            return (float)Math.Max(.0011, Math.Min(.6, Math.Max(0, horizontal - target) / Math.Max(.5, lateralAcceleration * 1.2)));
        }
    }
}
