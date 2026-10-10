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

        // Per-eye sub-rectangle of the rendered image that corresponds to that eye's true
        // frustum. We render a symmetric frustum big enough to contain the eye's asymmetric
        // one (symmetric renders correctly; asymmetric renders nothing but background) and
        // let the compositor crop. The mapping is linear in tangent space, so the result is
        // geometrically exact rather than an approximation.
        private static VRTextureBounds_t _leftBounds = new VRTextureBounds_t { uMin = 0f, vMin = 1f, uMax = 1f, vMax = 0f };
        private static VRTextureBounds_t _rightBounds = new VRTextureBounds_t { uMin = 0f, vMin = 1f, uMax = 1f, vMax = 0f };

        // The same crop expressed as a UV rectangle, applied by our own blit so the eye's
        // view fills the whole submit texture. Full-frame by default.
        private static Rect _leftCrop = new Rect(0f, 0f, 1f, 1f);
        private static Rect _rightCrop = new Rect(0f, 0f, 1f, 1f);

        // One stable RenderTexture per eye, created once and never swapped. An earlier
        // version double-buffered these and re-pointed camera.targetTexture every frame to
        // dodge a compositor race - but the real cause of that race was submitting from
        // inside the render callback (see OnEndCameraRendering), and the buffer juggling
        // only desynced what was rendered from what was submitted.
        // Two textures per eye, the same split the Pathologic 2 mod uses:
        //
        //  - the RENDER target, whose descriptor is cloned from the game's own so the
        //    pipeline is willing to render a camera into it. This game's is
        //    RGB111110Float - a packed HDR format the OpenVR compositor rejects outright
        //    with TextureUsesUnsupportedFormat.
        //  - the SUBMIT texture, a plain ARGB32 sRGB surface the compositor accepts. We
        //    blit render -> submit each frame, which also does the linear->gamma encode.
        private static RenderTexture _leftEyeTexture;
        private static RenderTexture _rightEyeTexture;
        private static RenderTexture _leftSubmitTexture;
        private static RenderTexture _rightSubmitTexture;

        // TEMP EXPERIMENT toggle - see the comment where the eye textures are allocated.
        // F11 flips this at runtime and forces a rig rebuild, so we can compare both sizes
        // in a single session instead of one game restart per attempt.
        private static bool UseScreenSizedEyeTextures = false;

        // Mono mirror mode (F12). Instead of rendering the scene ourselves, copy the game's
        // own finished frame into both eye textures. No parallax, but the view is still
        // head-tracked (the game's camera carries the HMD pose), and it isolates the
        // question of whether anything is wrong downstream of rendering: the pixels here
        // are known-good, so if the headset shows them, the submit path is fine and the
        // fault is purely in our eye cameras' rendering.
        private static bool _mirrorGameFrameToEyes = true;
        private static Camera _gameMainCamera;

        // Which eye the game's own render is being aimed at this frame. Camera.Render()
        // does nothing under a Scriptable Render Pipeline (it fails silently - no
        // exception, no pixels), so we can't render the scene on demand. Instead we ride
        // the game's single normal render: offset the camera to one eye, let the game draw
        // its frame, copy the result to that eye, and alternate. Both eyes stay submitted
        // every frame, so each simply refreshes at half the game's rate.
        private static EVREye _currentEye = EVREye.Eye_Left;

        // The eye the game's most recent render was actually set up for. Read next frame,
        // when that render's result is sitting in the game's target texture ready to copy.
        private static EVREye _eyeOfLastRender = EVREye.Eye_Left;

        // Whether to override the game camera's projection with the HMD's per-eye frustum.
        // Off by default: doing so renders grey. Parallax works without it; only the field
        // of view is then the game's rather than the headset's.
        private static bool _applyEyeProjection;

        // Widen the game's FOV to the headset's using camera.fieldOfView, which leaves
        // Unity's implicit projection in place. On by default in stereo: a ~45 degree image
        // stretched across ~100+ degrees of lens can't fuse no matter how good the parallax.
        // Multiplier on the eye separation - larger means stronger depth. F5/F6 adjust.
        private static float _ipdScale = 1f;

        // Metres to pull the viewpoint back along the view axis. F3/F4 adjust.
        private static float _cameraPullback;

        // Scales the crop relative to what was rendered. Only used by the symmetric+crop
        // fallback; the asymmetric path needs no crop at all.
        private static float _fovCalibration = 1f;

        // Submit each eye the instant it finishes rendering (as Pathologic 2 does) instead
        // of both together a frame later. F8 toggles. Submitting mid-pipeline previously
        // hung the compositor, so this stays opt-in until proven safe here.
        private static bool _submitImmediately;

        // Scales HMD translation into game units - effectively the world scale. F1/F2 adjust.
        private static float _headTranslationScale = 1f;

        // Take only the game camera's yaw as the base for the head pose, so the headset
        // defines which way is up. F10 toggles, for comparison.
        private static bool _levelHorizon = true;

        // Use the headset's true off-centre frustum on the eye cameras (F8 toggles). This is
        // what Pathologic 2 does and why it has no fishbowl distortion.
        private static bool _useAsymmetricEyeProjection = true;

        private static bool _matchHmdFieldOfView = true;
        private static float _originalFieldOfView;
        private static bool _haveOriginalFieldOfView;

        // CONTROL TEST (F7): assign an explicit projection matrix that Unity itself
        // generates, with the camera's own parameters - i.e. mathematically identical to
        // what the camera already uses, differing only in being set explicitly. If this
        // greys the screen too, the pipeline breaks on ANY explicit projection and the
        // OpenVR matrix is blameless; if it renders fine, our matrix is the problem.
        private static bool _applyUnityPerspectiveTest;

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

            // Copy FIRST, then submit. The eye render targets hold last frame's render, so
            // copying before submitting hands the compositor a one-frame-old image instead
            // of a two-frame-old one. The old order (submit, then copy) meant we submitted
            // the copy made last frame - which had itself captured the frame before that -
            // while the compositor reprojected it as if current. That extra frame of stale
            // pose is what makes the world swim and warp as the head turns.
            MirrorGameFrameToEyes();
            CaptureRenderedEye();

            // Skipped when submitting immediately from the render callback instead.
            if (!_submitImmediately && _stereoRigCreated
                && _leftSubmitTexture != null && _rightSubmitTexture != null)
            {
                ProbeEyeTexture(_leftSubmitTexture);
                SubmitEye(EVREye.Eye_Left, _leftSubmitTexture);
                SubmitEye(EVREye.Eye_Right, _rightSubmitTexture);
            }

            // Submit-then-wait is the intended OpenVR order: the wait blocks until the
            // compositor is ready for the next frame, so the poses we get back are the
            // freshest possible for the render we are about to drive.
            compositor.WaitGetPoses(RenderPoses, GamePoses);

            ReassertEyeCameras();

            var hmdPose = RenderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!hmdPose.bPoseIsValid)
                return;

            var m = hmdPose.mDeviceToAbsoluteTracking;
            _hmdPosRaw = GetPosition(m);
            _hmdRotRaw = GetRotation(m);
        }

        /// <summary>
        /// Copies both eye cameras' output into the textures we hand to the compositor.
        /// Runs from LateUpdate, where the previous frame's rendering is complete and we
        /// are well clear of the pipeline's own work - Graphics.Blit executes immediately
        /// and issuing it mid-pipeline produced a flat grey image.
        ///
        /// The eye cameras render into targets whose descriptor is cloned from the game's
        /// (RGB111110Float - the only format this pipeline will render a camera into), and
        /// this copy converts that HDR content into the 8-bit sRGB surface OpenVR accepts.
        /// Both eyes come from the SAME frame, so there is no temporal mismatch between them.
        /// </summary>
        private static void CaptureRenderedEye()
        {
            if (_mirrorGameFrameToEyes || !_stereoRigCreated)
                return;

            if (_leftEyeTexture == null || _rightEyeTexture == null
                || _leftSubmitTexture == null || _rightSubmitTexture == null)
                return;

            // Blit the eye's sub-rectangle so it fills the submit texture. With the true
            // asymmetric frustum the crop is the whole frame; with the symmetric fallback it
            // selects the part matching the eye's real frustum.
            BlitEye(_leftEyeTexture, _leftSubmitTexture, _leftCrop);
            BlitEye(_rightEyeTexture, _rightSubmitTexture, _rightCrop);
        }

        private static void CopyAndSubmitEye(EVREye eye, RenderTexture source, RenderTexture destination, Rect crop)
        {
            if (source == null || destination == null)
                return;

            BlitEye(source, destination, crop);
            SubmitEye(eye, destination);
        }

        private static void BlitEye(RenderTexture source, RenderTexture destination, Rect crop)
        {
            if (crop.width <= 0f || crop.height <= 0f)
            {
                Graphics.Blit(source, destination);
                return;
            }

            Graphics.Blit(source, destination,
                new Vector2(crop.width, crop.height),
                new Vector2(crop.x, crop.y));
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
            if (source == null || _leftSubmitTexture == null || _rightSubmitTexture == null)
                return;

            Graphics.Blit(source, _leftSubmitTexture);
            Graphics.Blit(source, _rightSubmitTexture);

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
            // Parented to the game camera, so this local offset rides on top of the head
            // pose written during the game camera's own render. Scaled by _ipdScale (F5/F6).
            var eyeToHead = _system.GetEyeToHeadTransform(eye);
            eyeCamera.transform.localPosition = GetPosition(eyeToHead) * _ipdScale;

            // Identity, exactly as Pathologic 2 does - it takes the eye's POSITION from
            // eyeToHead but deliberately ignores its rotation. Any error in that quaternion
            // rotates the two eyes differently, which introduces vertical disparity that
            // grows towards the edges of the field and changes with head angle: a very close
            // match for the distortion here, which survives both narrow FOV and no crop.
            eyeCamera.transform.localRotation = Quaternion.identity;
            eyeCamera.useOcclusionCulling = false;
            eyeCamera.nearClipPlane = _gameMainCamera != null ? _gameMainCamera.nearClipPlane : eyeCamera.nearClipPlane;
            eyeCamera.farClipPlane = _gameMainCamera != null ? _gameMainCamera.farClipPlane : eyeCamera.farClipPlane;

            ApplyEyeCameraProjection(eyeCamera, eye);
        }

        /// <summary>
        /// Gives an eye camera its frustum. Two ways, switchable with F8:
        ///
        ///  - asymmetric (default): the headset's true off-centre frustum, exactly what
        ///    Pathologic 2 uses. Geometrically correct, so no fishbowl warping. This greyed
        ///    the screen when applied to the GAME's camera, but that camera also feeds the
        ///    game's own post-processing; a private camera of ours may well tolerate it.
        ///  - symmetric + crop: a wider symmetric frustum, with the eye's real extents
        ///    selected afterwards via the compositor's texture bounds. Always renders, but
        ///    the crop is an approximation and warps as the head moves.
        /// </summary>
        private static void ApplyEyeCameraProjection(Camera eyeCamera, EVREye eye)
        {
            if (_useAsymmetricEyeProjection)
            {
                // Build the off-centre frustum with Unity's own constructor from the raw
                // tangents, rather than copying OpenVR's matrix element-for-element. Those
                // two disagree about the vertical convention - OpenVR reports top negative
                // and bottom positive, which flips the sign of the vertical off-centre term
                // when pasted straight into a Unity matrix. Matrix4x4.Frustum takes plain
                // edge distances at the near plane and cannot be misinterpreted.
                float rawLeft = 0f, rawRight = 0f, rawTop = 0f, rawBottom = 0f;
                _system.GetProjectionRaw(eye, ref rawLeft, ref rawRight, ref rawTop, ref rawBottom);

                var near = eyeCamera.nearClipPlane;
                var far = eyeCamera.farClipPlane;

                // Sorted into Unity's y-up convention, exactly as the crop path does.
                var frustumLeft = Mathf.Min(rawLeft, rawRight) * near;
                var frustumRight = Mathf.Max(rawLeft, rawRight) * near;
                // NOT negated. OpenVR's pfTop/pfBottom are already signed offsets in the
                // same sense Unity wants - pfTop comes back negative and pfBottom positive,
                // so they only need sorting. Negating them flipped the frustum vertically:
                // we were rendering ~1.43 up / 0.97 down where the headset actually wants
                // 0.97 up / 1.43 down. Cross-checked against GetProjectionMatrix, whose
                // m11/m12 imply top=+0.966, bottom=-1.428 for this eye.
                var frustumBottom = Mathf.Min(rawTop, rawBottom) * near;
                var frustumTop = Mathf.Max(rawTop, rawBottom) * near;

                var projection = Matrix4x4.Frustum(
                    frustumLeft, frustumRight, frustumBottom, frustumTop, near, far);

                eyeCamera.projectionMatrix = projection;
                eyeCamera.nonJitteredProjectionMatrix = projection;

                // With the off-centre matrix the camera was not merely drawing an empty
                // frame - it was not being rendered AT ALL (its end-of-render callback never
                // fired). That is what LightweightRP does when Camera.TryGetCullingParameters
                // fails: "if (!camera.TryGetCullingParameters(...)) return;", silently.
                //
                // So give it a culling frustum it will accept: a WIDE but ordinary
                // perspective frustum that comfortably contains the real one. The previous
                // attempt used Matrix4x4.Ortho with +/-99999 extents, which is extreme enough
                // to fail that same validation - fixing nothing.
                eyeCamera.cullingMatrix = Matrix4x4.Perspective(
                    150f, 1f, eyeCamera.nearClipPlane, eyeCamera.farClipPlane)
                    * eyeCamera.worldToCameraMatrix;
                eyeCamera.useOcclusionCulling = false;

                // The eye frustum IS the rendered frustum, so no cropping - and reset it, in
                // case we've just toggled back from the symmetric fallback.
                var fullBounds = FullTextureBounds;
                var fullCrop = new Rect(0f, 0f, 1f, 1f);
                if (eye == EVREye.Eye_Left)
                {
                    _leftBounds = fullBounds;
                    _leftCrop = fullCrop;
                }
                else
                {
                    _rightBounds = fullBounds;
                    _rightCrop = fullCrop;
                }

                return;
            }

            if (!_matchHmdFieldOfView)
            {
                // Render at the game's own narrow field of view, with no widening and no
                // crop. The picture will look magnified - that is expected and not the
                // point. The question is whether the distortion SURVIVES at 45 degrees: if
                // it disappears, rendering this game's scenes at ~110 degrees is what
                // produces it; if it persists, the field of view is innocent and the fault
                // is elsewhere entirely.
                // Match the game camera's projection EXACTLY - field of view AND aspect. The
                // earlier version of this test matched only the FOV and took aspect from the
                // texture, so the projections still differed. That matters if the game's
                // renderer feeds screen-space effects from the main camera's matrices: our
                // cameras would then be shading with values computed for a different
                // projection, giving a warp that varies with view angle and survives any
                // FOV change.
                eyeCamera.ResetProjectionMatrix();
                if (_gameMainCamera != null)
                {
                    eyeCamera.fieldOfView = _gameMainCamera.fieldOfView;
                    eyeCamera.aspect = _gameMainCamera.aspect;
                }

                var fullCropOnly = new Rect(0f, 0f, 1f, 1f);
                if (eye == EVREye.Eye_Left)
                    _leftCrop = fullCropOnly;
                else
                    _rightCrop = fullCropOnly;

                return;
            }

            ApplySymmetricEyeFrustum(eyeCamera, eye);
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
                ApplyEyeCameraProjection(camera, EVREye.Eye_Left);
                return;
            }

            if (camera == _rightEyeCamera)
                ApplyEyeCameraProjection(camera, EVREye.Eye_Right);
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

            // Compose the head pose against the game camera's YAW only, discarding its pitch
            // and roll. This game frames its shots cinematically - tilted, pitched cameras -
            // and composing inside that tilted frame means turning your head rotates the
            // world about a tilted axis, so the horizon skews and the error grows the
            // further you look from centre. That is static, depends on head angle, and shows
            // up in every mode including the plain mirror, which is exactly what we see. It
            // is also invisible on a flat monitor, which is why it passed in Phase 1.
            // Levelling the base lets the headset define up, as it must in VR.
            var effectiveBaseRot = _levelHorizon
                ? Quaternion.Euler(0f, baseRot.eulerAngles.y, 0f)
                : baseRot;

            var correctedRot = effectiveBaseRot * hmdRot;
            var correctedPos = basePos + effectiveBaseRot * hmdPos;

            // The game camera now only ever carries the head pose, plus an optional
            // pullback along its OWN view axis (using the head rotation here would make the
            // offset a lever arm and swing the viewpoint around the room when you turn).
            // Per-eye offsets and frustums live on the two dedicated eye cameras, which
            // render the scene themselves - so nothing here disturbs the game's rendering.
            if (!_mirrorGameFrameToEyes && _cameraPullback != 0f)
                correctedPos += baseRot * new Vector3(0f, 0f, -_cameraPullback);

            t.SetPositionAndRotation(correctedPos, correctedRot);

            // Store what we actually wrote (eye offset included) so next frame's guard
            // still recognises our own output and doesn't compound the offset.
            _lastAppliedPos = correctedPos;
            _lastAppliedRot = correctedRot;
            _haveLastApplied = true;
        }

        /// <summary>
        /// RenderPipelineManager.endCameraRendering handler. In stereo mode the game's own
        /// camera has just drawn this frame from one eye's viewpoint, so copy that result
        /// into the matching eye's submit texture. The copy also converts the game's HDR
        /// render target into the 8-bit sRGB surface the compositor will accept.
        ///
        /// Nothing is submitted from here: this fires while the pipeline is still recording
        /// commands, and handing a texture to OpenVR before the GPU has actually run them
        /// raced the compositor badly enough to hang the game. Submission happens a frame
        /// later, from PumpPoses.
        /// </summary>
        public static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            // Nothing is copied or submitted from here. Graphics.Blit runs immediately and
            // doesn't mix with an SRP that is still recording this frame's commands - doing
            // the eye copy here produced flat grey, the same way submitting from here used
            // to hang the compositor. Both happen from LateUpdate instead.
            if (camera == _leftEyeCamera)
                _leftRenderCount++;

            // Pure maths, no GPU work, so it's safe here - and here is the only place the
            // projection the camera ACTUALLY rendered with is guaranteed current.
            if (_mirrorGameFrameToEyes || !_stereoRigCreated)
                return;

            // Match Pathologic 2's timing exactly: copy and submit each eye the moment that
            // eye finishes rendering, in the same frame, rather than both together a frame
            // later from LateUpdate. That is now the only structural difference left between
            // the two mods - P2 renders correct geometry and displays correctly on this
            // machine, ours renders correct geometry and displays distorted.
            if (_submitImmediately)
            {
                if (camera == _leftEyeCamera)
                    CopyAndSubmitEye(EVREye.Eye_Left, _leftEyeTexture, _leftSubmitTexture, _leftCrop);
                else if (camera == _rightEyeCamera)
                    CopyAndSubmitEye(EVREye.Eye_Right, _rightEyeTexture, _rightSubmitTexture, _rightCrop);
            }

            if (_useAsymmetricEyeProjection)
            {
                // No crop needed in this mode, but verify the claim it rests on: that the
                // frustum the camera ACTUALLY rendered matches the headset's. If these two
                // agree and the image still looks wrong, the fault is not in our rendering.
                if (camera == _leftEyeCamera)
                    VerifyRenderedFrustum(camera, EVREye.Eye_Left);
                return;
            }

            // Only needed for the symmetric+crop fallback; with the true asymmetric frustum
            // the whole texture is the eye view.
            if (camera == _leftEyeCamera)
                UpdateEyeBoundsFromRenderedProjection(camera, EVREye.Eye_Left);
            else if (camera == _rightEyeCamera)
                UpdateEyeBoundsFromRenderedProjection(camera, EVREye.Eye_Right);
        }

        /// <summary>
        /// Works out which part of the just-rendered image corresponds to this eye's true
        /// frustum, by reading back the projection the camera actually used.
        ///
        /// Deriving this from the fieldOfView/aspect we requested is not reliable: Unity
        /// snapshots camera state at its own moment and Cinemachine drives fieldOfView from
        /// its LateUpdate, so the values that reach the renderer may not be ours. A crop
        /// that doesn't match the render is exactly what makes the world appear magnified
        /// and warp like a fishbowl when the head moves, because the compositor then
        /// stretches the image non-uniformly across the lens.
        ///
        /// For a standard projection matrix, the frustum's tangent extents come straight
        /// out of it: with a = m00 and c = m02, the horizontal tangents are (c+1)/a and
        /// (c-1)/a; likewise vertically from m11 and m12.
        /// </summary>
        /// <summary>
        /// Compares the frustum the camera actually rendered against the headset's own, both
        /// as tangents. If they match, the submitted image is geometrically what OpenVR
        /// expects and nothing in our rendering can be causing the distortion.
        /// </summary>
        private static void VerifyRenderedFrustum(Camera camera, EVREye eye)
        {
            if (_verifyLogCount++ % 150 != 0)
                return;

            var projection = camera.projectionMatrix;
            var a = projection.m00;
            var b = projection.m11;
            if (Mathf.Abs(a) < 1e-6f || Mathf.Abs(b) < 1e-6f)
                return;

            var renderedLeft = Mathf.Min((projection.m02 + 1f) / a, (projection.m02 - 1f) / a);
            var renderedRight = Mathf.Max((projection.m02 + 1f) / a, (projection.m02 - 1f) / a);
            var renderedBottom = Mathf.Min((projection.m12 + 1f) / b, (projection.m12 - 1f) / b);
            var renderedTop = Mathf.Max((projection.m12 + 1f) / b, (projection.m12 - 1f) / b);

            // Derive the reference from GetProjectionMatrix - the call Pathologic 2 uses -
            // rather than from our own reading of GetProjectionRaw. Checking our frustum
            // against our own interpretation of the raw values is what let a vertical sign
            // error report a perfect match: both sides shared the same mistake.
            var reference = ToUnityMatrix(_system.GetProjectionMatrix(
                eye, camera.nearClipPlane, camera.farClipPlane));
            var ra = reference.m00;
            var rb = reference.m11;
            if (Mathf.Abs(ra) < 1e-6f || Mathf.Abs(rb) < 1e-6f)
                return;

            var eyeLeft = Mathf.Min((reference.m02 + 1f) / ra, (reference.m02 - 1f) / ra);
            var eyeRight = Mathf.Max((reference.m02 + 1f) / ra, (reference.m02 - 1f) / ra);
            var eyeBottom = Mathf.Min((reference.m12 + 1f) / rb, (reference.m12 - 1f) / rb);
            var eyeTop = Mathf.Max((reference.m12 + 1f) / rb, (reference.m12 - 1f) / rb);

            var worstError = Mathf.Max(
                Mathf.Max(Mathf.Abs(renderedLeft - eyeLeft), Mathf.Abs(renderedRight - eyeRight)),
                Mathf.Max(Mathf.Abs(renderedBottom - eyeBottom), Mathf.Abs(renderedTop - eyeTop)));

            MelonLogger.Msg($"NirvanaVR: verify rendered x[{renderedLeft:F3},{renderedRight:F3}] y[{renderedBottom:F3},{renderedTop:F3}] " +
                $"| headset x[{eyeLeft:F3},{eyeRight:F3}] y[{eyeBottom:F3},{eyeTop:F3}] " +
                $"| worst error {worstError:F4} -> {(worstError < 0.01f ? "MATCH (our render is correct)" : "MISMATCH (this is the distortion)")} " +
                $"| target {(camera.targetTexture != null ? camera.targetTexture.width + "x" + camera.targetTexture.height : "none")}");
        }

        private static long _verifyLogCount;

        private static void UpdateEyeBoundsFromRenderedProjection(Camera camera, EVREye eye)
        {
            var projection = camera.projectionMatrix;
            var a = projection.m00;
            var b = projection.m11;
            if (Mathf.Abs(a) < 1e-6f || Mathf.Abs(b) < 1e-6f)
                return;

            // Sort rather than assume an ordering. Unity can hand back a matrix with a
            // negative m11 (it flips Y when rendering into a texture on D3D), which silently
            // inverts these extents - and an inverted crop rotates the submitted image 180
            // degrees, which reads as head tracking running backwards on both axes.
            var renderedLeft = Mathf.Min((projection.m02 + 1f) / a, (projection.m02 - 1f) / a);
            var renderedRight = Mathf.Max((projection.m02 + 1f) / a, (projection.m02 - 1f) / a);
            var renderedBottom = Mathf.Min((projection.m12 + 1f) / b, (projection.m12 - 1f) / b);
            var renderedTop = Mathf.Max((projection.m12 + 1f) / b, (projection.m12 - 1f) / b);

            var width = renderedRight - renderedLeft;
            var height = renderedTop - renderedBottom;
            if (width < 1e-6f || height < 1e-6f)
                return;

            float rawLeft = 0f, rawRight = 0f, rawTop = 0f, rawBottom = 0f;
            _system.GetProjectionRaw(eye, ref rawLeft, ref rawRight, ref rawTop, ref rawBottom);

            // Sorted, NOT negated - see the frustum construction for why.
            var eyeLeft = Mathf.Min(rawLeft, rawRight);
            var eyeRight = Mathf.Max(rawLeft, rawRight);
            var eyeBottom = Mathf.Min(rawTop, rawBottom);
            var eyeTop = Mathf.Max(rawTop, rawBottom);

            // Calibration (F1/F2). Every step of this geometry checks out on paper, yet the
            // image is still distorted in a way that scales with viewing angle - the
            // signature of the rendered field of view not matching the displayed one. This
            // scales the crop about its centre, so if some factor makes the distortion
            // vanish, that factor IS the mismatch and tells us what the real ratio should be.
            if (Mathf.Abs(_fovCalibration - 1f) > 1e-4f)
            {
                var centreX = (eyeLeft + eyeRight) * 0.5f;
                var centreY = (eyeBottom + eyeTop) * 0.5f;
                eyeLeft = centreX + (eyeLeft - centreX) * _fovCalibration;
                eyeRight = centreX + (eyeRight - centreX) * _fovCalibration;
                eyeBottom = centreY + (eyeBottom - centreY) * _fovCalibration;
                eyeTop = centreY + (eyeTop - centreY) * _fovCalibration;
            }

            var uLeft = Mathf.Clamp01((eyeLeft - renderedLeft) / width);
            var uRight = Mathf.Clamp01((eyeRight - renderedLeft) / width);
            var vBottom = Mathf.Clamp01((eyeBottom - renderedBottom) / height);
            var vTop = Mathf.Clamp01((eyeTop - renderedBottom) / height);

            // u ascending; v descending, which is the vertical flip a Unity RenderTexture
            // needs on D3D11 - the same orientation the full-frame bounds used.
            // Store the crop as a plain UV rectangle. We apply it ourselves when copying into
            // the submit texture rather than passing it to the compositor as texture bounds:
            // the distortion is static and scales with viewing angle, which is what you get
            // when the full symmetric render is stretched onto the narrower eye frustum -
            // i.e. exactly as if the bounds were being ignored. Doing the crop in our own
            // blit removes the dependency on how the compositor interprets them.
            var crop = new Rect(uLeft, Mathf.Min(vTop, vBottom), uRight - uLeft, Mathf.Abs(vTop - vBottom));

            if (eye == EVREye.Eye_Left)
                _leftCrop = crop;
            else
                _rightCrop = crop;

            var bounds = new VRTextureBounds_t
            {
                uMin = uLeft,
                uMax = uRight,
                vMin = vTop,
                vMax = vBottom
            };

            if (eye == EVREye.Eye_Left)
                _leftBounds = bounds;
            else
                _rightBounds = bounds;

            // TEMP DIAGNOSTIC, ~every 5s: is the camera actually rendering at the headset's
            // field of view, and does the eye's frustum fit inside what was rendered? If the
            // render is still the game's ~45 degrees, the eye extents overflow it, the crop
            // clamps to the whole image for both eyes, and nothing can fuse.
            if (eye == EVREye.Eye_Left && _boundsLogCount++ % 150 == 0)
            {
                // State of every toggle, so the log says exactly which configuration
                // produced a given result instead of us tracking key presses by memory.
                MelonLogger.Msg($"NirvanaVR: config mode={(_mirrorGameFrameToEyes ? "MIRROR" : "STEREO")} " +
                    $"frustum={(_useAsymmetricEyeProjection ? "asymmetric" : "symmetric+crop")} " +
                    $"fov={(_matchHmdFieldOfView ? "headset" : "game")} horizon={(_levelHorizon ? "level" : "game")} " +
                    $"ipd={_ipdScale:F2} pullback={_cameraPullback:F2} fovCal={_fovCalibration:F2}");

                // Print BOTH eyes: sampling one every N calls always lands on the same eye,
                // which would hide the right eye silently keeping default full-texture
                // bounds - a left/right mismatch that would itself look like distortion.
                MelonLogger.Msg($"NirvanaVR: bounds L u[{_leftBounds.uMin:F3},{_leftBounds.uMax:F3}] v[{_leftBounds.vMin:F3},{_leftBounds.vMax:F3}] " +
                    $"| R u[{_rightBounds.uMin:F3},{_rightBounds.uMax:F3}] v[{_rightBounds.vMin:F3},{_rightBounds.vMax:F3}] " +
                    $"| rendered x[{renderedLeft:F3},{renderedRight:F3}] y[{renderedBottom:F3},{renderedTop:F3}] " +
                    $"| eyeL x[{eyeLeft:F3},{eyeRight:F3}] y[{eyeBottom:F3},{eyeTop:F3}]");
            }
        }

        private static long _boundsLogCount;

        // TEMP DIAGNOSTIC: how many times the SRP has actually run the left eye camera.
        // If this climbs while the probed texture stays bit-identical, the camera is being
        // rendered but its output isn't landing in targetTexture. If it stops climbing,
        // the render pipeline has quietly stopped scheduling our camera at all.
        private static long _leftRenderCount;

        // TEMP DIAGNOSTIC: F10 points the left eye at the screen instead of its RenderTexture,
        // so we can see on the monitor whether it produces a live image or a frozen one.
        private static bool _mirrorLeftEyeToScreen;

        /// <summary>
        /// The cloned eye cameras stay disabled in every mode. Neither path uses them any
        /// more - mirror mode blits the game's finished frame, stereo mode re-renders the
        /// game's own camera - and leaving them enabled actively broke stereo: they'd
        /// render and OnEndCameraRendering would overwrite the pending submit textures with
        /// their HDR render targets, which the compositor rejects with
        /// TextureUsesUnsupportedFormat. They're kept purely as the scene-change sentinel
        /// that IsEyeRigAttachedTo watches.
        /// </summary>
        private static void ApplyEyeCameraEnabledForMode()
        {
            if (_leftEyeCamera == null || _rightEyeCamera == null)
                return;

            // Enabled in stereo mode: these are what actually render the two eye views now,
            // both within the same frame - which is what removes the temporal mismatch that
            // made the alternating single-camera approach double up during head movement.
            _leftEyeCamera.enabled = !_mirrorGameFrameToEyes;
            _rightEyeCamera.enabled = !_mirrorGameFrameToEyes;
        }

        /// <summary>
        /// Applies the HMD's per-eye frustum to the game camera, from LateUpdate - i.e.
        /// before the pipeline runs, so the culling matrix set here is actually the one
        /// used to cull this frame.
        ///
        /// The off-centre frustum is the whole difficulty: an explicit but SYMMETRIC matrix
        /// renders fine (the F7 control test), while the HMD's asymmetric one renders
        /// nothing but background, because Unity culls the scene against it. So we pair the
        /// real projection with a deliberately enormous culling frustum: the projection
        /// decides what's drawn, the culling matrix ensures nothing is thrown away first.
        ///
        /// _currentEye has already been flipped by CaptureRenderedEye at this point, so it
        /// refers to the eye the upcoming render is for.
        /// </summary>
        private static void ApplyEyeProjection()
        {
            if (_mirrorGameFrameToEyes || !_stereoRigCreated || _gameMainCamera == null)
                return;

            var camera = _gameMainCamera;

            if (_matchHmdFieldOfView)
                ApplySymmetricEyeFrustum(camera, _currentEye);

            if (!_applyEyeProjection)
                return;

            // Built from the camera's CURRENT clip planes: shaders derive _ZBufferParams
            // from nearClipPlane/farClipPlane, so a matrix encoding different planes than
            // the camera advertises makes depth-based effects reconstruct nonsense.
            var projection = ToUnityMatrix(_system.GetProjectionMatrix(
                _currentEye, camera.nearClipPlane, camera.farClipPlane));

            camera.projectionMatrix = projection;
            camera.nonJitteredProjectionMatrix = projection;
            camera.cullingMatrix = Matrix4x4.Ortho(-99999f, 99999f, -99999f, 99999f, 0.001f, 99999f)
                * camera.worldToCameraMatrix;
            camera.useOcclusionCulling = false;
        }

        /// <summary>
        /// Gives the camera a SYMMETRIC frustum wide enough to contain this eye's real
        /// (off-centre) frustum, and works out which sub-rectangle of the resulting image
        /// the eye actually occupies. Uses fieldOfView/aspect, so Unity keeps its implicit
        /// projection - no explicit matrix, which is what breaks this game's rendering.
        ///
        /// OpenVR's raw values are tangents of the half-angles, with y measured downwards,
        /// so top comes out negative and bottom positive; negating them puts us in Unity's
        /// y-up convention.
        /// </summary>
        private static void ApplySymmetricEyeFrustum(Camera camera, EVREye eye)
        {
            float rawLeft = 0f, rawRight = 0f, rawTop = 0f, rawBottom = 0f;
            _system.GetProjectionRaw(eye, ref rawLeft, ref rawRight, ref rawTop, ref rawBottom);

            var xMin = rawLeft;
            var xMax = rawRight;
            var yMin = -rawBottom;
            var yMax = -rawTop;

            var tanX = Mathf.Max(Mathf.Abs(xMin), Mathf.Abs(xMax));
            var tanY = Mathf.Max(Mathf.Abs(yMin), Mathf.Abs(yMax));
            if (tanX <= 0f || tanY <= 0f)
                return;

            if (!_haveOriginalFieldOfView)
            {
                _originalFieldOfView = camera.fieldOfView;
                _haveOriginalFieldOfView = true;
            }

            // Small margin so the eye's frustum sits just inside what we render. The old
            // 1.35 was compensating for the game camera rendering ~0.84x the frustum it was
            // asked for; our own cameras honour the explicit matrix exactly (requested tanY
            // 1.928 came back as 1.928), so the padding is no longer needed - and shrinking
            // it hands a much larger share of the texture's pixels to the visible region.
            tanX *= 1.08f;
            tanY *= 1.08f;

            var fovY = 2f * Mathf.Atan(tanY) * Mathf.Rad2Deg;
            var aspect = tanX / tanY;

            camera.fieldOfView = fovY;
            camera.aspect = aspect;

            // Set the matrix explicitly rather than trusting fieldOfView/aspect to produce
            // it. They don't: the camera reported fov=110 while actually rendering ~100
            // (gate fit reinterprets the vertical FOV once aspect is overridden), so the
            // eye's frustum overflowed the rendered one and the crop clamped on both axes -
            // which is what kept the images from fusing. A SYMMETRIC explicit matrix renders
            // correctly here (that was the F7 control test); only off-centre ones fail.
            var projection = Matrix4x4.Perspective(fovY, aspect, camera.nearClipPlane, camera.farClipPlane);
            camera.projectionMatrix = projection;
            camera.nonJitteredProjectionMatrix = projection;

            // The crop rectangle is NOT computed here - it's derived after the render, from
            // the projection the camera actually used (see
            // UpdateEyeBoundsFromRenderedProjection), because the values we set here may not
            // be the ones that reach the renderer.
        }

        /// <summary>
        /// F1/F2: scale how far HMD movement carries in the game world. 1.0 assumes the game
        /// is built at 1 unit = 1 metre; if it isn't, head movement produces the wrong
        /// parallax and the world appears to swell and shrink as you turn.
        /// </summary>
        public static void AdjustHeadTranslationScale(float delta)
        {
            _headTranslationScale = Mathf.Clamp(_headTranslationScale + delta, 0f, 8f);
            MelonLogger.Msg($"NirvanaVR: head translation scale {_headTranslationScale:F2}x " +
                "(0 = rotation only, no positional tracking at all)");
        }

        /// <summary>F3/F4: move the viewpoint back from / closer to the scene.</summary>
        public static void AdjustCameraPullback(float delta)
        {
            _cameraPullback = Mathf.Clamp(_cameraPullback + delta, -2f, 20f);
            MelonLogger.Msg($"NirvanaVR: camera pullback {_cameraPullback:F2}m");
        }

        /// <summary>F5/F6: weaken or strengthen the stereo depth.</summary>
        public static void AdjustIpdScale(float delta)
        {
            _ipdScale = Mathf.Clamp(_ipdScale + delta, 0.1f, 6f);
            MelonLogger.Msg($"NirvanaVR: IPD scale {_ipdScale:F2}x");
        }

        /// <summary>
        /// F7: render at the headset's wide field of view (with crop) versus the game's own
        /// narrow one (no crop). Isolates whether wide-FOV rendering causes the distortion.
        /// </summary>
        public static void ToggleUnityPerspectiveTest()
        {
            _matchHmdFieldOfView = !_matchHmdFieldOfView;
            MelonLogger.Msg($"NirvanaVR: field of view = {(_matchHmdFieldOfView ? "HEADSET (~110 deg, cropped to the eye frustum)" : "GAME (~45 deg, uncropped - will look magnified)")}");
        }

        /// <summary>
        /// F8: toggle the per-eye projection override, so it can be tested without a
        /// rebuild. Expect grey while it's on until the matrix problem is solved.
        /// </summary>
        public static void ToggleEyeProjection()
        {
            _submitImmediately = !_submitImmediately;
            MelonLogger.Msg($"NirvanaVR: submit timing = {(_submitImmediately ? "IMMEDIATE (same frame, in the render callback - Pathologic 2's timing)" : "DEFERRED (both eyes next frame from LateUpdate)")}");
        }

        /// <summary>
        /// F12: switch between mirroring the game's own frame (mono, guaranteed image) and
        /// our own per-eye cameras (true stereo, currently rendering grey in-game).
        /// </summary>
        public static void ToggleGameFrameMirror()
        {
            _mirrorGameFrameToEyes = !_mirrorGameFrameToEyes;
            ApplyEyeCameraEnabledForMode();

            // Returning to mirror mode hands the game's camera back its own projection and
            // culling - in stereo mode we overwrite both each frame.
            if (_mirrorGameFrameToEyes && _gameMainCamera != null)
            {
                _gameMainCamera.ResetProjectionMatrix();
                _gameMainCamera.ResetCullingMatrix();

                if (_haveOriginalFieldOfView)
                    _gameMainCamera.fieldOfView = _originalFieldOfView;
            }
            MelonLogger.Msg($"NirvanaVR: {(_mirrorGameFrameToEyes ? "MIRROR mode - copying the game's own frame to both eyes (mono)" : "STEREO mode - re-rendering the game's own camera once per eye")}");
        }

        /// <summary>
        /// Flips the eye-texture resolution experiment and tears the rig down so the next
        /// EnsureStereoRig call rebuilds it at the other size.
        /// </summary>
        public static void ToggleEyeTextureResolution()
        {
            UseScreenSizedEyeTextures = !UseScreenSizedEyeTextures;

            DestroyEyeCamera(ref _leftEyeCamera);
            DestroyEyeCamera(ref _rightEyeCamera);
            ReleaseEyeTexture(ref _leftEyeTexture);
            ReleaseEyeTexture(ref _rightEyeTexture);
            ReleaseEyeTexture(ref _leftSubmitTexture);
            ReleaseEyeTexture(ref _rightSubmitTexture);
            _stereoRigCreated = false;
            _rebuildCooldownRemaining = -1;

            MelonLogger.Msg($"NirvanaVR: eye texture size -> {(UseScreenSizedEyeTextures ? "screen size" : "HMD recommended size")}, rebuilding rig");
        }

        /// <summary>F10: level the horizon (yaw-only base) versus the game camera's full
        /// rotation, so the two can be compared directly.</summary>
        public static void ToggleLevelHorizon()
        {
            _levelHorizon = !_levelHorizon;
            _haveLastApplied = false; // the guard's cached pose is from the other convention
            MelonLogger.Msg($"NirvanaVR: horizon {(_levelHorizon ? "LEVEL (yaw-only base - headset defines up)" : "FOLLOWS GAME CAMERA (inherits its pitch and roll)")}");
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
                // Tear down any eye cameras that are still alive (the stale-parent case) so
                // we don't leave orphans rendering into textures we're about to release.
                DestroyEyeCamera(ref _leftEyeCamera);
                DestroyEyeCamera(ref _rightEyeCamera);
                ReleaseEyeTexture(ref _leftEyeTexture);
                ReleaseEyeTexture(ref _rightEyeTexture);
                ReleaseEyeTexture(ref _leftSubmitTexture);
                ReleaseEyeTexture(ref _rightSubmitTexture);
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

            _leftEyeTexture = CreateEyeTexture(width, height, "NirvanaVR_LeftEyeRT", mainCamera);
            _rightEyeTexture = CreateEyeTexture(width, height, "NirvanaVR_RightEyeRT", mainCamera);
            _leftSubmitTexture = CreateSubmitTexture(width, height, "NirvanaVR_LeftSubmitRT");
            _rightSubmitTexture = CreateSubmitTexture(width, height, "NirvanaVR_RightSubmitRT");

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

        private static RenderTexture CreateEyeTexture(uint width, uint height, string name, Camera gameCamera)
        {
            // Clone the descriptor of the RenderTexture the game itself renders into, only
            // changing the size. A camera rendering into our own hand-rolled descriptor
            // produced flat grey in-game - even the game's own camera did - while blitting
            // into it worked fine, so the texture, not the camera, was the problem. The
            // game's target is linear (sRGB=False); a Linear-colour-space LightweightRP
            // expects that of a camera target. Rather than guess which of the descriptor
            // fields matters, copy the one that demonstrably works.
            var template = gameCamera != null ? gameCamera.targetTexture : null;

            RenderTexture rt;
            if (template != null)
            {
                var descriptor = template.descriptor;
                descriptor.width = (int)width;
                descriptor.height = (int)height;

                // A scene render needs somewhere to put depth even if the game's own target
                // doesn't carry it.
                if (descriptor.depthBufferBits < 24)
                    descriptor.depthBufferBits = 24;

                rt = new RenderTexture(descriptor) { name = name };
                MelonLogger.Msg($"NirvanaVR: eye RT '{name}' {width}x{height} cloned from game target - " +
                    $"format={descriptor.colorFormat} sRGB={descriptor.sRGB} depth={descriptor.depthBufferBits} " +
                    $"msaa={descriptor.msaaSamples} dynamicScale={descriptor.useDynamicScale}");
            }
            else
            {
                // Menus render straight to the screen, so there's no descriptor to copy yet.
                // The rig gets rebuilt on the next scene change and picks one up then.
                rt = new RenderTexture((int)width, (int)height, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = name };
                MelonLogger.Msg($"NirvanaVR: eye RT '{name}' {width}x{height} using fallback descriptor (game camera renders to screen)");
            }

            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.Create();
            return rt;
        }

        /// <summary>
        /// The surface actually handed to the compositor: plain 8-bit sRGB, no depth, no
        /// MSAA. Nothing renders into this directly - we blit the eye's render target into
        /// it - so it only has to satisfy OpenVR, not the render pipeline.
        /// </summary>
        private static RenderTexture CreateSubmitTexture(uint width, uint height, string name)
        {
            var rt = new RenderTexture((int)width, (int)height, 0,
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

            // Clear the same way the game's own camera does, so backgrounds (skybox and the
            // like) appear instead of a flat fill - that flat fill was the blue back wall.
            // Force the alpha opaque regardless: the game clears to transparent black
            // because it composites its frame later, but we hand this straight to the
            // compositor.
            cam.clearFlags = mainCamera.clearFlags;
            var background = mainCamera.backgroundColor;
            cam.backgroundColor = new Color(background.r, background.g, background.b, 1f);

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

            // Whether the eye cameras are even being rendered. If leftRenders stops climbing
            // while this says enabled=True, the pipeline is skipping them - which is exactly
            // what an unusable projection matrix causes, and it looks identical to "the
            // scene rendered grey".
            MelonLogger.Msg($"NirvanaVR: eye cams enabled L={(_leftEyeCamera != null && _leftEyeCamera.enabled)} " +
                $"R={(_rightEyeCamera != null && _rightEyeCamera.enabled)} " +
                $"frustum={(_useAsymmetricEyeProjection ? "asymmetric" : "symmetric+crop")} leftRenders={_leftRenderCount}");

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
            // Derive the colour space from the texture instead of hardcoding it: an sRGB
            // texture stores gamma-encoded bytes, a linear one stores linear values, and
            // telling the compositor the wrong one crushes or washes out the mid-tones.
            var texture = new Texture_t
            {
                handle = eyeTexture.GetNativeTexturePtr(),
                eType = ETextureType.DirectX,
                eColorSpace = eyeTexture.sRGB ? EColorSpace.Gamma : EColorSpace.Linear
            };

            // Always the full texture: the eye's view already fills it, because the crop is
            // applied when we copy into this texture rather than being delegated to the
            // compositor.
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
            // Scaled by _headTranslationScale (F1/F2). Rotating your head also MOVES the
            // headset - it pivots about your neck, not your eyes - and we feed that
            // translation into the game in metres. If the game's world isn't built at
            // 1 unit = 1 metre, that movement is scaled wrong, so turning your head gives
            // too much or too little parallax and objects appear to swell and shrink as you
            // look around, without any straight line ever bending.
            return Quaternion.AngleAxis(-_yawOffsetDeg, Vector3.up) * _hmdPosRaw * _headTranslationScale;
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
