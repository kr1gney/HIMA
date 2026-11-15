using System;

namespace RecoveryPilot
{
    // Constant-deceleration bridge to the low-altitude target velocity.
    internal static class LandingTrim
    {
        internal struct Demand { internal double Up, Side, Throttle; }

        internal static Demand Compute(double height, double down, double side, double gravity,
            double acceleration, double targetSpeed, double dragMagnitude, double uprightStart, double lateralPlanningHeight)
        {
            double speed = Math.Max(1.0, Math.Sqrt(down * down + side * side));
            double aeroUp = Math.Max(0, dragMagnitude) * down / speed;
            double aeroSide = -Math.Max(0, dragMagnitude) * side / speed;
            double desiredUp, desiredSide;
            if (height > 20.0)
            {
                double time = Math.Max(0.5, 2.0 * (height - 20.0) / Math.Max(1.0, down + targetSpeed));
                // Retain more descent speed when there is ample braking room;
                // switch back to the conservative profile near the stop window.
                // Do not rely on drag in the emergency stopping margin.
                double availableHeight = Math.Max(1.0, height - 20.0 - Math.Max(10.0, down));
                double deltaSquared = Math.Max(0.0, down * down - targetSpeed * targetSpeed);
                double safeNet = Math.Max(0.1, acceleration * 0.70 - gravity);
                double stopDistance = deltaSquared / (2.0 * safeNet);
                double gain = stopDistance < availableHeight * 0.55 ? 0.70 : 1.15;
                double deceleration = gain * deltaSquared / (2.0 * availableHeight);
                desiredUp = Math.Max(0.01, gravity + deceleration - aeroUp);
                // Lateral thrust is progressively restricted from UprightStart,
                // long before the fully upright hold. Plan to remove lateral
                // speed before that restriction, including rotation lead time.
                double deadlineHeight = Math.Max(20.0, uprightStart) + Math.Min(100.0, Math.Max(0, down) * 1.2);
                double deadlineTime = Math.Max(1.5, (lateralPlanningHeight - deadlineHeight) / Math.Max(4.0, down * 0.8));
                double lateralTime = Math.Max(0.5, Math.Min(time * 0.65, deadlineTime));
                double sideToRemove = Math.Sign(side) * Math.Max(0.0, Math.Abs(side) - 0.5);
                desiredSide = -sideToRemove / lateralTime - aeroSide;
            }
            else
            {
                desiredUp = Math.Max(0.01, gravity + (down - targetSpeed) * 1.2 - aeroUp);
                desiredSide = -side * 0.8 - aeroSide;
            }
            // Extra atmospheric braking is welcome: do not spend thrust
            // accelerating in the same direction as lateral motion to undo it.
            if (desiredSide * side > 0) desiredSide = 0;
            // Preserve the requested upward component if the two-axis demand
            // exceeds available thrust; excess lateral demand must not steal
            // the vertical braking needed to avoid impact.
            double lateralLimit = Math.Sqrt(Math.Max(0, acceleration * acceleration - desiredUp * desiredUp));
            desiredSide = Math.Max(-lateralLimit, Math.Min(lateralLimit, desiredSide));
            double magnitude = Math.Sqrt(desiredUp * desiredUp + desiredSide * desiredSide);
            double keepLit = Math.Min(0.05, 0.25 * Math.Max(0, gravity) / Math.Max(0.01, acceleration));
            return new Demand { Up = desiredUp, Side = desiredSide,
                Throttle = Math.Max(keepLit, Math.Min(1.0, magnitude / Math.Max(0.01, acceleration))) };
        }
    }
}
