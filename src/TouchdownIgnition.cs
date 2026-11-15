using System;

namespace RecoveryPilot
{
    // Conservative no-drag estimate; height is already measured from the
    // lowest part to the local terrain, so do not add the bottom offset again.
    internal static class TouchdownIgnition
    {
        internal static double BurnHeight(double down, double side, double target, double gravity, double acceleration)
        {
            if (double.IsNaN(down) || double.IsNaN(side) || double.IsNaN(target) ||
                double.IsNaN(gravity) || double.IsNaN(acceleration)) return double.PositiveInfinity;
            double available = acceleration * 0.85;
            double denominator = available * available - gravity * gravity;
            if (gravity <= 0 || available <= gravity || denominator <= 0) return double.PositiveInfinity;
            double delta = Math.Max(0, down - target);
            double b = gravity * delta;
            double t = (b + Math.Sqrt(b * b + denominator * (side * side + delta * delta))) / denominator;
            return Math.Max(0, (Math.Max(0, down) + Math.Max(0, target)) * 0.5 * t);
        }
    }
}
