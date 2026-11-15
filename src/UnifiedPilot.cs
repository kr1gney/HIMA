using System.Collections.Generic;
using HarmonyLib;
using ModLoader;
using ModLoader.Helpers;
using SFS.UI.ModGUI;
using UITools;
using UnityEngine;
using UIType = SFS.UI.ModGUI.Type;

namespace UnifiedPilot
{
    public sealed class Main : Mod
    {
        public override string ModNameID => "HIMA";
        public override string DisplayName => "Harmony Intelligent Mobility Alliance (HIMA)";
        public override string Author => "Codex";
        public override string MinimumGameVersionNecessary => "1.6.00.16";
        public override string ModVersion => "1.4.6";
        public override string Description => "Ascent, recovery, rendezvous, docking and planetary travel.";
        public override Dictionary<string,string> Dependencies { get; } = new Dictionary<string,string> { { "UITools", "1.0" }, { "ANAIS", "1.0" } };
        public override void Early_Load() { new Harmony(ModNameID).PatchAll(); }
        public override void Load()
        {
            var ascent = new GameObject("AscentPilot_Controller"); Object.DontDestroyOnLoad(ascent);
            var recovery = new GameObject("RecoveryPilot_Controller"); Object.DontDestroyOnLoad(recovery);
            var docking = new GameObject("OrbitDock_Controller"); Object.DontDestroyOnLoad(docking);
            var a=ascent.AddComponent<AscentPilot.AscentController>();
            var r=recovery.AddComponent<RecoveryPilot.RecoveryController>();
            docking.AddComponent<RendezvousPilot.Controller>();
            var d=docking.AddComponent<AutoDock.Controller>();
            var n=docking.AddComponent<PlanetNavigation.Controller>();
            var menu=docking.AddComponent<Menu>(); menu.Initialize(a,r,d,n);
        }
    }
    public sealed class Menu : MonoBehaviour
    {
        private static Menu instance;
        private AscentPilot.AscentController ascent;
        private RecoveryPilot.RecoveryController recovery;
        private AutoDock.Controller dock;
        private PlanetNavigation.Controller navigation;
        private GameObject holder;
        private int page;
        private Container[] pages;
        internal void Initialize(AscentPilot.AscentController a, RecoveryPilot.RecoveryController r, AutoDock.Controller d, PlanetNavigation.Controller n) { instance=this; ascent=a; recovery=r; dock=d; navigation=n; }
        internal static bool OtherActive(int which)
        {
            var m=instance;
            return m!=null && ((which!=1 && m.ascent!=null && m.ascent.IsActive) || (which!=2 && m.recovery!=null && m.recovery.IsActive) || (which!=3 && m.dock!=null && m.dock.IsActive) || (which!=4 && m.navigation!=null && m.navigation.IsActive));
        }
        private void Awake() { SceneHelper.OnWorldSceneLoaded += Enter; SceneHelper.OnWorldSceneUnloaded += Exit; }
        private void OnDestroy() { Exit(); SceneHelper.OnWorldSceneLoaded -= Enter; SceneHelper.OnWorldSceneUnloaded -= Exit; if(instance==this) instance=null; }
        private void Enter()
        {
            holder=Builder.CreateHolder(Builder.SceneToAttach.CurrentScene,"UnifiedPilot_Menu");
            var window=UIToolsBuilder.CreateClosableWindow(holder.transform,Builder.GetRandomID(),430,550,30,90,true,true,0.95f,"HIMA",false);
            window.RegisterPermanentSaving("UnifiedPilot.Menu");
            window.CreateLayoutGroup(UIType.Vertical,TextAnchor.UpperCenter,4f);
            pages=new Container[5];
            for(int i=0;i<pages.Length;i++) { pages[i]=Builder.CreateContainer(window); pages[i].Size=new Vector2(410,500); pages[i].CreateLayoutGroup(UIType.Vertical,TextAnchor.UpperCenter,6f); }
            Builder.CreateLabel(pages[0],390,35,0,0,"SELECT A FLIGHT MODE");
            Builder.CreateButton(pages[0],360,38,0,0,()=>SetPage(1),"Ascent / Orbit");
            Builder.CreateButton(pages[0],360,38,0,0,()=>SetPage(2),"Recovery / Landing");
            Builder.CreateButton(pages[0],360,38,0,0,()=>SetPage(3),"Rendezvous / Docking");
            Builder.CreateButton(pages[0],360,38,0,0,()=>SetPage(4),"Planet Navigation");
            for(int i=1;i<pages.Length;i++) Builder.CreateButton(pages[i],360,32,0,0,()=>SetPage(0),"< Back to menu");
            ascent.CreateUI(pages[1]); recovery.CreateFlightUI(pages[2]); dock.CreateUI(pages[3]); navigation.CreateUI(pages[4]);
            SetPage(0);
        }
        private void Exit() { page=0; pages=null; if(holder!=null) Destroy(holder); holder=null; }
        private void SetPage(int selected)
        {
            page=selected;
            if(pages!=null) for(int i=0;i<pages.Length;i++) if(pages[i]!=null) pages[i].Active=i==page;
        }
    }
}
