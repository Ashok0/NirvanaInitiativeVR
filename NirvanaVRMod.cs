using System;
using Il2Cpp;
using Il2CppCinemachine;
using Il2CppGame;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

[assembly: MelonInfo(typeof(NirvanaVR.NirvanaVRMod), "NirvanaVR", "0.1.0", "Ashok")]
[assembly: MelonGame("SpikeChunsoft", "AI_TheSomniumFiles2")]

namespace NirvanaVR
{
    public class NirvanaVRMod : MelonMod
    {
        private GameObject _mainCameraObject;
        private CinemachineBrain _brain;

        // Cache keyed on the active vcam's GameObject so we don't GetComponent every frame.
        private GameObject _lastVcamObject;
        private CinemachineCustomAxis _customAxis;
        private CinemachineCameraOffsetRotation _offsetRotation;

        public override void OnInitializeMelon()
        {
            VRHead.Init();

            RenderPipelineManager.beginCameraRendering +=
                (Action<ScriptableRenderContext, Camera>)VRHead.OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering +=
                (Action<ScriptableRenderContext, Camera>)VRHead.OnEndCameraRendering;

            MelonLogger.Msg("NirvanaVR 0.1.0 loaded");
        }

        public override void OnLateUpdate()
        {
            VRHead.PumpPoses();

            if (Input.GetKeyDown(KeyCode.F1))
                VRHead.AdjustHeadTranslationScale(-0.25f);

            if (Input.GetKeyDown(KeyCode.F2))
                VRHead.AdjustHeadTranslationScale(0.25f);

            if (Input.GetKeyDown(KeyCode.F3))
                VRHead.AdjustCameraPullback(-0.25f);

            if (Input.GetKeyDown(KeyCode.F4))
                VRHead.AdjustCameraPullback(0.25f);

            if (Input.GetKeyDown(KeyCode.F5))
                VRHead.AdjustIpdScale(-0.25f);

            if (Input.GetKeyDown(KeyCode.F6))
                VRHead.AdjustIpdScale(0.25f);

            if (Input.GetKeyDown(KeyCode.F7))
                VRHead.ToggleLeftEyeScreenMirror();

            if (Input.GetKeyDown(KeyCode.F8))
                VRHead.ToggleEyeProjection();

            if (Input.GetKeyDown(KeyCode.F9))
                VRHead.RecenterYaw();

            if (Input.GetKeyDown(KeyCode.F10))
                VRHead.ToggleLevelHorizon();

            if (Input.GetKeyDown(KeyCode.F11))
                VRHead.ToggleEyeTextureResolution();

            if (Input.GetKeyDown(KeyCode.F12))
                VRHead.ToggleGameFrameMirror();

            DisableCinemachineHeadControl();
        }

        /// <summary>
        /// Finds the Main Camera's active Cinemachine virtual camera and disables the
        /// components that would otherwise fight the HMD for look direction.
        /// </summary>
        private void DisableCinemachineHeadControl()
        {
            // Re-find whenever the cached object is gone OR merely inactive: a scene change
            // can swap in a brand new "Main Camera" and leave the old one alive but
            // deactivated, which a plain null check won't catch. GameObject.Find only
            // returns active objects, so this always re-homes us onto the live one.
            if (_mainCameraObject == null || !_mainCameraObject.activeInHierarchy)
            {
                _mainCameraObject = GameObject.Find("Main Camera");
                if (_mainCameraObject == null)
                    return;

                // Cached components belonged to the previous Main Camera - drop them.
                _brain = null;
                _lastVcamObject = null;
                _customAxis = null;
                _offsetRotation = null;
            }

            VRHead.EnsureStereoRig(_mainCameraObject);

            if (_brain == null)
            {
                _brain = _mainCameraObject.GetComponent<CinemachineBrain>();
                if (_brain == null)
                    return;
            }

            var activeVcam = _brain.ActiveVirtualCamera;
            if (activeVcam == null || !activeVcam.IsValid)
                return;

            var vcamObject = activeVcam.VirtualCameraGameObject;
            if (vcamObject == null)
                return;

            if (vcamObject != _lastVcamObject)
            {
                _lastVcamObject = vcamObject;
                _customAxis = vcamObject.GetComponent<CinemachineCustomAxis>();
                _offsetRotation = vcamObject.GetComponent<CinemachineCameraOffsetRotation>();
            }

            if (_customAxis != null)
                _customAxis.inputEnabled = false;

            if (_offsetRotation != null)
                _offsetRotation.active = false;
        }
    }
}
