using System;

namespace RecoveryPilot
{
    internal static class EntryPreparation
    {
        internal static double LeadSeconds(double angle, double spin, double thrustToWeight, double warpStep)
        {
            double turn = Math.Min(35, Math.Abs(angle) / 6 + Math.Abs(spin) / 3);
            double lowTwr = Math.Max(0, Math.Min(30, (2.0 - thrustToWeight) * 20));
            double warp = Math.Max(0, Math.Min(120, warpStep * 3));
            return 30 + turn + lowTwr + warp;
        }
        internal static bool ShouldStart(double timeToEntry, double lead)
        {
            return !double.IsNaN(timeToEntry) && !double.IsInfinity(timeToEntry) && timeToEntry >= 0 && timeToEntry <= lead;
        }
    }
}
