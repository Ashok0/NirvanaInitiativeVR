using MelonLoader;
using UnityEngine;

namespace NirvanaVR
{
    /// <summary>
    /// Gets the game's 2D interface into the headset.
    ///
    /// The problem: every one of this game's canvases is ScreenSpaceOverlay, which Unity
    /// draws straight to the backbuffer after all cameras have run. No camera can capture
    /// it, so it lands on the monitor and never reaches our eye textures - which is exactly
    /// what you see: dialogue, portraits and cursor on the screen but absent in VR.
    ///
    /// The fix, in three parts:
    ///   1. Switch those canvases to ScreenSpaceCamera. The game already assigns UICamera
    ///      as their worldCamera, and UICamera already culls exactly the UI layer, so they
    ///      start rendering through a camera we can redirect.
    ///   2. Point UICamera at a RenderTexture with a transparent clear, so we capture the
    ///      interface with its alpha intact.
    ///   3. Composite that texture over both eye images, scaled down and centred so it sits
    ///      comfortably in view rather than smeared across the full ~110 degree field.
    /// </summary>
    public static class VRUI
    {
        private static Camera _uiCamera;
        private static RenderTexture _uiTexture;
        private static Material _compositeMaterial;
        private static bool _initialised;

        // Fraction of the eye image the interface spans. A screen-space UI stretched over
        // the whole VR field is unreadable at the edges and uncomfortable to track, so it
        // gets pulled in to roughly the centre. F5/F6-style tuning lives on Page Up/Down.
        private static float _uiScale = 0.62f;

        // Re-apply periodically: scene changes rebuild canvases, and any new one arrives in
        // ScreenSpaceOverlay again.
        private const int RefreshInterval = 60;
        private static int _framesSinceRefresh = RefreshInterval;

        public static RenderTexture UiTexture => _uiTexture;

        public static void AdjustScale(float delta)
        {
            _uiScale = Mathf.Clamp(_uiScale + delta, 0.2f, 1f);
            MelonLogger.Msg($"NirvanaVR: UI scale {_uiScale:F2}");
        }

        /// <summary>Call once per frame from game logic, before rendering.</summary>
        public static void Update()
        {
            if (++_framesSinceRefresh < RefreshInterval)
                return;

            _framesSinceRefresh = 0;
            EnsureUiCamera();
            RedirectOverlayCanvases();
        }

        private static void EnsureUiCamera()
        {
            if (_uiCamera == null)
            {
                var uiCameraObject = GameObject.Find("UICamera");
                if (uiCameraObject == null)
                    return;

                _uiCamera = uiCameraObject.GetComponent<Camera>();
                if (_uiCamera == null)
                    return;
            }

            if (_uiTexture == null)
            {
                // Screen-sized: the canvases lay themselves out against the camera's pixel
                // dimensions, so matching the game's own resolution keeps the interface
                // proportioned exactly as the game intends.
                var width = Mathf.Max(64, Screen.width);
                var height = Mathf.Max(64, Screen.height);

                _uiTexture = new RenderTexture(width, height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "NirvanaVR_UIRT",
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                _uiTexture.Create();
            }

            if (_uiCamera.targetTexture != _uiTexture)
            {
                _uiCamera.targetTexture = _uiTexture;

                // Transparent clear so only the interface itself composites over the scene.
                _uiCamera.clearFlags = CameraClearFlags.SolidColor;
                _uiCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);

                MelonLogger.Msg($"NirvanaVR: UI capture active ({_uiTexture.width}x{_uiTexture.height})");
            }

            if (!_initialised)
            {
                _initialised = true;
                MelonLogger.Msg("NirvanaVR: UI redirected into VR - dialogue, portraits and cursor should now appear in the headset");
            }
        }

        /// <summary>
        /// The root canvases carrying the actual interface - dialogue, portraits, cursor and
        /// menus. Deliberately NOT every canvas: this game also builds full-screen effect
        /// layers out of canvases (CanvasBrightness dims the image, Mask drives transitions,
        /// Canvas1 holds background art). Those are opaque and full-frame, so pulling them
        /// into the UI texture blacked out the monitor and filled the VR panel with grey.
        /// Left alone, they keep drawing to the screen exactly as the game intends.
        /// </summary>
        private static readonly string[] InterfaceCanvasNames =
        {
            "#Canvas",           // cursor, menus, save/load
            "Canvas2",           // message window, bust shots, navi
            "OptionWorldCanvas"  // options
        };

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

