// Copyright 2025 URAV ADVANCED LEARNING SYSTEMS PRIVATE LIMITED
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Events;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR.API;
using UnityEngine.XR.OpenXR.Features.Meta;
using Uralstech.Utils.Singleton;

#nullable enable
namespace Uralstech.UXR.QuestMeshing
{
    /// <summary>
    /// Script that will preprocess depth data for meshing.
    /// </summary>
    /// <remarks>
    /// This script by itself does very little. It is meant to be used with shaders which
    /// utilize the data exposed by this script and with other scripts from this package,
    /// like <see cref="DepthMesher"/> and <see cref="CPUDepthSampler"/>.
    /// 
    /// This <b>directly conflicts</b> with Meta's EnvironmentDepthManager and any other
    /// script that acquires the depth textures using XROcclusionSubsystem.TryGetFrame,
    /// potentially including Unity's AROcclusionManager. This script automatically
    /// populates the following global shader variables, to keep some compatibility
    /// with EnvironmentDepthManager:
    /// <list type="bullet">
    /// <item><description>_EnvironmentDepthTexture</description></item>
    /// <item><description>_EnvironmentDepthReprojectionMatrices</description></item>
    /// <item><description>_EnvironmentDepthZBufferParams</description></item>
    /// <item><description>_PreprocessedEnvironmentDepthTexture</description></item>
    /// </list>
    /// <br/>
    /// The following custom global shader variables are also defined by this script:
    /// <list type="bullet">
    /// <item>
    /// <term>_EnvironmentDepthInverseReprojectionMatrices</term>
    /// <description>The inverse of _EnvironmentDepthReprojectionMatrices, used to convert points from the current depth texture frame into world space.</description>
    /// </item>
    /// <item>
    /// <term>_EnvironmentDepthProjectionMatrices</term>
    /// <description>Projection matrices for the current depth texture frame.</description>
    /// </item>
    /// <item>
    /// <term>_EnvironmentDepthViewMatrices</term>
    /// <description>View matrices for the current depth texture frame.</description>
    /// </item>
    /// <item>
    /// <term>_EnvironmentDepthWorldPositionsTexture</term>
    /// <description>Texture containing the points in the (left eye's) XR depth texture converted to world space.</description>
    /// </item>
    /// <item>
    /// <term>_EnvironmentDepthWorldNormalsTexture</term>
    /// <description>Texture containing the normal vectors of the points in the (left eye's) XR depth texture converted to world space.</description>
    /// </item>
    /// </list>
    /// </remarks>
    [AddComponentMenu("Uralstech/UXR/QuestMeshing/Depth Preprocessor")]
    public class DepthPreprocessor : DontCreateNewSingleton<DepthPreprocessor>
    {
        #region Shader Properties
#pragma warning disable IDE1006 // Naming Styles
        private static readonly int Global_DepthTexture = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int Global_ReprojectionMatrices = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
        private static readonly int Global_ZBufferParams = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        private static readonly int Global_PreprocessedDepthTexture = Shader.PropertyToID("_PreprocessedEnvironmentDepthTexture");

        private static readonly int Global_InverseReprojectionMatrices = Shader.PropertyToID("_EnvironmentDepthInverseReprojectionMatrices");
        private static readonly int Global_ProjectionMatrices = Shader.PropertyToID("_EnvironmentDepthProjectionMatrices");
        private static readonly int Global_ViewMatrices = Shader.PropertyToID("_EnvironmentDepthViewMatrices");
        private static readonly int Global_PositionsTexture = Shader.PropertyToID("_EnvironmentDepthWorldPositionsTexture");
        private static readonly int Global_NormalsTexture = Shader.PropertyToID("_EnvironmentDepthWorldNormalsTexture");

        private static readonly int DP_TextureSize = Shader.PropertyToID("TextureSize");
        private static readonly int DP_PositionsTexture = Shader.PropertyToID("PositionsTexture");
        private static readonly int DP_NormalsTexture = Shader.PropertyToID("NormalsTexture");
#pragma warning restore IDE1006 // Naming Styles
        #endregion

