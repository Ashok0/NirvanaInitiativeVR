using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace NirvanaVR
{
    /// <summary>
    /// Owns the OpenVR session, pumps HMD poses once per frame, and stamps the
    /// resulting head transform onto the game's Main Camera every time it renders.
    /// </summary>
    public static class VRHead
    {
        private static bool _initialized;
        private static CVRSystem _system;

        private static readonly TrackedDevicePose_t[] RenderPoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static readonly TrackedDevicePose_t[] GamePoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        // Raw HMD pose for the current frame, straight out of WaitGetPoses.
        private static Vector3 _hmdPosRaw = Vector3.zero;
        private static Quaternion _hmdRotRaw = Quaternion.identity;

        // Yaw recenter offset (degrees), captured by RecenterYaw().
        private static float _yawOffsetDeg;

        // Idempotent-apply guard: what we last wrote to the camera, and the Cinemachine
        // base transform that produced it. If, next callback, the camera transform still
        // equals what we last wrote (Cinemachine hasn't touched it again this frame),
        // we must NOT treat our own previous output as the new "Cinemachine base" -
        // we'd compound the HMD offset onto itself. Reuse the cached base instead.
        private static bool _haveLastApplied;
        private static Vector3 _lastAppliedPos;
        private static Quaternion _lastAppliedRot;
        private static Vector3 _lastBasePos;
        private static Quaternion _lastBaseRot;

        // --- Stereo eye rig (Phase 2) ---
        // Two extra Camera components, parented under Main Camera's transform so they
        // inherit the corrected HMD pose "for free" via Unity's transform hierarchy, each
        // rendering to its own RenderTexture. Unity's engine-level render loop picks up any
        // enabled Camera automatically once per frame (same mechanism that already drives
        // Main Camera + UICamera) - no manual Camera.Render() calls needed, which matters
        // because calling Camera.Render() from inside a beginCameraRendering/
        // endCameraRendering callback (i.e. while the SRP is already mid-frame) throws.
        private const string LeftEyeCameraName = "NirvanaVR_LeftEye";
        private const string RightEyeCameraName = "NirvanaVR_RightEye";

        private static bool _stereoRigCreated;
        private static Camera _leftEyeCamera;
        private static Camera _rightEyeCamera;
        private static Matrix4x4 _leftEyeProjection;
        private static Matrix4x4 _rightEyeProjection;

        // ~1s at 90Hz - how long to wait, after noticing the eye cameras were destroyed by
        // a scene transition, before touching cameras/textures/the compositor again.
        private const int RebuildCooldownFrames = 90;
        private static int _rebuildCooldownRemaining = -1; // -1 = not currently cooling down

        // V is flipped (vMin=1, vMax=0): a Unity RenderTexture is bottom-up relative to
        // what the OpenVR compositor expects on D3D11. Straight 0..1 bounds submit the
        // image upside down.
        private static readonly VRTextureBounds_t FullTextureBounds =
            new VRTextureBounds_t { uMin = 0f, vMin = 1f, uMax = 1f, vMax = 0f };

        // One stable RenderTexture per eye, created once and never swapped. An earlier
        // version double-buffered these and re-pointed camera.targetTexture every frame to
        // dodge a compositor race - but the real cause of that race was submitting from
        // inside the render callback (see OnEndCameraRendering), and the buffer juggling
        // only desynced what was rendered from what was submitted.
        private static RenderTexture _leftEyeTexture;
        private static RenderTexture _rightEyeTexture;

        // TEMP EXPERIMENT toggle - see the comment where the eye textures are allocated.
        // F11 flips this at runtime and forces a rig rebuild, so we can compare both sizes
        // in a single session instead of one game restart per attempt.
        private static bool UseScreenSizedEyeTextures = true;

        // Mono mirror mode (F12). Instead of rendering the scene ourselves, copy the game's
        // own finished frame into both eye textures. No parallax, but the view is still
        // head-tracked (the game's camera carries the HMD pose), and it isolates the
        // question of whether anything is wrong downstream of rendering: the pixels here
        // are known-good, so if the headset shows them, the submit path is fine and the
        // fault is purely in our eye cameras' rendering.
        private static bool _mirrorGameFrameToEyes = true;
        private static Camera _gameMainCamera;

        public static void Init()
        {
            if (_initialized)
                return;

            var err = EVRInitError.None;
            _system = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);

            if (err != EVRInitError.None || _system == null)
            {
                MelonLogger.Error($"NirvanaVR: OpenVR.Init failed - {OpenVR.GetStringForHmdError(err)}");
                _initialized = false;
                return;
            }

            _initialized = true;
            MelonLogger.Msg("NirvanaVR: OpenVR initialized");

            // The game's targetFrameRate is -1 (uncapped) by default, and before this app
            // has real OpenVR "scene focus" WaitGetPoses doesn't reliably gate us to the
            // headset's vsync either - so on a fast loading screen we were bursting
            // Submit() calls at 100-300+/sec, far above what the SteamVR compositor's IPC
            // channel is designed for, and eventually wedging it. Cap to the HMD's actual
            // refresh rate so we never render/submit faster than the compositor can drain.
            var propErr = ETrackedPropertyError.TrackedProp_Success;
            var refreshRate = _system.GetFloatTrackedDeviceProperty(
                OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref propErr);
            if (propErr != ETrackedPropertyError.TrackedProp_Success || refreshRate <= 0f)
                refreshRate = 90f; // Sensible fallback if the property isn't available yet.

            Application.targetFrameRate = Mathf.RoundToInt(refreshRate);
            MelonLogger.Msg($"NirvanaVR: capped Application.targetFrameRate to {Application.targetFrameRate} (HMD refresh {refreshRate:F1}Hz)");
        }

        /// <summary>Call once per frame (OnLateUpdate) to fetch the latest HMD pose.</summary>
        public static void PumpPoses()
        {
            if (!_initialized)
                return;

            var compositor = OpenVR.Compositor;
            if (compositor == null)
                return;

            // Submit whatever eye textures finished rendering last frame - see the comment
            // on OnEndCameraRendering. Doing this first, before WaitGetPoses, keeps a
            // stable per-frame order: submit last frame's eyes, then fetch this frame's
            // poses.
            if (_pendingLeftSubmit != null)
            {
                ProbeEyeTexture(_pendingLeftSubmit);
                SubmitEye(EVREye.Eye_Left, _pendingLeftSubmit);
                _pendingLeftSubmit = null;
            }

            if (_pendingRightSubmit != null)
            {
                SubmitEye(EVREye.Eye_Right, _pendingRightSubmit);
                _pendingRightSubmit = null;
            }

            compositor.WaitGetPoses(RenderPoses, GamePoses);

            // The eye cameras are simply left enabled with a fixed target texture, and the
            // pipeline renders them every frame like any other camera. Application
            // .targetFrameRate (set in Init) already paces us to the HMD's refresh rate.
            ReassertEyeCameras();
            MirrorGameFrameToEyes();

            var hmdPose = RenderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!hmdPose.bPoseIsValid)
                return;

            var m = hmdPose.mDeviceToAbsoluteTracking;
            _hmdPosRaw = GetPosition(m);
            _hmdRotRaw = GetRotation(m);
        }

        /// <summary>
        /// Copies the game's own finished frame into both eye textures and queues them for
        /// submission. Runs from LateUpdate, so the source holds the last fully rendered
        /// frame and the copy is well clear of the render pipeline's own work.
        /// </summary>
        private static void MirrorGameFrameToEyes()
        {
            if (!_mirrorGameFrameToEyes || !_stereoRigCreated)
                return;

            if (_gameMainCamera == null)
                return;

            // In menus the game renders straight to the screen, so there's no frame to
            // copy - leave the eye cameras' own output alone in that case.
            var source = _gameMainCamera.targetTexture;
            if (source == null || _leftEyeTexture == null || _rightEyeTexture == null)
                return;

            Graphics.Blit(source, _leftEyeTexture);
            Graphics.Blit(source, _rightEyeTexture);

            _pendingLeftSubmit = _leftEyeTexture;
            _pendingRightSubmit = _rightEyeTexture;
        }

        /// <summary>
        /// Re-applies the per-eye pose and projection from game logic each frame. The
        /// projection especially: setting it only from beginCameraRendering risks landing
        /// after the pipeline has already derived this frame's culling parameters from
        /// whatever matrix the camera had, and anything that touches the camera can reset
        /// it. (Pathologic 2 does the same from its LateUpdate, plus an OnPreCull guard.)
        /// </summary>
        private static void ReassertEyeCameras()
        {
            if (!_stereoRigCreated || _leftEyeCamera == null || _rightEyeCamera == null)
                return;

            ReassertEyeCamera(_leftEyeCamera, EVREye.Eye_Left, _leftEyeProjection);
            ReassertEyeCamera(_rightEyeCamera, EVREye.Eye_Right, _rightEyeProjection);
        }

        private static void ReassertEyeCamera(Camera eyeCamera, EVREye eye, Matrix4x4 projection)
        {
            var eyeToHead = _system.GetEyeToHeadTransform(eye);
            eyeCamera.transform.localPosition = GetPosition(eyeToHead);
            eyeCamera.transform.localRotation = GetRotation(eyeToHead);
            eyeCamera.projectionMatrix = projection;
            eyeCamera.useOcclusionCulling = false;
        }

        /// <summary>Zeroes the yaw of the current HMD orientation as the new forward.</summary>
        public static void RecenterYaw()
        {
            _yawOffsetDeg = _hmdRotRaw.eulerAngles.y;
            MelonLogger.Msg($"NirvanaVR: yaw recentered to {_yawOffsetDeg:F1} deg");
        }

        /// <summary>
        /// RenderPipelineManager.beginCameraRendering handler. Dispatches per camera:
        /// Main Camera gets the mono head-pose overlay (drives the flatscreen mirror and,
        /// via transform parenting, the eye cameras too); the eye cameras just need their
        /// static per-eye projection matrix reasserted in case anything reset it.
        /// </summary>
        public static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_initialized || camera == null)
                return;

            if (camera.name == "Main Camera")
            {
                ApplyMonoHeadPose(camera);
                return;
            }

            // NB: targetTexture is deliberately NOT assigned here - see PumpPoses. URP has
            // already snapshotted it by the time this fires.
            if (camera == _leftEyeCamera)
            {
                camera.projectionMatrix = _leftEyeProjection;
                return;
            }

            if (camera == _rightEyeCamera)
                camera.projectionMatrix = _rightEyeProjection;
        }

        /// <summary>
        /// By the time this fires, Cinemachine has already written this frame's transform
        /// onto the camera - we read that as the base and layer the (recentered) HMD pose
        /// on top of it.
        /// </summary>
        private static void ApplyMonoHeadPose(Camera camera)
        {
            var t = camera.transform;
            var currentPos = t.position;
            var currentRot = t.rotation;

            Vector3 basePos;
            Quaternion baseRot;

            if (_haveLastApplied && currentPos == _lastAppliedPos && currentRot == _lastAppliedRot)
            {
                // Cinemachine hasn't re-driven the camera since our last write - reuse the
                // cached base rather than re-deriving it from our own previous output.
                basePos = _lastBasePos;
                baseRot = _lastBaseRot;
            }
            else
            {
                // Fresh Cinemachine output for this frame.
                basePos = currentPos;
                baseRot = currentRot;
                _lastBasePos = basePos;
                _lastBaseRot = baseRot;
            }

            var hmdRot = GetRecenteredRotation();
            var hmdPos = GetRecenteredPosition();

            var correctedRot = baseRot * hmdRot;
            var correctedPos = basePos + baseRot * hmdPos;

            t.SetPositionAndRotation(correctedPos, correctedRot);

            _lastAppliedPos = correctedPos;
            _lastAppliedRot = correctedRot;
            _haveLastApplied = true;
        }

        // Textures whose render commands were recorded this frame and are waiting to be
        // handed to the compositor - see the comment on OnEndCameraRendering below.
        private static RenderTexture _pendingLeftSubmit;
        private static RenderTexture _pendingRightSubmit;

        /// <summary>
        /// RenderPipelineManager.endCameraRendering handler. Fires once a camera's draw
        /// commands are recorded into the ScriptableRenderContext - but URP doesn't
        /// actually flush those commands to the GPU (context.Submit()) until after the
        /// whole camera batch finishes, which happens AFTER this callback returns. Handing
        /// a texture to OpenVR's (cross-process) Compositor.Submit() here, before the GPU
        /// has actually rendered into it, appears to race with SteamVR's compositor and
        /// intermittently wedge it. So we only record which texture is ready here, and
        /// actually submit it at the start of the NEXT frame (PumpPoses), once a full
        /// frame boundary - and therefore the GPU flush - has definitely passed.
        /// </summary>
        public static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            // Read the texture off the camera itself rather than indexing by _bufferIndex:
            // this is by definition the target that was just rendered, so the thing we
            // submit can never drift out of sync with the thing that was drawn.
            if (camera == _leftEyeCamera)
            {
                _leftRenderCount++;
                _pendingLeftSubmit = camera.targetTexture;
            }
            else if (camera == _rightEyeCamera)
            {
                _pendingRightSubmit = camera.targetTexture;
            }
        }

        // TEMP DIAGNOSTIC: how many times the SRP has actually run the left eye camera.
        // If this climbs while the probed texture stays bit-identical, the camera is being
        // rendered but its output isn't landing in targetTexture. If it stops climbing,
        // the render pipeline has quietly stopped scheduling our camera at all.
        private static long _leftRenderCount;

        // TEMP DIAGNOSTIC: F10 points the left eye at the screen instead of its RenderTexture,
        // so we can see on the monitor whether it produces a live image or a frozen one.
        private static bool _mirrorLeftEyeToScreen;

        private static void ApplyEyeCameraEnabledForMode()
        {
            if (_leftEyeCamera == null || _rightEyeCamera == null)
                return;

            _leftEyeCamera.enabled = !_mirrorGameFrameToEyes;
            _rightEyeCamera.enabled = !_mirrorGameFrameToEyes;
        }

        /// <summary>
        /// F12: switch between mirroring the game's own frame (mono, guaranteed image) and
        /// our own per-eye cameras (true stereo, currently rendering grey in-game).
        /// </summary>
        public static void ToggleGameFrameMirror()
        {
            _mirrorGameFrameToEyes = !_mirrorGameFrameToEyes;
            ApplyEyeCameraEnabledForMode();
            MelonLogger.Msg($"NirvanaVR: {(_mirrorGameFrameToEyes ? "MIRROR mode - copying the game's own frame to both eyes (mono)" : "STEREO mode - per-eye cameras rendering the scene")}");
        }

        /// <summary>
        /// Flips the eye-texture resolution experiment and tears the rig down so the next
        /// EnsureStereoRig call rebuilds it at the other size.
        /// </summary>
        public static void ToggleEyeTextureResolution()
        {
            UseScreenSizedEyeTextures = !UseScreenSizedEyeTextures;

            _pendingLeftSubmit = null;
            _pendingRightSubmit = null;
            DestroyEyeCamera(ref _leftEyeCamera);
            DestroyEyeCamera(ref _rightEyeCamera);
            ReleaseEyeTexture(ref _leftEyeTexture);
            ReleaseEyeTexture(ref _rightEyeTexture);
            _stereoRigCreated = false;
            _rebuildCooldownRemaining = -1;

            MelonLogger.Msg($"NirvanaVR: eye texture size -> {(UseScreenSizedEyeTextures ? "screen size" : "HMD recommended size")}, rebuilding rig");
        }

        public static void ToggleLeftEyeScreenMirror()
        {
            if (_leftEyeCamera == null)
            {
                MelonLogger.Msg("NirvanaVR: no left eye camera to mirror");
                return;
            }

            _mirrorLeftEyeToScreen = !_mirrorLeftEyeToScreen;
            _leftEyeCamera.targetTexture = _mirrorLeftEyeToScreen ? null : _leftEyeTexture;
            MelonLogger.Msg($"NirvanaVR: left eye screen mirror {(_mirrorLeftEyeToScreen ? "ON - left eye now draws to the monitor" : "OFF")}");
        }

        /// <summary>
        /// Lazily builds the two eye cameras the first time a valid Main Camera is available.
        /// Safe to call every frame - no-ops once the rig exists. Must be called from game
        /// logic (e.g. OnLateUpdate), never from a render callback.
        /// </summary>
        public static void EnsureStereoRig(GameObject mainCameraObject)
        {
            if (!_initialized || mainCameraObject == null)
                return;

            // Don't touch cameras/textures/the compositor until SteamVR confirms this
            // process actually has VR scene focus (see the matching check in PumpPoses).
            var compositor = OpenVR.Compositor;
            if (compositor == null || !compositor.CanRenderScene())
                return;

            var mainCamera = mainCameraObject.GetComponent<Camera>();
            if (mainCamera == null)
                return;

            // Self-heal across scene transitions. Our eye cameras hang off Main Camera's
            // transform, so a scene change can leave the rig dead in ways a plain
            // "== null" check misses - most importantly when the game swaps in a NEW
            // "Main Camera" object and our cameras are left parented under the old one.
            // The old one isn't destroyed (so our references don't read as null), it's just
            // no longer the camera being rendered, so our eyes silently stop drawing and
            // stop submitting - the compositor then shows its "waiting for app" room.
            // Validate positively instead: still alive, still parented to the CURRENT Main
            // Camera, and still active in the hierarchy.
            //
            // Rebuilding instantly (same frame we notice the loss) reliably hung the game -
            // that moment lands right in the middle of the scene transition's own asset
            // streaming/shader-compile churn, and creating new cameras/RenderTextures and
            // immediately submitting them to the (separate-process) SteamVR compositor
            // while that's happening appears to wedge something. Give it a cooldown to
            // settle first.
            if (_stereoRigCreated && !IsEyeRigAttachedTo(mainCamera))
            {
                if (_rebuildCooldownRemaining < 0)
                {
                    MelonLogger.Msg("NirvanaVR: eye cameras lost (scene change?) - cooling down before rebuild");
                    _rebuildCooldownRemaining = RebuildCooldownFrames;
                }

                if (_rebuildCooldownRemaining > 0)
                {
                    _rebuildCooldownRemaining--;
                    return;
                }

                MelonLogger.Msg("NirvanaVR: rebuilding stereo rig after cooldown");
                // Drop any not-yet-submitted textures from the old rig before releasing
                // them, so PumpPoses doesn't try to submit a destroyed RenderTexture.
                _pendingLeftSubmit = null;
                _pendingRightSubmit = null;
                // Tear down any eye cameras that are still alive (the stale-parent case) so
                // we don't leave orphans rendering into textures we're about to release.
                DestroyEyeCamera(ref _leftEyeCamera);
                DestroyEyeCamera(ref _rightEyeCamera);
                ReleaseEyeTexture(ref _leftEyeTexture);
                ReleaseEyeTexture(ref _rightEyeTexture);
                _stereoRigCreated = false;
                _rebuildCooldownRemaining = -1;
            }

            if (_stereoRigCreated)
                return;

            uint width = 0, height = 0;
            _system.GetRecommendedRenderTargetSize(ref width, ref height);
            if (width == 0 || height == 0)
                return; // Headset not ready yet - try again next frame.

            // TEMP EXPERIMENT: match the game's own backbuffer size rather than the HMD's
            // recommended size. The same camera renders the world correctly to the screen
            // (960x544) but produces flat grey into a 1344x1440 RenderTexture, which points
            // at a render pass in the game's LightweightRP that is sized to the screen and
            // produces garbage at any other resolution. If this fixes the image, the eye
            // resolution is the constraint and we work up from here.
            if (UseScreenSizedEyeTextures && Screen.width > 0 && Screen.height > 0)
            {
                width = (uint)Screen.width;
                height = (uint)Screen.height;
            }

            _leftEyeTexture = CreateEyeTexture(width, height, "NirvanaVR_LeftEyeRT");
            _rightEyeTexture = CreateEyeTexture(width, height, "NirvanaVR_RightEyeRT");

            _leftEyeProjection = ToUnityMatrix(
                _system.GetProjectionMatrix(EVREye.Eye_Left, mainCamera.nearClipPlane, mainCamera.farClipPlane));
            _rightEyeProjection = ToUnityMatrix(
                _system.GetProjectionMatrix(EVREye.Eye_Right, mainCamera.nearClipPlane, mainCamera.farClipPlane));

            _leftEyeCamera = CreateEyeCamera(LeftEyeCameraName, mainCamera, _leftEyeTexture, _leftEyeProjection, EVREye.Eye_Left);
            _rightEyeCamera = CreateEyeCamera(RightEyeCameraName, mainCamera, _rightEyeTexture, _rightEyeProjection, EVREye.Eye_Right);
            _gameMainCamera = mainCamera;

            // While mirroring the game's frame there's no reason to pay for two extra scene
            // renders - the eye cameras' output would just be overwritten by the blit.
            ApplyEyeCameraEnabledForMode();

            _stereoRigCreated = true;
            var gameTarget = mainCamera.targetTexture;
            MelonLogger.Msg($"NirvanaVR: stereo rig created ({width}x{height} per eye); " +
                $"game camera target={(gameTarget != null ? $"'{gameTarget.name}' {gameTarget.width}x{gameTarget.height}" : "screen")}, " +
                $"left eye target='{_leftEyeCamera.targetTexture?.name}'");
        }

        private static RenderTexture CreateEyeTexture(uint width, uint height, string name)
        {
            // Explicit sRGB to pair with the EColorSpace.Gamma we submit - leaving either
            // side implicit invites a gamma mismatch that crushes mid-tones.
            var rt = new RenderTexture((int)width, (int)height, 24,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = name,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
            return rt;
        }

        /// <summary>
        /// True only if both eye cameras are alive, still parented to this Main Camera, and
        /// active in the hierarchy - i.e. they will actually render this frame. A plain
        /// null check isn't enough: a scene change can swap in a new "Main Camera" and leave
        /// our (undestroyed, non-null) eye cameras dangling under the old one.
        /// </summary>
        private static bool IsEyeRigAttachedTo(Camera mainCamera)
        {
            return IsEyeCameraAttachedTo(_leftEyeCamera, mainCamera)
                && IsEyeCameraAttachedTo(_rightEyeCamera, mainCamera);
        }

        private static bool IsEyeCameraAttachedTo(Camera eyeCamera, Camera mainCamera)
        {
            return eyeCamera != null
                && eyeCamera.transform.parent == mainCamera.transform
                && eyeCamera.gameObject.activeInHierarchy;
        }

        private static void DestroyEyeCamera(ref Camera eyeCamera)
        {
            if (eyeCamera != null)
                Object.Destroy(eyeCamera.gameObject);

            eyeCamera = null;
        }

        private static void ReleaseEyeTexture(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Object.Destroy(texture);
            }

            texture = null;
        }

        /// <summary>
        /// Creates one eye camera as a child of Main Camera's transform, offset by the
        /// headset's per-eye IPD/lens offset. Because it's parented, it automatically
        /// tracks the corrected (HMD-overlaid) Main Camera pose every frame with no extra
        /// per-frame work - Unity resolves world transforms from the hierarchy on demand,
        /// and Main Camera's transform is written (via ApplyMonoHeadPose) before this
        /// camera renders, since it's given a higher camera depth than Main Camera.
        /// </summary>
        private static Camera CreateEyeCamera(string name, Camera mainCamera, RenderTexture targetTexture,
            Matrix4x4 projection, EVREye eye)
        {
            var go = new GameObject(name);
            go.transform.SetParent(mainCamera.transform, false);

            var eyeToHead = _system.GetEyeToHeadTransform(eye);
            go.transform.localPosition = GetPosition(eyeToHead);
            go.transform.localRotation = GetRotation(eyeToHead);

            var cam = go.AddComponent<Camera>();

            // Deliberately NOT Camera.CopyFrom(mainCamera). CopyFrom clones the source's
            // render-target state as well as its visible settings, and in-game the game's
            // Main Camera renders into its own 'Main' RenderTexture (in the menu it renders
            // straight to the screen). Cloning that state made our eye cameras render
            // somewhere other than the targetTexture we assigned - they ran every frame,
            // but our RT was only ever written once and then sat frozen, which is exactly
            // the stale grey image in the headset. Copy the fields we actually want.
            cam.cullingMask = mainCamera.cullingMask;
            cam.nearClipPlane = mainCamera.nearClipPlane;
            cam.farClipPlane = mainCamera.farClipPlane;
            cam.renderingPath = mainCamera.renderingPath;
            cam.allowHDR = mainCamera.allowHDR;
            cam.orthographic = false;
            cam.layerCullSpherical = mainCamera.layerCullSpherical;
            cam.allowDynamicResolution = false;

            cam.targetTexture = targetTexture;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.depth = mainCamera.depth + (eye == EVREye.Eye_Left ? 1 : 2);

            // Occlusion culling must be OFF. It's computed against the camera's projection,
            // and the asymmetric off-centre frustum OpenVR hands us makes Unity cull away
            // essentially the whole scene - the camera then renders nothing but its clear
            // colour, which is exactly the symptom we were chasing. (The Pathologic 2 mod
            // disables this for the same reason.)
            cam.useOcclusionCulling = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.stereoSeparation = 0f;
            cam.stereoConvergence = 0f;
            cam.usePhysicalProperties = false;
            cam.allowMSAA = false;

            cam.projectionMatrix = projection;

            // Clear opaque: Main Camera clears to transparent black (alpha 0) and CopyFrom
            // inherits that. The game composites its own frame later so the alpha never
            // matters there, but we hand this texture straight to the compositor.
            // TEMP: blue rather than black, so "solid colour" (nothing drawn) is instantly
            // distinguishable from "black scene".
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 1f, 1f);

            return cam;
        }

        // ~5s at 90Hz x 2 eyes.
        private const long SubmitHeartbeatInterval = 900;
        private static long _submitCount;

        // TEMP DIAGNOSTIC: read back one pixel from the texture we're about to submit, so
        // the log says what's actually IN it. Distinguishes "the camera never rendered into
        // this texture" from "it rendered fine but the handoff to OpenVR is wrong".
        private static Texture2D _probeTexture;
        private static long _probeCount;

        private static void ProbeEyeTexture(RenderTexture rt)
        {
            // Once every ~5s - ReadPixels stalls the pipeline, so keep it rare.
            if (_probeCount++ % 300 != 0)
                return;

            // Sample our eye texture AND the game's own Main Camera render target at the
            // same moment. If the game's target is just as dark, our render matches the
            // game and the brightness comes from a later post/tonemap pass we're missing.
            // If the game's target is bright and ours is dark, our render itself is wrong.
            var eyeSample = SampleAverage(rt);
            MelonLogger.Msg($"NirvanaVR: probe - eye '{rt.name}' {rt.width}x{rt.height} sRGB={rt.sRGB} avg={eyeSample} leftRenders={_leftRenderCount}");

            var mainCameraObject = GameObject.Find("Main Camera");
            var mainCamera = mainCameraObject != null ? mainCameraObject.GetComponent<Camera>() : null;
            var gameTarget = mainCamera != null ? mainCamera.targetTexture : null;
            if (gameTarget != null)
                MelonLogger.Msg($"NirvanaVR: probe - game '{gameTarget.name}' {gameTarget.width}x{gameTarget.height} sRGB={gameTarget.sRGB} avg={SampleAverage(gameTarget)}");
            else
                MelonLogger.Msg("NirvanaVR: probe - game Main Camera has no targetTexture (renders straight to screen)");
        }

        /// <summary>
        /// Average of five sample points (centre + quarters), so a single dark object in the
        /// middle of the frame can't masquerade as "the whole image is black".
        /// </summary>
        private static Color SampleAverage(RenderTexture rt)
        {
            if (_probeTexture == null)
                _probeTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);

            var previous = RenderTexture.active;
            RenderTexture.active = rt;

            var total = new Color(0f, 0f, 0f, 0f);
            var offsets = new[] { 0.5f, 0.25f, 0.75f };
            var samples = 0;
            for (var i = 0; i < offsets.Length; i++)
            {
                var x = Mathf.Clamp(rt.width * offsets[i], 0f, rt.width - 1f);
                var y = Mathf.Clamp(rt.height * offsets[i == 0 ? 0 : (i == 1 ? 2 : 1)], 0f, rt.height - 1f);
                _probeTexture.ReadPixels(new Rect(x, y, 1f, 1f), 0, 0, false);
                _probeTexture.Apply(false);
                total += _probeTexture.GetPixel(0, 0);
                samples++;
            }

            RenderTexture.active = previous;
            return total / samples;
        }

        private static void SubmitEye(EVREye eye, RenderTexture eyeTexture)
        {
            var compositor = OpenVR.Compositor;
            if (compositor == null || eyeTexture == null)
                return;

            // Game runs on the D3D11 backend (confirmed via SystemInfo.graphicsDeviceType) -
            // revisit ETextureType if that ever changes.
            // Gamma (not Auto) to match the sRGB RenderTexture we render into.
            var texture = new Texture_t
            {
                handle = eyeTexture.GetNativeTexturePtr(),
                eType = ETextureType.DirectX,
                eColorSpace = EColorSpace.Gamma
            };

            var bounds = FullTextureBounds;
            var err = compositor.Submit(eye, ref texture, ref bounds, EVRSubmitFlags.Submit_Default);

            // Heartbeat: first submit, then roughly every 5s. Cheap, and it makes a silent
            // stall (rig quietly detached, compositor showing its "waiting" room) obvious
            // in the log instead of invisible.
            if (_submitCount == 0 || _submitCount % SubmitHeartbeatInterval == 0)
                MelonLogger.Msg($"NirvanaVR: submitting to compositor (submit #{_submitCount}, last result {err})");
            _submitCount++;

            if (err != EVRCompositorError.None)
                MelonLogger.Warning($"NirvanaVR: Compositor.Submit({eye}) failed - {err}");
        }

        // OpenVR's HmdMatrix44_t is row-major, matching Unity's Matrix4x4 m-index
        // convention (mRC = row R, col C) - a direct element copy is the correct and
        // sufficient conversion; Unity adapts it for the active graphics API (D3D
        // reversed-Z etc.) internally when it consumes camera.projectionMatrix.
        private static Matrix4x4 ToUnityMatrix(HmdMatrix44_t m)
        {
            var result = new Matrix4x4();
            result.m00 = m.m0; result.m01 = m.m1; result.m02 = m.m2; result.m03 = m.m3;
            result.m10 = m.m4; result.m11 = m.m5; result.m12 = m.m6; result.m13 = m.m7;
            result.m20 = m.m8; result.m21 = m.m9; result.m22 = m.m10; result.m23 = m.m11;
            result.m30 = m.m12; result.m31 = m.m13; result.m32 = m.m14; result.m33 = m.m15;
            return result;
        }

        private static Quaternion GetRecenteredRotation()
        {
            return Quaternion.AngleAxis(-_yawOffsetDeg, Vector3.up) * _hmdRotRaw;
        }

        private static Vector3 GetRecenteredPosition()
        {
            return Quaternion.AngleAxis(-_yawOffsetDeg, Vector3.up) * _hmdPosRaw;
        }

        // OpenVR (right-handed, -Z forward) -> Unity (left-handed, +Z forward).
        private static Vector3 GetPosition(HmdMatrix34_t m)
        {
            return new Vector3(m.m3, m.m7, -m.m11);
        }

        // D*M*D quaternion extraction from the 3x3 rotation block of the HmdMatrix34_t.
        private static Quaternion GetRotation(HmdMatrix34_t m)
        {
            var q = new Quaternion
            {
                w = Mathf.Sqrt(Mathf.Max(0f, 1f + m.m0 + m.m5 + m.m10)) * 0.5f,
                x = Mathf.Sqrt(Mathf.Max(0f, 1f + m.m0 - m.m5 - m.m10)) * 0.5f,
                y = Mathf.Sqrt(Mathf.Max(0f, 1f - m.m0 + m.m5 - m.m10)) * 0.5f,
                z = Mathf.Sqrt(Mathf.Max(0f, 1f - m.m0 - m.m5 + m.m10)) * 0.5f
            };

            q.x = CopySign(q.x, m.m6 - m.m9);
            q.y = CopySign(q.y, m.m8 - m.m2);
            q.z = CopySign(q.z, m.m4 - m.m1);

            return q;
        }

        private static float CopySign(float sizeval, float signval)
        {
            return signval >= 0f ? Mathf.Abs(sizeval) : -Mathf.Abs(sizeval);
        }
    }
}
