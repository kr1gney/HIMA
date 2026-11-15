using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using SFS.UI.ModGUI;
using UITools;
using ModGUIType = SFS.UI.ModGUI.Type;
using HarmonyLib;
using ModLoader;
using ModLoader.Helpers;
using SFS;
using SFS.Parts.Modules;
using SFS.World;
using SFS.WorldBase;
using SFS.World.Drag;
using UnityEngine;

namespace AscentPilot
{


    public enum AscentPhase
    {
        Standby,            // 待命中 (解析星球与配置目标)
        Liftoff,            // 垂直起步突破塔架
        MaxQAtmospheric,    // 动压限制爬升 (动态油门减阻)
        GravityTurn,        // 幂律重力转向
        RecoveryClimb,      // Establish a new ascending trajectory after passing apoapsis
        CoastingToAp,       // 熄火滑行 (智能时间加速)
        Circularization,    // 远地点顺行圆化点火
        LowerPeriapsis,
        CoastToPeriapsis,
        OrbitAchieved       // 入轨闭环
    }

    public sealed class AscentController : MonoBehaviour
    {
        // 飞行控制与状态
        public AscentPhase Phase { get; private set; } = AscentPhase.Standby;
        private Rocket rocket;
        private Planet currentPlanet;
        private bool inWorld;
        private bool previousRcs;
        private bool lowerOrbitMode;
        private bool periapsisWarpStopped;
        private float nextDiagnosticTime;
        private int orbitDirection = -1;
        private readonly Collider2D[] groundContacts = new Collider2D[8];
        private string statusMessage = "Ready: choose an orbit altitude";

        // 用户输入与目标高度 (单位：km)
        private string targetAltitudeInput = "40";
        private double targetAltitudeKm = 40.0;
        private double recommendedAltitudeKm = 40.0;

        // 特性开关
        private bool limitAtmosphericTWR = true;

        // 星球物理属性缓存
        private string planetName = "--";
        private double planetRadiusKm = 0.0;
        private double planetGravity = 9.8;
        private double atmoHeightKm = 0.0;
        private bool hasAtmosphere = false;

        // 飞行遥测数据
        private double currentApoapsisKm = 0.0;
        private double currentPeriapsisKm = 0.0;
        private double currentSpeed = 0.0;
        private double currentAglKm = 0.0;
        private double eccentricity = 1.0;

        private readonly int windowId = Builder.GetRandomID();
        private GameObject uiHolder;
        private ClosableWindow window;
        private Label telemetryLabel;
        private Label statusLabel;
        private InputWithLabel altitudeInput;
        private SFS.UI.ModGUI.Button startButton;

        private void Awake()
        {
            SceneHelper.OnWorldSceneLoaded += OnEnterWorld;
            SceneHelper.OnWorldSceneUnloaded += OnExitWorld;
        }

        private void OnDestroy()
        {
            Abort("Mod disabled");
            DestroyUI();
            SceneHelper.OnWorldSceneLoaded -= OnEnterWorld;
            SceneHelper.OnWorldSceneUnloaded -= OnExitWorld;
        }

        private void OnEnterWorld()
        {
            inWorld = true;
            Phase = AscentPhase.Standby;
            UpdatePlanetTelemetry();
        }

        private void OnExitWorld()
        {
            inWorld = false;
            Abort("Flight scene closed");
            DestroyUI();
        }

        // 动态检测火箭当前所在星球，计算推荐的黄金停泊轨道高度
        private void UpdatePlanetTelemetry()
        {
            var current = PlayerController.main?.player?.Value as Rocket;
            if (current == null || current.location == null) return;

            var planet = current.location.planet.Value;
            if (planet == null) return;

            bool planetChanged = currentPlanet != planet;
            currentPlanet = planet;
            planetName = planet.codeName;
            planetRadiusKm = planet.Radius / 1000.0;
            planetGravity = planet.GetGravity(planet.Radius);
            hasAtmosphere = planet.HasAtmospherePhysics;

            if (hasAtmosphere)
            {
                atmoHeightKm = planet.AtmosphereHeightPhysics / 1000.0;
                // 有大气天体: 大气顶层高出约 30%~35%（例如地球 30km -> 推荐 40km，火星 20km -> 推荐 28km）
                recommendedAltitudeKm = Math.Ceiling((atmoHeightKm * 1.33) / 5.0) * 5.0;
            }
            else
            {
                atmoHeightKm = 0.0;
                // 无大气真空天体 (月球、水星等): 跨越最高环形山，推荐 15km
                recommendedAltitudeKm = Math.Ceiling(Math.Max(15.0, (planet.maxTerrainHeight + 5000.0) / 1000.0) / 5.0) * 5.0;
            }

            if (Phase == AscentPhase.Standby && (planetChanged || string.IsNullOrEmpty(targetAltitudeInput)))
            {
                targetAltitudeKm = recommendedAltitudeKm;
                targetAltitudeInput = recommendedAltitudeKm.ToString("F0", CultureInfo.InvariantCulture);
                if (altitudeInput != null) altitudeInput.textInput.Text = targetAltitudeInput;
            }
        }

