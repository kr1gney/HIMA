using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModLoader;
using ModLoader.Helpers;
using SFS;
using SFS.Builds;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.UI.ModGUI;
using SFS.World;
using SFS.WorldBase;
using SFS.World.Drag;
using UITools;
using UnityEngine;
using ModGUIType = SFS.UI.ModGUI.Type;

namespace RecoveryPilot
{


    // Register on the template before SFS clones it and loads saved variables.
    [HarmonyPatch]
    internal static class LandingLegOptions
    {
        internal const string Key = "RecoveryPilot_AutoDeployLeg";

        internal static bool IsLeg(Part part)
        {
            if (part == null || part.name == null) return false;
            string name = part.name.Replace('_', ' ').Replace('-', ' ');
            return name.IndexOf("landing leg", StringComparison.OrdinalIgnoreCase) >= 0 && part.HasModule<ToggleModule>();
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(PartsLoader).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "CreatePart" && method.GetParameters().Length > 0 &&
                    (method.GetParameters()[0].ParameterType == typeof(PartSave) || method.GetParameters()[0].ParameterType == typeof(VariantRef)))
                .Cast<MethodBase>();
        }

        private static void Prefix(object __0)
        {
            Part template = null;
            if (__0 is PartSave save && Base.partsLoader != null)
                Base.partsLoader.parts.TryGetValue(save.name, out template);
            else if (__0 is VariantRef variant) template = variant.part;
            if (!IsLeg(template) || template.variablesModule == null) return;
            var variables = template.variablesModule.boolVariables;
            if (!variables.Has(Key)) variables.SetValue(Key, false, (true, true));
        }
    }

    [HarmonyPatch(typeof(Part), nameof(Part.DrawPartStats))]
    internal static class LandingLegSettings
    {
        private static void Postfix(Part __instance, Part[] allParts, StatsMenu drawer, PartDrawSettings settings)
        {
            if (!settings.build || !LandingLegOptions.IsLeg(__instance)) return;
            var variable = __instance.variablesModule.boolVariables.GetVariable(LandingLegOptions.Key);
            if (variable == null) return;
            Part[] legs = allParts.Where(LandingLegOptions.IsLeg)
                .Where(p => p.variablesModule.boolVariables.Has(LandingLegOptions.Key)).ToArray();
            drawer.DrawToggle(10, () => "Auto deploy on recovery", () =>
            {
                bool next = !variable.Value;
                Undo.main.RecordStatChangeStep(legs, () =>
                {
                    foreach (Part leg in legs) leg.variablesModule.boolVariables.SetValue(LandingLegOptions.Key, next);
                });
            }, () => variable.Value, update => variable.onValueChange += update, update => variable.onValueChange -= update);
        }
    }

    public sealed class RecoveryController : MonoBehaviour
    {
        private double LandingSpeed = 3.5;
        private const double HorizontalDoneSpeed = 0.5;
        private readonly Collider2D[] groundContacts = new Collider2D[8];
        private Rocket rocket;
        private Planet target;
        private bool active;
        private bool inWorld;
        private bool previousRcs;
        private float unstableSeconds;
        private int groundContactFrames;
        private bool terminalBurnStarted;
        private bool terminalEngagedEver, terminalReascending;
        private int ignitionCount;
        private string lastStage="", touchdownBasis="unavailable", lastPrediction="not evaluated";
        private double touchdownTolerance=2.5, lastPredictedBurnHeight=double.NaN, lastPredictedFinishHeight=double.NaN;
        private float nextTouchdownScan;
        private bool terminalTrimActive;
        private bool vacuumIgnitionConfirmed;
        private float vacuumStableSeconds;
        private bool coastAfterBraking;
        private bool vacuumSecondCoast, vacuumSecondCoastUsed;
        private float nextVacuumTerrainSample;
        private double vacuumTerrainEnvelope;
        private bool enteredAtmosphere;
        private bool deorbitComplete;
        private bool smallBodyMode;
        private bool smallBodyRadialPending;
        private bool entryPreparationStarted;
        private bool entryPreparationComplete;
        private float entryStableSeconds;
        private Vector2 predictedEntryDirection;
        private string status = "Standby";
        private readonly int flightWindowID = Builder.GetRandomID();
        private GameObject flightHolder;
        private ClosableWindow flightWindow;
        private Label flightTargetLabel;
        private Label flightStatusLabel;
        private Label flightEngineLabel;
        private Label flightAeroLabel;
        private string aeroStatus = "Aero: awaiting atmosphere";
        private string engineStatus = "Engines: --";
        private float nextDiagnosticTime;
        private string lastInactiveDiagnostic = "";
        private float bottomOffset = 4f;
        private float comBottomOffset = 4f;
        private bool aeroPreparingRetro, aeroHighDragValid, aeroHighDragEngaged, aeroHighDragPermitted;
        private float nextAeroCandidate, aeroTargetAngle;
        private float aeroStallSeconds, aeroSpinSeconds;
        private double aeroRetroFactor, aeroBestFactor;
        private double lastFlightSpeed;
        private double lastFlightVertical;
        private float lastForceSide;
        private float lastDesiredSide;
        private AeroCalibration aeroCalibration = new AeroCalibration();
        private double previousMass;
        private double fullThrottleMassFlow;
        private double sampledDragFactor;
        private double sampledDensity;
        private float flightSampleClock;
        private float nextCandidateCheck;
        private string predictionWindow = "Collecting data";
        private SFS.UI.ModGUI.Button flightToggleButton;

        private void Awake()
        {
            SceneHelper.OnWorldSceneLoaded += EnterWorld;
            SceneHelper.OnWorldSceneUnloaded += ExitWorld;
        }

        private void OnDestroy()
        {
            Stop("Mod disabled");
            DestroyFlightUI();
            SceneHelper.OnWorldSceneLoaded -= EnterWorld;
            SceneHelper.OnWorldSceneUnloaded -= ExitWorld;
        }

        private void EnterWorld() { inWorld = true; target = null; status = "Ready"; }
        private void ExitWorld() { Stop("Standby"); inWorld = false; target = null; DestroyFlightUI(); }

        internal void CreateFlightUI(Container page)
        {
            DestroyFlightUI();
            flightTargetLabel = Builder.CreateLabel(page, 330, 30, 0, 0, "Planet: automatic");
            flightToggleButton = Builder.CreateButton(page, 310, 38, 0, 0,
                () => { if (active) Stop("Stopped by user"); else StartRecovery(); }, "Start recovery");
            flightStatusLabel = Builder.CreateLabel(page, 330, 82, 0, 0, status);
            flightEngineLabel = Builder.CreateLabel(page, 390, 105, 0, 0, engineStatus);
            flightAeroLabel = Builder.CreateLabel(page, 390, 55, 0, 0, aeroStatus);
            Builder.CreateLabel(page, 330, 26, 0, 0, "Uses enabled engines with fuel");
        }

        private void DestroyFlightUI()
        {
            if (flightHolder != null) UnityEngine.Object.Destroy(flightHolder);
            flightHolder = null;
            flightWindow = null;
            flightTargetLabel = null;
            flightStatusLabel = null;
            flightEngineLabel = null;
            flightAeroLabel = null;
            flightToggleButton = null;
        }

        private void Update()
        {
            if (inWorld && !active && flightTargetLabel != null)
            {
                var current = PlayerController.main == null ? null : PlayerController.main.player.Value as Rocket;
                var planet = current == null || current.location == null ? null : current.location.planet.Value;
                flightTargetLabel.Text = "Planet: " + (planet == null ? "none" : planet.codeName);
            }
            if (inWorld && flightStatusLabel != null && flightStatusLabel.Text != status)
                flightStatusLabel.Text = status;
            if (inWorld && flightEngineLabel != null && flightEngineLabel.Text != engineStatus)
                flightEngineLabel.Text = engineStatus;
            if (inWorld && flightAeroLabel != null && flightAeroLabel.Text != aeroStatus)
                flightAeroLabel.Text = aeroStatus;
            if (inWorld && flightToggleButton != null)
            {
                string text = active ? "Stop recovery" : "Start recovery";
                if (flightToggleButton.Text != text) flightToggleButton.Text = text;
            }
            if (inWorld && active && Time.unscaledTime >= nextDiagnosticTime)
            {
                nextDiagnosticTime = Time.unscaledTime + 2f;
                float commanded = rocket != null ? rocket.throttle.throttlePercent.Value : 0f;
                float actual = rocket != null ? rocket.throttle.output_Throttle.Value : 0f;
                float engineOutput = rocket == null ? 0f : rocket.partHolder.GetModules<EngineModule>()
                    .Where(e => e != null && e.engineOn.Value).Select(e => e.throttle_Out.Value).DefaultIfEmpty(0f).Max();
                Debug.Log("[HIMA Recovery 1.4.6] " + status.Replace('\n', ' ') + " | " + engineStatus.Replace('\n',' ') + " | " + aeroStatus.Replace('\n',' ') + " | prediction="+lastPrediction+" vacuumBurnHeight="+lastPredictedBurnHeight.ToString("F1")+" predictedFinishHeight="+lastPredictedFinishHeight.ToString("F1")+
                    string.Format(" | commanded={0:F2} actual={1:F2} engineOutput={2:F2}", commanded, actual, engineOutput));
            }
            else if (inWorld && !active && status != lastInactiveDiagnostic)
            {
                lastInactiveDiagnostic = status;
                Debug.Log("[RecoveryPilot 0.14.9] " + status);
            }
            if(active)
            {
                string stage=terminalReascending ? "terminal ascent/coast" : vacuumSecondCoast ? "second coast" : terminalBurnStarted ? "terminal burn" : terminalEngagedEver ? "terminal coast" : smallBodyRadialPending ? "small-body brake" : !deorbitComplete ? "deorbit" : enteredAtmosphere ? "atmosphere coast" : "space coast";
                if(stage!=lastStage) { Debug.Log("[HIMA Recovery 1.4.6] Stage: "+lastStage+" -> "+stage+" | "+status.Replace('\n',' ')); lastStage=stage; }
            }
        }

        internal bool IsActive => active;
        internal void SetMenuVisible(bool show) { if (flightWindow != null) flightWindow.Active = show; }
        private void StartRecovery()
        {
            if (UnifiedPilot.Menu.OtherActive(2)) { status = "Stop the other autopilot first"; return; }
            var current = PlayerController.main == null ? null : PlayerController.main.player.Value as Rocket;
            if (current == null || current.location == null || current.location.planet.Value == null)
            { status = "No controlled rocket"; return; }
            target = current.location.planet.Value;
            if (flightTargetLabel != null) flightTargetLabel.Text = "Planet: " + target.codeName;
            if (!HasLandableSurface(target)) { status = "Cannot land: no solid surface on this body"; return; }
            if (IsTouchingGround(current)) { status = "Already landed"; return; }
            double radius = current.location.Value.Radius;
            bottomOffset = 4f;
            double height = radius - target.Radius - target.GetTerrainHeightAtAngle(current.location.position.Value.AngleRadians, true) - GetBottomOffset(current);
            if (height < 1) { status = "Already landed"; return; }

            var chosen = AvailableEngines(current);
            if (chosen.Length == 0) { status = "No enabled fueled engines"; return; }
            bool hasTorque = current.partHolder.GetModules<TorqueModule>()
                .Any(module => module != null && (module.enabled.Local || module.enabled.Value));
            if (!hasTorque && !current.partHolder.HasModule<RcsModule>())
            { status = "No steering torque or RCS"; return; }
            double thrust = EngineForce(chosen).magnitude * 9.8;
            double gravity = target.mass / (radius * radius);
            if (current.rb2d == null || current.rb2d.mass <= 0 || thrust / current.rb2d.mass <= gravity * 1.05)
            { status = "Insufficient thrust"; return; }

            rocket = current;
            previousRcs = rocket.arrowkeys.rcs.Value;
            rocket.arrowkeys.rcs.Value = true;
            SetThrottle(0f);
            active = true;
            unstableSeconds = 0f;
            groundContactFrames = 0;
            terminalBurnStarted = false;
            terminalEngagedEver=false; terminalReascending=false; ignitionCount=0; lastStage="start"; lastPrediction="not evaluated"; lastPredictedBurnHeight=double.NaN; lastPredictedFinishHeight=double.NaN; nextTouchdownScan=0;
            LandingSpeed=3.5; touchdownTolerance=double.PositiveInfinity; touchdownBasis="unavailable";
            terminalTrimActive = false;
            vacuumIgnitionConfirmed = false;
            vacuumStableSeconds = 0f;
            coastAfterBraking = false;
            vacuumSecondCoast = false; vacuumSecondCoastUsed = false;
            nextVacuumTerrainSample=0; vacuumTerrainEnvelope=double.NaN;
            enteredAtmosphere = false;
            deorbitComplete = false;
            double surfaceGravity = target.Radius > 0 ? target.mass / (target.Radius * target.Radius) : double.NaN;
            smallBodyMode = SmallBodyGuidance.NeedsRadialDescent(target.Radius, surfaceGravity, target.maxTerrainHeight);
            smallBodyRadialPending = smallBodyMode;
            entryPreparationStarted = false;
            entryPreparationComplete = false;
            entryStableSeconds = 0f;
            predictedEntryDirection = Vector2.zero;
            status = "Calculating braking";
            nextDiagnosticTime = 0f;
            aeroCalibration = new AeroCalibration();
            previousMass = rocket.rb2d.mass;
            fullThrottleMassFlow = 0;
            flightSampleClock = 0;
            nextCandidateCheck = 0;
            nextAeroCandidate=0; aeroPreparingRetro=false; aeroHighDragValid=false; aeroHighDragEngaged=false; aeroHighDragPermitted=false; aeroRetroFactor=0; aeroBestFactor=0; aeroStallSeconds=0; aeroSpinSeconds=0;
            aeroStatus="Aero: awaiting atmosphere";
            predictionWindow = "Collecting data";
            UpdateTouchdownTarget(current);
            Debug.Log(string.Format("[RecoveryPilot 0.14.9] Started target={0} atmosphereFlag={1} effectiveAtmosphere={2} atmosphereHeight={3:F1} bottom={4:F1} engines={5} mass={6:F2} thrust={7:F2}", target.codeName, target.HasAtmospherePhysics, HasUsefulAtmosphere(target), target.AtmosphereHeightPhysics, bottomOffset, chosen.Length, rocket.rb2d.mass, thrust));
            Debug.Log(string.Format("[RecoveryPilot 0.14.9] Body radius={0:F0} m surfaceGravity={1:F3} maxTerrain={2:F0} m radialDescent={3}", target.Radius, surfaceGravity, target.maxTerrainHeight, smallBodyMode));
        }

        // Predict the next descending atmosphere-boundary crossing on the
        // current unpowered orbit; this does not commit an ignition.
        private bool TryPredictEntry(out Vector2 retrograde, out double seconds)
        {
            retrograde = Vector2.zero; seconds = double.PositiveInfinity;
            if (rocket == null || target == null || WorldTime.main == null || !HasUsefulAtmosphere(target)) return false;
            double entryRadius = target.Radius + target.AtmosphereHeightPhysics;
            bool valid;
            Orbit orbit = Orbit.TryCreateOrbit(rocket.location.Value, true, false, out valid);
            if (!valid || orbit == null || orbit.ecc < 1e-7 || orbit.periapsis > entryRadius || orbit.apoapsis < entryRadius) return false;
            double anomaly = Kepler.GetTrueAnomalyAtRadius(entryRadius, orbit.slr, orbit.ecc) * -orbit.direction;
            double now = WorldTime.main.worldTime;
            double time = orbit.GetNextTrueAnomalyPassTime(now, anomaly);
            if (double.IsNaN(time) || double.IsInfinity(time) || time < now || time > orbit.orbitEndTime + .1) return false;
            Location atEntry = orbit.GetLocation(time);
            if (atEntry == null || atEntry.planet != target || double.IsNaN(atEntry.velocity.x) || double.IsNaN(atEntry.velocity.y)) return false;
            retrograde = new Vector2((float)-atEntry.velocity.x, (float)-atEntry.velocity.y);
            if (retrograde.sqrMagnitude < .001f) return false;
            seconds = time - now;
            return true;
        }

        private float GetBottomOffset(Rocket craft)
        {
            if (craft == null || craft.partHolder == null || craft.rb2d == null) return bottomOffset;
            Vector2 com = craft.rb2d.worldCenterOfMass;
            var radial = craft.location.position.Value.normalized;
            Vector2 up = new Vector2((float)radial.x, (float)radial.y);
            float lowest = float.NegativeInfinity;
            foreach (var part in craft.partHolder.parts)
            {
                if (part == null) continue;
                foreach (var col in part.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null || col.isTrigger || !col.enabled || !col.gameObject.activeInHierarchy || col.attachedRigidbody != craft.rb2d) continue;
                    Bounds b = col.bounds;
                    if (b.size.sqrMagnitude <= 0.0001f) continue;
                    float projectedExtent = Mathf.Abs(up.x) * b.extents.x + Mathf.Abs(up.y) * b.extents.y;
                    float distance = Vector2.Dot(com - (Vector2)b.center, up) + projectedExtent;
                    lowest = Mathf.Max(lowest, distance);
                }
            }
            if (!float.IsNegativeInfinity(lowest))
            {
                comBottomOffset=Mathf.Max(0f,lowest);
                Vector2 reference = craft.mass == null ? com : (Vector2)craft.rb2d.transform.position + (Vector2)craft.rb2d.transform.TransformVector(craft.mass.GetCenterOfMass());
                float comAboveReference=Vector2.Dot(com-reference,up);
                bottomOffset=Mathf.Max(0f,lowest-comAboveReference);
            }
            return bottomOffset;
        }

        private static double ImpactTolerance(ColliderModule m)
        {
            if(m==null) return 2.5;
            switch(m.impactTolerance)
            {
                case ColliderModule.ImpactTolerance.Low: return 2.5;
                case ColliderModule.ImpactTolerance.Medium: return 5.5;
                case ColliderModule.ImpactTolerance.High: return 12.5;
                case ColliderModule.ImpactTolerance.Wheel: return 50.5;
                default: return 2.5;
            }
        }

        private void UpdateTouchdownTarget(Rocket craft)
        {
            if(craft==null || craft.partHolder==null || craft.rb2d==null || craft.location==null) return;
            var radial=craft.location.position.Value.normalized;
            Vector2 up=new Vector2((float)radial.x,(float)radial.y), com=craft.rb2d.worldCenterOfMass;
            var candidates=new List<KeyValuePair<float,ColliderModule>>();
            float lowest=float.NegativeInfinity;
            foreach(var part in craft.partHolder.parts)
            {
                if(part==null) continue;
                foreach(var col in part.GetComponentsInChildren<Collider2D>(true))
                {
                    if(col==null || !col.enabled || col.isTrigger || !col.gameObject.activeInHierarchy || col.attachedRigidbody!=craft.rb2d) continue;
                    Bounds b=col.bounds; if(b.size.sqrMagnitude<=.0001f) continue;
                    float projection=Vector2.Dot(com-(Vector2)b.center,up)+Mathf.Abs(up.x)*b.extents.x+Mathf.Abs(up.y)*b.extents.y;
                    var module=col.GetComponent<ColliderModule>();
                    if(module==null) module=col.GetComponentInParent<ColliderModule>();
                    if(module!=null && (!module.enabled || !module.gameObject.activeInHierarchy)) module=null;
                    candidates.Add(new KeyValuePair<float,ColliderModule>(projection,module));
                    lowest=Mathf.Max(lowest,projection);
                }
            }
            double found=double.PositiveInfinity; bool unknown=false; int considered=0;
            foreach(var candidate in candidates)
            {
                if(candidate.Key<lowest-2f) continue;
                considered++; if(candidate.Value==null) unknown=true;
                found=Math.Min(found,ImpactTolerance(candidate.Value));
            }
            if(considered==0) { found=2.5; unknown=true; }
            bool remembered=found>touchdownTolerance;
            if(found<touchdownTolerance) { touchdownTolerance=found; LandingSpeed=RecoverySafety.TouchdownTarget(found); }
            touchdownBasis=unknown || remembered ? "conservative" : "near-bottom";
            nextTouchdownScan=Time.unscaledTime+.5f;
        }

        private void BeginTerminal(string reason)
        {
            if(!terminalBurnStarted) Debug.Log("[HIMA Recovery 1.4.6] Terminal burn: "+reason);
            terminalEngagedEver=true; terminalReascending=false; terminalBurnStarted=true;
            deorbitComplete=true; smallBodyRadialPending=false;
        }

        // Project the same live, non-trigger colliders along the current thrust
        // axis. If that axis is later made vertical, this estimates the lowest
        // point's new distance below the mass center and the overall span.
        private static void GetThrustAxisGeometry(Rocket craft, Vector2 force, out float bottom, out float span)
        {
            bottom = 0f; span = 0f;
            if (craft == null || craft.rb2d == null || craft.partHolder == null || force.sqrMagnitude < .0001f) return;
            Vector2 axis = force.normalized, com = craft.rb2d.worldCenterOfMass;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (var part in craft.partHolder.parts)
            {
                if (part == null) continue;
                foreach (var col in part.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null || col.isTrigger || !col.enabled || !col.gameObject.activeInHierarchy || col.attachedRigidbody != craft.rb2d) continue;
                    Bounds b = col.bounds;
                    if (b.size.sqrMagnitude <= .0001f) continue;
                    float center = Vector2.Dot((Vector2)b.center - com, axis);
                    float extent = Mathf.Abs(axis.x) * b.extents.x + Mathf.Abs(axis.y) * b.extents.y;
                    min = Mathf.Min(min, center - extent); max = Mathf.Max(max, center + extent);
                }
            }
            if (!float.IsInfinity(min) && !float.IsInfinity(max))
            { bottom = Mathf.Max(0f, -min); span = Mathf.Max(0f, max - min); }
        }

        private static EngineModule[] AvailableEngines(Rocket craft)
        {
            return craft.partHolder.GetModules<EngineModule>()
                .Where(e => e != null && e.engineOn.Value && e.source != null && e.source.CanFlow(new MsgNone()))
                .ToArray();
        }

        // On small, low-gravity, rugged bodies remove orbital tangential speed
        // high above the highest known terrain before allowing a radial fall.
        private bool HandleSmallBodyRadialization(Double2 position, double vertical, Vector2 tangentVelocity,
            Vector2 combined, double engineAcceleration, double terrain)
        {
            if (!smallBodyRadialPending) return false;
            double speed = tangentVelocity.magnitude;
            double highestTerrain = Math.Max(terrain, Math.Max(0, target.maxTerrainHeight));
            double horizontalTarget = SmallBodyGuidance.HorizontalTarget(position.magnitude, target.Radius + highestTerrain);
            if (speed <= horizontalTarget)
            {
                SetThrottle(0f);
                rocket.arrowkeys.turnAxis.Value = 0f;
                rocket.arrowkeys.horizontalAxis.Value = Vector2.zero;
                smallBodyRadialPending = false;
                deorbitComplete = true;
                status = "Small body: radial free fall";
                Debug.Log(string.Format("[RecoveryPilot 0.14.9] Small body horizontal braking complete: side={0:F2} vertical={1:F2}", speed, vertical));
                return false;
            }
            if (combined.sqrMagnitude < .001f) { Stop("Stopped: unknown thrust direction"); return true; }
            Vector2 desired = -tangentVelocity.normalized;
            float angle = Vector2.SignedAngle(combined, desired);
            float spin = rocket.rb2d.angularVelocity;
            double clearance = position.magnitude - target.Radius - highestTerrain - bottomOffset;
            double surfaceGravity = target.mass / Math.Max(1.0, target.Radius * target.Radius);
            double needed = SmallBodyGuidance.RequiredClearance(speed, Math.Max(0, -vertical), surfaceGravity, engineAcceleration, angle, spin);
            if (clearance <= needed)
            {
                // Do not spend the remaining descent time in a horizontal-only burn.
                smallBodyRadialPending = false;
                deorbitComplete = true;
                SetThrottle(0f);
                Debug.Log(string.Format("[RecoveryPilot 0.14.9] Small body radial burn skipped: clearance={0:F1} needed={1:F1}", clearance, needed));
                return false;
            }
            LimitControlTimewarp();
            float turn = Mathf.Clamp(-angle * .035f + spin * .04f, -1f, 1f);
            if (rocket.arrowkeys.rcs.Value && rocket.partHolder.HasModule<RcsModule>() &&
                (Math.Abs(angle) > 4 || Math.Abs(spin) > 2.5f) && Math.Abs(turn) > .02f)
                turn = Mathf.Sign(turn) * Mathf.Max(.96f, Math.Abs(turn));
            rocket.arrowkeys.turnAxis.Value = turn;
            rocket.arrowkeys.horizontalAxis.Value = Vector2.zero;
            rocket.arrowkeys.verticalAxis.Value = Vector2.zero;
            double lateralAcceleration = engineAcceleration * Math.Max(0, Vector2.Dot(combined.normalized, desired));
            float throttle = Math.Abs(angle) < 5f && Math.Abs(spin) < 3f
                ? SmallBodyGuidance.Throttle(speed, horizontalTarget, lateralAcceleration) : 0f;
            SetThrottle(throttle);
            status = string.Format("Small body: cancel orbital speed\nHorizontal {0:F1} m/s  Turn {1:F0} deg\nClearance {2:F0} m", speed, angle, clearance);
            return true;
        }

        private void DeployLandingLegs(double height, double vertical, double gravity, double speed)
        {
            if (vertical >= 0) return;
            var legs = rocket.partHolder.parts.Where(LandingLegOptions.IsLeg)
                .Where(p => p.variablesModule.boolVariables.GetValue(LandingLegOptions.Key)).ToArray();
            if (legs.Length == 0) return;
            double maxAnimation = legs.SelectMany(p => p.GetModules<ToggleModule>())
                .Where(t => t != null && t.state != null).Select(t => (double)t.state.animationTime).DefaultIfEmpty(0).Max();
            double time = EstimateTimeToGround(height, Math.Max(0, -vertical), gravity);
            if (time > Math.Max(10.0, maxAnimation + 4.0) || (speed > 150.0 && time > Math.Max(3.0, maxAnimation + 1.0))) return;
            LimitControlTimewarp();
            foreach (var leg in legs)
                foreach (var toggle in leg.GetModules<ToggleModule>())
                    if (toggle != null && toggle.state != null && toggle.state.targetTime.Value < 0.5f)
                    {
                        toggle.state.Activate();
                        Debug.Log(string.Format("[RecoveryPilot 0.14.9] Deploying leg {0}: height={1:F1} time={2:F1}", leg.name, height, time));
                    }
        }

        private static Vector2 EngineForce(IEnumerable<EngineModule> engines)
        {
            Vector2 result = Vector2.zero;
            foreach (var engine in engines)
            {
                Vector2 local = engine.thrustNormal.Value * Math.Max(0f, engine.thrust.Value);
                result += Base.worldBase.AllowsCheats
                    ? (Vector2)engine.transform.TransformVector(local)
                    : engine.transform.TransformVectorUnscaled(local);
            }
            return result;
        }

        private static double GetPeriapsisRadius(Double2 position, Double2 velocity, double mu)
        {
            double radius = position.magnitude;
            if (radius <= 0 || mu <= 0) return 0;
            double angularMomentum = position.x * velocity.y - position.y * velocity.x;
            double energy = velocity.sqrMagnitude * 0.5 - mu / radius;
            double eccentricity = Math.Sqrt(Math.Max(0, 1.0 + 2.0 * energy * angularMomentum * angularMomentum / (mu * mu)));
            return angularMomentum * angularMomentum / (mu * (1.0 + eccentricity));
        }

        private static double EstimateTimeToGround(double height, double downSpeed, double downAcceleration)
        {
            double remaining = Math.Max(0, height);
            if (downAcceleration <= 0.01)
                return downSpeed > 0.1 ? Math.Min(300, remaining / downSpeed) : 300;
            double t = (Math.Sqrt(downSpeed * downSpeed + 2.0 * downAcceleration * remaining) - downSpeed) / downAcceleration;
            return Math.Max(0.1, Math.Min(300, t));
        }

        // Full-throttle, constant gravity benchmark: |(vx, down-target+g*t)| = a*t.
        private double CombinedBurnTime(double horizontal, double down, double gravity, double acceleration)
        {
            double deltaDown = down - LandingSpeed;
            double denominator = acceleration * acceleration - gravity * gravity;
            if (denominator <= 0.001) return 1e6;
            double b = gravity * deltaDown;
            double discriminant = b * b + denominator * (horizontal * horizontal + deltaDown * deltaDown);
            return Math.Max(0.0, (b + Math.Sqrt(Math.Max(0.0, discriminant))) / denominator);
        }

        private static bool HasLandableSurface(Planet planet)
        {
            return planet != null && planet.data != null && planet.data.hasTerrain &&
                planet.data.terrain != null && planet.data.terrain.collider;
        }

        private static bool HasUsefulAtmosphere(Planet planet)
        {
            if (planet == null || !planet.HasAtmospherePhysics || planet.AtmosphereHeightPhysics <= 1) return false;
            if (SandboxSettings.main != null && SandboxSettings.main.settings.noAtmosphericDrag) return false;
            double density = planet.GetAtmosphericDensity(planet.AtmosphereHeightPhysics * .5);
            return !double.IsNaN(density) && !double.IsInfinity(density) && density > 1e-9;
        }

        private double DragFactor(Double2 velocity)
        {
            if (!HasUsefulAtmosphere(target) || velocity.magnitude < 1) return 0;
            return DragFactorAtRotation(velocity,0);
        }
        private double DragFactorAtRotation(Double2 velocity, float offsetDegrees)
        {
            float angle = (float)velocity.AngleRadians - Mathf.PI / 2f;
            var surfaces = AeroModule.GetExposedSurfaces(Aero_Rocket.GetDragSurfaces(rocket.partHolder, Matrix2x2.Angle(-angle + offsetDegrees*Mathf.Deg2Rad)));
            double factor = 0;
            foreach (var surface in surfaces)
            {
                Vector2 line = surface.line.end - surface.line.start;
                if (line.x >= 0.01f) factor += line.x * line.x / (line.x + Math.Abs(line.y));
            }
            return 1.5 * factor / Math.Max(0.01, rocket.rb2d.mass);
        }

        private void UpdateAeroCandidates(Double2 velocity, Vector2 force)
        {
            // Once committed, opposite broadside choices are 180 degrees
            // apart. Small measurement changes must not command a flip.
            int lockedIndex = aeroHighDragEngaged && aeroTargetAngle < -45 ? 1 :
                aeroHighDragEngaged && aeroTargetAngle > 45 ? 2 : 0;
            aeroHighDragValid=false; aeroRetroFactor=0; aeroBestFactor=0; aeroTargetAngle=0;
            if(force.sqrMagnitude<.001f || velocity.magnitude<100 || rocket.rb2d.mass<=0) return;
            Vector2 retro=new Vector2((float)-velocity.x,(float)-velocity.y).normalized;
            float retroAngle=Vector2.SignedAngle(force,retro);
            // Compare the aligned axis and both orientations of the perpendicular
            // axis; asymmetric craft can expose different surfaces on each side.
            float[] offsets={0,-90,90};
            double[] factors=new double[offsets.Length];
            for(int i=0;i<offsets.Length;i++) factors[i]=DragFactorAtRotation(velocity,retroAngle+offsets[i]);
            int selected=AeroAttitudePlan.SelectLocked(factors,lockedIndex);
            if(selected<0) return;
            aeroRetroFactor=factors[0]; aeroBestFactor=factors[selected];
            aeroTargetAngle=offsets[selected]; aeroHighDragValid=selected!=0;
        }

        private LandingPrediction.Outcome PredictLanding(Double2 position, Double2 velocity, double surfaceRadius,
            double acceleration, double factor, double delay, out double finishHeight)
        {
            double mass = Math.Max(0.01, rocket.rb2d.mass);
            return LandingPrediction.PredictDetailed(position.x, position.y, velocity.x, velocity.y,
                target.Radius, surfaceRadius, target.mass, acceleration, factor * aeroCalibration.Scale,
                LandingSpeed, delay, h => target.GetAtmosphericDensity(h), mass, fullThrottleMassFlow, out finishHeight);
        }

        private void SampleFlight(Double2 position, Double2 velocity, double height, double thrust, double gravity, Vector2 combined)
        {
            double dt = Time.fixedDeltaTime;
            double mass = rocket.rb2d.mass;
            double throttle = rocket.throttle.output_Throttle.Value;
            bool physics = WorldTime.main != null && WorldTime.main.realtimePhysics.Value;
            bool plausibleMass = previousMass > 0 && Math.Abs(mass - previousMass) < previousMass * 0.02;
            if (physics && plausibleMass && dt > 0.001 && dt < 0.2 && throttle > 0.05 && mass < previousMass)
            {
                double rate = (previousMass - mass) / (dt * throttle);
                if (rate > 0 && rate < mass * 0.1)
                    fullThrottleMassFlow += (rate - fullThrottleMassFlow) * (1.0 - Math.Exp(-dt / 2.0));
            }
            previousMass = mass;
            double altitude = position.magnitude - target.Radius;
            bool usefulAir = HasUsefulAtmosphere(target);
            sampledDensity = usefulAir ? Math.Max(0, target.GetAtmosphericDensity(altitude)) : 0;
            sampledDragFactor = usefulAir && altitude <= target.AtmosphereHeightPhysics ? DragFactor(velocity) : 0;
            bool stable = physics && plausibleMass && Math.Abs(rocket.rb2d.angularVelocity) < 5 &&
                Math.Abs(rocket.arrowkeys.turnAxis.Value) < 0.1f &&
                rocket.arrowkeys.horizontalAxis.Value.sqrMagnitude < 0.0001f && !IsTouchingGround(rocket);
            if (!physics || !plausibleMass) aeroCalibration.ResetSample();
            aeroCalibration.Observe(position.x, position.y, velocity.x, velocity.y, target.mass,
                sampledDensity, sampledDragFactor, throttle, dt, stable);

            flightSampleClock += (float)dt;
            if (physics && flightSampleClock >= 0.25f && target.HasAtmospherePhysics &&
                altitude <= Math.Max(80000.0, target.AtmosphereHeightPhysics * 1.5))
            {
                flightSampleClock = 0;
                Vector2 retro = new Vector2((float)-velocity.x, (float)-velocity.y);
                double angle = combined.sqrMagnitude > 0.001f && retro.sqrMagnitude > 0.001f ? Vector2.SignedAngle(combined, retro) : 0;
                double radial = Double2.Dot(velocity, position.normalized);
                double side = (position.x * velocity.y - position.y * velocity.x) / position.magnitude;
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[RecoveryPilot 0.14.9] sample dt={0:F3} h={1:F1} bottom={2:F1} down={3:F2} side={4:F2} mass={5:F2} g={6:F3} rho={7:G6} k={8:G6} aero={9:F3} scale={10:F3} confidence={11:F1} thrust={12:F1} throttle={13:F3} angle={14:F1} spin={15:F1} flow={16:F4} forceSide={17:F3} targetSide={18:F3}",
                    dt, height, bottomOffset, -radial, side, mass, gravity, sampledDensity, sampledDragFactor,
                    aeroCalibration.ObservedDrag, aeroCalibration.Scale, aeroCalibration.ConfidenceSeconds, thrust, throttle,
                    angle, rocket.rb2d.angularVelocity, fullThrottleMassFlow, lastForceSide, lastDesiredSide));
            }
        }

        private bool IsTouchingGround(Rocket current)
        {
            if (current == null || current.rb2d == null) return false;
            int count = current.rb2d.GetContacts(groundContacts);
            int groundLayer = LayerMask.NameToLayer("Celestial Body");
            for (int i = 0; i < count; i++)
                if (groundContacts[i] != null && groundContacts[i].gameObject.layer == groundLayer)
                    return true;
            return false;
        }

        // Conservative terrain envelope across the predicted low-horizontal-speed drift corridor.
        private bool TryVacuumTerrainClearance(Double2 position, Vector2 tangentVelocity, double down, double gravity, out double clearance)
        {
            clearance=0;
            if(target==null || position.magnitude<1 || gravity<=0 || double.IsNaN(gravity)) return false;
            double radius=position.magnitude;
            if(Time.unscaledTime<nextVacuumTerrainSample && !double.IsNaN(vacuumTerrainEnvelope))
            { clearance=radius-target.Radius-vacuumTerrainEnvelope-bottomOffset; return true; }
            vacuumTerrainEnvelope=double.NaN;
            double localTerrain=target.GetTerrainHeightAtAngle(position.AngleRadians,true);
            if(double.IsNaN(localTerrain) || double.IsInfinity(localTerrain)) return false;
            double localHeight=Math.Max(0,radius-target.Radius-localTerrain-bottomOffset);
            double fallTime=(Math.Sqrt(down*down+2*gravity*localHeight)-down)/gravity;
            if(double.IsNaN(fallTime) || double.IsInfinity(fallTime) || fallTime>90) return false;
            double horizon=Math.Max(8,fallTime), minimum=double.PositiveInfinity, maximum=double.NegativeInfinity;
            Vector2 tangent=new Vector2((float)(-position.y/radius),(float)(position.x/radius));
            for(int i=0;i<=24;i++)
            {
                double t=horizon*i/24.0;
                for(int lane=-2;lane<=2;lane++)
                {
                    double offset=lane*15.0;
                    double x=position.x+tangentVelocity.x*t+tangent.x*offset;
                    double y=position.y+tangentVelocity.y*t+tangent.y*offset;
                    double h=target.GetTerrainHeightAtAngle(Math.Atan2(y,x),true);
                    if(double.IsNaN(h) || double.IsInfinity(h)) return false;
                    minimum=Math.Min(minimum,h); maximum=Math.Max(maximum,h);
                }
            }
            double terrainMargin=Math.Max(30,Math.Min(300,(maximum-minimum)*.5));
            vacuumTerrainEnvelope=maximum+terrainMargin;
            nextVacuumTerrainSample=Time.unscaledTime+.2f;
            clearance=radius-target.Radius-vacuumTerrainEnvelope-bottomOffset;
            return !double.IsNaN(clearance) && !double.IsInfinity(clearance);
        }

        private void FixedUpdate()
        {
            if (!active) return;
            if (rocket == null || target == null || PlayerController.main == null || PlayerController.main.player.Value != rocket)
            { Stop("Stopped: rocket changed"); return; }
            var body = rocket.location.planet.Value;
            if (body != target || rocket.rb2d == null) { Stop("Stopped: target changed"); return; }
            if (!HasLandableSurface(body)) { Stop("Stopped: no solid surface on this body"); return; }

            // SFS itself identifies ground contact from the rocket Rigidbody2D's
            // contacts with the Celestial Body layer. The rocket's center can still
            // be several meters above the terrain when landing legs touch down.
            if (IsTouchingGround(rocket))
            {
                if (groundContactFrames == 0)
                {
                    var contactVelocity = rocket.location.velocity.Value;
                    var contactUp = rocket.location.position.Value.normalized;
                    Debug.Log(string.Format("[RecoveryPilot 0.14.9] Ground contact speed={0:F2} vertical={1:F2} previousSpeed={2:F2} previousVertical={3:F2} bottomOffset={4:F2}", contactVelocity.magnitude, Double2.Dot(contactVelocity, contactUp), lastFlightSpeed, lastFlightVertical, bottomOffset));
                }
                SetThrottle(0f);
                rocket.arrowkeys.turnAxis.Value = 0f;
                rocket.arrowkeys.horizontalAxis.Value = Vector2.zero;
                rocket.arrowkeys.verticalAxis.Value = Vector2.zero;
                if (++groundContactFrames >= 2) Stop(lastFlightSpeed >= 5.0 ?
                    string.Format("Ground contact at {0:F1} m/s: check craft", lastFlightSpeed) : "Landed: throttle off");
                else status = "Ground contact: shutting down";
                return;
            }
            groundContactFrames = 0;

            var position = rocket.location.position.Value;
            var velocity = rocket.location.velocity.Value;
            lastFlightSpeed = velocity.magnitude;
            lastFlightVertical = Double2.Dot(velocity, position.normalized);
            double radius = rocket.location.Value.Radius;
            double terrain = target.GetTerrainHeightAtAngle(rocket.location.position.Value.AngleRadians, true);
            double height = Math.Max(0.1, radius - target.Radius - terrain - GetBottomOffset(rocket));
            if(Time.unscaledTime>=nextTouchdownScan) UpdateTouchdownTarget(rocket);
            double gravity = target.mass / (radius * radius);
            var engines = AvailableEngines(rocket);
            Vector2 combined = EngineForce(engines);
            double thrust = combined.magnitude * 9.8;
            double engineAcceleration = thrust / Math.Max(0.01, rocket.rb2d.mass);
            engineStatus = string.Format("Engines {0}  Force {1:F1} kN  Mass {2:F1} t\nRef ASL {3:F0} m  Terrain {4:F0} m\nBottom offset {5:F1} m  AGL {6:F0} m\nLand target {7:F1} m/s  Limit {8:F1} ({9})\nEngine starts {10}", engines.Length, thrust, rocket.rb2d.mass, radius-target.Radius,terrain,bottomOffset,height,LandingSpeed,touchdownTolerance,touchdownBasis,ignitionCount);
            if (engines.Length == 0) { Stop("Stopped: no enabled fueled engines"); return; }
            if (engineAcceleration <= gravity * 1.05) { Stop("Stopped: insufficient thrust or fuel"); return; }

            var radial = position.normalized;
            double vertical = Double2.Dot(velocity, radial);
            double downSpeed = Math.Max(0, -vertical);
            Vector2 tangentVelocity = new Vector2(
                (float)(velocity.x - radial.x * vertical),
                (float)(velocity.y - radial.y * vertical));
            double horizontal = tangentVelocity.magnitude;
            // A rising surface ahead shortens the available horizontal braking
            // window. Use the corridor only to advance lateral planning, with a
            // bounded allowance; retain live ground clearance for vertical control.
            double lateralPlanningHeight = height;
            if (terminalBurnStarted && HasUsefulAtmosphere(target) && vertical < 0 && horizontal > 20 && height < 2000)
            {
                double corridorClearance;
                if (TryVacuumTerrainClearance(position, tangentVelocity, downSpeed, gravity, out corridorClearance))
                    lateralPlanningHeight = Math.Max(0, height - Math.Min(200.0, Math.Max(0, height - corridorClearance)));
            }
            Vector2 localUp = new Vector2((float)radial.x, (float)radial.y);
            float forceAxisBottom, forceAxisSpan;
            GetThrustAxisGeometry(rocket, combined, out forceAxisBottom, out forceAxisSpan);
            double tiltFromUp = combined.sqrMagnitude > .001f ? Vector2.SignedAngle(localUp, combined) : 0;
            var stability = LandingStability.Compute(height, downSpeed, horizontal, gravity, tiltFromUp,
                rocket.rb2d.angularVelocity, forceAxisSpan, comBottomOffset, forceAxisBottom);
            double landingReserve = vertical < 0 ? stability.IgnitionMargin : 0;
            SampleFlight(position, velocity, height, thrust, gravity, combined);
            DeployLandingLegs(height, vertical, gravity, velocity.magnitude);
            rocket.arrowkeys.horizontalAxis.Value = horizontal < 5.0
                ? -Vector2.ClampMagnitude(tangentVelocity / 5f, 1f) : Vector2.zero;

            bool earthWithAir = HasUsefulAtmosphere(target);
            double altitudeAboveSeaLevel = radius - target.Radius;
            if (RecoverySafety.AllowOrbitalRecovery(terminalEngagedEver) && smallBodyRadialPending && HandleSmallBodyRadialization(position, vertical, tangentVelocity, combined, engineAcceleration, terrain)) return;
            if (RecoverySafety.AllowOrbitalRecovery(terminalEngagedEver) && !deorbitComplete && (!earthWithAir || altitudeAboveSeaLevel >= target.AtmosphereHeightPhysics))
            {
                entryPreparationStarted = false;
                entryPreparationComplete = false;
                entryStableSeconds = 0f;
                double periapsis = GetPeriapsisRadius(position, velocity, target.mass);
                // Predict the terrain at the osculating periapsis, rather than
                // using the terrain below the current orbital position.
                double periAngle = RecoverySafety.PeriapsisAngle(position.x,position.y,velocity.x,velocity.y,target.mass);
                if(double.IsNaN(periAngle) || double.IsInfinity(periAngle)) { SetThrottle(0f); status="Cannot determine deorbit terrain angle"; return; }
                double impactPeriapsis = target.Radius + target.GetTerrainHeightAtAngle(periAngle, true);
                if (!deorbitComplete && periapsis > impactPeriapsis + 1.0)
                {
                    LimitControlTimewarp();
                    Vector2 retrograde = new Vector2((float)-velocity.x, (float)-velocity.y);
                    if (retrograde.sqrMagnitude < 0.01f || combined.sqrMagnitude < 0.001f)
                    { Stop("Stopped: cannot calculate deorbit direction"); return; }
                    float angle = Vector2.SignedAngle(combined, retrograde);
                    float spin = rocket.rb2d.angularVelocity;
                    float deorbitTurn = Mathf.Clamp(-angle * 0.035f + spin * 0.04f, -1f, 1f);
                    if (rocket.arrowkeys.rcs.Value && rocket.partHolder.HasModule<RcsModule>() &&
                        (Math.Abs(angle) > 4f || Math.Abs(spin) > 2.5f) && Math.Abs(deorbitTurn) > 0.02f)
                        deorbitTurn = Mathf.Sign(deorbitTurn) * Mathf.Max(0.96f, Math.Abs(deorbitTurn));
                    rocket.arrowkeys.turnAxis.Value = deorbitTurn;
                    rocket.arrowkeys.horizontalAxis.Value = Vector2.zero;
                    double gap = periapsis - impactPeriapsis;
                    double requiredDv=DeorbitGuidance.RequiredDeltaV(position.x,position.y,velocity.x,velocity.y,target.mass,impactPeriapsis+1.0);
                    if(double.IsNaN(requiredDv) || double.IsInfinity(requiredDv)) { SetThrottle(0); status="Cannot plan safe deorbit burn"; return; }
                    float deorbitThrottle = (float)DeorbitGuidance.Throttle(requiredDv,engineAcceleration);
                    SetThrottle(Math.Abs(angle) < 12f && Math.Abs(spin) < 12f ? deorbitThrottle : 0f);
                    status = string.Format("Deorbit burn  Altitude {0:F0} m\nPe to terrain {1:F0} m  dV {2:F1} m/s\nThrottle {3:P0}", altitudeAboveSeaLevel,gap,requiredDv,deorbitThrottle);
                    return;
                }
                deorbitComplete = true;
            }
            if (earthWithAir && altitudeAboveSeaLevel >= target.AtmosphereHeightPhysics)
            {
                if (enteredAtmosphere && vertical > 0) { if(!terminalEngagedEver) deorbitComplete = false; enteredAtmosphere = false; }
            }
            if (earthWithAir && altitudeAboveSeaLevel < target.AtmosphereHeightPhysics) { enteredAtmosphere = true; entryPreparationStarted = false; }
            if (terminalBurnStarted && RecoverySafety.Reascending(terminalEngagedEver,vertical))
            {
                terminalBurnStarted = false; terminalTrimActive = false;
                vacuumIgnitionConfirmed = false; vacuumStableSeconds = 0f;
                terminalReascending=true; vacuumSecondCoast=false;
                Debug.Log("[HIMA Recovery 1.4.6] Terminal ascent: coast until descent; deorbit remains complete");
            }
            if(terminalReascending)
            {
                if(!RecoverySafety.ResumeDescent(vertical))
                { SetThrottle(0f); rocket.arrowkeys.turnAxis.Value=0f; rocket.arrowkeys.horizontalAxis.Value=Vector2.zero; status=string.Format("Terminal ascent: coasting\nAltitude {0:F0} m  Vertical {1:F1} m/s",height,vertical); return; }
                vacuumIgnitionConfirmed=false; vacuumStableSeconds=0f;
                BeginTerminal("resuming after terminal ascent");
            }

            bool outsideAtmosphere = !earthWithAir || altitudeAboveSeaLevel >= target.AtmosphereHeightPhysics;
            double safeAcceleration = engineAcceleration * 0.85 > gravity
                ? engineAcceleration * 0.85 : (engineAcceleration + gravity) * 0.5;
            double vacuumBurnTime = CombinedBurnTime(horizontal, downSpeed, gravity, safeAcceleration);
            double vacuumBurnHeight = 20.0 + Math.Max(0, (downSpeed + LandingSpeed) * 0.5 * vacuumBurnTime) * 1.15 + downSpeed * 2.0 + gravity * 2.0 + landingReserve;
            lastPredictedBurnHeight=earthWithAir ? double.NaN : vacuumBurnHeight;
            double vacuumTerrainClearance=height;
            if(!earthWithAir && terminalBurnStarted && !vacuumSecondCoastUsed && !smallBodyMode && vertical<0 && horizontal<=1.5 && downSpeed<=10 && Math.Abs(tiltFromUp)<=12 && Math.Abs(rocket.rb2d.angularVelocity)<=4)
            {
                double sampledClearance;
                if(TryVacuumTerrainClearance(position,tangentVelocity,downSpeed,gravity,out sampledClearance) && VacuumGuidance.CanCoast(sampledClearance,downSpeed,horizontal,gravity,engineAcceleration,LandingSpeed,landingReserve))
                {
                    terminalBurnStarted=false; vacuumSecondCoast=true; vacuumSecondCoastUsed=true; coastAfterBraking=true;
                    vacuumIgnitionConfirmed=false; vacuumStableSeconds=0;
                    SetThrottle(0f); rocket.arrowkeys.turnAxis.Value=0f;
                    Debug.Log(string.Format("[RecoveryPilot] Safe second coast: clearance={0:F1} down={1:F1} horizontal={2:F1}",sampledClearance,downSpeed,horizontal));
                }
            }
            if(vacuumSecondCoast)
            {
                if(!TryVacuumTerrainClearance(position,tangentVelocity,downSpeed,gravity,out vacuumTerrainClearance))
                { vacuumSecondCoast=false; BeginTerminal("terrain forecast unavailable"); Debug.LogWarning("[RecoveryPilot] Terrain forecast unavailable; resume braking"); }
                else if(vertical>=0 || horizontal>3 || vacuumTerrainClearance<=Math.Max(vacuumBurnHeight,VacuumGuidance.SecondBurnHeight(downSpeed,horizontal,gravity,engineAcceleration,LandingSpeed,landingReserve)))
                { vacuumSecondCoast=false; BeginTerminal("second vacuum burn"); Debug.Log(string.Format("[RecoveryPilot] Second ignition: clearance={0:F1} down={1:F1} horizontal={2:F1}",vacuumTerrainClearance,downSpeed,horizontal)); }
            }
            bool vacuumIgnition = !earthWithAir && !vacuumSecondCoast && vertical < 0 && height <= vacuumBurnHeight;
            if (vacuumIgnition && !terminalBurnStarted)
            {
                BeginTerminal("vacuum ignition");
                Debug.Log(string.Format("[RecoveryPilot 0.14.9] Vacuum burn: height={0:F1} down={1:F1} horizontal={2:F1}", height, downSpeed, horizontal));
            }
            if (outsideAtmosphere && !terminalBurnStarted)
            {
                var worldTime = WorldTime.main;
                Vector2 retro = new Vector2((float)-velocity.x, (float)-velocity.y);
                double retroError = combined.sqrMagnitude > .001f && retro.sqrMagnitude > .001f ? Vector2.SignedAngle(combined, retro) : 0;
                double warpAllowance = worldTime == null ? 0 : Math.Min(10.0, worldTime.timewarpSpeed * Time.fixedDeltaTime * 4.0);
                double preparationDelay = VacuumGuidance.PreparationSeconds(retroError, rocket.rb2d.angularVelocity, warpAllowance);
                bool prepareVacuumBurn = !earthWithAir && vertical < 0 &&
                    VacuumGuidance.Prepare(height, vacuumBurnHeight, downSpeed, gravity, preparationDelay);
                bool prepareAtmosphereEntry = false;
                if (earthWithAir && deorbitComplete && vertical < 0 && !entryPreparationComplete)
                {
                    Vector2 entryRetro;
                    double entrySeconds;
                    bool entryValid = TryPredictEntry(out entryRetro, out entrySeconds);
                    if (entryValid && !entryPreparationStarted)
                    {
                        predictedEntryDirection = entryRetro.normalized;
                        try
                        {
                            if (Time.unscaledTime >= nextAeroCandidate)
                            {
                                UpdateAeroCandidates(new Double2(-entryRetro.x, -entryRetro.y), combined);
                                nextAeroCandidate = Time.unscaledTime + 1f;
                            }
                            if (aeroHighDragValid)
                                predictedEntryDirection = Quaternion.Euler(0, 0, aeroTargetAngle) * predictedEntryDirection;
                            aeroStatus = string.Format("Entry drag 0 deg {0:F4}  selected {1:+0;-0;0} deg {2:F4}", aeroRetroFactor, aeroTargetAngle, aeroBestFactor);
                        }
                        catch (Exception ex)
                        {
                            aeroHighDragValid = false;
                            aeroStatus = "Entry drag unavailable; retrograde fallback";
                            Debug.LogWarning("[HIMA Recovery 1.4.6] Entry drag selection unavailable: " + ex.Message);
                        }
                        double entryAngle = Math.Abs(Vector2.SignedAngle(combined, predictedEntryDirection));
                        double warpStep = worldTime == null ? 0 : worldTime.timewarpSpeed * Time.fixedDeltaTime;
                        double lead = EntryPreparation.LeadSeconds(entryAngle, rocket.rb2d.angularVelocity, engineAcceleration / Math.Max(.01, gravity), warpStep);
                        if (!entryPreparationStarted && EntryPreparation.ShouldStart(entrySeconds, lead))
                        {
                            entryPreparationStarted = true;
                            Debug.Log(string.Format("[HIMA Recovery 1.4.6] Entry attitude preparation: eta={0:F1}s lead={1:F1}s turn={2:F1} selected={3:F0} drag0={4:F4} dragSelected={5:F4}", entrySeconds, lead, entryAngle, aeroTargetAngle, aeroRetroFactor, aeroBestFactor));
                        }
                    }
                    prepareAtmosphereEntry = entryPreparationStarted && predictedEntryDirection.sqrMagnitude > .001f;
                    if (prepareAtmosphereEntry)
                    {
                        float angle = Vector2.SignedAngle(combined, predictedEntryDirection);
                        if (Math.Abs(angle) < 5f && Math.Abs(rocket.rb2d.angularVelocity) < 2.5f)
                            entryStableSeconds += Time.fixedDeltaTime;
                        else entryStableSeconds = 0f;
                        if (entryStableSeconds >= .5f)
                        {
                            entryPreparationComplete = true;
                            entryPreparationStarted = false;
                            prepareAtmosphereEntry = false;
                            if (aeroHighDragValid) { aeroHighDragPermitted = true; aeroHighDragEngaged = true; }
                            Debug.Log("[RecoveryPilot 0.14.9] Entry pre-alignment complete; releasing steering");
                        }
                    }
                }
                if (!prepareVacuumBurn && !prepareAtmosphereEntry)
                {
                    SetThrottle(0f);
                    rocket.arrowkeys.turnAxis.Value = 0f;
                    rocket.arrowkeys.horizontalAxis.Value = Vector2.zero;
                    status = vacuumSecondCoast ? string.Format("Second coast: terrain clearance {0:F0} m\nDown {1:F1} m/s; awaiting burn",vacuumTerrainClearance,downSpeed) : string.Format("Space coast  Alt {0:F0} m  Down {1:F1}\nBurn height {2:F0} m", height, downSpeed, vacuumBurnHeight);
                    return;
                }
            }
            LimitControlTimewarp();

            Vector2 up = new Vector2((float)radial.x, (float)radial.y);
            if (combined.sqrMagnitude < 0.001f) { Stop("Stopped: unknown thrust direction"); return; }
            Vector2 tangent = new Vector2(-up.y, up.x);
            float signedHorizontal = Vector2.Dot(tangentVelocity, tangent);
            Vector2 desiredForce = up;
            float throttle = 0f;
            string phase = "Free fall";
            double netVerticalAcceleration = Math.Max(0.01, engineAcceleration - gravity);
            double verticalStopDistance = Math.Max(0, downSpeed * downSpeed - LandingSpeed * LandingSpeed) / (2.0 * netVerticalAcceleration);
            double combinedBurnTime = CombinedBurnTime(horizontal, downSpeed, gravity, engineAcceleration);
            Vector2 aeroRetro=new Vector2((float)-velocity.x,(float)-velocity.y);
            bool canAssessAero=earthWithAir && enteredAtmosphere && vertical<0 && !terminalBurnStarted && velocity.magnitude>200 && height>1500 && sampledDensity>1e-9 && aeroRetro.sqrMagnitude>.001f;
            if(canAssessAero && !aeroPreparingRetro && Time.unscaledTime>=nextAeroCandidate)
            {
                nextAeroCandidate=Time.unscaledTime+1f;
                try
                {
                    UpdateAeroCandidates(velocity,combined);
                    double angle=Vector2.SignedAngle(combined,aeroRetro);
                    double turnDelay=AeroAttitudePlan.TurnDelay(angle,rocket.rb2d.angularVelocity);
                    double fallbackFactor=AeroAttitudePlan.ConservativeFactor(sampledDragFactor,aeroRetroFactor);
                    double ignored;
                    var forecast=PredictLanding(position,velocity,target.Radius+terrain+bottomOffset+landingReserve,engineAcceleration,fallbackFactor,turnDelay+20,out ignored);
                    bool uncertainSafe = forecast == LandingPrediction.Outcome.Uncertain &&
                        AeroAttitudePlan.UncertainCoastSafe(height, downSpeed, verticalStopDistance, turnDelay, landingReserve);
                    aeroHighDragPermitted=aeroHighDragValid && (forecast==LandingPrediction.Outcome.Viable || uncertainSafe);
                    aeroStatus=aeroRetroFactor<=1e-9 ? "Aero comparison unavailable; retrograde fallback" :
                        string.Format("Aero now/retro {0:F2}  best/retro {1:F2}\nCandidate {2:+0;-0;0} deg  {3} ({4})",sampledDragFactor/aeroRetroFactor,aeroBestFactor/aeroRetroFactor,aeroTargetAngle,aeroHighDragPermitted ? "enabled" : "standby",forecast);
                    if(aeroHighDragEngaged && !aeroHighDragPermitted) { aeroPreparingRetro=true; aeroHighDragPermitted=false; }
                }
                catch(Exception ex)
                {
                    aeroHighDragPermitted=false; aeroHighDragValid=false;
                    aeroStatus="Aero comparison unavailable; retrograde fallback";
                    if(aeroHighDragEngaged) aeroPreparingRetro=true;
                    Debug.LogWarning("[RecoveryPilot] Aero candidate unavailable: "+ex.Message);
                }
            }
            if(aeroHighDragEngaged && !canAssessAero &&
                (terminalBurnStarted || vertical>=0 || velocity.magnitude<=200 || height<=1500))
            { aeroPreparingRetro=true; aeroHighDragPermitted=false; }
            {
                // Terminal vertical braking takes priority if the remaining
                // distance is exhausted, even when a residual lateral speed remains.
                double dragFactor = sampledDragFactor;
                if (earthWithAir && !terminalBurnStarted && vertical < 0)
                {
                    Vector2 retro = new Vector2((float)-velocity.x, (float)-velocity.y);
                    double alignmentError = retro.sqrMagnitude > 0.01f ? Math.Abs(Vector2.SignedAngle(combined, retro)) : 0;
                    double alignmentDelay = aeroHighDragEngaged || aeroPreparingRetro
                        ? AeroAttitudePlan.TurnDelay(alignmentError,rocket.rb2d.angularVelocity)
                        : Math.Min(2.0, Math.Max(0, alignmentError - 12.0) / 45.0);
                    double predictionDrag = (aeroHighDragEngaged || aeroPreparingRetro) && aeroRetroFactor>0
                        ? AeroAttitudePlan.ConservativeFactor(dragFactor,aeroRetroFactor) : dragFactor;
                    double ignoredHeight;
                    var prediction = PredictLanding(position, velocity, target.Radius + terrain + bottomOffset + landingReserve,
                        engineAcceleration, predictionDrag, 0.75 + alignmentDelay, out ignoredHeight);
                    lastPrediction=prediction.ToString(); lastPredictedFinishHeight=ignoredHeight;
                    if (prediction == LandingPrediction.Outcome.Viable && Time.unscaledTime >= nextCandidateCheck)
                    {
                        nextCandidateCheck = Time.unscaledTime + 1f;
                        double ignoredCandidate;
                        var later = PredictLanding(position, velocity, target.Radius + terrain + bottomOffset + landingReserve,
                            engineAcceleration, predictionDrag, 5.0 + alignmentDelay, out ignoredCandidate);
                        predictionWindow = later == LandingPrediction.Outcome.Viable ? "5 s coast predicted safe" :
                            later == LandingPrediction.Outcome.TooLate ? "Burn window within ~5 s" : "Prediction uncertain";
                    }
                    // When prediction cannot resolve the trajectory, retain a
                    // conservative engine-only emergency threshold.
                    double emergencyDistance = 20.0 + downSpeed * 0.8 + verticalStopDistance * 1.1 + landingReserve;
                    double precisionHeight = TouchdownIgnition.BurnHeight(downSpeed, horizontal, LandingSpeed, gravity, engineAcceleration)
                        + landingReserve + downSpeed * (0.75 + alignmentDelay) + 10.0;
                    bool precisionIgnition = velocity.magnitude <= 300 && height <= precisionHeight;
                    if (prediction == LandingPrediction.Outcome.TooLate ||
                        (prediction == LandingPrediction.Outcome.Uncertain && height <= emergencyDistance) || precisionIgnition)
                    {
                        BeginTerminal(precisionIgnition ? "precision ignition" : "atmospheric prediction "+prediction);
                        Debug.Log(string.Format("[RecoveryPilot 0.14.9] Predicted burn: height={0:F1} down={1:F1} horizontal={2:F1} outcome={3} precisionHeight={4:F1}", height, downSpeed, horizontal, prediction, precisionHeight));
                    }
                }

                if (terminalBurnStarted)
                {
                    phase = horizontal > HorizontalDoneSpeed ? "Final braking with lateral correction" : "Final vertical braking";
                    double remaining = Math.Max(0.3, combinedBurnTime);
                    double requiredUp = Math.Max(0.01, gravity + (downSpeed - LandingSpeed) / remaining);
                    double requiredSide = -signedHorizontal / remaining;
                    Vector2 requiredForce = up * (float)requiredUp + tangent * (float)requiredSide;
                    if (requiredForce.sqrMagnitude > 0.0001f) desiredForce = requiredForce.normalized;
                    if (downSpeed > LandingSpeed + 3.0 || horizontal > 2.0)
                        throttle = 1f;
                    else
                    {
                        requiredUp = Math.Max(0.0, gravity + (downSpeed - LandingSpeed) * 1.2);
                        requiredSide = -signedHorizontal * 0.8;
                        requiredForce = up * (float)requiredUp + tangent * (float)requiredSide;
                        if (requiredForce.sqrMagnitude > 0.0001f) desiredForce = requiredForce.normalized;
                        throttle = (float)Math.Min(1.0, requiredForce.magnitude / engineAcceleration);
                    }

                    // Use the same forward model as the ignition decision. If
                    // full thrust would finish far above the surface, trim the
                    // demand while retaining the original two-axis guidance.
                    if (earthWithAir && !terminalTrimActive && height > 80)
                    {
                        double finishHeight;
                        var fullBurn = PredictLanding(position, velocity, target.Radius + terrain + bottomOffset,
                            engineAcceleration, dragFactor, 0, out finishHeight);
                        if (fullBurn == LandingPrediction.Outcome.Viable && finishHeight > Math.Max(80, height * 0.10) && finishHeight < height)
                        {
                            terminalTrimActive = true;
                            Debug.Log(string.Format("[RecoveryPilot 0.14.9] Continuous throttle bridge: height={0:F1} fullStop={1:F1} calibrated={2}", height, finishHeight, aeroCalibration.Ready));
                        }
                    }
                    if (terminalTrimActive)
                    {
                        double speed = velocity.magnitude;
                        double aeroMagnitude = dragFactor * aeroCalibration.Scale * sampledDensity * speed * speed;
                        var trim = LandingTrim.Compute(height, downSpeed, signedHorizontal, gravity,
                            engineAcceleration, LandingSpeed, aeroMagnitude, stability.UprightStart, lateralPlanningHeight);
                        Vector2 trimmedForce = up * (float)trim.Up + tangent * (float)trim.Side;
                        if (trimmedForce.sqrMagnitude > 0.0001f) desiredForce = trimmedForce.normalized;
                        throttle = (float)trim.Throttle;
                        phase = "Continuous braking";
                    }
                }
                else
                {
                    phase = coastAfterBraking ? "Coasting after braking" : earthWithAir && enteredAtmosphere ? "Atmospheric coast" : "Coasting";
                    if (earthWithAir && enteredAtmosphere) phase += " (" + predictionWindow + ")";
                    Vector2 retrograde = new Vector2((float)-velocity.x, (float)-velocity.y);
                    if (!earthWithAir && vertical < 0)
                    {
                        double remaining = Math.Max(.3, CombinedBurnTime(horizontal, downSpeed, gravity, engineAcceleration));
                        double side = Vector2.Dot(tangentVelocity, tangent);
                        Vector2 burnDirection = up * (float)Math.Max(.01, gravity + (downSpeed - LandingSpeed) / remaining)
                            - tangent * (float)(side / remaining);
                        if (burnDirection.sqrMagnitude > .0001f) desiredForce = burnDirection.normalized;
                        phase = "Preparing vacuum landing";
                    }
                    else if (earthWithAir && entryPreparationStarted && predictedEntryDirection.sqrMagnitude > .001f)
                    {
                        desiredForce = predictedEntryDirection;
                        phase = "Preparing atmospheric entry";
                    }
                    else if (retrograde.sqrMagnitude > 0.01f)
                    {
                        desiredForce = retrograde.normalized;
                        if(!aeroPreparingRetro && aeroHighDragPermitted)
                        {
                            desiredForce=Quaternion.Euler(0,0,aeroTargetAngle)*desiredForce;
                            aeroHighDragEngaged=true;
                            float targetError=Math.Abs(Vector2.SignedAngle(combined,desiredForce));
                            float spin=Math.Abs(rocket.rb2d.angularVelocity);
                            aeroStallSeconds=targetError>30 && spin<1 ? aeroStallSeconds+Time.fixedDeltaTime : 0;
                            aeroSpinSeconds=spin>30 ? aeroSpinSeconds+Time.fixedDeltaTime : 0;
                            if(aeroStallSeconds>4 || aeroSpinSeconds>2)
                            { aeroPreparingRetro=true; aeroHighDragPermitted=false; desiredForce=retrograde.normalized; phase="Unstable high drag: returning retrograde"; }
                            else phase=string.Format("High drag coast: {0:F0}% of retro",100*aeroBestFactor/Math.Max(1e-9,aeroRetroFactor));
                        }
                        else if(aeroPreparingRetro) phase="Preparing retrograde before burn";
                    }
                }
            }

            if (vertical < 0 && height < stability.UprightStart && desiredForce.sqrMagnitude > .0001f)
            {
                float wantedTilt = Vector2.SignedAngle(up, desiredForce);
                float limit = (float)stability.MaxTiltDegrees;
                float limitedTilt = Mathf.Clamp(wantedTilt, -limit, limit);
                limitedTilt = (float)LandingStability.FinalTilt(limitedTilt, terminalBurnStarted, height, stability.UprightHold, signedHorizontal);
                if (Mathf.Abs(wantedTilt - limitedTilt) > .01f)
                {
                    float previousUp = Mathf.Max(0f, Vector2.Dot(desiredForce.normalized, up));
                    Vector2 uprightForce = Quaternion.Euler(0f, 0f, limitedTilt) * up;
                    float newUp = Mathf.Max(.15f, Vector2.Dot(uprightForce, up));
                    // Maintain the requested vertical thrust component as the
                    // horizontal component is phased out near touchdown.
                    if (throttle > 0f) throttle = Mathf.Clamp01(throttle * previousUp / newUp);
                    desiredForce = uprightForce;
                    if (height <= stability.UprightHold) phase = "Upright for touchdown";
                }
            }
            lastForceSide = combined.sqrMagnitude > .001f ? Vector2.Dot(combined.normalized, tangent) : 0f;
            lastDesiredSide = desiredForce.sqrMagnitude > .001f ? Vector2.Dot(desiredForce.normalized, tangent) : 0f;
            float error = Vector2.SignedAngle(combined, desiredForce);
            // In Rocket.ApplyTorque, positive turnAxis SUBTRACTS angular velocity.
            // The damping term must therefore have the same sign as angular velocity.
            float angularVelocity = rocket.rb2d.angularVelocity;
            float turn = Mathf.Clamp(-error * 0.035f + angularVelocity * 0.04f, -1f, 1f);
            // RcsModule only uses rotation jets at near-full turn input, or
            // when angular velocity is already high. Request a short full input
            // while substantially off target; near the target, retain the
            // proportional command so cockpit torque can settle smoothly.
            if (rocket.arrowkeys.rcs.Value && rocket.partHolder.HasModule<RcsModule>() &&
                (Math.Abs(error) > 4f || Math.Abs(angularVelocity) > 2.5f) && Math.Abs(turn) > 0.02f)
                turn = Mathf.Sign(turn) * Mathf.Max(0.96f, Math.Abs(turn));
            rocket.arrowkeys.turnAxis.Value = turn;

            // A large initial angle is normal; only a sustained uncontrolled spin
            // should disconnect the autopilot.
            if (Math.Abs(rocket.rb2d.angularVelocity) > 90f)
                unstableSeconds += Time.fixedDeltaTime;
            else
                unstableSeconds = 0f;
            if (unstableSeconds > 5f) { Stop("Stopped: uncontrolled spin"); return; }

            if (!earthWithAir && terminalBurnStarted && !vacuumIgnitionConfirmed)
            {
                if (VacuumGuidance.Stable(error, angularVelocity)) vacuumStableSeconds += Time.fixedDeltaTime;
                else vacuumStableSeconds = 0;
                if (vacuumStableSeconds >= .4f) vacuumIgnitionConfirmed = true;
                double urgentHeight = 25.0 + verticalStopDistance * 1.2 + downSpeed * 1.5;
                double upwardAcceleration = engineAcceleration * Vector2.Dot(combined.normalized, up);
                bool emergency = height <= urgentHeight && downSpeed > LandingSpeed + 2 && upwardAcceleration > gravity * 1.05 && Math.Abs(error) < 30 && Math.Abs(angularVelocity) < 10;
                if (!vacuumIgnitionConfirmed && !emergency)
                {
                    SetThrottle(0f);
                    status = string.Format("Stabilizing before vacuum burn\nAngle {0:F0} deg  Spin {1:F1} deg/s", error, angularVelocity);
                    return;
                }
                if (emergency) vacuumIgnitionConfirmed = true;
            }

            if (Math.Abs(error) > 12f || Math.Abs(rocket.rb2d.angularVelocity) > 12f)
            {
                // During an urgent descent, retain thrust only if its current
                // upward component exceeds local gravity; otherwise keep turning.
                Vector2 actualForce = combined.normalized;
                double upwardAcceleration = engineAcceleration * Vector2.Dot(actualForce, up);
                if (terminalBurnStarted && downSpeed > LandingSpeed && upwardAcceleration > gravity * 1.1 && Math.Abs(error) < (earthWithAir ? 60f : 30f) && Math.Abs(angularVelocity) < (earthWithAir ? 30f : 10f))
                {
                    float aligningThrottle = terminalTrimActive && earthWithAir ? throttle :
                        Mathf.Clamp01(Mathf.Max(throttle, (float)((gravity + (downSpeed - LandingSpeed) * 0.5) / upwardAcceleration)));
                    if (!earthWithAir) aligningThrottle = Mathf.Min(aligningThrottle, (float)VacuumGuidance.ThrottleCap(downSpeed, LandingSpeed, gravity, upwardAcceleration));
                    SetThrottle(aligningThrottle);
                    status = string.Format("Braking while aligning {0:F0} deg\nAlt {1:F0} m  Down {2:F1} m/s", error, height, downSpeed);
                }
                else
                {
                    // Avoid rapid restart chatter after ignition only while the
                    // actual thrust still points upward and does not add to a
                    // significant horizontal drift. Dangerous attitudes still cut.
                    bool safeIdle = earthWithAir && terminalTrimActive && terminalBurnStarted &&
                        rocket.throttle.throttleOn.Value && downSpeed > LandingSpeed &&
                        Math.Abs(error) < 60f && Math.Abs(angularVelocity) < 35f &&
                        Vector2.Dot(actualForce, up) > 0.2f &&
                        (horizontal < 2.0 || signedHorizontal * Vector2.Dot(actualForce, tangent) <= 0f);
                    SetThrottle(safeIdle ? Mathf.Max(0.01f, Mathf.Min(0.05f, throttle)) : 0f);
                    status = string.Format(safeIdle ? "Aligning {0:F0} deg; idle thrust\nAlt {1:F0} m  Down {2:F1} m/s" :
                        "Aligning {0:F0} deg; engines paused\nAlt {1:F0} m  Down {2:F1} m/s", error, height, downSpeed);
                }
                return;
            }

            float vacuumThrottleCap = 1f;
            if (!earthWithAir && terminalBurnStarted && height > 30)
            {
                double actualUpAcceleration = engineAcceleration * Vector2.Dot(combined.normalized, up);
                vacuumThrottleCap = (float)VacuumGuidance.ThrottleCap(downSpeed, LandingSpeed, gravity, actualUpAcceleration);
                throttle = Mathf.Min(throttle, vacuumThrottleCap);
            }
            if (throttle > 0f && throttle < 1f)
            {
                float oldThrottle = rocket.throttle.throttlePercent.Value;
                float rate = throttle > oldThrottle ? 8f : terminalTrimActive ? 1f : 5f;
                throttle = Mathf.MoveTowards(oldThrottle, throttle, rate * Time.fixedDeltaTime);
            }
            throttle = Mathf.Min(throttle, vacuumThrottleCap);
            SetThrottle(throttle);
            status = string.Format("{0}  Altitude {1:F0} m\nDown {2:F1}  Horizontal {3:F1} m/s\nThrottle {4:P0}  Upright margin {5:F0} m", phase, height, downSpeed, horizontal, throttle, landingReserve);
        }

        private void SetThrottle(float amount)
        {
            if (rocket == null) return;
            bool wasOn = rocket.throttle.throttleOn.Value;
            bool willBeOn = amount > 0.001f;
            if(active && !wasOn && willBeOn) ignitionCount++;
            rocket.throttle.throttlePercent.Value = amount;
            rocket.throttle.throttleOn.Value = willBeOn;
            if (active && wasOn != willBeOn)
                Debug.Log(string.Format("[HIMA Recovery 1.4.6] Engine throttle {0}: startCount={1} speed={2:F1} vertical={3:F1} terminal={4} secondCoast={5}", willBeOn ? "on" : "off",ignitionCount,lastFlightSpeed,lastFlightVertical,terminalEngagedEver,vacuumSecondCoast));
        }

        private static void LimitControlTimewarp()
        {
            var time = WorldTime.main;
            if (time == null || (time.realtimePhysics.Value && time.timewarpSpeed <= 3.0)) return;
            time.StopTimewarp(false);
            int maxPhysics = Base.worldBase.settings.difficulty.MaxPhysicsTimewarpIndex;
            time.SetTimewarpIndex_ForLoad(Math.Min(2, maxPhysics));
        }

        private void Stop(string reason)
        {
            if (active) Debug.Log("[HIMA Recovery 1.4.6] Stop: " + reason + " | " + status.Replace('\n', ' ') + " | starts="+ignitionCount+" | " + engineStatus.Replace('\n',' '));
            if (active && rocket != null)
            {
                SetThrottle(0);
                rocket.arrowkeys.turnAxis.Value = 0;
                rocket.arrowkeys.horizontalAxis.Value = Vector2.zero;
                rocket.arrowkeys.rcs.Value = previousRcs;
            }
            active = false;
            groundContactFrames = 0;
            terminalBurnStarted = false;
            terminalEngagedEver=false; terminalReascending=false;
            terminalTrimActive = false;
            vacuumIgnitionConfirmed = false;
            vacuumStableSeconds = 0f;
            coastAfterBraking = false;
            vacuumSecondCoast = false; vacuumSecondCoastUsed = false;
            enteredAtmosphere = false;
            rocket = null;
            status = reason;
        }
    }
}
