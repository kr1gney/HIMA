using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModLoader;
using ModLoader.Helpers;
using SFS;
using SFS.Parts.Modules;
using SFS.UI.ModGUI;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UITools;
using UnityEngine;
using UIType = SFS.UI.ModGUI.Type;

namespace RendezvousPilot
{
    // ANAIS exposes its solutions only through internal classes. Copy a snapshot
    // while holding its own lock; never retain its mutable working objects.
    internal sealed class AnaisBridge
    {
        private readonly System.Type manager, dataType, transferType, panelType;
        private readonly FieldInfo outputField, lockField, timeField, transferField, finalField, settingsField;
        private readonly FieldInfo showField, modeField, arrivalField, orbitField;
        private readonly MethodInfo validMethod, deltaMethod;
        private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        public AnaisBridge()
        {
            Assembly a = null;
            foreach (var x in AppDomain.CurrentDomain.GetAssemblies()) if (x.GetName().Name == "ANAIS") { a = x; break; }
            if (a == null) throw new Exception("ANAIS assembly not found");
            manager = a.GetType("AnaisManager", true); dataType = a.GetType("AnaisDataSet", true);
            transferType = a.GetType("AnaisTransfer", true); panelType = a.GetType("ANAIS_Panel", true);
            outputField = manager.GetField("anaisOutputDataSet", AllStatic); lockField = manager.GetField("AnaisOutputDataLock", AllStatic);
            timeField = dataType.GetField("_timeNow", AllInstance); transferField = dataType.GetField("_anaisTransfer", AllInstance);
            finalField = dataType.GetField("_finalApproachMode", AllInstance); settingsField = panelType.GetField("_settings", AllStatic);
            var settingType = a.GetType("ANAIS_Settings", true); showField = settingType.GetField("_showTransfer", AllInstance); modeField = settingType.GetField("_transferType", AllInstance);
            arrivalField = transferType.GetField("arrivalTime", AllInstance); orbitField = transferType.GetField("transferOrbit", AllInstance);
            validMethod = transferType.GetMethod("isValid", AllInstance); deltaMethod = transferType.GetMethod("GetDeltaV_start", AllInstance);
            if (outputField == null || lockField == null || timeField == null || transferField == null || finalField == null || settingsField == null || showField == null || modeField == null || arrivalField == null || orbitField == null || validMethod == null || deltaMethod == null) throw new Exception("Incompatible ANAIS version");
        }
        public bool SettingsReady()
        {
            object settings = settingsField.GetValue(null);
            return settings != null && (bool)showField.GetValue(settings) && modeField.GetValue(settings).ToString() == "RENDEZ_VOUS";
        }
        public bool Read(out bool final, out Double2 dv, out double time, out double arrival, out double periapsis)
        {
            final = false; dv = default(Double2); time = double.NegativeInfinity; arrival = double.NaN; periapsis = double.NaN;
            lock (lockField.GetValue(null))
            {
                object data = outputField.GetValue(null); if (data == null) return false;
                time = (double)timeField.GetValue(data); final = (bool)finalField.GetValue(data);
                if (final) return true;
                object transfer = transferField.GetValue(data);
                if (transfer == null || !(bool)validMethod.Invoke(transfer, null)) return false;
                dv = (Double2)deltaMethod.Invoke(transfer, null);
                arrival = (double)arrivalField.GetValue(transfer);
                var orbit = orbitField.GetValue(transfer) as Orbit;
                if (orbit == null) return false;
                periapsis = orbit.periapsis;
                return true;
            }
        }
    }

    public sealed class Controller : MonoBehaviour
    {
        public static Controller Instance { get; private set; }
        private enum Phase { Idle, Transfer, Coast, Approach, Done }
        private Phase phase;
        private Rocket rocket;
        private MapRocket target;
        private AnaisBridge bridge;
        private bool wasRcs, world;
        private string status = "Select a target rocket in map view";
        private double lastPlanAt, coastStartedAt;
        private float lastLog;

