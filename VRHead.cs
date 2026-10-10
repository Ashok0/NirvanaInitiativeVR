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
        }

        /// <summary>Call once per frame (OnLateUpdate) to fetch the latest HMD pose.</summary>
        public static void PumpPoses()
        {
            if (!_initialized)
                return;

            var compositor = OpenVR.Compositor;
            if (compositor == null)
                return;

            compositor.WaitGetPoses(RenderPoses, GamePoses);

            var hmdPose = RenderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!hmdPose.bPoseIsValid)
                return;

            var m = hmdPose.mDeviceToAbsoluteTracking;
            _hmdPosRaw = GetPosition(m);
            _hmdRotRaw = GetRotation(m);
        }

        /// <summary>Zeroes the yaw of the current HMD orientation as the new forward.</summary>
        public static void RecenterYaw()
        {
            _yawOffsetDeg = _hmdRotRaw.eulerAngles.y;
            MelonLogger.Msg($"NirvanaVR: yaw recentered to {_yawOffsetDeg:F1} deg");
        }

        /// <summary>
        /// RenderPipelineManager.beginCameraRendering handler. By the time this fires,
        /// Cinemachine has already written this frame's transform onto the camera - we
        /// read that as the base and layer the (recentered) HMD pose on top of it.
        /// </summary>
        public static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_initialized || camera == null || camera.name != "Main Camera")
                return;

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

        /// <summary>
        /// RenderPipelineManager.endCameraRendering handler.
        /// Phase 2 placeholder: this is where the per-eye stereo Submit() to the
        /// OpenVR compositor will go once stereo rendering is wired up. For now it
        /// only logs which camera just finished rendering.
        /// </summary>
        public static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            // TODO(phase 2): OpenVR.Compositor.Submit(EVREye.Eye_Left/Right, ref eyeTexture, ...)
            MelonLogger.Msg($"NirvanaVR: endCameraRendering for camera '{camera?.name}'");
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
