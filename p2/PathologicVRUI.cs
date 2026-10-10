using System;
using UnityEngine;
using UnityEngine.Rendering;

// =============================================================================
//  Pathologic 2 VR — UI in the headset (v7: stereo panel, lazy follow)
// =============================================================================
//  V6 captured the UI camera into _uiRT and stamped that 2D image onto each
//  eye's SubmitTex at identical pixel coordinates. Identical pixels in both
//  eyes = zero disparity = "infinitely far", while the scene you're focusing
//  on is 1-2 m away. The eyes can't fuse it (double vision), and because it's
//  painted after the scene it's necessarily head-locked.
//
//  V7: the UI RT goes on a quad in the WORLD, drawn once per eye through that
//  eye's own view + projection via Graphics.DrawMesh(..., camera). Correct
//  stereo disparity falls out for free. The quad is drawn only into the two
//  overlay cameras VrStereoRenderer creates (children of the eye cams, same
//  pose + projection, clearFlags = Depth, render into SubmitTex after the
//  gamma blit) — so it can never be occluded by geometry and never appears on
//  the desktop or in the eye cams' own scene render.
//
//  Placement is "lazy follow": the panel sits PanelDistance in front of the
//  head and stays put while you look around within FollowAngleThreshold.
//  Past that it glides back to centre at FollowSpeed deg/s. It is position-
//  locked to the head (walking doesn't leave it behind) but rotation-lazy.
//    FollowAngleThreshold = 0   -> smooth head-lock
//    FollowAngleThreshold = 999 -> fully world-locked until F8
//  F8 snaps the panel in front of you.
//
//  Capture is unchanged from V6: explicit second render of the real UI camera
//  into _uiRT every LateUpdate, cleared to transparent. No gate, no redirect.
// =============================================================================

namespace PathologicVR
{
    public static class VRUI
    {
        // ---- Tunables -------------------------------------------------------
        public static float PanelDistance        = 2.0f;   // metres in front of head
        public static float PanelWidth           = 2.4f;   // metres; height follows RT aspect
        public static float FollowAngleThreshold = 25f;    // degrees before panel starts re-centering
        public static float FollowSpeed          = 90f;    // degrees per second while re-centering
        public static bool  PanelFlipV           = false;  // set true if the panel renders upside-down
        public static KeyCode RecenterPanelKey   = KeyCode.F8;

        // ---- Capture state --------------------------------------------------
        private static Camera _uiCam;
        private static RenderTexture _uiRT;
        private static float _nextRebind;
        private static float _nextTelemetry;
        private static Texture2D _probeTex;
        private static float _lastFill;      // telemetry only
        private static bool _loggedReady;

        // ---- Panel state ----------------------------------------------------
        private static Mesh _quad;
        private static Material _panelMat;
        private static int _panelLayer = -1;
        private static Vector3 _panelDir;
        private static bool _panelInit;
        private static bool _following;
        private static bool _loggedPanel;

        /// Layer reserved for the panel. Resolved once; picks the highest
        /// unnamed layer so we never share it with game content.
        public static int PanelLayer
        {
            get
            {
                if (_panelLayer < 0)
                {
                    _panelLayer = 30;
                    for (int i = 31; i >= 8; i--)
                    {
                        if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { _panelLayer = i; break; }
                    }
                }
                return _panelLayer;
            }
        }

        public static void EnsureAndUpdate(VrStereoRenderer stereo, Camera gameCam)
        {
            if (Time.unscaledTime >= _nextRebind)
            {
                _nextRebind = Time.unscaledTime + 2f;
                try { EnsureUiCam(); }
                catch (Exception e) { PathologicVR6DOFTest.Log.Warn("VRUI bind failed: " + e.Message); }
            }

            CaptureViaExplicitRender();

            try { DrawPanel(stereo, gameCam); }
            catch (Exception e) { PathologicVR6DOFTest.Log.Warn("VRUI panel failed: " + e.Message); }

            if (Time.unscaledTime >= _nextTelemetry)
            {
                _nextTelemetry = Time.unscaledTime + 5f;
                try { UpdateFill(); Telemetry(); }
                catch { }
            }
        }

