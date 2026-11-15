using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModLoader;
using ModLoader.Helpers;
using SFS;
using SFS.Input;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.UI.ModGUI;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UITools;
using UnityEngine;
using UIType = SFS.UI.ModGUI.Type;

namespace AutoDock
{


    [HarmonyPatch]
    internal static class PortMarker
    {
        internal const string Key = "AutoDock_Selected";
        private static IEnumerable<MethodBase> TargetMethods() => typeof(PartsLoader).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreatePart" && m.GetParameters().Length > 0 && (m.GetParameters()[0].ParameterType == typeof(PartSave) || m.GetParameters()[0].ParameterType == typeof(VariantRef))).Cast<MethodBase>();
        private static void Prefix(object __0)
        {
            Part template = null;
            if (__0 is PartSave save && Base.partsLoader != null) Base.partsLoader.parts.TryGetValue(save.name, out template);
            else if (__0 is VariantRef variant) template = variant.part;
            Ensure(template);
        }
        internal static void Ensure(Part p)
        {
            if (p == null || p.variablesModule == null || !p.HasModule<DockingPortModule>()) return;
            var vars = p.variablesModule.boolVariables;
            if (!vars.Has(Key)) vars.SetValue(Key, false, (true, true));
        }
        internal static bool Selected(Part p) => p != null && p.variablesModule != null && p.variablesModule.boolVariables.GetValue(Key);
        internal static bool SetSingle(Part chosen, bool toggle)
        {
            if (chosen == null || chosen.Rocket == null || chosen.Rocket.partHolder == null) return false;
            Ensure(chosen);
            bool next = !toggle || !Selected(chosen);
            foreach (Part p in chosen.Rocket.partHolder.parts)
            {
                if (p == null || !p.HasModule<DockingPortModule>()) continue;
                Ensure(p);
                p.variablesModule.boolVariables.SetValue(Key, p == chosen && next);
            }
            SFS.UI.MsgDrawer.main?.Log(next ? "Auto Dock port: ON" : "Auto Dock port: OFF");
            Debug.Log("[AutoDock] " + (next ? "Selected " : "Cleared ") + chosen.name + " on " + chosen.Rocket.rocketName);
            return next;
        }
    }

    [HarmonyPatch(typeof(Part), nameof(Part.DrawPartStats))]
    internal static class PortMenu
    {
        private static void Postfix(Part __instance, Part[] allParts, StatsMenu drawer, PartDrawSettings settings)
        {
            if (!settings.game || __instance == null || !__instance.HasModule<DockingPortModule>()) return;
            PortMarker.Ensure(__instance);
            var v = __instance.variablesModule.boolVariables.GetVariable(PortMarker.Key);
            if (v == null) return;
            drawer.DrawToggle(9865, () => "Auto Dock port", () =>
            {
                PortMarker.SetSingle(__instance, true);
            }, () => v.Value, update => v.onValueChange += update, update => v.onValueChange -= update);
        }
    }

    // Selection mode consumes the click before the original port activation,
    // so selecting a connected port cannot trigger its normal separation action.
    [HarmonyPatch(typeof(Rocket), "ClickPart")]
    internal static class PortClickPatch
    {
        private static bool Prefix(Rocket __instance, TouchPosition position, ref bool __result)
        {
            if (!Controller.SelectingPort) return true;
            Controller.HandlePortClick(__instance, position);
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(RocketManager), nameof(RocketManager.MergeRockets))]
    internal static class DockCompletedPatch
    {
        private static void Postfix(Part part_A, Part part_B) { Controller.NotifyMerge(part_A, part_B); }
    }

    public sealed class Controller : MonoBehaviour
    {
        private enum State { Idle, Armed, Brake, Stage, Align, Final, Magnet, Done }
        private static Controller instance;
        private State state;
        private Rocket ship, targetRocket;
        private MapRocket target;
        private DockingPortModule ownPort, otherPort;
        private Part ownPart, otherPart;
        private bool wasRcs, acquired, inWorld, selectingPort, awaitingRendezvous;
        private float magnetStarted, magnetBest;
        private float pulseClock, lastProgress, missingDirectionSince = -1f, bestDistance = float.PositiveInfinity;
        private bool rcsDirectionMissing;
        private string status = "Select one port on each vessel";
        private GameObject ui;
        private Label telemetry, message, portStatus;
        private SFS.UI.ModGUI.Button toggle, selectButton;
        private const float SearchDistance = 600f;

