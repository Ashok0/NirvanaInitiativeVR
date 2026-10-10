using MelonLoader;
using UnityEngine;

namespace NirvanaVR
{
    /// <summary>
    /// Puts the game's interface into the headset by turning its canvases into world-space
    /// panels floating in front of the player - the approach the Pathologic 2 mod uses.
    ///
    /// The game's canvases are ScreenSpaceOverlay, which Unity draws straight to the
    /// backbuffer after every camera. Nothing can capture that, so the interface reaches the
    /// monitor and never the eye textures.
    ///
    /// The obvious alternative - render them through a camera into a texture and composite
    /// that over each eye - went badly. This game mixes real interface (dialogue, portraits,
    /// cursor) with full-screen effect layers (brightness, transition masks, filters) in the
    /// same hierarchy, so capturing wholesale produced an opaque wash that blacked out the
    /// monitor and left a grey rectangle in VR, with the dialogue text missing entirely.
    ///
    /// World space avoids all of it. A world-space canvas is ordinary geometry: both eye
    /// cameras render it naturally, stereo comes out right with no per-eye maths, alpha
    /// behaves exactly as it always did, text renders through its normal path, and the
    /// monitor keeps drawing it through the game's own camera.
    /// </summary>
    public static class VRUI
    {
        // Only the canvases carrying actual interface. The game also builds full-screen
        // effects out of canvases - CanvasBrightness dims the image, Mask drives transitions,
        // Canvas1 holds background art - and those belong flat on the screen, not on a panel
        // hanging in the room.
        private static readonly string[] InterfaceCanvasNames =
        {
            "#Canvas",           // cursor, menus, save/load
            "Canvas2",           // message window, bust shots, navi
            "OptionWorldCanvas"  // options
        };

        private const int UiLayer = 5;

        // How far apart (in metres) two simultaneously-visible panels sit from each other,
        // by their index in InterfaceCanvasNames. Earlier entries sit CLOSER to the head -
        // #Canvas (cursor/menus/save-load, index 0) is the modal-dialog layer and belongs in
        // front of Canvas2 (message window/portraits, index 1), matching how a "Load this
        // save data?" prompt is meant to sit over the portrait art behind it. Without this,
        // every interface canvas landed on the exact same point in space, and which of two
        // coincident panels drew on top was undefined - that's what read as flicker.
        private const float LayerSeparation = 0.05f;

        private static float _panelDistance = 2.2f;
        private static float _panelWidth = 1.4f;

        // ON, but strictly non-destructive: nothing is reparented and every change is
        // recorded so Insert puts the game back exactly as it was. The earlier version
        // reparented these canvases under the camera, so a scene change destroyed the camera
        // and took the game's UI with it - that is what stopped saves from loading, and it
        // is gone.
        private static bool _enabled = true;
        private static bool _announced;

        /// <summary>What each canvas looked like before we touched it, so toggling off puts
        /// the game back exactly as it was rather than leaving it half-converted.</summary>
        private struct CanvasState
        {
            public Canvas Canvas;
            public RenderMode RenderMode;
            public Camera WorldCamera;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public UnityEngine.UI.CanvasScaler Scaler;
            public bool ScalerWasEnabled;
        }

        private static readonly System.Collections.Generic.List<CanvasState> Modified =
            new System.Collections.Generic.List<CanvasState>();

        private const int RefreshInterval = 30;
        private static int _framesSinceRefresh = RefreshInterval;

        public static void AdjustScale(float delta)
        {
            _panelWidth = Mathf.Clamp(_panelWidth + delta * 4f, 0.8f, 6f);
            MelonLogger.Msg($"NirvanaVR: UI panel {_panelWidth:F2}m wide at {_panelDistance:F2}m");
        }

        public static void AdjustDistance(float delta)
        {
            _panelDistance = Mathf.Clamp(_panelDistance + delta, 0.6f, 8f);
            MelonLogger.Msg($"NirvanaVR: UI panel distance {_panelDistance:F2}m");
        }