        // ---------------------------------------------------------------------
        //  Capture (unchanged from V6)
        // ---------------------------------------------------------------------
        private static void EnsureUiCam()
        {
            Canvas[] all = UnityEngine.Object.FindObjectsOfType<Canvas>();
            Camera best = null; int bestCount = 0;
            foreach (var c in all)
            {
                if (!c.isActiveAndEnabled) continue;
                if (c.renderMode != RenderMode.ScreenSpaceCamera) continue;
                if (c.worldCamera == null) continue;
                int count = 0;
                foreach (var c2 in all)
                    if (c2.isActiveAndEnabled && c2.worldCamera == c.worldCamera) count++;
                if (count > bestCount) { bestCount = count; best = c.worldCamera; }
            }
            if (best == null || best == _uiCam) return;
            _uiCam = best;
            if (_uiRT == null)
            {
                _uiRT = new RenderTexture(
                    Mathf.Max(320, Screen.width), Mathf.Max(240, Screen.height),
                    24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                _uiRT.Create();
            }
            _loggedReady = false;
        }

        private static void CaptureViaExplicitRender()
        {
            if (_uiCam == null || _uiRT == null) return;

            RenderTexture savedTarget = _uiCam.targetTexture;
            CameraClearFlags savedClear = _uiCam.clearFlags;
            Color savedBg = _uiCam.backgroundColor;

            _uiCam.targetTexture = _uiRT;
            _uiCam.clearFlags = CameraClearFlags.SolidColor;
            _uiCam.backgroundColor = Color.clear;
            _uiCam.Render();

            _uiCam.targetTexture = savedTarget;
            _uiCam.clearFlags = savedClear;
            _uiCam.backgroundColor = savedBg;

            if (!_loggedReady)
            {
                _loggedReady = true;
                PathologicVR6DOFTest.Log.Info(
                    "VRUI ready: explicit double-render on '" + _uiCam.name + "' " +
                    "(ortho=" + _uiCam.orthographic + ") -> stereo panel.");
            }
        }

        // ---------------------------------------------------------------------
        //  Panel
        // ---------------------------------------------------------------------
        private static bool EnsurePanelAssets()
        {
            if (_quad == null)
            {
                _quad = new Mesh { name = "VRUI_PanelQuad" };
                _quad.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3( 0.5f, -0.5f, 0f),
                    new Vector3( 0.5f,  0.5f, 0f), new Vector3(-0.5f,  0.5f, 0f),
                };
                _quad.uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(1f, 1f), new Vector2(0f, 1f),
                };
                _quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                // Both windings so it's visible regardless of the shader's cull mode.
                _quad.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
                _quad.RecalculateBounds();
            }

            if (_panelMat == null)
            {
                string[] candidates = { "UI/Default", "Sprites/Default", "Unlit/Transparent", "Legacy Shaders/Transparent/Diffuse" };
                Shader s = null; string picked = null;
                foreach (var n in candidates)
                {
                    s = Shader.Find(n);
                    if (s != null) { picked = n; break; }
                }
                if (s == null)
                {
                    if (!_loggedPanel)
                    {
                        _loggedPanel = true;
                        PathologicVR6DOFTest.Log.Error("VRUI panel: no usable transparent shader found in build.");
                    }
                    return false;
                }
                _panelMat = new Material(s) { name = "VRUI_PanelMat" };
                _panelMat.renderQueue = 4000; // Overlay queue: after everything else
                if (!_loggedPanel)
                {
                    _loggedPanel = true;
                    PathologicVR6DOFTest.Log.Info("VRUI panel: shader='" + picked + "' layer=" + PanelLayer +
                        " dist=" + PanelDistance + "m width=" + PanelWidth + "m follow>" + FollowAngleThreshold + "deg");
                }
            }
            return true;
        }

        private static void DrawPanel(VrStereoRenderer stereo, Camera head)
        {
            if (_uiRT == null || head == null || stereo == null) return;
            if (!EnsurePanelAssets()) return;

            Transform h = head.transform;
            Vector3 fwd = h.forward;

            if (!_panelInit || Input.GetKeyDown(RecenterPanelKey))
            {
                _panelDir = fwd;
                _panelInit = true;
                _following = false;
            }

            float ang = Vector3.Angle(_panelDir, fwd);
            if (ang > FollowAngleThreshold) _following = true;
            if (_following)
            {
                float step = FollowSpeed * Time.unscaledDeltaTime * Mathf.Deg2Rad;
                _panelDir = Vector3.RotateTowards(_panelDir, fwd, step, 0f);
                if (Vector3.Angle(_panelDir, fwd) < 0.5f) _following = false;
            }
            if (_panelDir.sqrMagnitude < 1e-6f) _panelDir = fwd;
            _panelDir.Normalize();

            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(_panelDir, up)) > 0.99f) up = h.up;

            Vector3 pos = h.position + _panelDir * PanelDistance;
            Quaternion rot = Quaternion.LookRotation(_panelDir, up);
            float w = PanelWidth;
            float hgt = w * ((float)_uiRT.height / _uiRT.width);
            Matrix4x4 m = Matrix4x4.TRS(pos, rot, new Vector3(w, PanelFlipV ? -hgt : hgt, 1f));

            _panelMat.mainTexture = _uiRT;

            Camera[] cams = stereo.OverlayCameras;
            if (cams == null) return;
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] == null) continue;
                Graphics.DrawMesh(_quad, m, _panelMat, PanelLayer, cams[i], 0, null,
                                  ShadowCastingMode.Off, false);
            }
        }

        // ---------------------------------------------------------------------
        //  Telemetry only. Once per 5 s — ReadPixels is a GPU stall.
        // ---------------------------------------------------------------------
        private static void UpdateFill()
        {
            if (_uiRT == null) return;
            if (_probeTex == null) _probeTex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            var tmp = RenderTexture.GetTemporary(32, 32, 0);
            Graphics.Blit(_uiRT, tmp);
            var rprev = RenderTexture.active;
            RenderTexture.active = tmp;
            _probeTex.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
            _probeTex.Apply(false);
            RenderTexture.active = rprev;
            RenderTexture.ReleaseTemporary(tmp);
            var px = _probeTex.GetPixels32();
            int n = 0;
            foreach (var p in px) if (p.a > 8) n++;
            _lastFill = (float)n / px.Length;
        }

        private static void Telemetry()
        {
            PathologicVR6DOFTest.Log.Info(
                "VRUI telemetry: uiCam=" + (_uiCam != null ? _uiCam.name : "none") +
                ", fill=" + (_lastFill * 100f).ToString("F1") + "%" +
                ", panel=" + (_panelMat != null ? "on" : "off") +
                ", following=" + _following + ".");
        }
    }
}
