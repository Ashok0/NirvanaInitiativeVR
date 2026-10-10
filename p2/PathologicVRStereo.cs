using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Valve.VR;

namespace PathologicVR
{
    public class VrStereoRenderer : MonoBehaviour
    {
        private Camera _leftEyeCam;
        private Camera _rightEyeCam;
        private RenderTexture _leftTexture;
        private RenderTexture _rightTexture;

        private uint _renderWidth;
        private uint _renderHeight;
        private bool _initialized;

        private Matrix4x4 _leftProj;
        private Matrix4x4 _rightProj;

        // === Eye clip planes ===
        public static float EyeNear = 0.2f;
        public static float EyeFar = 3000f;
        public static bool OverrideEyeClipPlanes = true;

        // === SKYBOX DOUBLING FIX ===
        // Symptom: geometry touching the horizon shows a DOUBLED/ghosted skyline
        // that shifts only on head ROTATION (leaning does nothing) -> the band is
        // at infinity. Cause: Unity draws the skybox with each eye camera's FULL
        // view matrix, so the ±IPD/2 positional offset of the two eye cameras
        // produces two slightly different skybox frusta. A skybox is infinitely
        // far, so the eyes must see an IDENTICAL sky (zero offset); the leaked
        // offset makes the skyline double.
        //
        // Fix: a dedicated skybox-only camera per eye that (a) sits at the HEAD
        // origin with NO per-eye positional offset, (b) shares the head rotation,
        // (c) uses the same per-eye projection so FOV/edges line up, (d) clears
        // to skybox and draws NOTHING else (cullingMask = 0), (e) renders BEHIND
        // the eye camera. The eye camera then clears Depth-only and draws world
        // geometry over that identical sky. Result: pixel-identical sky in both
        // eyes (no doubling), full parallax retained for world geometry.
        public static bool SharedSkybox = true;

        // Neutralized (1.0 skips the boost).
        public float ExposureBoost = 1.0f;

        // === Per-eye post-processing effects to disable ===
        public static bool DisableSSR = true;
        public static bool DisableAO = true;
        public static bool DisableTAA = true;
        public static bool DisableMotionBlur = true;
        public static bool DisableChromaticAberration = true;
        public static bool DisableGrain = true;

        private static bool _dumpedProfileFields;
        private static bool _dumpedClipPlanes;

        // === Game gamma pass ===
        private Material _gammaMaterial;
        private FieldInfo _overlayGammaField;
        private RenderTexture _leftSubmitTex;
        private RenderTexture _rightSubmitTex;

        // === UI overlay cameras (v7) ===
        private Camera _leftOverlayCam;
        private Camera _rightOverlayCam;
        private readonly Camera[] _overlayCams = new Camera[2];
        public Camera[] OverlayCameras { get { return _initialized ? _overlayCams : null; } }

        // === Skybox cameras ===
        private Camera _leftSkyCam;
        private Camera _rightSkyCam;

