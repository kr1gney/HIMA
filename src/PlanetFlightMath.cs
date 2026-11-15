using System;
namespace PlanetNavigation
{
    internal static class PlanetFlightMath
    {
        internal static bool Captured(double eccentricity,double periapsis,double apoapsis,double minimumRadius,double soi,double bodyRadius)
        {
            return !double.IsNaN(eccentricity) && eccentricity>=0 && eccentricity<1 &&
                !double.IsNaN(periapsis) && !double.IsInfinity(periapsis) && periapsis>=minimumRadius &&
                !double.IsNaN(apoapsis) && !double.IsInfinity(apoapsis) && apoapsis>=periapsis &&
                (double.IsPositiveInfinity(soi) || (soi>0 && apoapsis<soi*.95)) &&
                apoapsis-periapsis<=Math.Max(3000,Math.Max(0,periapsis-bodyRadius)*.05);
        }
        internal static bool Derivative(double baseline,double step,bool hasPlus,double plus,bool hasMinus,double minus,out double value)
        {
            value=0;
            if(!(step>0) || double.IsNaN(baseline) || double.IsInfinity(baseline)) return false;
            if(hasPlus && hasMinus) value=(plus-minus)/(2*step);
            else if(hasPlus) value=(plus-baseline)/step;
            else if(hasMinus) value=(baseline-minus)/step;
            else return false;
            if(double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value)<.001) { value=0; return false; }
            return true;
        }
        internal static bool AtTargetPeriapsis(double altitude,double ideal,double safe)
        {
            return !double.IsNaN(altitude) && !double.IsInfinity(altitude) && altitude>=safe && Math.Abs(altitude-ideal)<=Math.Max(3000,ideal*.10);
        }
        internal static bool Correction(double altitude,double ideal,double gx,double gy,out double x,out double y)
        {
            x=y=0; double sq=gx*gx+gy*gy;
            if(double.IsNaN(sq) || double.IsInfinity(sq) || sq<1e-6) return false;
            double scale=(ideal-altitude)/sq; x=gx*scale; y=gy*scale;
            double mag=Math.Sqrt(x*x+y*y); if(double.IsNaN(mag) || double.IsInfinity(mag)) return false;
            if(mag>2) { x*=2/mag; y*=2/mag; } return true;
        }
        internal static double CapturePeriapsisSpeed(double mu, double periapsis, double apoapsis)
        {
            if (!(mu>0) || !(periapsis>0) || !(apoapsis>=periapsis)) return double.NaN;
            return Math.Sqrt(mu*(2/periapsis-2/(periapsis+apoapsis)));
        }
        internal static double WarpLead(double speed,double frameSeconds)
        {
            return 90+2*Math.Max(1,speed)*Math.Max(.02,frameSeconds);
        }
    }
}