        /// <summary>
        /// The inverse of <see cref="DepthReprojectionMatrices"/>, used to convert points from the current depth texture frame into world space.
        /// </summary>
        public readonly Matrix4x4[] DepthInverseReprojectionMatrices = new Matrix4x4[2];

        /// <summary>
        /// Matrices used to convert points in world space to points in the current depth texture frame.
        /// </summary>
        public readonly Matrix4x4[] DepthReprojectionMatrices = new Matrix4x4[2];

        /// <summary>
        /// Projection matrices for the current depth texture frame.
        /// </summary>
        public readonly Matrix4x4[] DepthProjectionMatrices = new Matrix4x4[2];

        /// <summary>
        /// View matrices for the current depth texture frame.
        /// </summary>
        public readonly Matrix4x4[] DepthViewMatrices = new Matrix4x4[2];

        /// <summary>
        /// Texture containing the points in the XR depth texture converted to world space.
        /// </summary>
        public RenderTexture? PositionsTexture { get; private set; }

        /// <summary>
        /// Texture containing the normal vectors of the points in the XR depth texture converted to world space.
        /// </summary>
        public RenderTexture? NormalsTexture { get; private set; }

        /// <summary>
        /// Flag set when this script processes its first depth frame after being enabled.
        /// </summary>
        public bool IsDataAvailable { get; private set; }

        public bool EnableSoftOcclusion;

        /// <summary>
        /// The compute shader used to preprocess the depth textures for meshing.
        /// </summary>
        [SerializeField, Tooltip("The compute shader used to preprocess the depth textures for meshing.")]
        private ComputeShader _shader; 


        [Tooltip("If not set, is obtained using FindAnyObjectByType.")]
        [SerializeField] private OVRCameraRig _cameraRig;

        /// <summary>
        /// Called when this script processes its first depth frame after being enabled.
        /// </summary>
        [Tooltip("Called when this script processes its first depth frame.")]
        public UnityEvent? OnDataAvailable;

        [Space, Header("Debug")]
        [SerializeField] private RawImage _positionsDebugPreview;
        [SerializeField] private RawImage _normalsDebugPreview;

        [Tooltip("Enables logging on frequently called update methods.")]
        [SerializeField] private bool _verboseLogging;

        private readonly Dictionary<IntPtr, (uint textureId, RenderTexture? renderTexture)> _nativeDepthTextures = new();
        private MetaOpenXROcclusionSubsystem _occlusionSubsystem;
        private XRDisplaySubsystem _displaySubsystem;
        private ComputeShaderKernel _kernel;
        private bool _permissionSetup;
        private bool _awakeCompleted;

        private Material? _softOcclusionPreprocessMat;
        private RenderTexture? _softOcclusionPreprocessTex;
        private RenderTargetSetup? _softOcclusionPreprocessSetup;

        protected override void Awake()
        {
            base.Awake();
            if (_shader == null)
            {
                Debug.LogError($"{nameof(DepthPreprocessor)}: Compute shader is not assigned. Depth processing will fail.");
                return;
            }

            if (_cameraRig == null)
            {
                _cameraRig = FindAnyObjectByType<OVRCameraRig>();
                if (_cameraRig == null)
                {
                    Debug.LogError($"{nameof(DepthPreprocessor)}: Could not find camera rig.");
                    return;
                }
            }

            XRLoader? loader = LoaderUtility.GetActiveLoader();
#pragma warning disable UNT0008 // Null propagation on Unity objects
            XRDisplaySubsystem? displaySubsystem = loader?.GetLoadedSubsystem<XRDisplaySubsystem>();
#pragma warning restore UNT0008 // Null propagation on Unity objects

            if (displaySubsystem == null)
            {
                Debug.LogError($"{nameof(DepthPreprocessor)}: Could not get loaded XR display subsystem.");
                return;
            }

            _displaySubsystem = displaySubsystem;
            XROcclusionSubsystem baseOcclusionSubsystem = loader!.GetLoadedSubsystem<XROcclusionSubsystem>();
            if (baseOcclusionSubsystem == null || baseOcclusionSubsystem is not MetaOpenXROcclusionSubsystem metaOcclusionSubsystem)
            {
                Debug.LogError($"{nameof(DepthPreprocessor)}: Could not get loaded Meta OpenXR occlusion subsystem.");
                return;
            }

            _occlusionSubsystem = metaOcclusionSubsystem;
            _kernel = new ComputeShaderKernel(_shader, "PreprocessDepth");
            _awakeCompleted = true;
        }

