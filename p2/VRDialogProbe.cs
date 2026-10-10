// VRDialogUIProbe.cs
// Diagnoses (and by default fixes) UI canvases that the VR UI mirror can't capture.
// Self-bootstraps as its own BepInEx plugin so no edits to other files are needed.

using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace PathologicVR
{
    [BepInPlugin("com.andrew.pathologicvr.dialoguiprobe", "Pathologic VR Dialog UI Probe", "1.0.0")]
    public class VRDialogUIProbePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            VRDialogUIProbe.Ensure();
        }
    }

    public class VRDialogUIProbe : MonoBehaviour
    {
        // Set false to run as a pure diagnostic (dump only, change nothing).
        public static bool ConformCanvases = true;

        // Seconds between scans. Dump is only logged when something changed.
        public static float Interval = 1.0f;

        static VRDialogUIProbe _inst;
        float _next;
        string _lastDump = "";

        public static void Ensure()
        {
            if (_inst != null) return;
            var go = new GameObject("VRDialogUIProbe");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _inst = go.AddComponent<VRDialogUIProbe>();
            LogInfo("armed (ConformCanvases=" + ConformCanvases + ")");
        }

        static void LogInfo(string msg)
        {
            if (VRDialogUIProbePlugin.Log != null) VRDialogUIProbePlugin.Log.LogInfo("[VRDialogUIProbe] " + msg);
            else Debug.Log("[VRDialogUIProbe] " + msg);
        }

        void LateUpdate()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;

            Canvas[] all = Resources.FindObjectsOfTypeAll<Canvas>();

            // --- Majority vote for the real UI camera (same rule the mirror uses) ---
            var votes = new Dictionary<Camera, int>();
            foreach (var c in all)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera != null)
                {
                    int n;
                    votes.TryGetValue(c.worldCamera, out n);
                    votes[c.worldCamera] = n + 1;
                }
            }
            Camera uiCam = null;
            int best = -1;
            foreach (var kv in votes)
                if (kv.Value > best) { best = kv.Value; uiCam = kv.Key; }

            // --- Dump every active root canvas, flag the ones the mirror can't see ---
            var sb = new StringBuilder();
            int conformed = 0;

            foreach (var c in all)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !c.isRootCanvas) continue;

                int texts = 0;
                foreach (var comp in c.GetComponentsInChildren<Component>(false))
                    if (comp != null && comp.GetType().Name.Contains("Text")) texts++;

                bool unbound  = c.renderMode == RenderMode.ScreenSpaceOverlay
                             || (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == null);
                bool otherCam = c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera != null && c.worldCamera != uiCam;
                bool world    = c.renderMode == RenderMode.WorldSpace;

                string flag = unbound ? "!!UNBOUND" : otherCam ? "!!OTHERCAM" : world ? "!!WORLD" : "ok";

                sb.AppendFormat("\n  {0,-10} '{1}' mode={2} cam={3} camDepth={4} layer={5} sort={6} texts={7} scene={8}",
                    flag,
                    c.name,
                    c.renderMode,
                    c.worldCamera ? c.worldCamera.name : "null",
                    c.worldCamera ? c.worldCamera.depth.ToString("0.#") : "-",
                    LayerMask.LayerToName(c.gameObject.layer),
                    c.sortingOrder,
                    texts,
                    c.gameObject.scene.name);

                if (ConformCanvases && unbound && uiCam != null)
                {
                    c.renderMode    = RenderMode.ScreenSpaceCamera;
                    c.worldCamera   = uiCam;
                    c.planeDistance = 0f;                 // matches UIService.Initialise
                    conformed++;
                    sb.Append("  -> CONFORMED to '").Append(uiCam.name).Append("'");
                }
            }

            string dump = sb.ToString();
            if (dump == _lastDump && conformed == 0) return;
            _lastDump = dump;

            LogInfo("majorityUICam=" + (uiCam ? uiCam.name : "null")
                  + " votes=" + best
                  + " conformedThisScan=" + conformed
                  + " rootCanvases:" + dump);
        }
    }
}