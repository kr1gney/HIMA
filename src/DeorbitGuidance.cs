using System;
namespace RecoveryPilot
{
    internal static class DeorbitGuidance
    {
        internal static double Periapsis(double px,double py,double vx,double vy,double mu)
        {
            double radius=Math.Sqrt(px*px+py*py);
            if(!(radius>0) || !(mu>0)) return double.NaN;
            double h=px*vy-py*vx, energy=(vx*vx+vy*vy)*.5-mu/radius;
            double e=Math.Sqrt(Math.Max(0,1+2*energy*h*h/(mu*mu)));
            return h*h/(mu*(1+e));
        }
        internal static double RequiredDeltaV(double px,double py,double vx,double vy,double mu,double desiredRadius)
        {
            double speed=Math.Sqrt(vx*vx+vy*vy), initial=Periapsis(px,py,vx,vy,mu);
            if(!(speed>.1) || !(desiredRadius>0) || double.IsNaN(initial)) return double.NaN;
            if(initial<=desiredRadius) return 0;
            double lo=0, hi=Math.Min(.5,speed*.95), max=speed*.95;
            while(true)
            {
                double factor=1-hi/speed, value=Periapsis(px,py,vx*factor,vy*factor,mu);
                if(double.IsNaN(value)) return double.NaN;
                if(value<=desiredRadius) break;
                if(hi>=max) return double.NaN;
                lo=hi; hi=Math.Min(max,hi*2);
            }
            for(int i=0;i<35;i++)
            {
                double mid=(lo+hi)*.5, factor=1-mid/speed;
                if(Periapsis(px,py,vx*factor,vy*factor,mu)<=desiredRadius) hi=mid; else lo=mid;
            }
            return hi;
        }
        internal static double Throttle(double deltaV,double acceleration)
        {
            if(double.IsNaN(deltaV) || double.IsInfinity(deltaV) || deltaV<0 || !(acceleration>0) || double.IsInfinity(acceleration)) return 0;
            if(deltaV==0) return 0;
            return Math.Max(.002,Math.Min(1,deltaV/(acceleration*1.5)));
        }
    }
}
