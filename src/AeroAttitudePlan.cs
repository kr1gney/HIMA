using System;
namespace RecoveryPilot
{
    internal static class AeroAttitudePlan
    {
        internal static int Select(double[] factors, double minimumGain)
        {
            if(factors==null || factors.Length<1 || !(factors[0]>0) || double.IsInfinity(factors[0])) return -1;
            int best=0;
            for(int i=1;i<factors.Length;i++) if(!double.IsNaN(factors[i]) && !double.IsInfinity(factors[i]) && factors[i]>factors[best]) best=i;
            return factors[best]>=factors[0]*minimumGain ? best : 0;
        }
        internal static int SelectLocked(double[] factors, int lockedIndex)
        {
            if (lockedIndex != 1 && lockedIndex != 2) return Select(factors, 1.0);
            if (factors == null || factors.Length <= lockedIndex || !(factors[0] > 0) ||
                double.IsInfinity(factors[0]) || !(factors[lockedIndex] > 0) ||
                double.IsInfinity(factors[lockedIndex])) return -1;
            return lockedIndex;
        }
        internal static double TurnDelay(double angle,double spin)
        {
            if(double.IsNaN(angle) || double.IsNaN(spin)) return 30;
            return Math.Min(30, Math.Max(0,(Math.Abs(angle)-8)/8.0)+Math.Min(8,Math.Abs(spin)/3.0)+2);
        }
        internal static double ConservativeFactor(double current,double retro)
        {
            if(!(current>=0) || !(retro>=0) || double.IsInfinity(current) || double.IsInfinity(retro)) return 0;
            return Math.Min(current,retro);
        }
        internal static bool UncertainCoastSafe(double height, double down, double verticalStopDistance, double turnDelay, double reserve)
        {
            if (double.IsNaN(height) || double.IsNaN(down) || double.IsNaN(verticalStopDistance) || double.IsNaN(turnDelay)) return false;
            double margin = 1.15 * (Math.Max(0, verticalStopDistance) + Math.Max(0, down) * (Math.Max(0, turnDelay) + 3.0)) + Math.Max(0, reserve);
            return height > Math.Max(3000.0, margin);
        }
    }
}