        internal void CreateUI(Container page)
        {
            DestroyUI();
            telemetryLabel = Builder.CreateLabel(page, 360, 84, 0, 0, "Waiting for rocket");
            altitudeInput = Builder.CreateInputWithLabel(page, 355, 34, 0, 0, "Orbit altitude (km)", targetAltitudeInput, text => targetAltitudeInput = text);
            var row = Builder.CreateContainer(page);
            row.Size = new Vector2(355, 32);
            row.CreateLayoutGroup(ModGUIType.Horizontal, TextAnchor.MiddleCenter, 5f);
            Builder.CreateButton(row, 105, 30, 0, 0, () => AdjustAltitude(-5), "-5 km");
            Builder.CreateButton(row, 105, 30, 0, 0, () => { targetAltitudeInput = recommendedAltitudeKm.ToString("F0", CultureInfo.InvariantCulture); altitudeInput.textInput.Text = targetAltitudeInput; }, "Recommended");
            Builder.CreateButton(row, 105, 30, 0, 0, () => AdjustAltitude(5), "+5 km");
            Builder.CreateToggleWithLabel(page, 355, 28, () => limitAtmosphericTWR, () => limitAtmosphericTWR = !limitAtmosphericTWR, 0, 0, "Limit atmospheric TWR");
            statusLabel = Builder.CreateLabel(page, 355, 70, 0, 0, "Ready");
            startButton = Builder.CreateButton(page, 340, 36, 0, 0, () => { if (Phase == AscentPhase.Standby || Phase == AscentPhase.OrbitAchieved) StartAscent(); else Abort("Stopped by user"); }, "Start orbit pilot");
            Builder.CreateLabel(page, 355, 25, 0, 0, "Stage manually when fuel runs out");
        }

        private void DestroyUI()
        {
            if (uiHolder != null) UnityEngine.Object.Destroy(uiHolder);
            uiHolder = null; window = null; telemetryLabel = null; statusLabel = null; altitudeInput = null; startButton = null;
        }

        private void Update()
        {
            CheckPeriapsisTimewarp();
            if (!inWorld || telemetryLabel == null) return;
            if (Phase == AscentPhase.Standby) UpdatePlanetTelemetry();
            RefreshTelemetry();
            telemetryLabel.Text = string.Format("Body: {0}   Atmosphere: {1:F1} km\nAltitude: {2:F1} km   Speed: {3:F0} m/s\nAp: {4:F1} km   Pe: {5:F1} km   e: {6:F3}", planetName, atmoHeightKm, currentAglKm, currentSpeed, currentApoapsisKm, currentPeriapsisKm, eccentricity);
            statusLabel.Text = statusMessage;
            startButton.Text = Phase == AscentPhase.Standby || Phase == AscentPhase.OrbitAchieved ? "Start ascent" : "Abort ascent";
            if (Phase != AscentPhase.Standby && Phase != AscentPhase.OrbitAchieved && Time.unscaledTime >= nextDiagnosticTime)
            {
                nextDiagnosticTime = Time.unscaledTime + 2f;
                Debug.Log(string.Format("[AscentPilot 1.5.0] {0} | {1} | altitude={2:F1}km ap={3:F1}km pe={4:F1}km speed={5:F1}m/s", Phase, statusMessage, currentAglKm, currentApoapsisKm, currentPeriapsisKm, currentSpeed));
            }
        }

        private static Orbit GetOrbit(Location location)
        {
            if (location == null || location.planet == null) return null;
            bool success;
            return Orbit.TryCreateOrbit(location, true, false, out success);
        }

        private void RefreshTelemetry()
        {
            var curRocket = PlayerController.main?.player?.Value as Rocket;
            if (curRocket != null && curRocket.location != null && currentPlanet != null)
            {
                double r = curRocket.location.Value.Radius;
                currentSpeed = curRocket.location.velocity.Value.magnitude;
                double terrain = currentPlanet.GetTerrainHeightAtAngle(curRocket.location.position.Value.AngleRadians, true);
                currentAglKm = Math.Max(0, (r - currentPlanet.Radius - terrain) / 1000.0);

                var orbit = GetOrbit(curRocket.location.Value);
                if (orbit != null)
                {
                    currentApoapsisKm = (orbit.apoapsis - currentPlanet.Radius) / 1000.0;
                    currentPeriapsisKm = (orbit.periapsis - currentPlanet.Radius) / 1000.0;
                    eccentricity = orbit.ecc;
                }
            }
        }

