using System;

namespace RecoveryPilot
{
    // Estimate the along-track environmental acceleration from consecutive
    // unpowered physics samples. Invalid data never updates the calibration.
    internal sealed class AeroCalibration
    {
        private bool hasPrevious;
        private double px, py, vx, vy, previousDensity, previousFactor, previousThrottle;
        private double ratio = 1.0;
        private double weight;
        internal double ObservedDrag { get; private set; }
        internal double Scale => weight >= 2.0 ? ratio : 1.0;
        internal bool Ready => weight >= 2.0;
        internal double ConfidenceSeconds => weight;

        internal void ResetSample() { hasPrevious = false; }

        internal void Observe(double x, double y, double velX, double velY, double mu,
            double density, double factor, double throttle, double deltaTime, bool reliable)
        {
            if (hasPrevious && reliable && previousThrottle < 0.001 && throttle < 0.001 &&
                deltaTime >= 0.001 && deltaTime <= 0.2 && previousDensity > 1e-7 && previousFactor > 1e-8)
            {
                double r = Math.Sqrt(px * px + py * py);
                double speed = Math.Sqrt(vx * vx + vy * vy);
                if (r > 0 && speed > 100)
                {
                    double gx = -mu * px / (r * r * r), gy = -mu * py / (r * r * r);
                    double residualX = (velX - vx) / deltaTime - gx;
                    double residualY = (velY - vy) / deltaTime - gy;
                    double alongDrag = -(residualX * vx + residualY * vy) / speed;
                    double crossDrag = Math.Abs(residualX * vy - residualY * vx) / speed;
                    double predicted = previousFactor * previousDensity * speed * speed;
                    if (alongDrag > 0.05 && predicted > 0.05 && crossDrag < Math.Max(2.0, alongDrag * 0.5))
                    {
                        double sample = alongDrag / predicted;
                        if (sample >= 0.25 && sample <= 4.0)
                        {
                            double alpha = 1.0 - Math.Exp(-deltaTime / 3.0);
                            ratio += (sample - ratio) * alpha;
                            weight = Math.Min(10.0, weight + deltaTime);
                            ObservedDrag = alongDrag;
                        }
                    }
                }
            }
            px = x; py = y; vx = velX; vy = velY;
            previousDensity = density; previousFactor = factor; previousThrottle = throttle;
            hasPrevious = reliable;
        }
    }
}