        /// <summary>
        /// Moves the interface canvases onto UICamera. Overlay canvases are the only ones
        /// that need this - anything already rendering through a camera is captured normally.
        /// </summary>
        private static void RedirectOverlayCanvases()
        {
            if (_uiCamera == null)
                return;

            var canvases = Object.FindObjectsOfType<Canvas>();
            if (canvases == null)
                return;

            foreach (var canvas in canvases)
            {
                if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;

                // ROOT canvases only. Nested ones (this game has several - the message text,
                // the prompt, the bust shots) exist purely to control sort order and take
                // their render mode from the root. Overriding them individually breaks their
                // rendering: they came out as empty grey rectangles with no text or artwork.
                if (!canvas.isRootCanvas || !IsInterfaceCanvas(canvas))
                    continue;

                // The canvas has to be on a layer UICamera actually draws, or redirecting it
                // sends it nowhere at all.
                _uiCamera.cullingMask |= 1 << canvas.gameObject.layer;

                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = _uiCamera;

                // Comfortably between the UI camera's clip planes; the exact value doesn't
                // matter for a screen-space canvas beyond being inside the frustum.
                canvas.planeDistance = Mathf.Clamp(
                    (_uiCamera.nearClipPlane + _uiCamera.farClipPlane) * 0.5f,
                    _uiCamera.nearClipPlane + 0.01f,
                    _uiCamera.farClipPlane - 0.01f);
            }
        }

        /// <summary>
        /// Draws the captured interface over one eye's image, centred and scaled. Called
        /// after the eye's scene render has been copied into the submit texture.
        /// </summary>
        public static void Composite(RenderTexture destination, Valve.VR.EVREye eye)
        {
            if (_uiTexture == null || destination == null)
                return;

            var material = GetCompositeMaterial();
            if (material == null)
                return;

            if (!VRHead.TryGetEyeView(eye, out var eyeOffset,
                    out var frustumLeft, out var frustumRight, out var frustumBottom, out var frustumTop))
                return;

            // Treat the interface as a flat panel floating a fixed distance ahead of the
            // head, then work out where that panel lands in THIS eye. Because each eye sits
            // slightly to one side and looks through its own off-centre frustum, the panel
            // occupies a different part of each eye's image - which is precisely what makes
            // it fuse into one panel at a comfortable distance instead of doubling.
            var uiAspect = (float)_uiTexture.width / _uiTexture.height;
            var halfWidth = _uiScale * PanelHalfWidthMetres;
            var halfHeight = halfWidth / uiAspect;

            // Panel corners in this eye's space, as tangents at unit distance.
            var leftTangent = (-halfWidth - eyeOffset.x) / PanelDistanceMetres;
            var rightTangent = (halfWidth - eyeOffset.x) / PanelDistanceMetres;
            var bottomTangent = (-halfHeight - eyeOffset.y) / PanelDistanceMetres;
            var topTangent = (halfHeight - eyeOffset.y) / PanelDistanceMetres;

            // Tangents -> normalised position within this eye's frustum.
            var frustumWidth = frustumRight - frustumLeft;
            var frustumHeight = frustumTop - frustumBottom;
            var u0 = (leftTangent - frustumLeft) / frustumWidth;
            var u1 = (rightTangent - frustumLeft) / frustumWidth;
            var v0 = (bottomTangent - frustumBottom) / frustumHeight;
            var v1 = (topTangent - frustumBottom) / frustumHeight;

            var previous = RenderTexture.active;
            RenderTexture.active = destination;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, destination.width, destination.height, 0f);

            // LoadPixelMatrix puts y=0 at the top, while v runs bottom-up.
            var rect = new Rect(
                u0 * destination.width,
                (1f - v1) * destination.height,
                (u1 - u0) * destination.width,
                (v1 - v0) * destination.height);

            Graphics.DrawTexture(rect, _uiTexture, material);

            GL.PopMatrix();
            RenderTexture.active = previous;
        }

        // Where the interface sits in space. Two metres is far enough to be comfortable to
        // focus on and clear of the game's own geometry in most shots.
        private const float PanelDistanceMetres = 2f;
        private const float PanelHalfWidthMetres = 1.15f;

        /// <summary>
        /// An alpha-blending material, so the interface composites over the scene instead of
        /// replacing it. Built-in UI shaders are always present in a uGUI game; the
        /// fallbacks cover the case where a particular one was stripped from the build.
        /// </summary>
        private static Material GetCompositeMaterial()
        {
            if (_compositeMaterial != null)
                return _compositeMaterial;

            foreach (var shaderName in new[] { "UI/Default", "Sprites/Default", "Unlit/Transparent" })
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    continue;

                _compositeMaterial = new Material(shader);
                MelonLogger.Msg($"NirvanaVR: UI composite shader '{shaderName}'");
                return _compositeMaterial;
            }

            MelonLogger.Error("NirvanaVR: no alpha-blending shader available - UI cannot be composited");
            return null;
        }
    }
}