        protected void OnEnable()
        {
            if (!_awakeCompleted)
                return;

            Application.onBeforeRender += OnBeforeRender;
            if (!_permissionSetup && Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            {
                _permissionSetup = true;
                _occlusionSubsystem.Start();
                TrySetupRenderTexture();
            }

            if (!_permissionSetup)
                Debug.LogWarning($"{nameof(DepthPreprocessor)}: Scene permission not given, waiting.");
        }

        protected void OnDisable()
        {
            if (!_awakeCompleted)
                return;

            Application.onBeforeRender -= OnBeforeRender;
            if (_occlusionSubsystem.running)
                _occlusionSubsystem.Stop();

            ReleaseNativeTextures();
            ToggleShaderKeywords(false);
            IsDataAvailable = false;
        }

        protected void OnDestroy()
        {
            if (PositionsTexture != null)
            {
                PositionsTexture.Release();
                Destroy(PositionsTexture);
                PositionsTexture = null;
            }

            if (NormalsTexture != null)
            {
                NormalsTexture.Release();
                Destroy(NormalsTexture);
                NormalsTexture = null;
            }

            if (_softOcclusionPreprocessMat != null)
            {
                Destroy(_softOcclusionPreprocessMat);
                _softOcclusionPreprocessMat = null;
            }

            if (_softOcclusionPreprocessTex != null)
            {
                _softOcclusionPreprocessTex.Release();
                Destroy(_softOcclusionPreprocessTex);
                _softOcclusionPreprocessTex = null;
            }
        }

        private void OnBeforeRender()
        {
            if (!_permissionSetup)
            {
                if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
                    return;

                _permissionSetup = true;
                _occlusionSubsystem.Start();
                if (!TrySetupRenderTexture())
                    return;
            }

            if (PositionsTexture == null || NormalsTexture == null
                || (_nativeDepthTextures.Count == 0 && !TrySetupNativeTextures()))
                return;

            if (!_occlusionSubsystem.TryGetFrame(Allocator.Temp, out XROcclusionFrame frame)
                || !frame.TryGetReprojectionMatrices(_cameraRig.trackingSpace.worldToLocalMatrix, DepthReprojectionMatrices, DepthProjectionMatrices, DepthViewMatrices, out Vector4 zBufferParams))
            {
                if (_verboseLogging)
                    Debug.LogError($"{nameof(DepthPreprocessor)}: Failed to get depth frame and intrinsic data.");
                return;
            }

            NativeArray<XRTextureDescriptor> descs = _occlusionSubsystem.GetTextureDescriptors(Allocator.Temp);
            if (descs.Length != 1)
            {
                if (_verboseLogging)
                    Debug.LogError($"{nameof(DepthPreprocessor)}: Expected depth texture descriptors of Length 1, got Length {descs.Length}.");
                return;
            }

            XRTextureDescriptor desc = descs[0];
            if (desc.nativeTexture == IntPtr.Zero
                || !_nativeDepthTextures.TryGetValue(desc.nativeTexture, out (uint texId, RenderTexture? tex) nativeTexData))
            {
                if (_verboseLogging)
                    Debug.LogError($"{nameof(DepthPreprocessor)}: Could not find depth texture.");
                return;
            }

            if (nativeTexData.tex == null)
            {
                nativeTexData.tex = _displaySubsystem.GetRenderTexture(nativeTexData.texId);
                if (nativeTexData.tex == null)
                {
                    if (_verboseLogging)
                        Debug.LogError($"{nameof(DepthPreprocessor)}: Could not get depth render texture from XR display subsystem.");
                    return;
                }

                _nativeDepthTextures[desc.nativeTexture] = nativeTexData;
            }

            Shader.SetGlobalTexture(Global_DepthTexture, nativeTexData.tex);
            Shader.SetGlobalMatrixArray(Global_ReprojectionMatrices, DepthReprojectionMatrices);
            Shader.SetGlobalVector(Global_ZBufferParams, zBufferParams);

            DepthInverseReprojectionMatrices[0] = DepthReprojectionMatrices[0].inverse;
            DepthInverseReprojectionMatrices[1] = DepthReprojectionMatrices[1].inverse;
            
            Shader.SetGlobalMatrixArray(Global_InverseReprojectionMatrices, DepthInverseReprojectionMatrices);
            Shader.SetGlobalMatrixArray(Global_ProjectionMatrices, DepthProjectionMatrices);
            Shader.SetGlobalMatrixArray(Global_ViewMatrices, DepthViewMatrices);

            _kernel.Dispatch(PositionsTexture.width, PositionsTexture.height, 1);
            if (!IsDataAvailable)
            {
                // Callback shouldn't block Application.onBeforeRender
                Task.Run(async () =>
                {
                    await Awaitable.MainThreadAsync();
                    OnDataAvailable?.Invoke();
                });

                ToggleShaderKeywords(true);
                IsDataAvailable = true;
            }

            if (EnableSoftOcclusion)
                RenderSoftOcclusion();
        }

        private bool _encounteredUnrecoverablePreprocessError = false;
        private void RenderSoftOcclusion()
        {
            if (_encounteredUnrecoverablePreprocessError)
                return;

            if (_softOcclusionPreprocessMat == null)
            {
                Shader shader = Shader.Find("Meta/EnvironmentDepth/Preprocessing");
                if (shader == null)
                {
                    _encounteredUnrecoverablePreprocessError = true;
                    Debug.LogError("Meta/EnvironmentDepth/Preprocessing shader not found for soft occlusion.");
                    return;
                }

                _softOcclusionPreprocessMat = new Material(shader);
            }

            if (_softOcclusionPreprocessTex == null)
            {
                _softOcclusionPreprocessTex = new RenderTexture(PositionsTexture!.width, PositionsTexture.height, GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormat.None)
                {
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = 2,
                    depth = 0
                };

                _softOcclusionPreprocessTex.Create();
                Shader.SetGlobalTexture(Global_PreprocessedDepthTexture, _softOcclusionPreprocessTex);

                _softOcclusionPreprocessSetup = new RenderTargetSetup
                {
                    color = new RenderBuffer[1] { _softOcclusionPreprocessTex.colorBuffer },
                    depth = _softOcclusionPreprocessTex.depthBuffer,
                    depthSlice = -1,
                    colorLoad = new RenderBufferLoadAction[1] { RenderBufferLoadAction.DontCare },
                    colorStore = new RenderBufferStoreAction[1],
                    depthLoad = RenderBufferLoadAction.DontCare,
                    depthStore = RenderBufferStoreAction.DontCare,
                    mipLevel = 0,
                    cubemapFace = CubemapFace.Unknown
                };
            }

            Graphics.SetRenderTarget(_softOcclusionPreprocessSetup!.Value);
            _softOcclusionPreprocessMat.SetPass(0);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 3, 2);
        }