        public static void Toggle()
        {
            _enabled = !_enabled;

            if (!_enabled)
                RestoreCanvases();

            MelonLogger.Msg($"NirvanaVR: UI panels {(_enabled ? "ON - experimental" : "OFF - canvases restored to the game's own settings")}");
        }

        private static void RestoreCanvases()
        {
            foreach (var state in Modified)
            {
                if (state.Canvas == null)
                    continue;

                state.Canvas.renderMode = state.RenderMode;
                state.Canvas.worldCamera = state.WorldCamera;

                if (state.Scaler != null)
                    state.Scaler.enabled = state.ScalerWasEnabled;

                var t = state.Canvas.transform;
                t.localPosition = state.LocalPosition;
                t.localRotation = state.LocalRotation;
                t.localScale = state.LocalScale;
            }

            Modified.Clear();
            _announced = false;
        }

        /// <summary>
        /// Call once per frame from game logic. Repositioning already-tracked panels is
        /// cheap and happens every frame, so the panel follows the head smoothly; only the
        /// expensive FindObjectsOfType scan for newly-appeared canvases (a dialog box like
        /// the "Load this save data?" prompt) is throttled. The old code throttled both
        /// together, so a tracked panel only updated its position ~2-3 times a second and
        /// visibly lagged/juddered behind head movement.
        /// </summary>
        public static void Update(Camera headCamera)
        {
            if (!_enabled || headCamera == null)
                return;

            RepositionTrackedPanels(headCamera);

            if (++_framesSinceRefresh < RefreshInterval)
                return;

            _framesSinceRefresh = 0;
            DiscoverNewCanvases(headCamera);
        }

        /// <summary>Every frame: just move the panels we already know about.</summary>
        private static void RepositionTrackedPanels(Camera headCamera)
        {
            foreach (var state in Modified)
            {
                if (state.Canvas == null)
                    continue;

                PlacePanel(state.Canvas, headCamera);
            }
        }

        /// <summary>Throttled: scan for canvases that appeared or changed since last time.</summary>
        private static void DiscoverNewCanvases(Camera headCamera)
        {
            var canvases = Object.FindObjectsOfType<Canvas>();
            if (canvases == null)
                return;

            foreach (var canvas in canvases)
            {
                if (canvas == null || !canvas.isRootCanvas || !IsInterfaceCanvas(canvas))
                    continue;

                if (canvas.renderMode != RenderMode.WorldSpace)
                {
                    Remember(canvas);
                    canvas.renderMode = RenderMode.WorldSpace;
                    canvas.worldCamera = headCamera;
                    PlacePanel(canvas, headCamera);
                }
            }
        }

        /// <summary>
        /// Hangs the canvas in front of the head at a fixed distance and physical size. A
        /// canvas measures itself in pixels, so the scale converts its pixel width into the
        /// metres we want it to span.
        /// </summary>
        private static void PlacePanel(Canvas canvas, Camera headCamera)
        {
            var rect = canvas.GetComponent<RectTransform>();
            if (rect == null)
                return;

            var pixelWidth = rect.rect.width;
            if (pixelWidth <= 1f)
                return;

            // Deliberately NOT reparented. Putting these under the camera meant a scene
            // change destroyed the camera and the game's UI with it, which is what stopped
            // saves from loading. Drive the world transform instead and leave the hierarchy
            // exactly as the game built it.
            var panel = canvas.transform;
            var head = headCamera.transform;
            var scale = _panelWidth / pixelWidth;

            // Pull this canvas's own distance in slightly by its layer index, so two
            // simultaneously-visible panels (e.g. a save/load confirm dialog over the
            // portrait art) don't land on the exact same point in space.
            var distance = _panelDistance - GetCanvasDepthIndex(canvas) * LayerSeparation;

            panel.position = head.position + head.forward * distance;
            panel.rotation = head.rotation;
            panel.localScale = new Vector3(scale, scale, scale);

            if (!_announced)
            {
                _announced = true;
                MelonLogger.Msg($"NirvanaVR: UI moved to world-space panels ({_panelWidth:F2}m wide, {_panelDistance:F2}m ahead)");
            }
        }

        private static void Remember(Canvas canvas)
        {
            foreach (var existing in Modified)
            {
                if (existing.Canvas == canvas)
                    return;
            }

            var t = canvas.transform;
            var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();

            Modified.Add(new CanvasState
            {
                Canvas = canvas,
                RenderMode = canvas.renderMode,
                WorldCamera = canvas.worldCamera,
                LocalPosition = t.localPosition,
                LocalRotation = t.localRotation,
                LocalScale = t.localScale,
                Scaler = scaler,
                ScalerWasEnabled = scaler != null && scaler.enabled
            });

            // A CanvasScaler sizes the canvas from the screen. In world space that fights
            // the fixed physical size we want, and children anchored to screen dimensions
            // end up off the panel entirely - which is why the small confirmation popups
            // survived the switch while the bigger menus did not.
            if (scaler != null)
                scaler.enabled = false;
        }

        private static bool IsInterfaceCanvas(Canvas canvas)
        {
            var name = canvas.gameObject.name;
            foreach (var candidate in InterfaceCanvasNames)
            {
                if (name == candidate)
                    return true;
            }

            return false;
        }

        /// <summary>Position in InterfaceCanvasNames, used as this canvas's depth layer.</summary>
        private static int GetCanvasDepthIndex(Canvas canvas)
        {
            var name = canvas.gameObject.name;
            for (var i = 0; i < InterfaceCanvasNames.Length; i++)
            {
                if (name == InterfaceCanvasNames[i])
                    return i;
            }

            return InterfaceCanvasNames.Length;
        }

        /// <summary>
        /// The eye cameras inherit the game camera's culling mask, which excludes the UI
        /// layer - the game never expected its 3D camera to draw interface. World-space
        /// panels only show up once that layer is added back.
        /// </summary>
        public static void IncludeUiLayer(Camera camera)
        {
            if (camera != null)
                camera.cullingMask |= 1 << UiLayer;
        }
    }
}
