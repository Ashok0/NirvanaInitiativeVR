using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Valve.VR;

namespace PathologicVR
{
    [BepInPlugin("com.andrew.pathologicvr.hmdtest", "Pathologic 2 VR - 6DOF Test", "0.4.1")]
    public class PathologicVR6DOFTest : BaseUnityPlugin
    {
        internal static ManualLogSourceProxy Log;

        private void Awake()
        {
            Log = new ManualLogSourceProxy(Logger);
            Log.Info("=== Pathologic 2 VR 6DOF + Stereo starting ===");

            VRHead.InitOpenVR();

            var harmony = new Harmony("com.andrew.pathologicvr.hmdtest");
            bool hooked = CameraHook.TryPatch(harmony);
            if (!hooked)
                Log.Error("Could not patch CameraService.ComputeUpdate");

            VRStereo.Init(harmony);
            Log.Info("=== Awake complete ===");
        }

        private void Update()
        {
            VRHead.PumpPoses();

            if (Input.GetKeyDown(KeyCode.F9))
            {
                VRHead.Recenter();
                Log.Info("Recentered yaw.");
            }
        }

        private void OnDestroy()
        {
            VRHead.Shutdown();
        }
    }

    internal static class VRHead
    {
        private static CVRSystem _hmd;
        public static bool Ready { get; private set; }

        private static readonly TrackedDevicePose_t[] _renderPoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private static readonly TrackedDevicePose_t[] _gamePoses =
            new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        public static bool HasPose { get; private set; }
        public static Vector3 HmdPosition { get; private set; }
        public static Quaternion HmdRotationRaw { get; private set; }

        private static float _yawOffset;

        public static void InitOpenVR()
        {
            if (Ready) return;

            EVRInitError err = EVRInitError.None;
            _hmd = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
            if (err != EVRInitError.None || _hmd == null)
            {
                PathologicVR6DOFTest.Log.Error("OpenVR.Init failed: " + err);
                Ready = false;
                return;
            }

            uint w = 0, h = 0;
            _hmd.GetRecommendedRenderTargetSize(ref w, ref h);
            PathologicVR6DOFTest.Log.Info($"OpenVR initialized. RT size: {w}x{h}");
            Ready = true;
        }

        public static void PumpPoses()
        {
            if (!Ready || OpenVR.Compositor == null) return;

            var cerr = OpenVR.Compositor.WaitGetPoses(_renderPoses, _gamePoses);
            if (cerr != EVRCompositorError.None) return;

            var hmdPose = _renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            if (!hmdPose.bPoseIsValid)
            {
                HasPose = false;
                return;
            }

            HmdMatrix34_t m = hmdPose.mDeviceToAbsoluteTracking;

            HmdPosition = new Vector3(m.m3, m.m7, -m.m11);
            HmdRotationRaw = MatrixToQuaternion(m);
            HasPose = true;
        }

        public static void Recenter()
        {
            if (!HasPose) return;
            _yawOffset = HmdRotationRaw.eulerAngles.y;
        }

        // === HEIGHT FIX IS HERE ===
        public static void ApplyTo(Transform camT, Vector3 basePos, Quaternion baseRot)
        {
            if (!HasPose) return;

            // Rotation: yaw recenter only, full pitch/roll from HMD
            Quaternion recenter = Quaternion.Euler(0f, -_yawOffset, 0f);
            Quaternion headRot = recenter * HmdRotationRaw;
            camT.rotation = baseRot * headRot;

            // Position: ONLY horizontal tracking (X/Z). Keep game camera height (Y).
            // This stops the "floating a foot above the hands" problem.
            Vector3 hmdHorizontal = new Vector3(HmdPosition.x, 0f, HmdPosition.z);
            Vector3 headOffset = (baseRot * recenter) * hmdHorizontal;
            camT.position = basePos + headOffset;
        }

        public static void Shutdown()
        {
            if (Ready)
            {
                OpenVR.Shutdown();
                Ready = false;
            }
        }