        private bool TrySetupNativeTextures()
        {
            if (!_occlusionSubsystem.TryGetSwapchainTextureDescriptors(out NativeArray<NativeArray<XRTextureDescriptor>> swapchainDescriptors))
            {
                if (_verboseLogging)
                    Debug.LogError($"{nameof(DepthPreprocessor)}: Could not get depth texture swapchain descriptors.");
                return false;
            }

            foreach (NativeArray<XRTextureDescriptor> swapchainDescriptor in swapchainDescriptors)
            {
                if (swapchainDescriptor.Length != 1)
                {
                    if (_verboseLogging)
                        Debug.LogError($"{nameof(DepthPreprocessor)}: Expected depth texture swapchain descriptor with Length 1, but got Length {swapchainDescriptor.Length}.");
                    ReleaseNativeTextures();
                    return false;
                }

                XRTextureDescriptor descriptor = swapchainDescriptor[0];
                if (descriptor.nativeTexture == IntPtr.Zero)
                {
                    if (_verboseLogging)
                        Debug.LogError($"{nameof(DepthPreprocessor)}: Swapchain descriptor's texture pointer is a nullptr");
                    ReleaseNativeTextures();
                    return false;
                }

                UnityXRDepthTextureFormat depthTextureFormat;
                switch (descriptor.format)
                {
                    case TextureFormat.RFloat:
                        depthTextureFormat = UnityXRDepthTextureFormat.kUnityXRDepthTextureFormat24bitOrGreater; break;
                    case TextureFormat.R16 or TextureFormat.RHalf:
                        depthTextureFormat = UnityXRDepthTextureFormat.kUnityXRDepthTextureFormat16bit; break;
                    default:
                        if (_verboseLogging)
                            Debug.LogError($"{nameof(DepthPreprocessor)}: Got unexpected depth texture descriptor format: {descriptor.format}");
                        ReleaseNativeTextures();
                        return false;
                }

                UnityXRRenderTextureDesc unityDescriptor = new()
                {
                    shadingRateFormat = UnityXRShadingRateFormat.kUnityXRShadingRateFormatNone,
                    shadingRate = new UnityXRTextureData(),
                    width = (uint)descriptor.width,
                    height = (uint)descriptor.height,
                    textureArrayLength = (uint)descriptor.depth,
                    flags = 0,
                    colorFormat = UnityXRRenderTextureFormat.kUnityXRRenderTextureFormatNone,
                    depthFormat = depthTextureFormat,
                    depth = new UnityXRTextureData()
                    {
                        nativePtr = descriptor.nativeTexture,
                    }
                };

                if (!UnityXRDisplay.CreateTexture(unityDescriptor, out uint textureId))
                {
                    if (_verboseLogging)
                        Debug.LogError($"{nameof(DepthPreprocessor)}: Could not create depth texture for swapchain descriptor.");
                    ReleaseNativeTextures();
                    return false;
                }

                _nativeDepthTextures.Add(descriptor.nativeTexture, (textureId, null));
            }

            return true;
        }

