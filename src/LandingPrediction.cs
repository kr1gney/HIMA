using System;

namespace RecoveryPilot
{
    // Two-dimensional coast followed by a full-power burn. The caller supplies
    // the game's density curve and the current effective drag coefficient.
    internal static class LandingPrediction
    {
        internal enum Outcome { Viable, TooLate, Uncertain }

        internal static Outcome Predict(double px, double py, double vx, double vy,
            double planetRadius, double surfaceRadius, double mu, double acceleration,
            double dragFactor, double targetSpeed, double coastSeconds, Func<double, double> densityAtHeight)
        {
            double ignored;
            return PredictDetailed(px, py, vx, vy, planetRadius, surfaceRadius, mu, acceleration,
                dragFactor, targetSpeed, coastSeconds, densityAtHeight, 0, 0, out ignored);
        }

        internal static Outcome PredictDetailed(double px, double py, double vx, double vy,
            double planetRadius, double surfaceRadius, double mu, double acceleration,
            double dragFactor, double targetSpeed, double coastSeconds, Func<double, double> densityAtHeight,
            double initialMass, double fullThrottleMassFlow, out double finishHeight)
        {
            finishHeight = double.NaN;
            if (mu <= 0 || acceleration <= 0 || surfaceRadius <= 0) return Outcome.Uncertain;
            double elapsed = 0;
            double consumedMass = 0;
            for (int i = 0; i < 3000 && elapsed < 300.0; i++)
            {
                double r = Math.Sqrt(px * px + py * py);
                if (r < 1 || double.IsNaN(r)) return Outcome.Uncertain;
                double ux = px / r, uy = py / r;
                double height = r - surfaceRadius;
                double vertical = vx * ux + vy * uy;
                double down = Math.Max(0, -vertical);
                double side = -vx * uy + vy * ux;
                double sideLimit = targetSpeed < 3.5 ? .5 : 1.5;
                double speedMargin = targetSpeed < 3.5 ? .15 : .5;
                if (height <= 20.0)
                {
                    finishHeight = height;
                    return vertical <= 0.5 && down <= targetSpeed + speedMargin && Math.Abs(side) <= sideLimit ? Outcome.Viable : Outcome.TooLate;
                }
                if (elapsed >= coastSeconds && down <= targetSpeed + (targetSpeed < 3.5 ? .15 : .25) && Math.Abs(side) <= Math.Min(1.0,sideLimit) && vertical <= 0.5)
                {
                    finishHeight = height;
                    return Outcome.Viable;
                }

                double speed = Math.Sqrt(vx * vx + vy * vy);
                double dt = height < 500.0 ? 0.05 : 0.2;
                dt = Math.Min(dt, Math.Max(0.01, (height - 20.0) / Math.Max(1.0, down) * 0.2));
                double g = mu / (r * r);
                double density = Math.Max(0, densityAtHeight(r - planetRadius));
                double massScale = initialMass > 0 ? initialMass / Math.Max(initialMass * 0.5, initialMass - consumedMass) : 1.0;
                double drag = Math.Max(0, dragFactor) * massScale * density * speed;
                double ax = -g * ux - drag * vx;
                double ay = -g * uy - drag * vy;
                if (elapsed >= coastSeconds)
                {
                    double remainingMass = initialMass > 0 ? Math.Max(initialMass * 0.5, initialMass - consumedMass) : 0;
                    double available = initialMass > 0 ? acceleration * initialMass / remainingMass : acceleration;
                    double denom = available * available - g * g;
                    if (denom <= 0.001) return Outcome.Uncertain;
                    double delta = down - targetSpeed;
                    double b = g * delta;
                    double t = Math.Max(0.3, (b + Math.Sqrt(Math.Max(0, b * b + denom * (side * side + delta * delta)))) / denom);
                    double forceUp = Math.Max(0.01, g + delta / t);
                    double forceSide = -side / t;
                    double power = 1.0;
                    if (down <= targetSpeed + 3.0 && Math.Abs(side) <= 2.0)
                    {
                        forceUp = Math.Max(0, g + delta * 1.2);
                        forceSide = -side * 0.8;
                        power = Math.Min(1.0, Math.Sqrt(forceUp * forceUp + forceSide * forceSide) / available);
                    }
                    double forceLength = Math.Sqrt(forceUp * forceUp + forceSide * forceSide);
                    if (forceLength > 0.000001)
                    {
                        ax += power * available * (forceUp * ux - forceSide * uy) / forceLength;
                        ay += power * available * (forceUp * uy + forceSide * ux) / forceLength;
                    }
                    if (initialMass > 0 && fullThrottleMassFlow > 0)
                        consumedMass += fullThrottleMassFlow * power * dt;
                }
                vx += ax * dt; vy += ay * dt;
                px += vx * dt; py += vy * dt;
                elapsed += dt;
            }
            return Outcome.Uncertain;
        }
    }
}