        private void AdjustAltitude(double delta)
        {
            if (double.TryParse(targetAltitudeInput, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                val = Math.Max(hasAtmosphere ? atmoHeightKm + 2 : 5, val + delta);
                targetAltitudeKm = val;
                targetAltitudeInput = val.ToString("F0", CultureInfo.InvariantCulture);
                if (altitudeInput != null) altitudeInput.textInput.Text = targetAltitudeInput;
            }
        }

        internal bool IsActive => Phase != AscentPhase.Standby && Phase != AscentPhase.OrbitAchieved;
        internal void SetMenuVisible(bool show) { if (window != null) window.Active = show; }
        private void StartAscent()
        {
            if (UnifiedPilot.Menu.OtherActive(1)) { statusMessage = "Stop the other autopilot first"; return; }
            var recovery = GameObject.Find("RecoveryPilot_Controller");
            if (recovery != null)
            {
                var controller = recovery.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().FullName == "RecoveryPilot.RecoveryController");
                var field = controller?.GetType().GetField("active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null && (bool)field.GetValue(controller)) { statusMessage = "Stop Recovery Pilot before starting ascent"; return; }
            }
            UpdatePlanetTelemetry();
            if (!double.TryParse(targetAltitudeInput, NumberStyles.Float, CultureInfo.InvariantCulture, out targetAltitudeKm) || double.IsNaN(targetAltitudeKm) || double.IsInfinity(targetAltitudeKm) || targetAltitudeKm <= 0)
            {
                statusMessage = "Enter a valid positive orbit altitude";
                return;
            }

            if (hasAtmosphere && targetAltitudeKm <= atmoHeightKm)
            {
                statusMessage = $"Target ({targetAltitudeKm:F0} km) is inside the atmosphere ({atmoHeightKm:F0} km)";
                return;
            }

            rocket = PlayerController.main?.player?.Value as Rocket;
            if (rocket == null || rocket.rb2d == null || rocket.location == null || rocket.location.planet.Value == null)
            {
                statusMessage = "No controlled rocket";
                return;
            }
            if (targetAltitudeKm * 1000.0 <= currentPlanet.maxTerrainHeight)
            { statusMessage = "Target orbit is below the highest terrain"; return; }
            var currentLocation = rocket.location.Value;
            double radius = currentLocation.Radius;
            double terrain = currentPlanet.GetTerrainHeightAtAngle(currentLocation.position.AngleRadians, true);
            double agl = radius - currentPlanet.Radius - terrain;
            double vertical = Double2.Dot(currentLocation.velocity, currentLocation.position.normalized);
            double thrust = GetTotalActiveThrust(rocket);
            double gravity = currentPlanet.GetGravity(radius);
            bool grounded = IsGrounded(rocket);
            var orbit = GetOrbit(currentLocation);
            double targetAltitude = targetAltitudeKm * 1000.0;
            var resume = AscentGuidance.ChooseResume(grounded, vertical, radius - currentPlanet.Radius, targetAltitude,
                orbit != null && orbit.ecc < 1.0, orbit == null ? 0 : orbit.apoapsis - currentPlanet.Radius,
                orbit == null ? 0 : orbit.periapsis - currentPlanet.Radius, orbit == null ? 1 : orbit.ecc);
            if (resume == AscentGuidance.ResumeState.AlreadyOrbiting)
            { Phase = AscentPhase.OrbitAchieved; rocket = null; statusMessage = "Already in the target orbit"; return; }
            if (thrust <= 0.01)
            { rocket = null; statusMessage = "Enable fueled engines before starting"; return; }
            if (grounded && thrust <= rocket.rb2d.mass * gravity)
            { rocket = null; statusMessage = "Insufficient thrust to lift off"; return; }

            bool lowerOrbit = AscentGuidance.ShouldLowerOrbit(grounded, orbit != null && orbit.ecc < 1.0,
                orbit == null ? 0 : orbit.periapsis, currentPlanet.Radius + targetAltitude, targetAltitude);
            lowerOrbitMode = lowerOrbit;
            periapsisWarpStopped = false;
            if (lowerOrbit) Phase = AscentPhase.LowerPeriapsis;
            else switch (resume)
            {
                case AscentGuidance.ResumeState.Liftoff: Phase = AscentPhase.Liftoff; break;
                case AscentGuidance.ResumeState.Coast: Phase = AscentPhase.CoastingToAp; break;
                case AscentGuidance.ResumeState.Circularize: Phase = AscentPhase.Circularization; break;
                case AscentGuidance.ResumeState.RecoverDescent: Phase = AscentPhase.RecoveryClimb; break;
                default:
                    double clearAlt = hasAtmosphere ? Math.Max(800.0, atmoHeightKm * 30.0) : 300.0;
                    Phase = agl < clearAlt ? AscentPhase.Liftoff :
                        hasAtmosphere && agl < atmoHeightKm * 1000.0 * 0.35 ? AscentPhase.MaxQAtmospheric : AscentPhase.GravityTurn;
                    break;
            }
            orbitDirection = -1;
            nextDiagnosticTime = 0f;
            previousRcs = rocket.arrowkeys.rcs.Value;
            rocket.arrowkeys.rcs.Value = true;
            statusMessage = resume == AscentGuidance.ResumeState.RecoverDescent ? "Descending: establishing a new climb" : "Flight state detected: " + Phase;
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[AscentPilot 1.5.0] Resume phase={0} altitude={1:F0}m vertical={2:F1}m/s TWR={3:F2} ap={4:F0}m pe={5:F0}m", Phase, agl, vertical, thrust / Math.Max(0.01, rocket.rb2d.mass * gravity), orbit == null ? double.NaN : orbit.apoapsis - currentPlanet.Radius, orbit == null ? double.NaN : orbit.periapsis - currentPlanet.Radius));
        }

        private bool IsGrounded(Rocket craft)
        {
            int count = craft.rb2d.GetContacts(groundContacts);
            int layer = LayerMask.NameToLayer("Celestial Body");
            for (int i = 0; i < count; i++)
                if (groundContacts[i] != null && groundContacts[i].gameObject.layer == layer) return true;
            return false;
        }

        private void CheckPeriapsisTimewarp()
        {
            var time = WorldTime.main;
            if (Phase != AscentPhase.CoastToPeriapsis || periapsisWarpStopped || time == null || rocket == null || rocket.rb2d == null || currentPlanet == null || rocket.location == null) return;
            var loc = rocket.location.Value;
            var orbit = GetOrbit(loc);
            if (orbit == null || orbit.ecc >= 1 || orbit.period <= 0) return;
            double toPe = Math.Max(0, orbit.GetNextTrueAnomalyPassTime(time.worldTime, 0) - time.worldTime);
            double radius = currentPlanet.Radius + targetAltitudeKm * 1000;
            double momentum = Math.Abs(loc.position.x * loc.velocity.y - loc.position.y * loc.velocity.x);
            double dv = Math.Max(0, momentum / Math.Max(1, orbit.periapsis) - Math.Sqrt(currentPlanet.mass / radius));
            double acceleration = GetTotalActiveThrust(rocket) / Math.Max(.1, rocket.rb2d.mass);
            double burnLead = Math.Max(2, dv / Math.Max(.1, acceleration) * .5);
            double stopLead = AscentGuidance.PeriapsisWarpStopLead(burnLead, time.timewarpSpeed, Time.unscaledDeltaTime);
            if (toPe <= stopLead)
            {
                if (time.timewarpSpeed > 1.0 || !time.realtimePhysics.Value) time.StopTimewarp(false);
                periapsisWarpStopped = true;
            }
        }

        public void Abort(string reason)
        {
            bool wasActive = Phase != AscentPhase.Standby && Phase != AscentPhase.OrbitAchieved;
            if (wasActive) Debug.Log("[AscentPilot 1.5.0] " + reason);
            Phase = AscentPhase.Standby;
            if (wasActive && rocket != null)
            {
                rocket.throttle.throttlePercent.Value = 0f;
                rocket.throttle.throttleOn.Value = false;
                rocket.arrowkeys.turnAxis.Value = 0f;
                rocket.arrowkeys.rcs.Value = previousRcs;
            }
            rocket = null;
            statusMessage = reason;
        }

        private void FixedUpdate()
        {
            if (Phase == AscentPhase.Standby || Phase == AscentPhase.OrbitAchieved)
                return;

            if (rocket == null || currentPlanet == null || PlayerController.main?.player?.Value != rocket)
            {
                Abort("Stopped: active rocket changed");
                return;
            }

            if (WorldTime.main != null && !WorldTime.main.realtimePhysics.Value && Phase != AscentPhase.CoastingToAp && Phase != AscentPhase.CoastToPeriapsis)
            {
                rocket.arrowkeys.turnAxis.Value = 0f;
                statusMessage = "Reduce timewarp to physics to resume control";
                return;
            }

            var loc = rocket.location;
            double radius = loc.Value.Radius;
            double terrain = currentPlanet.GetTerrainHeightAtAngle(loc.position.Value.AngleRadians, true);
            double agl = radius - currentPlanet.Radius - terrain;
            var orbit = GetOrbit(loc.Value);
            double apoapsis = orbit == null ? 0 : orbit.apoapsis - currentPlanet.Radius;
            double periapsis = orbit == null ? -currentPlanet.Radius : orbit.periapsis - currentPlanet.Radius;
            double targetRadius = currentPlanet.Radius + (targetAltitudeKm * 1000.0);

            // 监测推力：若推进剂耗尽，平稳提示手动分级，飞控保持姿态不退出
            float currentActiveThrust = GetTotalActiveThrust(rocket);
            bool outOfFuel = currentActiveThrust <= 0.01f;
            float currentMass = Mathf.Max(rocket.rb2d.mass, 0.1f);
            float currentTWR = (currentActiveThrust / currentMass) / Mathf.Max(0.1f, (float)currentPlanet.GetGravity(radius));
            double radialSpeedNow = Double2.Dot(loc.velocity.Value, loc.position.Value.normalized);
            if (agl > 100 && radialSpeedNow < -1.0 &&
                (Phase == AscentPhase.Liftoff || Phase == AscentPhase.MaxQAtmospheric ||
                 Phase == AscentPhase.GravityTurn || Phase == AscentPhase.CoastingToAp ||
                 (Phase == AscentPhase.Circularization && radius < targetRadius - 1000.0)))
            {
                if (Phase != AscentPhase.RecoveryClimb)
                    Debug.Log(string.Format("[AscentPilot 1.5.0] Passed apoapsis; recovering descent at {0:F0}m, vertical={1:F1}m/s, TWR={2:F2}", agl, radialSpeedNow, currentTWR));
                Phase = AscentPhase.RecoveryClimb;
            }

            double projectedApoapsis = apoapsis;
            if ((Phase == AscentPhase.Liftoff || Phase == AscentPhase.MaxQAtmospheric || Phase == AscentPhase.GravityTurn || Phase == AscentPhase.RecoveryClimb) && radialSpeedNow > 1.0 &&
                orbit != null && orbit.ecc < 1.0 && apoapsis >= targetAltitudeKm * 1000.0 &&
                hasAtmosphere && loc.Value.Height < currentPlanet.AtmosphereHeightPhysics)
            {
                double predicted = PredictCoastApoapsis(loc.Value, currentMass);
                if (!double.IsNaN(predicted)) projectedApoapsis = predicted;
            }
            if ((Phase == AscentPhase.Liftoff || Phase == AscentPhase.MaxQAtmospheric || Phase == AscentPhase.GravityTurn || Phase == AscentPhase.RecoveryClimb) && radialSpeedNow > 1.0 &&
                orbit != null && orbit.ecc < 1.0 && projectedApoapsis >= targetAltitudeKm * 1000.0)
            {
                Debug.Log(string.Format("[AscentPilot 1.5.0] Coast start: altitude={0:F0}m currentAp={1:F0}m predictedAp={2:F0}m", agl, apoapsis, projectedApoapsis));
                SetThrottle(0f);
                rocket.arrowkeys.turnAxis.Value = 0f;
                Phase = AscentPhase.CoastingToAp;
            }

            switch (Phase)
            {
                case AscentPhase.LowerPeriapsis:
                    if (orbit == null || orbit.ecc >= 1) { SetThrottle(0f); Abort("Lowering requires a bound orbit"); return; }
                    if (periapsis <= targetAltitudeKm * 1000.0 + 100.0)
                    {
                        SetThrottle(0f); rocket.arrowkeys.turnAxis.Value = 0f;
                        Phase = AscentPhase.CoastToPeriapsis;
                        statusMessage = "Target periapsis reached: coasting";
                        break;
                    }
                    Vector2 lowerUp = new Vector2((float)(loc.position.Value.x / radius), (float)(loc.position.Value.y / radius));
                    Vector2 lowerVelocity = new Vector2((float)loc.velocity.Value.x, (float)loc.velocity.Value.y);
                    Vector2 lowerTangent = new Vector2(-lowerUp.y, lowerUp.x);
                    double lowerSide = Vector2.Dot(lowerVelocity, lowerTangent);
                    Vector2 retrograde = lowerTangent * (lowerSide >= 0 ? -1f : 1f);
                    float lowerError = SteerTowards(retrograde);
                    double desiredSpeed = AscentGuidance.TransferSpeedAtApoapsis(currentPlanet.mass, radius, targetRadius);
                    double requiredDv = Math.Max(0, Math.Abs(lowerSide) - desiredSpeed);
                    float lowerAccel = currentActiveThrust / currentMass;
                    float lowerThrottle = lowerAccel > 0.01f ? Mathf.Clamp((float)(requiredDv * 0.4 / lowerAccel), 0.03f, 1f) : 0f;
                    if (Math.Abs(lowerError) > 8f || Math.Abs(rocket.rb2d.angularVelocity) > 8f) lowerThrottle = 0;
                    SetThrottle(outOfFuel ? 0f : lowerThrottle);
                    statusMessage = outOfFuel ? "Lower orbit: stage and enable engines" : $"Lowering periapsis: {periapsis / 1000:F1}/{targetAltitudeKm:F1} km";
                    break;

                case AscentPhase.CoastToPeriapsis:
                    SetThrottle(0f); rocket.arrowkeys.turnAxis.Value = 0f;
                    if (orbit == null || orbit.ecc >= 1 || orbit.period <= 0) { Abort("Cannot determine a bound orbit"); return; }
                    double timeToPe = Math.Max(0, orbit.GetNextTrueAnomalyPassTime(WorldTime.main.worldTime, 0) - WorldTime.main.worldTime);
                    double peSpeed = Math.Abs(loc.position.Value.x * loc.velocity.Value.y - loc.position.Value.y * loc.velocity.Value.x) / Math.Max(1.0, orbit.periapsis);
                    double peDv = Math.Max(0, peSpeed - Math.Sqrt(currentPlanet.mass / targetRadius));
                    double peLead = Math.Max(2.0, peDv / Math.Max(.1, currentActiveThrust / currentMass) * .5);
                    bool pePhysics = WorldTime.main == null || WorldTime.main.realtimePhysics.Value;
                    if (pePhysics && timeToPe <= peLead + 15)
                    {
                        Vector2 upPe = new Vector2((float)(loc.position.Value.x / radius), (float)(loc.position.Value.y / radius));
                        Vector2 tangentPe = new Vector2(-upPe.y, upPe.x);
                        Vector2 velPe = new Vector2((float)loc.velocity.Value.x, (float)loc.velocity.Value.y);
                        SteerTowards(tangentPe * (Vector2.Dot(velPe, tangentPe) >= 0 ? -1f : 1f));
                    }
                    statusMessage = outOfFuel ? "Coasting: stage and enable engines" : $"Coasting to periapsis: {timeToPe:F0}s; burn lead {peLead:F0}s";
                    if (pePhysics && !outOfFuel && timeToPe <= peLead) Phase = AscentPhase.Circularization;
                    break;
                case AscentPhase.Liftoff:
                    SetThrottle(outOfFuel ? 0f : 1.0f);
                    PointTowardsRadial(loc.Value, 0f);

                    if (outOfFuel)
                        statusMessage = "Out of fuel: stage manually";
                    else
                        statusMessage = "Liftoff: climbing vertically";

                    double liftoffClearAlt = hasAtmosphere ? Math.Max(800.0, atmoHeightKm * 30.0) : 300.0;
                    if (agl >= liftoffClearAlt)
                    {
                        Phase = hasAtmosphere ? AscentPhase.MaxQAtmospheric : AscentPhase.GravityTurn;
                    }
                    break;

                case AscentPhase.MaxQAtmospheric:
                    // 动态油门防动压折断 (Max-Q Throttle Limiting)
                    // 在稠密大气层中，若 TWR > 2.2，动态限制油门以减小气动阻力与热阻损失
                    float atmoThrottle = 1.0f;
                    if (limitAtmosphericTWR && currentTWR > 2.2f && agl < (atmoHeightKm * 1000.0 * 0.6))
                    {
                        atmoThrottle = Mathf.Clamp(2.2f / currentTWR, 0.1f, 1.0f);
                    }
                    SetThrottle(outOfFuel ? 0f : atmoThrottle);

                    // 初段轻微仰角倾斜 (80 ~ 85 度)
                    PointTowardsRadial(loc.Value, AtmosphericTilt(loc.Value));

                    if (outOfFuel)
                        statusMessage = "Out of fuel: stage manually";
                    else
                        statusMessage = $"Atmospheric ascent: throttle {atmoThrottle:P0} (TWR: {currentTWR:F1})";

                    double maxQEndAlt = atmoHeightKm * 1000.0 * 0.35;
                    if (agl >= maxQEndAlt)
                    {
                        Phase = AscentPhase.GravityTurn;
                    }
                    break;

                case AscentPhase.GravityTurn:
                    SetThrottle(outOfFuel ? 0f : 1.0f);

                    double turnStart = hasAtmosphere ? (atmoHeightKm * 1000.0 * 0.35) : 300.0;
                    double turnEnd = hasAtmosphere ? (atmoHeightKm * 1000.0 * 1.1) : (targetAltitudeKm * 1000.0 * 0.7);

                    // 经典幂律重力转向方程 (90度垂直 -> 0度水平顺行)
                    float progress = Mathf.Clamp01((float)((agl - turnStart) / Math.Max(turnEnd - turnStart, 100.0)));
                    float pitchAngle = hasAtmosphere && loc.Value.Height < currentPlanet.AtmosphereHeightPhysics
                        ? 90f - AtmosphericTilt(loc.Value) : 90f * Mathf.Pow(1f - progress, 0.45f);

                    PointTowardsRadial(loc.Value, 90f - pitchAngle);

                    if (outOfFuel)
                        statusMessage = "Out of fuel: stage manually";
                    else
                        statusMessage = $"Gravity turn: pitch {pitchAngle:F0} deg | Ap {apoapsis / 1000.0:F1}/{targetAltitudeKm:F0} km";

                    // 远地点达标，切断发动机进入滑行段
                    break;

                case AscentPhase.RecoveryClimb:
                    if (outOfFuel)
                    {
                        SetThrottle(0f);
                        rocket.arrowkeys.turnAxis.Value = 0f;
                        statusMessage = "No thrust: stage and enable engines";
                        break;
                    }
                    Vector2 recoveryUp = new Vector2((float)(loc.position.Value.x / radius), (float)(loc.position.Value.y / radius));
                    Vector2 recoveryTangent = new Vector2(-recoveryUp.y, recoveryUp.x);
                    Vector2 recoveryVelocity = new Vector2((float)loc.velocity.Value.x, (float)loc.velocity.Value.y);
                    double recoverySide = Vector2.Dot(recoveryVelocity, recoveryTangent);
                    if (Math.Abs(recoverySide) > 2) orbitDirection = Math.Sign(recoverySide);
                    double maxRecoveryAccel = currentActiveThrust / currentMass;
                    double effectiveGravity = currentPlanet.GetGravity(radius) - recoverySide * recoverySide / radius;
                    var recoveryCommand = AscentGuidance.RecoveryAcceleration(maxRecoveryAccel, effectiveGravity, targetRadius, radius, radialSpeedNow);
                    Vector2 recoveryDemand = recoveryUp * (float)recoveryCommand.Up + recoveryTangent * (float)(orbitDirection * recoveryCommand.Side);
                    if (recoveryDemand.sqrMagnitude < 0.0001f) { SetThrottle(0f); statusMessage = "Cannot calculate recovery direction"; break; }
                    float recoveryError = SteerTowards(recoveryDemand.normalized);
                    bool aligned = Math.Abs(recoveryError) <= 12f && Math.Abs(rocket.rb2d.angularVelocity) <= 12f;
                    SetThrottle(aligned ? 1f : 0f);
                    statusMessage = maxRecoveryAccel <= Math.Max(0, effectiveGravity)
                        ? "Low effective thrust: arresting descent may be impossible"
                        : $"Recovering climb: vertical {radialSpeedNow:F1} m/s  TWR {currentTWR:F2}";
                    break;

                case AscentPhase.CoastingToAp:
                    SetThrottle(0f);
                    rocket.arrowkeys.turnAxis.Value = 0f;

                    if (orbit == null || orbit.ecc >= 1 || orbit.period <= 0) { Abort("Cannot determine a bound orbit"); return; }
                    double timeToAp = Math.Max(0, orbit.GetNextTrueAnomalyPassTime(WorldTime.main.worldTime, Math.PI) - WorldTime.main.worldTime);
                    double vCirc = Math.Sqrt(currentPlanet.mass / targetRadius);
                    double angularMomentum = Math.Abs(loc.position.Value.x * loc.velocity.Value.y - loc.position.Value.y * loc.velocity.Value.x);
                    double predictedApSpeed = angularMomentum / Math.Max(1.0, orbit.apoapsis);
                    double deltaVReq = Math.Max(0.0, vCirc - predictedApSpeed);

                    float a_max = Mathf.Max(0.1f, currentActiveThrust / currentMass);
                    float burnDuration = (float)(deltaVReq / a_max);
                    double burnLeadTime = Math.Max(2.0, burnDuration * 0.5);

                    bool outsideAir = !hasAtmosphere || loc.Value.Height >= currentPlanet.AtmosphereHeightPhysics;
                    bool prepareBurn = outsideAir && timeToAp <= burnLeadTime + 15.0;
                    bool physicsActive = WorldTime.main == null || WorldTime.main.realtimePhysics.Value;
                    if (prepareBurn && physicsActive) PointTowardsHorizontal(loc.Value);

                    if (outOfFuel)
                        statusMessage = "Coasting: stage and enable the next engines";
                    else if (prepareBurn && !physicsActive)
                        statusMessage = "Reduce timewarp to prepare circularization";
                    else
                        statusMessage = $"Coasting: apoapsis in {timeToAp:F0}s; burn lead {burnLeadTime:F0}s";

                    if (outsideAir && physicsActive && !outOfFuel && (timeToAp <= burnLeadTime || timeToAp <= 1.5))
                    {
                        Phase = AscentPhase.Circularization;
                    }
                    break;

                case AscentPhase.Circularization:
                    Vector2 radialUp = new Vector2((float)(loc.position.Value.x / radius), (float)(loc.position.Value.y / radius));
                    Vector2 tangent = new Vector2(-radialUp.y, radialUp.x);
                    Vector2 velocity = new Vector2((float)loc.velocity.Value.x, (float)loc.velocity.Value.y);
                    double signedTangentSpeed = Vector2.Dot(velocity, tangent);
                    if (Math.Abs(signedTangentSpeed) > 2) orbitDirection = Math.Sign(signedTangentSpeed);
                    double horizontalSpeed = signedTangentSpeed * orbitDirection;
                    double radialSpeed = Vector2.Dot(velocity, radialUp);
                    double vTargetCirc = Math.Sqrt(currentPlanet.mass / targetRadius);
                    double remDeltaV = vTargetCirc - horizontalSpeed;
                    double ecc = orbit == null ? 1.0 : orbit.ecc;

                    double tolerance = Math.Max(1000.0, targetAltitudeKm * 1000.0 * 0.025);
                    double safeOrbitAltitude = Math.Max(currentPlanet.maxTerrainHeight, hasAtmosphere ? currentPlanet.AtmosphereHeightPhysics : 0) + 100;
                    bool orbitComplete = lowerOrbitMode
                        ? AscentGuidance.LowerOrbitComplete(apoapsis, periapsis, targetAltitudeKm * 1000.0, safeOrbitAltitude)
                        : periapsis >= targetAltitudeKm * 1000.0 - tolerance && apoapsis <= targetAltitudeKm * 1000.0 + tolerance && ecc < 0.005;
                    if (orbit != null && orbit.ecc < 1 && orbitComplete)
                    {
                        SetThrottle(0f);
                        rocket.arrowkeys.turnAxis.Value = 0f;
                        rocket.arrowkeys.rcs.Value = previousRcs;
                        Phase = AscentPhase.OrbitAchieved;
                        statusMessage = $"Orbit achieved: target {targetAltitudeKm:F0} km (e={ecc:F4})";
                        rocket = null;
                        break;
                    }
                    // Accelerate tangentially while damping radial velocity so a
                    // near-vertical ascent does not push the apoapsis outward.
                    double maxAccel = currentActiveThrust / currentMass;
                    double desiredRadialSpeed = Math.Max(-60.0, Math.Min(60.0, (targetRadius - radius) * 0.08));
                    double radialAccel = currentPlanet.GetGravity(radius) - horizontalSpeed * horizontalSpeed / radius + (desiredRadialSpeed - radialSpeed) * 0.5;
                    radialAccel = Math.Max(-maxAccel * 0.7, Math.Min(maxAccel * 0.7, radialAccel));
                    radialAccel = AscentGuidance.LimitBelowTarget(radialAccel, radius, targetRadius, radialSpeed);
                    double tangentialAccel = remDeltaV * 0.7 + radialSpeed * horizontalSpeed / radius;
                    tangentialAccel = Math.Max(-maxAccel * 0.85, Math.Min(maxAccel * 0.85, tangentialAccel));
                    Vector2 demand = radialUp * (float)radialAccel + tangent * (float)(orbitDirection * tangentialAccel);
                    if (demand.sqrMagnitude < 0.0001f) { SetThrottle(0f); statusMessage = "Orbit nearly circular"; break; }
                    float steeringError = SteerTowards(demand.normalized);
                    float circThrottle = maxAccel > 0.01 ? Mathf.Clamp01(demand.magnitude / (float)maxAccel) : 0f;
                    if (Math.Abs(steeringError) > 12f || Math.Abs(rocket.rb2d.angularVelocity) > 12f) circThrottle = 0f;
                    SetThrottle(outOfFuel ? 0f : circThrottle);

                    if (outOfFuel)
                        statusMessage = "Circularization: out of fuel; stage manually";
                    else
                        statusMessage = $"Circularizing: horizontal delta-v {remDeltaV:F1} m/s\nVertical {radialSpeed:F1} m/s  throttle {circThrottle:P0}";
                    break;
            }
        }

        private float AtmosphericTilt(Location loc)
        {
            var up = loc.position.normalized;
            double verticalSpeed = Double2.Dot(loc.velocity, up);
            double horizontalSpeed = loc.velocity.x * -up.y + loc.velocity.y * up.x;
            double seaDensity = currentPlanet.GetAtmosphericDensity(0);
            double density = currentPlanet.GetAtmosphericDensity(Math.Max(0, loc.Height));
            double ratio = seaDensity > 1e-9 ? density / seaDensity : 0;
            float tilt = (float)AscentGuidance.AtmosphericTilt(verticalSpeed, horizontalSpeed, ratio);
            return horizontalSpeed > 2.0 ? -tilt : tilt;
        }

        private double PredictCoastApoapsis(Location loc, float mass)
        {
            if (mass <= 0 || loc.velocity.magnitude < 1 || SandboxSettings.main.settings.noAtmosphericDrag)
                return AscentGuidance.CoastApoapsis(loc.position.x, loc.position.y, loc.velocity.x, loc.velocity.y,
                    currentPlanet.Radius, currentPlanet.mass, currentPlanet.AtmosphereHeightPhysics, 0, h => currentPlanet.GetAtmosphericDensity(h));
            float angle = (float)loc.velocity.AngleRadians - Mathf.PI / 2f;
            var surfaces = AeroModule.GetExposedSurfaces(Aero_Rocket.GetDragSurfaces(rocket.partHolder, Matrix2x2.Angle(-angle)));
            double area = 0;
            foreach (var surface in surfaces)
            {
                Vector2 line = surface.line.end - surface.line.start;
                if (line.x >= 0.01f) area += line.x * line.x / (line.x + Math.Abs(line.y));
            }
            return AscentGuidance.CoastApoapsis(loc.position.x, loc.position.y, loc.velocity.x, loc.velocity.y,
                currentPlanet.Radius, currentPlanet.mass, currentPlanet.AtmosphereHeightPhysics,
                1.5 * area / mass, h => currentPlanet.GetAtmosphericDensity(h));
        }

        // 姿态控制：相对于当地天顶径向顺行偏角
        private void PointTowardsRadial(Location loc, float angleOffsetFromUp)
        {
            Vector2 up = ((Vector2)loc.position).normalized;
            float upAngleDeg = Mathf.Atan2(up.y, up.x) * Mathf.Rad2Deg - 90f;
            float targetAngleDeg = upAngleDeg - angleOffsetFromUp;

            ApplyPDSteering(targetAngleDeg);
        }

        // 姿态控制：锁定轨道绝对顺行 (Prograde)
        private void PointTowardsHorizontal(Location loc)
        {
            Vector2 up = ((Vector2)loc.position).normalized;
            Vector2 tangent = new Vector2(-up.y, up.x);
            float side = Vector2.Dot((Vector2)loc.velocity, tangent);
            if (Math.Abs(side) > 2f) orbitDirection = Math.Sign(side);
            SteerTowards(tangent * orbitDirection);
        }

        private void ApplyPDSteering(float targetAngleDeg)
        {
            float radians = (targetAngleDeg + 90f) * Mathf.Deg2Rad;
            Vector2 desired = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
            SteerTowards(desired);
        }

        private float SteerTowards(Vector2 desired)
        {
            Vector2 force = GetActiveForce(rocket);
            if (force.sqrMagnitude < 0.001f) force = rocket.rb2d.transform.up;
            float angleError = Vector2.SignedAngle(force, desired);
            float angularVel = rocket.rb2d.angularVelocity;

            // 姿态 PD 阻尼控制器
            float turnCmd = Mathf.Clamp(-angleError * 0.035f + angularVel * 0.04f, -1f, 1f);
            if (rocket.arrowkeys.rcs.Value && rocket.partHolder.HasModule<RcsModule>() &&
                (Math.Abs(angleError) > 4f || Math.Abs(angularVel) > 2.5f) && Math.Abs(turnCmd) > 0.02f)
                turnCmd = Mathf.Sign(turnCmd) * Mathf.Max(0.96f, Math.Abs(turnCmd));
            rocket.arrowkeys.turnAxis.Value = turnCmd;
            return angleError;
        }

        private void SetThrottle(float amount)
        {
            rocket.throttle.throttlePercent.Value = amount;
            rocket.throttle.throttleOn.Value = amount > 0.001f;
        }

        private float GetTotalActiveThrust(Rocket r)
        {
            return GetActiveForce(r).magnitude * 9.8f;
        }

        private static Vector2 GetActiveForce(Rocket r)
        {
            if (r == null || r.partHolder == null) return Vector2.zero;
            Vector2 total = Vector2.zero;
            foreach (var engine in r.partHolder.GetModules<EngineModule>())
            {
                if (engine == null || !engine.engineOn.Value || engine.source == null || !engine.source.CanFlow(new MsgNone())) continue;
                Vector2 local = engine.thrustNormal.Value * Math.Max(0f, engine.thrust.Value);
                total += Base.worldBase.AllowsCheats ? (Vector2)engine.transform.TransformVector(local) : engine.transform.TransformVectorUnscaled(local);
            }
            return total;
        }
    }
}
