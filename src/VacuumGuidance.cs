using System;

namespace RecoveryPilot
{
    internal static class VacuumGuidance
    {
        internal static double PreparationSeconds(double angleError, double angularSpeed, double warpAllowance)
        {
            double steering = Math.Min(30, Math.Abs(angleError) / 8.0 + Math.Abs(angularSpeed) / 4.0);
            return 20 + steering + Math.Max(0, Math.Min(10, warpAllowance));
        }
        internal static bool Prepare(double height, double burnHeight, double down, double gravity, double seconds)
        {
            return down > 0 && height <= burnHeight + down * seconds + .5 * Math.Max(0, gravity) * seconds * seconds;
        }
        internal static bool Stable(double error, double angularSpeed) => Math.Abs(error) < 6 && Math.Abs(angularSpeed) < 3;
        internal static double SecondBurnHeight(double down, double sideways, double gravity, double acceleration, double landingSpeed, double reserve)
        {
            double net=acceleration*.80-gravity;
            if(!(net>0)) return double.PositiveInfinity;
            double delay=3.0;
            double delayed=down+gravity*delay;
            double braking=Math.Max(0,delayed*delayed-landingSpeed*landingSpeed)/(2*net);
            double lateral=Math.Abs(sideways)*delay + sideways*sideways/(2*Math.Max(.01,acceleration*.20));
            return 25 + Math.Max(0,reserve) + down*delay + .5*gravity*delay*delay + braking*1.25 + lateral;
        }
        internal static bool CanCoast(double clearance, double down, double sideways, double gravity, double acceleration, double landingSpeed, double reserve)
        {
            if(double.IsNaN(clearance) || double.IsInfinity(clearance) || gravity<=0 || acceleration<=gravity*1.3 || down<0 || down>10 || Math.Abs(sideways)>1.5) return false;
            const double coastSeconds=8;
            double futureDown=down+gravity*coastSeconds;
            double travel=down*coastSeconds+.5*gravity*coastSeconds*coastSeconds;
            double trigger=SecondBurnHeight(futureDown,sideways,gravity,acceleration,landingSpeed,reserve);
            return clearance>Math.Max(200,travel+trigger+40);
        }
        internal static double ThrottleCap(double down, double landingSpeed, double gravity, double upAcceleration)
        {
            if (upAcceleration <= .001) return 1;
            double allowedUp = Math.Max(0, gravity) + Math.Max(0, down - landingSpeed) / 3.0;
            return Math.Max(0, Math.Min(1, allowedUp / upAcceleration));
        }
    }
}