        public bool Active => phase != Phase.Idle && phase != Phase.Done;
        public bool Complete => phase == Phase.Done;
        public string Status => status;
        private void Awake() { Instance = this; SceneHelper.OnWorldSceneLoaded += Enter; SceneHelper.OnWorldSceneUnloaded += Exit; }
        private void OnDestroy() { Stop("Mod disabled"); Exit(); SceneHelper.OnWorldSceneLoaded -= Enter; SceneHelper.OnWorldSceneUnloaded -= Exit; if (Instance == this) Instance = null; }
        private void Enter()
        {
            world = true; phase = Phase.Idle;
        }
        private void Exit() { Stop("Flight closed"); world = false; }
        public void StartPilot()
        {
            Rocket r = PlayerController.main?.player?.Value as Rocket;
            MapRocket t = Map.navigation?.target as MapRocket;
            if (r == null || t == null || t.rocket == null || t.rocket == r) { status = "Select another rocket as the map target"; return; }
            if (r.location.planet.Value != t.Location.planet) { status = "Same-body orbits only"; return; }
            var planet = r.location.planet.Value;
            if (planet == null || (planet.HasAtmospherePhysics && r.location.Height < planet.AtmosphereHeightPhysics)) { status = "Start outside the atmosphere"; return; }
            bool ok; Orbit o = Orbit.TryCreateOrbit(r.location.Value, true, false, out ok);
            if (!ok || o == null || o.ecc >= 1 || o.periapsis < planet.Radius + Math.Max(planet.maxTerrainHeight + 1000, planet.HasAtmospherePhysics ? planet.AtmosphereHeightPhysics : 0)) { status = "A safe bound orbit is required"; return; }
            if (ActiveForce(r).magnitude < 0.001f) { status = "Enable fueled engines before starting"; return; }
            try { bridge = new AnaisBridge(); if (!bridge.SettingsReady()) { status = "ANAIS: enable Show transfer and Rendez-vous mode"; return; } }
            catch (Exception e) { status = "ANAIS unavailable: " + e.Message; return; }
            rocket = r; target = t; wasRcs = r.arrowkeys.rcs.Value; r.arrowkeys.rcs.Value = true;
            SetThrottle(0); r.arrowkeys.turnAxis.Value = 0;
            phase = Phase.Transfer; lastPlanAt = WorldTime.main.worldTime; status = "Waiting for ANAIS transfer solution";
        }
        public void Stop(string reason)
        {
            if (rocket != null) { rocket.throttle.throttlePercent.Value = 0; rocket.throttle.throttleOn.Value = false; rocket.arrowkeys.turnAxis.Value = 0; rocket.arrowkeys.rcs.Value = wasRcs; }
            rocket = null; target = null; phase = Phase.Idle; status = reason;
        }
        private void Update()
        {
            if (!world) return;
            if (phase != Phase.Idle && phase != Phase.Done && Time.unscaledTime - lastLog > 5f) { lastLog = Time.unscaledTime; Debug.Log("[RendezvousPilot] " + phase + ": " + status); }
        }
        private void FixedUpdate()
        {
            if (!world || phase == Phase.Idle || phase == Phase.Done) return;
            try { Fly(); } catch (Exception e) { Debug.LogException(e); Stop("Stopped: " + e.Message); }
        }
        private void Fly()
        {
            if (rocket == null || target == null || target.rocket == null || PlayerController.main?.player?.Value != rocket || Map.navigation.target != target) { Stop("Rocket or target changed"); return; }
            if (rocket.location.planet.Value != target.Location.planet) { Stop("Target changed sphere of influence"); return; }
            if (!bridge.SettingsReady()) { Stop("ANAIS transfer display / rendezvous mode disabled"); return; }
            double now = WorldTime.main.worldTime;
            Double2 offset = target.Location.position - rocket.location.position.Value;
            Double2 relative = target.Location.velocity - rocket.location.velocity.Value;
            double distance = offset.magnitude, relSpeed = relative.magnitude;
            double rcsBrake = AutoDock.Controller.EstimateRcsBraking(rocket, relative);
            double handoffSpeed = Math.Min(5.0, Math.Sqrt(Math.Max(0, distance - 150) * Math.Max(0, rcsBrake) * 0.8));
            if (distance < 550 && rcsBrake <= 0 && relSpeed < 2.0) { Stop("RCS cannot brake toward target; check fuel and thrusters"); return; }
            if (distance < 550 && rcsBrake > 0 && relSpeed < handoffSpeed)
            { Stop("Rendezvous complete: handing over to RCS"); phase = Phase.Done; return; }
            bool final; Double2 dv; double planTime, arrival, periapsis;
            bool hasPlan = bridge.Read(out final, out dv, out planTime, out arrival, out periapsis);
            bool fresh = hasPlan && now >= planTime && now - planTime < Math.Max(3, WorldTime.main.timewarpSpeed * 0.5);
            if (fresh) lastPlanAt = now;
            if (phase == Phase.Coast)
            {
                double closing = distance > 0 ? -Double2.Dot(offset, relative) / distance : 0;
                double accel = ActiveForce(rocket).magnitude * 9.8 / Math.Max(rocket.rb2d.mass, 0.01);
                double brakeDistance = accel > 0.001 ? relSpeed * relSpeed / (2 * accel) * 1.5 : double.PositiveInfinity;
                if (distance < 10000 || (closing > 0 && distance < brakeDistance + closing * 30 + 10000))
                {
                    phase = Phase.Approach;
                    if (!WorldTime.main.realtimePhysics.Value) WorldTime.main.StopTimewarp(false);
                }
            }
            else if (phase == Phase.Transfer && distance < 10000) phase = Phase.Approach;
            if (phase == Phase.Transfer)
            {
                if (!fresh) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value = 0; status = "Waiting for fresh ANAIS solution; enable map if needed"; return; }
                if (final || dv.magnitude < 2.5) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value = 0; phase = Phase.Coast; coastStartedAt = now; status = "Transfer established; coasting"; return; }
                var planet = rocket.location.planet.Value;
                double floor = planet.Radius + Math.Max(planet.maxTerrainHeight + 1000, planet.HasAtmospherePhysics ? planet.AtmosphereHeightPhysics : 0);
                if (double.IsNaN(periapsis) || periapsis < floor) { Stop("ANAIS transfer intersects atmosphere or terrain"); return; }
                Burn(dv, "Transfer", 2.0);
                return;
            }
            if (phase == Phase.Coast)
            {
                SetThrottle(0); rocket.arrowkeys.turnAxis.Value = 0;
                if (now - coastStartedAt > 30 && fresh && !final && dv.magnitude > 5 && distance > 10000) { phase = Phase.Transfer; status = "Correcting transfer"; return; }
                status = "Coasting to target; time warp is manual";
                return;
            }
            // Relative-velocity feedback with a conservative closing-speed cap.
            // The last 450 m and docking port alignment are deliberately manual.
            double desiredClosing = Math.Min(55, Math.Max(0, (distance - 300) / 30.0));
            Double2 demand = relative + offset.normalized * desiredClosing;
            if (demand.magnitude < 0.8) { SetThrottle(0); rocket.arrowkeys.turnAxis.Value = 0; status = string.Format("Approaching: {0:F0} m / {1:F1} m/s", distance, relSpeed); return; }
            Burn(demand, "Approach", 0.6);
        }
        private void Burn(Double2 demand, string name, double threshold)
        {
            if (!WorldTime.main.realtimePhysics.Value)
            {
                SetThrottle(0); rocket.arrowkeys.turnAxis.Value = 0;
                WorldTime.main.StopTimewarp(false);
                status = name + ": exiting high time warp for maneuver";
                return;
            }
            Vector2 force = ActiveForce(rocket);
            if (force.magnitude < 0.001f || rocket.rb2d.mass <= 0) { Stop("No active fueled engine; stage manually"); return; }
            Vector2 desired = new Vector2((float)demand.x, (float)demand.y).normalized;
            float error = Vector2.SignedAngle(force, desired), angular = rocket.rb2d.angularVelocity;
            float cmd = Mathf.Clamp(-error * 0.035f + angular * 0.04f, -1, 1);
            if (rocket.arrowkeys.rcs.Value && rocket.partHolder.HasModule<RcsModule>() && (Math.Abs(error) > 4 || Math.Abs(angular) > 2.5) && Math.Abs(cmd) > 0.02f) cmd = Mathf.Sign(cmd) * Mathf.Max(0.96f, Math.Abs(cmd));
            rocket.arrowkeys.turnAxis.Value = cmd;
            float accel = force.magnitude * 9.8f / rocket.rb2d.mass;
            float throttle = Mathf.Clamp((float)((demand.magnitude - threshold) / Math.Max(accel * 2.5, 0.1)), 0, 1);
            if (Math.Abs(error) > 7 || Math.Abs(angular) > 5) throttle = 0;
            SetThrottle(throttle);
            status = string.Format("{0}: delta-v {1:F1} m/s, turn {2:F0} deg, throttle {3:P0}", name, demand.magnitude, error, throttle);
        }
        private void SetThrottle(float v) { rocket.throttle.throttlePercent.Value = v; rocket.throttle.throttleOn.Value = v > 0.001f; }
        private static Vector2 ActiveForce(Rocket r)
        {
            Vector2 f = Vector2.zero;
            foreach (var e in r.partHolder.GetModules<EngineModule>())
            {
                if (e == null || !e.engineOn.Value || e.source == null || !e.source.CanFlow(new MsgNone())) continue;
                Vector2 local = e.thrustNormal.Value * Math.Max(0f, e.thrust.Value);
                f += Base.worldBase.AllowsCheats ? (Vector2)e.transform.TransformVector(local) : e.transform.TransformVectorUnscaled(local);
            }
            return f;
        }
    }
}