        private void Awake() { instance = this; SceneHelper.OnWorldSceneLoaded += Enter; SceneHelper.OnWorldSceneUnloaded += Exit; }
        private void OnDestroy() { Exit(); SceneHelper.OnWorldSceneLoaded -= Enter; SceneHelper.OnWorldSceneUnloaded -= Exit; if (instance == this) instance = null; }
        private void Enter()
        {
            inWorld = true; state = State.Idle;
        }
        internal void CreateUI(Container page)
        {
            telemetry = Builder.CreateLabel(page, 400, 65, 0, 0, "Select target in map");
            portStatus = Builder.CreateLabel(page, 400, 70, 0, 0, "Own port: --\nTarget port: --");
            message = Builder.CreateLabel(page, 400, 80, 0, 0, status);
            selectButton = Builder.CreateButton(page, 360, 35, 0, 0, BeginSelect, "Click a port to select");
            toggle = Builder.CreateButton(page, 350, 38, 0, 0, Toggle, "Start rendezvous + dock");
            Builder.CreateButton(page, 350, 34, 0, 0, StartDockOnly, "Dock only (near target)");
            Builder.CreateLabel(page, 400, 45, 0, 0, "Select one port per vessel\nTarget must be selected in map");
        }
        private void Exit() { Disarm("Flight closed"); selectingPort = false; inWorld = false; if (ui != null) Destroy(ui); ui = null; telemetry = null; }
        internal static bool SelectingPort => instance != null && instance.inWorld && instance.selectingPort;
        private void BeginSelect()
        {
            if (acquired) { status = "Stop auto dock before changing ports"; return; }
            selectingPort = !selectingPort;
            status = selectingPort ? "Click a docking port on the current vessel" : "Port selection cancelled";
            SFS.UI.MsgDrawer.main?.Log(status);
        }
        internal static void HandlePortClick(Rocket rocket, TouchPosition position)
        {
            var c = instance;
            if (c == null || rocket == null || rocket.partHolder == null) return;
            PartHit hit;
            float threshold = Mathf.Clamp((float)PlayerController.main.cameraDistance * .03f, 0f, 2f);
            if (!Part_Utility.RaycastParts(rocket.partHolder.GetArray(), position.World(0f), threshold, out hit) || hit?.part == null || !hit.part.HasModule<DockingPortModule>())
            {
                c.status = "Click a docking port; click button again to cancel";
                SFS.UI.MsgDrawer.main?.Log(c.status);
                return;
            }
            bool enabled = PortMarker.SetSingle(hit.part, true);
            c.selectingPort = false;
            c.status = enabled ? "Port selected on current vessel" : "Port selection cleared";
        }
        private static string DescribePorts(Rocket r)
        {
            if (r == null || r.partHolder == null) return "--";
            var ports = r.partHolder.parts.Where(p => p != null && p.HasModule<DockingPortModule>()).ToArray();
            if (ports.Length == 0) return "none found";
            var marked = ports.Where(PortMarker.Selected).ToArray();
            if (marked.Length == 0) return "not selected (" + ports.Length + " available)";
            if (marked.Length > 1) return marked.Length + " selected: choose one";
            var p = marked[0]; Vector2 pos = p.Position;
            return string.Format("{0}/{1}  pos {2:F1}, {3:F1}", Array.IndexOf(ports, p) + 1, ports.Length, pos.x, pos.y);
        }
        internal bool IsActive => (state != State.Idle && state != State.Done) || RendezvousActive();
        private void Toggle()
        {
            if (state != State.Idle && state != State.Done) { RendezvousPilot.Controller.Instance?.Stop("Stopped by user"); Disarm("Stopped by user"); return; }
            if (UnifiedPilot.Menu.OtherActive(3)) { status = "Stop the other autopilot first"; return; }
            var r = PlayerController.main?.player?.Value as Rocket;
            var t = Map.navigation?.target as MapRocket;
            if (r == null || t == null || t.rocket == null || t.rocket == r) { status = "Select another rocket as map target"; return; }
            if (r.location.planet.Value != t.Location.planet) { status = "Target must orbit the same body"; return; }
            double distance = (t.Location.position - r.location.position.Value).magnitude;
            double speed = (t.Location.velocity - r.location.velocity.Value).magnitude;
            var rendezvous = RendezvousPilot.Controller.Instance;
            bool needRendezvous = distance > SearchDistance || speed > 5;
            if (needRendezvous)
            {
                if (rendezvous == null) { status = "Rendezvous controller unavailable"; return; }
                rendezvous.StartPilot();
                if (!rendezvous.Active) { status = rendezvous.Status; return; }
            }
            Arm(r, t, needRendezvous);
        }
        private void StartDockOnly()
        {
            if (state != State.Idle && state != State.Done) { status = "Abort current operation first"; return; }
            if (UnifiedPilot.Menu.OtherActive(3)) { status = "Stop the other autopilot first"; return; }
            var r = PlayerController.main?.player?.Value as Rocket;
            var t = Map.navigation?.target as MapRocket;
            if (r == null || t == null || t.rocket == null || t.rocket == r) { status = "Select another rocket as map target"; return; }
            if (r.location.planet.Value != t.Location.planet) { status = "Target must orbit the same body"; return; }
            Arm(r, t, false);
        }
        private void Arm(Rocket r, MapRocket t, bool waitForRendezvous)
        {
            selectingPort = false; awaitingRendezvous = waitForRendezvous;
            ship = r; target = t; targetRocket = t.rocket; state = State.Armed; acquired = false;
            status = waitForRendezvous ? "Rendezvous running; docking armed" : "Docking armed; checking ports";
        }
        private void Disarm(string reason)
        {
            if (acquired && ship != null && ship.arrowkeys != null)
            {
                ship.arrowkeys.turnAxis.Value = 0f; ship.arrowkeys.horizontalAxis.Value = Vector2.zero; ship.arrowkeys.verticalAxis.Value = Vector2.zero; ship.arrowkeys.rcs.Value = wasRcs;
            }
            acquired = false; awaitingRendezvous = false; ship = null; target = null; targetRocket = null; ownPort = null; otherPort = null; ownPart = null; otherPart = null;
            state = State.Idle; status = reason;
        }
        internal static void NotifyMerge(Part a, Part b)
        {
            var c = instance;
            if (c == null || !c.acquired || c.ownPart == null || c.otherPart == null) return;
            if ((a == c.ownPart && b == c.otherPart) || (a == c.otherPart && b == c.ownPart))
            { c.Disarm("Docked successfully"); c.state = State.Done; }
        }
        private static bool RendezvousActive()
        {
            return RendezvousPilot.Controller.Instance != null && RendezvousPilot.Controller.Instance.Active;
        }
        private void Update()
        {
            if (!inWorld || telemetry == null) return;
            Rocket r = PlayerController.main?.player?.Value as Rocket; var t = Map.navigation?.target as MapRocket;
            if (r != null && t != null && t.rocket != null && r.location.planet.Value == t.Location.planet)
            {
                double d = (t.Location.position - r.location.position.Value).magnitude;
                double v = (t.Location.velocity - r.location.velocity.Value).magnitude;
                telemetry.Text = string.Format("Target: {0}\nDistance: {1:F0} m   Relative speed: {2:F2} m/s", t.Select_DisplayName, d, v);
            }
            else telemetry.Text = "Select another rocket as map target";
            portStatus.Text = "Own: " + DescribePorts(r) + "\nTarget: " + DescribePorts(t == null ? null : t.rocket);
            message.Text = awaitingRendezvous && RendezvousActive() ? "Rendezvous: " + RendezvousPilot.Controller.Instance.Status : status;
            toggle.Text = state == State.Idle || state == State.Done ? "Start rendezvous + dock" : "Abort operation";
            selectButton.Text = selectingPort ? "Cancel port selection" : "Click a port to select";
        }
        private void FixedUpdate()
        {
            if (!inWorld || state == State.Idle || state == State.Done) return;
            try
            {
                rcsDirectionMissing = false;
                Step();
                if (acquired && rcsDirectionMissing)
                {
                    if (missingDirectionSince < 0f) missingDirectionSince = Time.time;
                    status = "RCS cannot thrust in the required direction";
                    if (Time.time - missingDirectionSince > 8f) Disarm("RCS missing required thrust direction");
                }
                else missingDirectionSince = -1f;
            }
            catch (Exception e) { Debug.LogException(e); Disarm("Stopped: " + e.Message); }
        }
        private static List<DockingPortModule> Marked(Rocket rocket)
        {
            var result = new List<DockingPortModule>();
            if (rocket == null || rocket.partHolder == null) return result;
            foreach (var part in rocket.partHolder.parts)
                if (PortMarker.Selected(part)) foreach (var port in part.GetModules<DockingPortModule>()) if (port != null) result.Add(port);
            return result;
        }
        private void Step()
        {
            if (ship == null || target == null || targetRocket == null || PlayerController.main?.player?.Value != ship || Map.navigation.target != target || target.rocket != targetRocket) { Disarm("Target or controlled vessel changed"); return; }
            if (ship.location.planet.Value != targetRocket.location.planet.Value) { Disarm("Target changed sphere of influence"); return; }
            if (state == State.Armed)
            {
                if (RendezvousActive()) { status = "Waiting for rendezvous"; return; }
                if (awaitingRendezvous)
                {
                    var rendezvous = RendezvousPilot.Controller.Instance;
                    if (rendezvous == null || !rendezvous.Complete) { Disarm("Rendezvous stopped: " + (rendezvous == null ? "controller unavailable" : rendezvous.Status)); return; }
                    awaitingRendezvous = false;
                }
                double dist = (target.Location.position - ship.location.position.Value).magnitude;
                if (dist > SearchDistance) { status = string.Format("Armed: target {0:F0} m away (range {1:F0} m)", dist, SearchDistance); return; }
                if (!WorldTime.main.realtimePhysics.Value) { WorldTime.main.StopTimewarp(false); status = "Exiting high time warp for docking"; return; }
                if (!ship.rb2d.simulated || !targetRocket.rb2d.simulated) { status = "Waiting for both vessels in normal physics"; return; }
                if (ship.throttle.throttleOn.Value) { status = "Turn off main engines before docking"; return; }
                var mine = Marked(ship); var theirs = Marked(targetRocket);
                if (mine.Count != 1 || theirs.Count != 1) { status = string.Format("Enable exactly one port on each vessel (own {0}, target {1})", mine.Count, theirs.Count); return; }
                if (!mine[0].isDockable.Value || !theirs[0].isDockable.Value) { status = "Selected port occupied or on cooldown"; return; }
                if (!HasFuelledRcs(ship)) { status = "A fueled RCS system is required"; return; }
                ownPort = mine[0]; otherPort = theirs[0]; ownPart = ownPort.GetComponentInParent<Part>(); otherPart = otherPort.GetComponentInParent<Part>();
                if (ownPart == null || otherPart == null) { status = "Cannot resolve selected port geometry"; return; }
                wasRcs = ship.arrowkeys.rcs.Value; ship.arrowkeys.rcs.Value = true; acquired = true; state = State.Stage; lastProgress = Time.time; bestDistance = float.PositiveInfinity; pulseClock = 0;
                status = "Approaching target port";
                return;
            }
            if (!WorldTime.main.realtimePhysics.Value) { ClearInputs(); WorldTime.main.StopTimewarp(false); status = "Exiting high time warp for docking"; return; }
            if (!ship.rb2d.simulated || !targetRocket.rb2d.simulated) { ClearInputs(); status = "Waiting for both vessels in physics range"; return; }
            if (RendezvousActive()) { Disarm("Rendezvous Pilot active; avoid conflicting controls"); return; }
            if (ownPort == null || otherPort == null || ownPart == null || otherPart == null || !PortMarker.Selected(ownPart) || !PortMarker.Selected(otherPart)) { Disarm("Selected port changed or was removed"); return; }
            if (!ownPort.isDockable.Value || !otherPort.isDockable.Value) { Disarm("Selected port became occupied or unavailable"); return; }
            if (!ship.arrowkeys.rcs.Value || !HasFuelledRcs(ship)) { Disarm("RCS switched off or out of fuel"); return; }
            if (ship.throttle.throttleOn.Value) { Disarm("Main engine activated"); return; }
            Vector2 a = ownPort.transform.position, b = otherPort.transform.position;
            Vector2 normalA = ownPort.transform.up, normalB = otherPort.transform.up;
            Vector2 va = ship.rb2d.GetPointVelocity(a), vb = targetRocket.rb2d.GetPointVelocity(b);
            Vector2 rel = vb - va;
            float distance = Vector2.Distance(a, b);
            if (distance > SearchDistance * 1.5f) { Disarm("Target left docking range"); return; }
            float angle = Vector2.SignedAngle(normalA, -normalB);
            float relativeSpin = ship.rb2d.angularVelocity - targetRocket.rb2d.angularVelocity;
            float turn = Mathf.Clamp(-angle * .035f + relativeSpin * .05f, -1, 1);
            if ((Mathf.Abs(angle) > 3f || Mathf.Abs(relativeSpin) > 1.5f) && Mathf.Abs(turn) > .02f) turn = Mathf.Sign(turn) * Mathf.Max(.96f, Mathf.Abs(turn));
            ship.arrowkeys.turnAxis.Value = turn;
            if (Mathf.Abs(targetRocket.rb2d.angularVelocity) > 3f) { Translate(rel, .2f); status = "Target rotation too fast; stabilizing"; return; }
            if (state == State.Brake)
            {
                if (rel.magnitude > .3f) ship.arrowkeys.turnAxis.Value = 0f;
                Translate(rel, .35f);
                status = string.Format("Matching relative speed: {0:F2} m/s", rel.magnitude);
                if (rel.magnitude < .2f) { state = State.Stage; lastProgress = Time.time; bestDistance = float.PositiveInfinity; }
                return;
            }
            float stageDistance = Mathf.Clamp(OwnExtent(a) + 12f, 18f, 120f);
            Vector2 stage = b + normalB * stageDistance;
            float axial = Vector2.Dot(a - b, normalB);
            float lateral = Vector2.Dot(a - b, new Vector2(-normalB.y, normalB.x));
            if (state == State.Stage)
            {
                Vector2 gap = stage - a;
                Vector2 targetVel = targetRocket.rb2d.GetPointVelocity(stage);
                Vector2 relToStage = targetVel - va;
                if (Blocked(a, stage)) { Translate(rel, .2f); status = "Path to staging point blocked; reposition manually"; return; }
                float brakeAccel = gap.sqrMagnitude > .01f ? EstimateRcsAlong(ship, -gap.normalized) : 0;
                if (gap.magnitude > 3f && brakeAccel < .001f) { ClearInputs(); status = "RCS cannot brake toward the staging point"; return; }
                float safeSpeed = Mathf.Sqrt(Mathf.Max(0f, 2f * brakeAccel * Mathf.Max(0f, gap.magnitude - 2f) * .45f));
                float speed = Mathf.Min(12f, Mathf.Min(Mathf.Max(2f, gap.magnitude * .3f), safeSpeed));
                Translate(relToStage + (gap.sqrMagnitude > .01f ? gap.normalized * speed : Vector2.zero), .3f);
                status = string.Format("Moving to port front: {0:F1} m\nAngle {1:F1} deg, rel {2:F2} m/s", gap.magnitude, angle, rel.magnitude);
                float finalEntryDistance = Mathf.Clamp(10f + 4f / (2f * Mathf.Max(brakeAccel, .001f)), 10f, 150f);
                if (gap.magnitude < finalEntryDistance && axial > 8f && Mathf.Abs(lateral) < .5f && Mathf.Abs(angle) < 5f && Mathf.Abs(relativeSpin) < 2f)
                { state = State.Final; lastProgress = Time.time; bestDistance = distance; }
                else if (gap.magnitude < 2f && relToStage.magnitude < .3f) { state = State.Align; lastProgress = Time.time; bestDistance = float.PositiveInfinity; }
                return;
            }
            if (Mathf.Abs(angle) > 5f || Mathf.Abs(relativeSpin) > 2f) { Translate(rel, .2f); status = string.Format("Aligning port attitude: {0:F1} deg", angle); return; }
            if (state == State.Align)
            {
                Vector2 gap = stage - a;
                Vector2 relToStage = targetRocket.rb2d.GetPointVelocity(stage) - va;
                Translate(relToStage + Vector2.ClampMagnitude(gap * .10f, .5f), .2f);
                status = string.Format("Aligning: lateral {0:F2} m, angle {1:F1} deg", lateral, angle);
                if (Mathf.Abs(lateral) < .5f && Mathf.Abs(axial - stageDistance) < 2f && rel.magnitude < .3f) { state = State.Final; lastProgress = Time.time; bestDistance = distance; }
                return;
            }
            if (axial < -1f || Mathf.Abs(lateral) > Math.Max(3f, axial * .3f)) { state = State.Stage; status = "Repositioning in front of target port"; return; }
            Vector2 tangent = new Vector2(-normalB.y, normalB.x);
            float closingNow = Vector2.Dot(rel, normalB);
            float sidewaysNow = Mathf.Abs(Vector2.Dot(rel, tangent));
            if (state == State.Magnet)
            {
                if (closingNow > 3.8f || rel.magnitude > 4.2f)
                {
                    state = State.Final;
                    Translate(rel, .2f);
                    status = "Magnet approach too fast: braking";
                    return;
                }
                if (Mathf.Abs(lateral) > .8f || sidewaysNow > .5f || closingNow < -.1f)
                {
                    state = State.Final;
                    status = "Approach drift: correcting alignment";
                    return;
                }
                ClearInputs();
                if (distance < magnetBest - .1f) { magnetBest = distance; magnetStarted = Time.time; }
                if (Time.time - magnetStarted > 12f) { state = State.Final; status = "Coast stalled; nudging toward port"; return; }
                status = string.Format("Coasting / magnetic capture: {0:F1} m\nClosing {1:F2} m/s (RCS off)", distance, closingNow);
                return;
            }
            if (distance < 10f && axial > 0 && Mathf.Abs(lateral) < .5f && sidewaysNow < .25f && closingNow >= 2f && closingNow <= 3.4f)
            {
                state = State.Magnet; magnetBest = distance; magnetStarted = Time.time; ClearInputs();
                status = "Coasting into magnetic capture";
                return;
            }
            float closing = 2.5f;
            if (Mathf.Abs(lateral) > .6f) closing = 0f;
            Vector2 desired = -normalB * closing - tangent * Mathf.Clamp(lateral * .15f, -.25f, .25f);
            Translate(rel + desired, .15f);
            status = string.Format("Final approach: {0:F1} m, lateral {1:F2} m\nAngle {2:F1} deg, rel {3:F2} m/s", axial, lateral, angle, rel.magnitude);
            if (distance < bestDistance - .25f) { bestDistance = distance; lastProgress = Time.time; }
            if (Time.time - lastProgress > 90f) { Disarm("No docking progress; check RCS and ports"); return; }
        }
        private float OwnExtent(Vector2 port)
        {
            float extent = 0f;
            foreach (var col in ship.partHolder.GetComponentsInChildren<Collider2D>())
                if (col != null && col.enabled && !col.isTrigger) extent = Mathf.Max(extent, Vector2.Distance(port, col.bounds.center) + col.bounds.extents.magnitude);
            return extent;
        }
        private bool Blocked(Vector2 from, Vector2 to)
        {
            Vector2 delta = to - from; if (delta.magnitude < 1f) return false;
            foreach (var hit in Physics2D.CircleCastAll(from, .5f, delta.normalized, delta.magnitude))
                if (hit.collider != null && !hit.collider.isTrigger && hit.collider.transform.IsChildOf(targetRocket.transform)) return true;
            return false;
        }
        private static bool HasFuelledRcs(Rocket r) => r.partHolder.GetModules<RcsModule>().Any(x => x != null && x.source != null && x.source.CanFlow(new MsgNone()) && x.thrusters != null && x.thrusters.Count > 0);
        public static double EstimateRcsBraking(Rocket rocket, Double2 relativeVelocity)
        {
            if (rocket == null || rocket.partHolder == null || rocket.rb2d == null || !HasFuelledRcs(rocket)) return 0;
            Vector2 desired = new Vector2((float)relativeVelocity.x, (float)relativeVelocity.y);
            if (desired.sqrMagnitude < .01f) return .05;
            return EstimateRcsAlong(rocket, desired.normalized);
        }
        private static float EstimateRcsAlong(Rocket rocket, Vector2 wanted)
        {
            if (rocket == null || rocket.partHolder == null || rocket.rb2d == null || wanted.sqrMagnitude < .0001f) return 0;
            wanted.Normalize(); float best = 0;
            var modules = rocket.partHolder.GetModules<RcsModule>();
            foreach (var probe in modules)
            {
                if (probe == null || probe.thrusters == null) continue;
                foreach (var nozzle in probe.thrusters)
                {
                    Vector2 command = probe.transform.TransformVectorUnscaled(nozzle.thrustNormal).normalized;
                    if (command == Vector2.zero) continue;
                    Vector2 force = Vector2.zero;
                    foreach (var m in modules)
                    {
                        if (m == null || m.source == null || !m.source.CanFlow(new MsgNone()) || m.thrusters == null) continue;
                        Vector2 sum = Vector2.zero; int count = 0;
                        foreach (var thruster in m.thrusters)
                        {
                            Vector2 dir = m.transform.TransformVectorUnscaled(thruster.thrustNormal);
                            if (dir != Vector2.zero && Vector2.Angle(dir, command) <= m.directionAngleThreshold) { sum += dir; count++; }
                        }
                        force += sum * (m.thrust * count * 9.8f);
                    }
                    if (force.sqrMagnitude > .00001f && Vector2.Dot(force.normalized, wanted) > .55f)
                        best = Mathf.Max(best, Vector2.Dot(force, wanted) / Mathf.Max(rocket.rb2d.mass, .01f));
                }
            }
            return best;
        }
        private void ClearInputs() { if (ship != null) { ship.arrowkeys.horizontalAxis.Value = Vector2.zero; ship.arrowkeys.verticalAxis.Value = Vector2.zero; ship.arrowkeys.turnAxis.Value = 0; } }
        // RCS selects thrusters by angle and applies full thrust. Pick an available
        // net thrust vector and pulse it; do not treat axis magnitude as throttle.
        private void Translate(Vector2 velocityError, float responseSeconds)
        {
            ship.arrowkeys.verticalAxis.Value = Vector2.zero;
            if (velocityError.magnitude < .035f) { ship.arrowkeys.horizontalAxis.Value = Vector2.zero; return; }
            Vector2 wanted = velocityError.normalized, bestDir = Vector2.zero; float bestScore = 0, bestAccel = 0;
            foreach (var probeModule in ship.partHolder.GetModules<RcsModule>())
            {
                if (probeModule == null || probeModule.thrusters == null) continue;
                foreach (var probe in probeModule.thrusters)
                {
                    Vector2 candidate = probeModule.transform.TransformVectorUnscaled(probe.thrustNormal).normalized;
                    if (candidate == Vector2.zero) continue;
                    Vector2 force = Vector2.zero;
                    foreach (var m in ship.partHolder.GetModules<RcsModule>())
                    {
                        if (m == null || m.source == null || !m.source.CanFlow(new MsgNone()) || m.thrusters == null) continue;
                        Vector2 sum = Vector2.zero; int count = 0;
                        foreach (var thr in m.thrusters)
                        {
                            Vector2 dir = m.transform.TransformVectorUnscaled(thr.thrustNormal);
                            if (dir != Vector2.zero && Vector2.Angle(dir, candidate) <= m.directionAngleThreshold) { sum += dir; count++; }
                        }
                        force += sum * (m.thrust * count * 9.8f);
                    }
                    if (force.sqrMagnitude < .00001f) continue;
                    float alignment = Vector2.Dot(force.normalized, wanted);
                    float score = alignment - .45f * Mathf.Abs(Vector2.Dot(force.normalized, new Vector2(-wanted.y, wanted.x)));
                    if (score > bestScore && alignment > .55f) { bestScore = score; bestDir = candidate; bestAccel = force.magnitude / Mathf.Max(ship.rb2d.mass, .01f); }
                }
            }
            if (bestDir == Vector2.zero || bestAccel < .0001f) { ship.arrowkeys.horizontalAxis.Value = Vector2.zero; rcsDirectionMissing = true; return; }
            float desiredAccel = velocityError.magnitude / Mathf.Max(responseSeconds, .1f);
            float duty = Mathf.Clamp01(desiredAccel / bestAccel);
            pulseClock = (pulseClock + Time.fixedDeltaTime) % .2f;
            ship.arrowkeys.horizontalAxis.Value = pulseClock < duty * .2f ? bestDir : Vector2.zero;
        }
    }
}