        private static Quaternion MatrixToQuaternion(HmdMatrix34_t m)
        {
            Quaternion q = new Quaternion();
            q.w = Mathf.Sqrt(Mathf.Max(0f, 1f + m.m0 + m.m5 + m.m10)) / 2f;
            q.x = Mathf.Sqrt(Mathf.Max(0f, 1f + m.m0 - m.m5 - m.m10)) / 2f;
            q.y = Mathf.Sqrt(Mathf.Max(0f, 1f - m.m0 + m.m5 - m.m10)) / 2f;
            q.z = Mathf.Sqrt(Mathf.Max(0f, 1f - m.m0 - m.m5 + m.m10)) / 2f;
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

    internal static class CameraHook
    {
        private static Type _gameCameraType;
        private static PropertyInfo _gameCameraInstance;
        private static PropertyInfo _cameraTransformProp;

        // === Idempotent apply ===
        // During gameplay the game's camera controller rewrites the camera
        // transform from player yaw/pitch every ComputeUpdate, so reading
        // camT.rotation gives a clean base each frame. In menu / dialog /
        // cutscene camera kinds the controller leaves the transform alone, so
        // the value we read back is LAST FRAME'S OUTPUT — already containing
        // the HMD rotation. Multiplying headRot in again every frame spins the
        // view at (head angle x frame rate) deg/s, and position drifts the same
        // way. Fix: remember what we wrote; if the camera still holds exactly
        // that value, the game didn't touch it, so reuse the cached base.
        private static bool _haveLast;
        private static Vector3 _lastBasePos;
        private static Quaternion _lastBaseRot;
        private static Vector3 _lastAppliedPos;
        private static Quaternion _lastAppliedRot;

        // Telemetry: how often the guard fired (logged every 5 s if non-zero).
        private static int _guardHits;
        private static float _nextGuardLog;

        public static bool TryPatch(Harmony harmony)
        {
            Type camServiceType =
                AccessTools.TypeByName("Engine.Source.Services.CameraServices.CameraService")
                ?? AccessTools.TypeByName("CameraService");
            if (camServiceType == null) return false;

            MethodInfo computeUpdate = AccessTools.Method(camServiceType, "ComputeUpdate");
            if (computeUpdate == null) return false;

            _gameCameraType = AccessTools.TypeByName("GameCamera");
            if (_gameCameraType == null) return false;

            _gameCameraInstance = AccessTools.Property(_gameCameraType, "Instance");
            _cameraTransformProp = AccessTools.Property(_gameCameraType, "CameraTransform");
            if (_gameCameraInstance == null || _cameraTransformProp == null) return false;

            var postfix = new HarmonyMethod(typeof(CameraHook).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(computeUpdate, postfix: postfix);
            PathologicVR6DOFTest.Log.Info("Patched CameraService.ComputeUpdate");
            return true;
        }

        private static bool _loggedFirstHit;

        private static bool SameRot(Quaternion a, Quaternion b)
        {
            return Mathf.Abs(Quaternion.Dot(a, b)) > 0.9999995f;
        }

        private static bool SamePos(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude < 1e-8f;   // 0.1 mm
        }

        private static void Postfix()
        {
            if (!VRHead.Ready) return;

            object inst = _gameCameraInstance.GetValue(null, null);
            if (inst == null) return;

            var camT = _cameraTransformProp.GetValue(inst, null) as Transform;
            if (camT == null) return;

            if (!_loggedFirstHit)
            {
                _loggedFirstHit = true;
                PathologicVR6DOFTest.Log.Info("ComputeUpdate postfix hit. HMD driving camera. F9 = recenter.");
            }

            Vector3 curPos = camT.position;
            Quaternion curRot = camT.rotation;

            Vector3 basePos;
            Quaternion baseRot;
            if (_haveLast && SameRot(curRot, _lastAppliedRot) && SamePos(curPos, _lastAppliedPos))
            {
                // Game did not rewrite the camera this frame (menu/dialog/cutscene
                // camera kind). Use the base we derived last time, not our own output.
                basePos = _lastBasePos;
                baseRot = _lastBaseRot;
                _guardHits++;
            }
            else
            {
                basePos = curPos;
                baseRot = curRot;
            }

            VRHead.ApplyTo(camT, basePos, baseRot);

            _lastBasePos = basePos;
            _lastBaseRot = baseRot;
            _lastAppliedPos = camT.position;
            _lastAppliedRot = camT.rotation;
            _haveLast = true;

            if (Time.unscaledTime >= _nextGuardLog)
            {
                _nextGuardLog = Time.unscaledTime + 5f;
                if (_guardHits > 0)
                {
                    PathologicVR6DOFTest.Log.Info("CameraHook: game left camera untouched on " + _guardHits +
                                                  " frames in last 5s (menu/dialog/cutscene) - reused cached base.");
                    _guardHits = 0;
                }
            }

            VRStereo.EnsureRenderer();
        }
    }

    internal class ManualLogSourceProxy
    {
        private readonly BepInEx.Logging.ManualLogSource _src;
        public ManualLogSourceProxy(BepInEx.Logging.ManualLogSource src) { _src = src; }
        public void Info(string m) { _src.LogInfo(m); }
        public void Error(string m) { _src.LogError(m); }
        public void Warn(string m) { _src.LogWarning(m); }
    }
}
