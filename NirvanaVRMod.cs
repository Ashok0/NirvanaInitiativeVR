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

            if (Input.GetKeyDown(KeyCode.F9))
                VRHead.RecenterYaw();

            DisableCinemachineHeadControl();
        }

        /// <summary>
        /// Finds the Main Camera's active Cinemachine virtual camera and disables the
        /// components that would otherwise fight the HMD for look direction.
        /// </summary>
        private void DisableCinemachineHeadControl()
        {
            if (_mainCameraObject == null)
            {
                _mainCameraObject = GameObject.Find("Main Camera");
                if (_mainCameraObject == null)
                    return;
            }

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
