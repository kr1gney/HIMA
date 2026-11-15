using System;
using System.Collections.Generic;
using System.Reflection;
using ModLoader.Helpers;
using SFS;
using SFS.Parts.Modules;
using SFS.UI.ModGUI;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UnityEngine;

namespace PlanetNavigation
{
    internal sealed class PlanReader
    {
        private const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private readonly FieldInfo output, gate, time, transfer, target, orbits, settings, show, origin;
        private readonly MethodInfo valid, dv;
        internal PlanReader()
        {
            Assembly a=null; foreach(var x in AppDomain.CurrentDomain.GetAssemblies()) if(x.GetName().Name=="ANAIS") { a=x; break; }
            if(a==null) throw new Exception("ANAIS not loaded");
            var manager=a.GetType("AnaisManager",true); var data=a.GetType("AnaisDataSet",true);
            var trans=a.GetType("AnaisTransfer",true); var panel=a.GetType("ANAIS_Panel",true); var st=a.GetType("ANAIS_Settings",true);
            output=manager.GetField("anaisOutputDataSet",F); gate=manager.GetField("AnaisOutputDataLock",F);
            time=data.GetField("_timeNow",F); transfer=data.GetField("_anaisTransfer",F); target=data.GetField("_targetPlanet",F); orbits=data.GetField("_listPlayerOrbits",F);
            settings=panel.GetField("_settings",F); show=st.GetField("_showTransfer",F);
            valid=trans.GetMethod("isValid",F); dv=trans.GetMethod("GetDeltaV_start",F);
            origin=trans.GetField("originOrbit",F);
            if(output==null || gate==null || time==null || transfer==null || target==null || orbits==null || settings==null || show==null || valid==null || dv==null || origin==null) throw new Exception("Incompatible ANAIS version");
        }
        internal bool Enabled() { var s=settings.GetValue(null); return s!=null && (bool)show.GetValue(s); }
        internal bool Read(Planet expected, Planet current, out double epoch, out Double2 delta, out Orbit encounter)
        {
            epoch=double.NegativeInfinity; delta=default(Double2); encounter=null;
            lock(gate.GetValue(null))
            {
                var data=output.GetValue(null);
                if(data==null || !ReferenceEquals(target.GetValue(data),expected)) return false;
                epoch=(double)time.GetValue(data);
                var list=orbits.GetValue(data) as List<Orbit>;
                if(list!=null) foreach(var o in list) if(o!=null && o.Planet==expected) { encounter=o; break; }
                var p=transfer.GetValue(data);
                if(p==null || !(bool)valid.Invoke(p,null)) return false;
                var source=origin.GetValue(p) as Orbit;
                if(source==null || source.Planet!=current) return false;
                delta=(Double2)dv.Invoke(p,null);
                return !double.IsNaN(delta.x) && !double.IsNaN(delta.y) && !double.IsInfinity(delta.x) && !double.IsInfinity(delta.y);
            }
        }
    }
    public sealed class Controller : MonoBehaviour
    {
        private enum Phase { Idle, Transfer, CorrectionHold, Coast, Capture, Done }
        private Phase phase;
        private Rocket rocket; private MapPlanet selected; private Planet destination;
        private PlanReader reader; private bool wasRcs, world, captureWarpReset, capturePrepStarted, capturePrepReady, captureBurnStarted, captureThrustAligned;
        private float nextProbe; private Double2 correction; private bool correctionValid;
        private double bestCorrectionError; private float noProgressSeconds, lowThrottleSeconds;
        private string status="Select a planet in the map";
        private float nextDiagnostic;
        private Label info; private SFS.UI.ModGUI.Button button;
        internal bool IsActive => phase!=Phase.Idle && phase!=Phase.Done;
        private void Awake() { SceneHelper.OnWorldSceneLoaded+=Enter; SceneHelper.OnWorldSceneUnloaded+=Exit; }
        private void OnDestroy() { Exit(); SceneHelper.OnWorldSceneLoaded-=Enter; SceneHelper.OnWorldSceneUnloaded-=Exit; }
        private void Enter() { world=true; phase=Phase.Idle; }
        private void Exit() { Stop("Flight closed"); world=false; info=null; button=null; }
        internal void CreateUI(Container page)
        {
            Builder.CreateLabel(page,390,32,0,0,"PLANET NAVIGATION (ANAIS)");
            info=Builder.CreateLabel(page,390,140,0,0,status);
            button=Builder.CreateButton(page,360,38,0,0,Toggle,"Start navigation");
            Builder.CreateLabel(page,390,80,0,0,"Select a planet in the map.\nEnable ANAIS Show Transfer.\nUse manual time warp; keep ANAIS active.");
        }
        private void Toggle()
        {
            if(IsActive) { Stop("Stopped by user"); return; }
            if(UnifiedPilot.Menu.OtherActive(4)) { status="Stop the other autopilot first"; return; }
            var r=PlayerController.main?.player?.Value as Rocket; var t=Map.navigation?.target as MapPlanet;
            if(r==null || r.location==null || r.rb2d==null || t==null || t.planet==null) { status="Select a target planet and control a rocket"; return; }
            if(r.location.planet.Value==t.planet) { status="Already in the target sphere of influence"; return; }
            var p=r.location.planet.Value; if(p==null) { status="Current celestial body unavailable"; return; }
            bool ok; var orbit=Orbit.TryCreateOrbit(r.location.Value,true,false,out ok);
            double safe=p.Radius+Math.Max(p.maxTerrainHeight+1000,p.HasAtmospherePhysics?p.AtmosphereHeightPhysics+100:0);
            if(!ok || orbit==null || orbit.ecc>=1 || orbit.periapsis<safe) { status="Start from a safe bound space orbit"; return; }
            if(ActiveForce(r).magnitude<.001f) { status="Enable fueled engines before starting"; return; }
            try { reader=new PlanReader(); if(!reader.Enabled()) { status="Enable ANAIS Show Transfer"; return; } }
            catch(Exception e) { status="ANAIS unavailable: "+e.Message; return; }
            rocket=r; selected=t; destination=t.planet; wasRcs=r.arrowkeys.rcs.Value; r.arrowkeys.rcs.Value=true;
            correctionValid=false; nextProbe=0; bestCorrectionError=double.PositiveInfinity; noProgressSeconds=0; lowThrottleSeconds=0; captureWarpReset=false; capturePrepStarted=false; capturePrepReady=false; captureBurnStarted=false; captureThrustAligned=false;
            SetThrottle(0); r.arrowkeys.turnAxis.Value=0; phase=Phase.Transfer; status="Waiting for ANAIS solution; open map";
        }
        private void Stop(string why)
        {
            if(rocket!=null) { SetThrottle(0); if(rocket.arrowkeys!=null) { rocket.arrowkeys.turnAxis.Value=0; rocket.arrowkeys.rcs.Value=wasRcs; } }
            rocket=null; selected=null; destination=null; phase=Phase.Idle; status=why;
        }
        private void Update()
        {
            if(world && info!=null) info.Text=status;
            if(world && button!=null) button.Text=IsActive ? "Stop navigation" : "Start navigation";
            if(!IsActive || rocket==null || WorldTime.main==null) return;
            if(Time.unscaledTime>=nextDiagnostic) { nextDiagnostic=Time.unscaledTime+5f; Debug.Log("[HIMA PlanetNavigation] " + phase + ": " + status); }
            try { MonitorWarp(); } catch(Exception e) { Debug.LogException(e); Stop("Navigation error; stopped"); }
        }
        private static double SafeAltitude(Planet p) { return Math.Max(p.maxTerrainHeight+1000,p.HasAtmospherePhysics?p.AtmosphereHeightPhysics+1000:0); }
        private static double RecommendedAltitude(Planet p)
        {
            double preferred = p.HasAtmospherePhysics && p.AtmosphereHeightPhysics>0 ? p.AtmosphereHeightPhysics*1.33 : Math.Max(15000,p.maxTerrainHeight+5000);
            return Math.Ceiling(Math.Max(preferred,SafeAltitude(p)+1000)/5000)*5000;
        }
        private Orbit PredictedEncounter()
        {
            if(rocket==null || rocket.physics==null) return null;
            var trajectory=rocket.physics.GetTrajectory();
            if(trajectory==null || trajectory.paths==null) return null;
            foreach(var path in trajectory.paths) { var o=path as Orbit; if(o!=null && o.Planet==destination) return o; }
            return null;
        }
        private bool ProbePeriapsis(Double2 change, out double altitude)
        {
            altitude=double.NaN; var loc=rocket.location.Value;
            var simulated=Trajectory.CreateTrajectory(new Location(loc.time,loc.planet,loc.position,loc.velocity+change));
            if(simulated==null || simulated.paths==null) return false;
            foreach(var path in simulated.paths) { var o=path as Orbit; if(o!=null && o.Planet==destination && !double.IsNaN(o.periapsis) && !double.IsInfinity(o.periapsis)) { altitude=o.periapsis-destination.Radius; return true; } }
            return false;
        }
        private bool UpdateCorrection(double altitude,double ideal)
        {
            double gx,gy;
            bool hasX=EstimateDerivative(altitude,true,out gx), hasY=EstimateDerivative(altitude,false,out gy);
            if(!hasX && !hasY) return false;
            double x,y; if(!PlanetFlightMath.Correction(altitude,ideal,gx,gy,out x,out y)) return false;
            correction=new Double2(x,y); return true;
        }
        private bool EstimateDerivative(double baseline,bool xAxis,out double derivative)
        {
            derivative=0;
            foreach(double step in new[]{.5,.1,.02,.005})
            {
                double plus,minus;
                bool p=ProbePeriapsis(xAxis?new Double2(step,0):new Double2(0,step),out plus);
                bool m=ProbePeriapsis(xAxis?new Double2(-step,0):new Double2(0,-step),out minus);
                if(PlanetFlightMath.Derivative(baseline,step,p,plus,m,minus,out derivative)) return true;
            }
            return false;
        }
        private void MonitorWarp()
        {
            var time=WorldTime.main;
            if(phase!=Phase.Coast && phase!=Phase.CorrectionHold && phase!=Phase.Capture) return;
            double until=double.PositiveInfinity, burnLead=0;
            if(rocket.location.planet.Value==destination)
            {
                bool ok; var o=Orbit.TryCreateOrbit(rocket.location.Value,true,false,out ok);
                if(ok && o!=null)
                {
                    until=TimeToPeriapsis(o,time.worldTime);
                    var loc=rocket.location.Value;
                    double h=Math.Abs(loc.position.x*loc.velocity.y-loc.position.y*loc.velocity.x);
                    double desiredApo=o.periapsis;
                    double v=PlanetFlightMath.CapturePeriapsisSpeed(destination.mass,o.periapsis,desiredApo);
                    double accel=ActiveForce(rocket).magnitude*9.8/Math.Max(.1,rocket.rb2d.mass);
                    if(!double.IsNaN(v)) burnLead=Math.Max(0,h/Math.Max(1,o.periapsis)-v)/Math.Max(.1,accel)*.5;
                }
            }
            else
            {
                var encounter=PredictedEncounter();
                if(encounter!=null && encounter.periapsis>destination.Radius+SafeAltitude(destination))
                {
                    until=TimeToPeriapsis(encounter,time.worldTime);
                    double desiredApo=encounter.periapsis;
                    double v=PlanetFlightMath.CapturePeriapsisSpeed(destination.mass,encounter.periapsis,desiredApo);
                    double h=Math.Sqrt(Math.Max(0,destination.mass*encounter.slr));
                    double accel=ActiveForce(rocket).magnitude*9.8/Math.Max(.1,rocket.rb2d.mass);
                    if(!double.IsNaN(v)) burnLead=Math.Max(0,h/Math.Max(1,encounter.periapsis)-v)/Math.Max(.1,accel)*.5;
                }
            }
            double warpStep=2*Math.Max(1,time.timewarpSpeed)*Math.Max(.02,Time.unscaledDeltaTime);
            double prepareMargin=90+warpStep+burnLead;
            if(!capturePrepStarted && until>=0 && until<=prepareMargin)
            {
                if(time.timewarpSpeed>1 || !time.realtimePhysics.Value) time.StopTimewarp(false);
                capturePrepStarted=true;
                return; // Re-evaluate the second window using the new, lower warp speed next frame.
            }
            if(capturePrepStarted && !captureWarpReset && until>=0 && until<=burnLead+5+warpStep)
            {
                if(time.timewarpSpeed>1 || !time.realtimePhysics.Value) time.StopTimewarp(false);
                captureWarpReset=true;
            }
        }
        private static double TimeToPeriapsis(Orbit orbit,double now)
        {
            return orbit.period<=0 || orbit.ecc>=1 ? orbit.periapsisPassageTime-now : orbit.GetNextTrueAnomalyPassTime(now,0)-now;
        }
        private void FixedUpdate()
        {
            if(!world || !IsActive) return;
            try { Fly(); } catch(Exception e) { Debug.LogException(e); Stop("Navigation error; stopped"); }
        }
        private void Fly()
        {
            if(rocket==null || rocket.rb2d==null || rocket.location==null || PlayerController.main?.player?.Value!=rocket || Map.navigation?.target!=selected) { Stop("Rocket or navigation target changed"); return; }
            if(rocket.location.planet.Value==destination) { phase=Phase.Capture; Capture(); return; }
            if(!reader.Enabled()) { Stop("ANAIS Show Transfer disabled"); return; }
            double now=WorldTime.main.worldTime, stamp; Double2 delta; Orbit encounter;
            bool found=reader.Read(destination,rocket.location.planet.Value,out stamp,out delta,out encounter);
            bool fresh=found && now>=stamp && now-stamp<Math.Max(3,WorldTime.main.timewarpSpeed*.5);
            var liveEncounter=PredictedEncounter();
            encounter=liveEncounter;
            if(capturePrepStarted && !capturePrepReady && (phase==Phase.Coast || phase==Phase.CorrectionHold))
            {
                if(encounter==null || encounter.periapsis<=destination.Radius+SafeAltitude(destination)) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; status="Capture preparation: safe prediction unavailable"; return; }
                if(!WorldTime.main.realtimePhysics.Value) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; WorldTime.main.StopTimewarp(false); return; }
                double peTime=WorldTime.main.worldTime+TimeToPeriapsis(encounter,WorldTime.main.worldTime);
                var periLocation=encounter.GetLocation(peTime);
                Double2 retro=periLocation.velocity.normalized * -1;
                if(retro.magnitude<.001) { status="Cannot determine capture direction"; return; }
                SetThrottle(0); float prepError=Steer(retro);
                if(Math.Abs(prepError)<=7 && Math.Abs(rocket.rb2d.angularVelocity)<=5) { rocket.arrowkeys.turnAxis.Value=0; capturePrepReady=true; }
                status=capturePrepReady ? "Capture attitude ready; time warp available" : $"Preparing capture attitude: {prepError:F0} deg";
                return;
            }
            double ideal=RecommendedAltitude(destination), safe=SafeAltitude(destination);
            double predicted=encounter==null ? double.NaN : encounter.periapsis-destination.Radius;
            bool targetEncounter=encounter!=null && PlanetFlightMath.AtTargetPeriapsis(predicted,ideal,safe);
            if(phase==Phase.Coast)
            {
                SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0;
                status=encounter==null ? "Coasting: target prediction missing; check map" : predicted<safe ? "WARNING: unsafe target periapsis; correct manually" : $"Coasting: target Pe {predicted/1000:F1} km; ideal {ideal/1000:F1} km";
                return;
            }
            if(targetEncounter) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; phase=Phase.Coast; status=$"Target Pe {predicted/1000:F1} km: coasting"; return; }
            if(phase==Phase.CorrectionHold)
            {
                SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0;
                status=encounter==null ? "Correction paused: encounter missing\nAdjust manually or stop and restart" : $"Correction paused: Pe {predicted/1000:F1} / {ideal/1000:F1} km\nAdjust manually or stop and restart";
                return;
            }
            if(liveEncounter!=null)
            {
                if(Time.unscaledTime>=nextProbe) { correctionValid=UpdateCorrection(predicted,ideal); nextProbe=Time.unscaledTime+.5f; }
                if(!correctionValid) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; status=$"Waiting for target Pe prediction; Pe {predicted/1000:F1} km\nNo correction; time warp available"; return; }
                if(correction.magnitude<=.2)
                {
                    SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; phase=Phase.CorrectionHold;
                    status=$"Correction below 0.2 m/s; Pe {predicted/1000:F1} / {ideal/1000:F1} km\nAdjust manually or stop and restart";
                    return;
                }
                if(!WorldTime.main.realtimePhysics.Value) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; WorldTime.main.StopTimewarp(false); status="Exiting time warp for periapsis correction"; return; }
                Burn(correction,.05,"Adjusting target periapsis");
                double error=Math.Abs(predicted-ideal);
                if(error<bestCorrectionError-200) { bestCorrectionError=error; noProgressSeconds=0; }
                if(rocket!=null && rocket.throttle.throttleOn.Value)
                {
                    noProgressSeconds+=Time.fixedDeltaTime;
                    lowThrottleSeconds=rocket.throttle.throttlePercent.Value<=.05f ? lowThrottleSeconds+Time.fixedDeltaTime : 0;
                    if(noProgressSeconds>=8 || lowThrottleSeconds>=5)
                    {
                        SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; phase=Phase.CorrectionHold;
                        status=$"Correction paused: Pe {predicted/1000:F1} / {ideal/1000:F1} km\nAdjust manually or stop and restart";
                    }
                }
                return;
            }
            if(!fresh) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; status="Waiting for fresh ANAIS solution; open map"; return; }
            if(delta.magnitude<2.5) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; status="No safe encounter predicted; check ANAIS target altitude"; return; }
            Burn(delta,2.5,"Transfer");
        }
        private void Capture()
        {
            bool valid; var o=Orbit.TryCreateOrbit(rocket.location.Value,true,false,out valid);
            if(!valid || o==null) { Stop("Cannot determine target orbit"); return; }
            double floor=destination.Radius+SafeAltitude(destination);
            if(o.periapsis<floor) { Stop("Unsafe target periapsis: manual correction required"); return; }
            if(PlanetFlightMath.Captured(o.ecc,o.periapsis,o.apoapsis,floor,destination.SOI,destination.Radius))
            { Stop($"Navigation complete: orbit Pe {(o.periapsis-destination.Radius)/1000:F1} km Ap {(o.apoapsis-destination.Radius)/1000:F1} km"); phase=Phase.Done; return; }
            double desiredApo=o.periapsis;
            if(destination.SOI>0 && !double.IsInfinity(destination.SOI) && desiredApo>=destination.SOI*.95) { Stop("Target orbit too close to SOI boundary"); return; }
            double now=WorldTime.main.worldTime, toPe=TimeToPeriapsis(o,now);
            if(double.IsNaN(toPe) || double.IsInfinity(toPe) || (toPe< -2 && !captureBurnStarted)) { Stop("Missed target periapsis: manual correction required"); return; }
            double r=rocket.location.Value.Radius, h=Math.Abs(rocket.location.Value.position.x*rocket.location.Value.velocity.y-rocket.location.Value.position.y*rocket.location.Value.velocity.x);
            double peSpeed=h/Math.Max(1,o.periapsis), desired=PlanetFlightMath.CapturePeriapsisSpeed(destination.mass,o.periapsis,desiredApo);
            double need=Math.Max(0,peSpeed-desired), accel=ActiveForce(rocket).magnitude*9.8/Math.Max(.1,rocket.rb2d.mass);
            double lead=Math.Max(2,need/Math.Max(.1,accel)*.5);
            if(!capturePrepStarted && !captureBurnStarted && toPe>lead+90) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; status=$"Target approach: Pe in {toPe:F0}s; burn lead {lead:F0}s"; return; }
            if(!WorldTime.main.realtimePhysics.Value)
            {
                SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0;
                if(capturePrepReady && toPe>lead+5) { status=$"Capture attitude ready; Pe in {toPe:F0}s; time warp available"; return; }
                WorldTime.main.StopTimewarp(false); captureWarpReset=true; status="Exiting time warp for capture";
                return;
            }
            var vel=rocket.location.Value.velocity; var pos=rocket.location.Value.position; var tangent=new Double2(-pos.y/r,pos.x/r);
            double side=Double2.Dot(vel,tangent); Double2 retro=tangent*(side>=0?-1:1);
            if(!captureBurnStarted && toPe>lead)
            {
                SetThrottle(0);
                if(!capturePrepReady) { float err=Steer(retro); if(Math.Abs(err)<=7 && Math.Abs(rocket.rb2d.angularVelocity)<=5) { capturePrepReady=true; rocket.arrowkeys.turnAxis.Value=0; } }
                else rocket.arrowkeys.turnAxis.Value=0;
                status=capturePrepReady ? $"Capture attitude ready; Pe in {toPe:F0}s; time warp available" : $"Preparing capture: Pe in {toPe:F0}s; burn lead {lead:F0}s";
                return;
            }
            captureBurnStarted=true;
            double currentDesired=Math.Sqrt(Math.Max(0,destination.mass*(2/r-2/(o.periapsis+desiredApo))));
            double remaining=Math.Max(0,Math.Abs(side)-currentDesired);
            if(remaining<.5)
            {
                if(o.ecc<1 && o.period>0) { captureBurnStarted=false; SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; status="Orbit not circular yet; waiting for next periapsis"; }
                else Stop("Capture incomplete: manual correction required");
                return;
            }
            Burn(retro*remaining,.5,"Capture");
        }
        private void Burn(Double2 demand,double threshold,string label)
        {
            if(!WorldTime.main.realtimePhysics.Value) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value=0; WorldTime.main.StopTimewarp(false); status="Exiting time warp for "+label; return; }
            var force=ActiveForce(rocket); if(force.magnitude<.001f || rocket.rb2d.mass<=0) { Stop("No fueled active engine; stage manually"); return; }
            float error=Steer(demand); float accel=force.magnitude*9.8f/rocket.rb2d.mass;
            float throttle=Mathf.Clamp((float)((demand.magnitude-threshold)/Math.Max(accel*2.5,.1)),0,1);
            if(label=="Capture")
            {
                float angular=Math.Abs(rocket.rb2d.angularVelocity);
                if(captureThrustAligned) { if(Math.Abs(error)>14 || angular>12) captureThrustAligned=false; }
                else if(Math.Abs(error)<=7 && angular<=5) captureThrustAligned=true;
                if(!captureThrustAligned) throttle=0;
            }
            else if(Math.Abs(error)>7 || Math.Abs(rocket.rb2d.angularVelocity)>5) throttle=0;
            SetThrottle(throttle); status=$"{label}: remaining {demand.magnitude:F1} m/s; throttle {throttle:P0}";
        }
        private float Steer(Double2 demand)
        {
            Vector2 force=ActiveForce(rocket), desired=new Vector2((float)demand.x,(float)demand.y).normalized;
            float error=Vector2.SignedAngle(force,desired), angular=rocket.rb2d.angularVelocity;
            float cmd=Mathf.Clamp(-error*.035f+angular*.04f,-1,1);
            if(rocket.arrowkeys.rcs.Value && rocket.partHolder.HasModule<RcsModule>() && (Math.Abs(error)>4 || Math.Abs(angular)>2.5) && Math.Abs(cmd)>.02f) cmd=Mathf.Sign(cmd)*Mathf.Max(.96f,Math.Abs(cmd));
            rocket.arrowkeys.turnAxis.Value=cmd; return error;
        }
        private void SetThrottle(float v) { rocket.throttle.throttlePercent.Value=v; rocket.throttle.throttleOn.Value=v>.001f; }
        private static Vector2 ActiveForce(Rocket r)
        {
            if(r==null || r.partHolder==null) return Vector2.zero;
            Vector2 f=Vector2.zero; foreach(var e in r.partHolder.GetModules<EngineModule>())
            { if(e==null || !e.engineOn.Value || e.source==null || !e.source.CanFlow(new MsgNone())) continue; Vector2 local=e.thrustNormal.Value*Math.Max(0f,e.thrust.Value); f+=Base.worldBase.AllowsCheats ? (Vector2)e.transform.TransformVector(local) : e.transform.TransformVectorUnscaled(local); }
            return f;
        }
    }
}