        public void InitializeFromGameCamera(Camera sourceCamera)
        {
            if (_initialized || sourceCamera == null) return;

            var system = OpenVR.System;
            if (system == null) return;

            if (!_dumpedClipPlanes)
            {
                _dumpedClipPlanes = true;
                PathologicVR6DOFTest.Log.Info(
                    $"Source camera clip planes: near={sourceCamera.nearClipPlane:F4} far={sourceCamera.farClipPlane:F1} " +
                    $"(ratio={sourceCamera.farClipPlane / Mathf.Max(0.0001f, sourceCamera.nearClipPlane):F0}). " +
                    $"OverrideEyeClipPlanes={OverrideEyeClipPlanes} -> eye near={EyeNear:F3} far={EyeFar:F1}. SharedSkybox={SharedSkybox}.");
            }

            system.GetRecommendedRenderTargetSize(ref _renderWidth, ref _renderHeight);

            _leftEyeCam = CreateEyeCamera(sourceCamera, "VR_LeftEye", EVREye.Eye_Left, out _leftTexture);
            _rightEyeCam = CreateEyeCamera(sourceCamera, "VR_RightEye", EVREye.Eye_Right, out _rightTexture);

            _leftProj = GetUnityProjection(EVREye.Eye_Left, _leftEyeCam.nearClipPlane, _leftEyeCam.farClipPlane);
            _rightProj = GetUnityProjection(EVREye.Eye_Right, _rightEyeCam.nearClipPlane, _rightEyeCam.farClipPlane);

            _leftEyeCam.projectionMatrix = _leftProj;
            _rightEyeCam.projectionMatrix = _rightProj;

            _leftEyeCam.gameObject.AddComponent<EyeMatrixProtector>().Init(_leftProj);
            _rightEyeCam.gameObject.AddComponent<EyeMatrixProtector>().Init(_rightProj);

            _leftSubmitTex = new RenderTexture((int)_renderWidth, (int)_renderHeight, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _leftSubmitTex.Create();
            _rightSubmitTex = new RenderTexture((int)_renderWidth, (int)_renderHeight, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _rightSubmitTex.Create();
            GetSubmitComponent(_leftEyeCam).SubmitTex = _leftSubmitTex;
            GetSubmitComponent(_rightEyeCam).SubmitTex = _rightSubmitTex;

            if (SharedSkybox)
            {
                _leftSkyCam = CreateSkyboxCamera(_leftEyeCam, EVREye.Eye_Left, _leftTexture, _leftProj);
                _rightSkyCam = CreateSkyboxCamera(_rightEyeCam, EVREye.Eye_Right, _rightTexture, _rightProj);
                // Eye cameras no longer clear to skybox; the sky cam laid it down.
                _leftEyeCam.clearFlags = CameraClearFlags.Depth;
                _rightEyeCam.clearFlags = CameraClearFlags.Depth;
            }

            _leftOverlayCam = CreateOverlayCamera(_leftEyeCam, EVREye.Eye_Left, _leftSubmitTex);
            _rightOverlayCam = CreateOverlayCamera(_rightEyeCam, EVREye.Eye_Right, _rightSubmitTex);
            _overlayCams[0] = _leftOverlayCam;
            _overlayCams[1] = _rightOverlayCam;

            TrySetupGameGammaPass();

            _initialized = true;
            PathologicVR6DOFTest.Log.Info($"Stereo + aggressive PPS ready. ExposureBoost={ExposureBoost}. UI overlay cams on layer {VRUI.PanelLayer}. Eye near={_leftEyeCam.nearClipPlane:F3} far={_leftEyeCam.farClipPlane:F1}. SharedSkybox={SharedSkybox}. Disable SSR={DisableSSR} AO={DisableAO} TAA={DisableTAA} MB={DisableMotionBlur} CA={DisableChromaticAberration} Grain={DisableGrain}.");
        }

        private SubmitOnRender GetSubmitComponent(Camera cam)
        {
            return cam.GetComponent<SubmitOnRender>();
        }

        // Skybox-only camera: head-origin (no IPD offset), head rotation, same
        // per-eye projection, draws ONLY the skybox, renders behind the eye cam
        // into the SAME target texture. Both eyes get an identical sky.
        private Camera CreateSkyboxCamera(Camera eyeCam, EVREye eye, RenderTexture target, Matrix4x4 proj)
        {
            var go = new GameObject(eyeCam.name + "_Sky");
            // Parent to the HEAD (this renderer's transform = game camera), NOT
            // the eye camera, so it inherits head rotation but not the eye's
            // ±IPD/2 local position. Local pose identity => sits at head origin.
            go.transform.SetParent(this.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.cullingMask = 0;                 // draw nothing but the skybox
            cam.targetTexture = target;
            cam.depth = eyeCam.depth - 1;         // render BEFORE the eye camera
            cam.rect = new Rect(0, 0, 1, 1);
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.allowHDR = eyeCam.allowHDR;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.nearClipPlane = EyeNear;
            cam.farClipPlane = EyeFar;
            cam.projectionMatrix = proj;
            go.AddComponent<EyeMatrixProtector>().Init(proj);

            return cam;
        }

        private Camera CreateOverlayCamera(Camera eyeCam, EVREye eye, RenderTexture submitTex)
        {
            var go = new GameObject(eyeCam.name + "_UIOverlay");
            go.transform.SetParent(eyeCam.transform, false);

            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Depth;
            cam.backgroundColor = Color.clear;
            cam.cullingMask = 1 << VRUI.PanelLayer;
            cam.targetTexture = submitTex;
            cam.depth = eyeCam.depth + 0.5f;
            cam.rect = new Rect(0, 0, 1, 1);
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 100f;

            Matrix4x4 proj = GetUnityProjection(eye, cam.nearClipPlane, cam.farClipPlane);
            cam.projectionMatrix = proj;
            go.AddComponent<EyeMatrixProtector>().Init(proj);

            var s = go.AddComponent<SubmitAfterOverlay>();
            s.Owner = this;
            s.Eye = eye;
            s.SubmitTex = submitTex;

            return cam;
        }

        private void TrySetupGameGammaPass()
        {
            try
            {
                Type overlayType = AccessTools.TypeByName("OverlayCamera");
                if (overlayType == null)
                {
                    PathologicVR6DOFTest.Log.Warn("OverlayCamera type not found; HMD will miss the game gamma pass.");
                    return;
                }

                _overlayGammaField = overlayType.GetField("Gamma",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                var overlayInstance = UnityEngine.Object.FindObjectOfType(overlayType) as MonoBehaviour;
                Shader shader = null;
                if (overlayInstance != null)
                {
                    FieldInfo shaderField = overlayType.GetField("PostProcessShader",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    shader = shaderField != null ? shaderField.GetValue(overlayInstance) as Shader : null;
                }

                if (shader == null)
                {
                    PathologicVR6DOFTest.Log.Warn("OverlayCamera.PostProcessShader not found yet; will retry.");
                    return;
                }

                _gammaMaterial = new Material(shader);
                float g = ReadGameGamma();
                PathologicVR6DOFTest.Log.Info(
                    $"Game gamma pass hooked for HMD. OverlayCamera.Gamma={g:F2} (_Power={g / 2.2f:F2})");
            }
            catch (Exception e)
            {
                PathologicVR6DOFTest.Log.Warn("TrySetupGameGammaPass failed: " + e.Message);
            }
        }

        public float ReadGameGamma()
        {
            try
            {
                if (_overlayGammaField != null)
                    return (float)_overlayGammaField.GetValue(null);
            }
            catch { }
            return 2.2f;
        }

        private float _nextGammaRetry;

        public Material GammaMaterial
        {
            get
            {
                if (_gammaMaterial == null && Time.unscaledTime >= _nextGammaRetry)
                {
                    _nextGammaRetry = Time.unscaledTime + 1f;
                    TrySetupGameGammaPass();
                }
                return _gammaMaterial;
            }
        }

        private Camera CreateEyeCamera(Camera source, string name, EVREye eye, out RenderTexture targetTexture)
        {
            var go = new GameObject(name);
            go.transform.SetParent(source.transform, false);

            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(source);

            cam.rect = new Rect(0, 0, 1, 1);
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.stereoSeparation = 0f;
            cam.stereoConvergence = 0f;
            cam.allowHDR = source.allowHDR;
            cam.allowMSAA = false;
            cam.depth = source.depth - 1;
            cam.usePhysicalProperties = false;

            cam.cullingMask = source.cullingMask;
            cam.useOcclusionCulling = false;
            cam.layerCullSpherical = source.layerCullSpherical;
            cam.backgroundColor = source.backgroundColor;
            cam.clearFlags = source.clearFlags;
            cam.renderingPath = source.renderingPath;

            if (OverrideEyeClipPlanes)
            {
                cam.nearClipPlane = EyeNear;
                cam.farClipPlane = EyeFar;
            }
            else
            {
                cam.nearClipPlane = source.nearClipPlane;
                cam.farClipPlane = source.farClipPlane;
            }

            targetTexture = new RenderTexture(
                (int)_renderWidth, (int)_renderHeight, 24,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            targetTexture.Create();
            cam.targetTexture = targetTexture;

            try
            {
                var pp = source.GetComponent<UnityEngine.PostProcessing.PostProcessingBehaviour>();
                if (pp != null && pp.profile != null)
                {
                    var copy = go.AddComponent<UnityEngine.PostProcessing.PostProcessingBehaviour>();

                    var profileCopy = UnityEngine.Object.Instantiate(pp.profile);
                    copy.profile = profileCopy;
                    copy.enabled = true;

                    DumpProfileFieldsOnce(profileCopy);
                    BoostProfileExposure(profileCopy, ExposureBoost);
                    DisableBadStereoEffects(profileCopy);

                    PathologicVR6DOFTest.Log.Info("Aggressive PPS copy + boost -> " + name);
                }
            }
            catch (Exception e)
            {
                PathologicVR6DOFTest.Log.Warn("Aggressive PPS failed: " + e.Message);
            }

            var submit = go.AddComponent<SubmitOnRender>();
            submit.Owner = this;
            submit.Eye = eye;
            submit.Source = targetTexture;

            return cam;
        }

        private void DumpProfileFieldsOnce(ScriptableObject profile)
        {
            if (_dumpedProfileFields || profile == null) return;
            _dumpedProfileFields = true;
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var f in profile.GetType().GetFields(flags))
                    PathologicVR6DOFTest.Log.Info("PPS field: " + f.Name + " : " + f.FieldType.Name);
            }
            catch (Exception e)
            {
                PathologicVR6DOFTest.Log.Warn("DumpProfileFields error: " + e.Message);
            }
        }

        private void DisableBadStereoEffects(ScriptableObject profile)
        {
            if (profile == null) return;

            var targets = new System.Collections.Generic.List<string>();
            if (DisableSSR)                 targets.Add("screenSpaceReflection");
            if (DisableAO)                  targets.Add("ambientOcclusion");
            if (DisableTAA)                 targets.Add("antialiasing");
            if (DisableMotionBlur)          targets.Add("motionBlur");
            if (DisableChromaticAberration) targets.Add("chromaticAberration");
            if (DisableGrain)               targets.Add("grain");
            if (targets.Count == 0) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = profile.GetType();

                foreach (var fieldName in targets)
                {
                    var field = type.GetField(fieldName, flags);
                    if (field == null)
                    {
                        PathologicVR6DOFTest.Log.Warn("PPS model field not found: " + fieldName);
                        continue;
                    }

                    var model = field.GetValue(profile);
                    if (model == null) continue;

                    var enabledProp = model.GetType().GetProperty("enabled", flags);
                    if (enabledProp != null && enabledProp.CanWrite)
                    {
                        enabledProp.SetValue(model, false, null);
                        PathologicVR6DOFTest.Log.Info("Disabled PPS model: " + fieldName);
                        continue;
                    }

                    var settingsField = model.GetType().GetField("settings", flags);
                    if (settingsField != null)
                    {
                        var settings = settingsField.GetValue(model);
                        if (settings != null)
                        {
                            var enField = settings.GetType().GetField("enabled", flags);
                            if (enField != null && enField.FieldType == typeof(bool))
                            {
                                enField.SetValue(settings, false);
                                settingsField.SetValue(model, settings);
                                PathologicVR6DOFTest.Log.Info("Disabled PPS model via settings: " + fieldName);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                PathologicVR6DOFTest.Log.Warn("DisableBadStereoEffects error: " + e.Message);
            }
        }

        private void BoostProfileExposure(ScriptableObject profile, float multiplier)
        {
            if (profile == null || multiplier <= 1.01f) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var type = profile.GetType();

                string[] possibleFields = { "tonemapping", "m_Tonemapping", "colorGrading", "m_ColorGrading", "eyeAdaptation", "m_EyeAdaptation" };

                foreach (var fieldName in possibleFields)
                {
                    var field = type.GetField(fieldName, flags);
                    if (field == null) continue;

                    var model = field.GetValue(profile);
                    if (model == null) continue;

                    var settingsField = model.GetType().GetField("settings", flags);
                    if (settingsField == null) continue;

                    var settings = settingsField.GetValue(model);
                    if (settings == null) continue;

                    string[] expNames = { "exposure", "postExposure", "keyValue", "adaptationSpeed", "compensation" };

                    foreach (var expName in expNames)
                    {
                        var expField = settings.GetType().GetField(expName, flags);
                        if (expField != null && expField.FieldType == typeof(float))
                        {
                            float current = (float)expField.GetValue(settings);
                            float newValue = current + Mathf.Log(multiplier, 2f);
                            expField.SetValue(settings, newValue);
                            settingsField.SetValue(model, settings);
                            PathologicVR6DOFTest.Log.Info($"Boosted {fieldName}.{expName}: {current:F2} -> {newValue:F2}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                PathologicVR6DOFTest.Log.Warn("BoostProfileExposure error: " + e.Message);
            }
        }

        private static Matrix4x4 GetUnityProjection(EVREye eye, float near, float far)
        {
            return ToUnityMatrix(OpenVR.System.GetProjectionMatrix(eye, near, far));
        }

        private static Matrix4x4 ToUnityMatrix(HmdMatrix44_t m)
        {
            var r = new Matrix4x4();
            r.m00 = m.m0; r.m01 = m.m1; r.m02 = m.m2; r.m03 = m.m3;
            r.m10 = m.m4; r.m11 = m.m5; r.m12 = m.m6; r.m13 = m.m7;
            r.m20 = m.m8; r.m21 = m.m9; r.m22 = m.m10; r.m23 = m.m11;
            r.m30 = m.m12; r.m31 = m.m13; r.m32 = m.m14; r.m33 = m.m15;
            return r;
        }

        private static Vector3 EyeToHeadPosition(HmdMatrix34_t m)
        {
            return new Vector3(m.m3, m.m7, -m.m11);
        }

        private void LateUpdate()
        {
            if (!_initialized || OpenVR.System == null) return;

            var l = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Left);
            var r = OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Right);

            _leftEyeCam.transform.localPosition = EyeToHeadPosition(l);
            _rightEyeCam.transform.localPosition = EyeToHeadPosition(r);
            _leftEyeCam.transform.localRotation = Quaternion.identity;
            _rightEyeCam.transform.localRotation = Quaternion.identity;

            _leftEyeCam.projectionMatrix = _leftProj;
            _rightEyeCam.projectionMatrix = _rightProj;

            // Skybox cameras stay at head origin (zero offset) so both eyes get an
            // identical sky. Keep their local pose pinned in case anything moved it.
            if (_leftSkyCam != null)
            {
                _leftSkyCam.transform.localPosition = Vector3.zero;
                _leftSkyCam.transform.localRotation = Quaternion.identity;
                _leftSkyCam.projectionMatrix = _leftProj;
            }
            if (_rightSkyCam != null)
            {
                _rightSkyCam.transform.localPosition = Vector3.zero;
                _rightSkyCam.transform.localRotation = Quaternion.identity;
                _rightSkyCam.projectionMatrix = _rightProj;
            }

            if (_gameCamCache == null) _gameCamCache = GetComponent<Camera>();
            VRUI.EnsureAndUpdate(this, _gameCamCache);
        }

        private Camera _gameCamCache;

        public void IncludeLayerInEyes(int layer)
        {
            if (_leftEyeCam != null) _leftEyeCam.cullingMask |= 1 << layer;
            if (_rightEyeCam != null) _rightEyeCam.cullingMask |= 1 << layer;
        }

        internal void SubmitEye(RenderTexture source, EVREye eye)
        {
            var texture = new Texture_t
            {
                handle = source.GetNativeTexturePtr(),
                eType = ETextureType.DirectX,
                eColorSpace = EColorSpace.Gamma
            };
            var bounds = new VRTextureBounds_t { uMin = 0f, uMax = 1f, vMin = 1f, vMax = 0f };
            OpenVR.Compositor.Submit(eye, ref texture, ref bounds, EVRSubmitFlags.Submit_Default);
        }

        public class SubmitOnRender : MonoBehaviour
        {
            public VrStereoRenderer Owner;
            public EVREye Eye;
            public RenderTexture Source;
            public RenderTexture SubmitTex;

            private void OnRenderImage(RenderTexture src, RenderTexture dst)
            {
                if (SubmitTex != null)
                {
                    Material gammaMat = Owner.GammaMaterial;
                    if (gammaMat != null)
                    {
                        gammaMat.SetFloat("_Power", Owner.ReadGameGamma() / 2.2f);
                        Graphics.Blit(src, SubmitTex, gammaMat);
                    }
                    else
                    {
                        Graphics.Blit(src, SubmitTex);
                    }
                }

                Graphics.Blit(src, dst);
            }
        }

        public class SubmitAfterOverlay : MonoBehaviour
        {
            public VrStereoRenderer Owner;
            public EVREye Eye;
            public RenderTexture SubmitTex;

            private void OnPostRender()
            {
                if (Owner == null || SubmitTex == null || OpenVR.Compositor == null) return;
                Owner.SubmitEye(SubmitTex, Eye);
            }
        }
    }

    public class EyeMatrixProtector : MonoBehaviour
    {
        private Matrix4x4 _proj;
        private Camera _cam;

        public void Init(Matrix4x4 proj)
        {
            _proj = proj;
            _cam = GetComponent<Camera>();
        }

        private void OnPreCull() { if (_cam != null) _cam.projectionMatrix = _proj; }
        private void OnPreRender() { if (_cam != null) _cam.projectionMatrix = _proj; }
    }

    internal static class VRStereo
    {
        private static VrStereoRenderer _renderer;
        private static Type _gameCameraType;
        private static PropertyInfo _gameCameraInstance;
        private static PropertyInfo _cameraProp;
        private static bool _resolved;

        public static void Init(Harmony harmony)
        {
            _gameCameraType = AccessTools.TypeByName("GameCamera");
            if (_gameCameraType != null)
            {
                _gameCameraInstance = AccessTools.Property(_gameCameraType, "Instance");
                _cameraProp = AccessTools.Property(_gameCameraType, "Camera");
                _resolved = _gameCameraInstance != null && _cameraProp != null;
            }
            if (!_resolved) return;

            NeutralizeDynamicResolution(harmony);
            PathologicVR6DOFTest.Log.Info("VRStereo initialized (aggressive PPS)");
        }

        private static void NeutralizeDynamicResolution(Harmony harmony)
        {
            var drc = AccessTools.TypeByName("DynamicResolutionCamera");
            if (drc == null) return;
            var lu = AccessTools.Method(drc, "LateUpdate");
            if (lu == null) return;
            harmony.Patch(lu, prefix: new HarmonyMethod(typeof(VRStereo).GetMethod(nameof(DynResPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static bool DynResPrefix() => false;

        public static void EnsureRenderer()
        {
            if (!_resolved) return;
            var inst = _gameCameraInstance.GetValue(null, null);
            if (inst == null) return;
            var cam = _cameraProp.GetValue(inst, null) as Camera;
            if (cam == null) return;

            if (_renderer == null)
                _renderer = cam.gameObject.AddComponent<VrStereoRenderer>();
            _renderer.InitializeFromGameCamera(cam);
        }
    }
}