        private bool TrySetupRenderTexture()
        {
            if (!_occlusionSubsystem.TryGetEnvironmentDepth(out XRTextureDescriptor desc))
            {
                Debug.LogError($"{nameof(DepthPreprocessor)}: Could not get depth texture descriptor.");
                return false;
            }

            PositionsTexture = new RenderTexture(desc.width, desc.height, 0, GraphicsFormat.R16G16B16A16_SFloat)
            {
                enableRandomWrite = true
            };

            NormalsTexture = new RenderTexture(desc.width, desc.height, 0, GraphicsFormat.R8G8B8A8_SNorm)
            {
                enableRandomWrite = true
            };

            PositionsTexture.Create();
            NormalsTexture.Create();

            _shader.SetInts(DP_TextureSize, desc.width, desc.height);
            _kernel.SetTexture(DP_PositionsTexture, PositionsTexture);
            _kernel.SetTexture(DP_NormalsTexture, NormalsTexture);

            Shader.SetGlobalTexture(Global_PositionsTexture, PositionsTexture);
            Shader.SetGlobalTexture(Global_NormalsTexture, NormalsTexture);

            Debug.Log($"{nameof(DepthPreprocessor)}: Created position and normal textures, size: {desc.width}x{desc.height}.");

            if (_positionsDebugPreview != null)
                _positionsDebugPreview.texture = PositionsTexture;

            if (_normalsDebugPreview != null)
                _normalsDebugPreview.texture = NormalsTexture;

            return true;
        }

        private void ToggleShaderKeywords(bool toggle)
        {
            if (!toggle)
            {
                Shader.DisableKeyword("SOFT_OCCLUSION");
                Shader.DisableKeyword("HARD_OCCLUSION");
            }

            if (EnableSoftOcclusion)
            {
                Shader.EnableKeyword("SOFT_OCCLUSION");
                Shader.DisableKeyword("HARD_OCCLUSION");
            } else
            {
                Shader.DisableKeyword("SOFT_OCCLUSION");
                Shader.EnableKeyword("HARD_OCCLUSION");
            }
        }

        private void ReleaseNativeTextures()
        {
            foreach ((uint _, RenderTexture? renderTexture) in _nativeDepthTextures.Values)
            {
                if (renderTexture != null)
                    Destroy(renderTexture);
            }

            _nativeDepthTextures.Clear();
        }
    }
}
