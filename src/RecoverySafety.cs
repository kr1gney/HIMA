using System;
namespace RecoveryPilot
{
    internal static class RecoverySafety
    {
        internal static double PeriapsisAngle(double px,double py,double vx,double vy,double mu)
        {
            double radius=Math.Sqrt(px*px+py*py);
            if(!(radius>0) || !(mu>0)) return double.NaN;
            double rv=px*vx+py*vy, v2=vx*vx+vy*vy;
            double ex=((v2-mu/radius)*px-rv*vx)/mu, ey=((v2-mu/radius)*py-rv*vy)/mu;
            if(double.IsNaN(ex) || double.IsNaN(ey) || double.IsInfinity(ex) || double.IsInfinity(ey)) return double.NaN;
            return Math.Abs(ex)+Math.Abs(ey)>1e-8 ? Math.Atan2(ey,ex) : Math.Atan2(py,px);
        }
        internal static double TouchdownTarget(double tolerance)
        {
            if(!(tolerance>0) || double.IsNaN(tolerance) || double.IsInfinity(tolerance)) tolerance=2.5;
            return Math.Min(3.5,tolerance*.8);
        }
        internal static bool Reascending(bool terminalEngaged,double vertical) => terminalEngaged && vertical>2;
        internal static bool ResumeDescent(double vertical) => vertical<=.5;
        internal static bool AllowOrbitalRecovery(bool terminalEngaged) => !terminalEngaged;
    }
}
