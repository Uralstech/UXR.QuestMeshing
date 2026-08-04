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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.AI.Navigation;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Uralstech.Utils.Singleton;

#nullable enable
namespace Uralstech.UXR.QuestMeshing
{
    /// <summary>
    /// Converts the Meta Quest's Depth API textures into a 3D mesh in worldspace using the surface nets algorithm.
    /// </summary>
    [AddComponentMenu("Uralstech/UXR/QuestMeshing/Depth Mesher")]
    public class DepthMesher : DontCreateNewSingleton<DepthMesher>
    {
        #region Shader Properties
#pragma warning disable IDE1006 // Naming Styles
        private static readonly int MC_VolumeSize = Shader.PropertyToID("VolumeSize");
        private static readonly int MC_Volume = Shader.PropertyToID("Volume");
        private static readonly int MC_MetersPerVoxel = Shader.PropertyToID("MetersPerVoxel");
        private static readonly int MC_FrustumVolume = Shader.PropertyToID("FrustumVolume");
        private static readonly int MC_FrustumUpdateData = Shader.PropertyToID("FrustumUpdateData");
        private static readonly int MC_MaxTriangles = Shader.PropertyToID("MaxTriangles");
        private static readonly int MC_VertexBuffer = Shader.PropertyToID("VertexBuffer");
        private static readonly int MC_IndexBuffer = Shader.PropertyToID("IndexBuffer");
        private static readonly int MC_ValidTriangleCounter = Shader.PropertyToID("ValidTriangleCounter");
        private static readonly int MC_ValidVertexCounter = Shader.PropertyToID("ValidVertexCounter");
        private static readonly int MC_VertexIndexBuffer = Shader.PropertyToID("VertexIndexBuffer");
#pragma warning restore IDE1006 // Naming Styles
        #endregion
        
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct FrustumUpdateData
        {
            public static readonly int Size = Marshal.SizeOf<FrustumUpdateData>();
            
            public readonly Matrix4x4 ViewToWorldMatrix0;
        	public readonly Matrix4x4 ViewToWorldMatrix1;
        	public readonly Matrix4x4 WorldToTrackingMatrix;
        	public readonly Matrix4x4 TrackingToWorldMatrix;
        	
        	private readonly float _padding;

            public FrustumUpdateData(in Matrix4x4 viewToWorldMatrix0, in Matrix4x4 viewToWorldMatrix1,
                in Matrix4x4 worldToTrackingMatrix, in Matrix4x4 trackingToWorldMatrix) : this()
            {
                ViewToWorldMatrix0 = viewToWorldMatrix0;
                ViewToWorldMatrix1 = viewToWorldMatrix1;
                WorldToTrackingMatrix = worldToTrackingMatrix;
                TrackingToWorldMatrix = trackingToWorldMatrix;
            }
        }

        private const MeshColliderCookingOptions PhysicsDefaultCookingOptions =
            MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning
            | MeshColliderCookingOptions.WeldColocatedVertices
            | MeshColliderCookingOptions.UseFastMidphase;

        /// <summary>
        /// The TSDF volume used for the surface nets operation.
        /// </summary>
        public RenderTexture Volume { get; private set; } = null!;

        /// <summary>
        /// The generated mesh.
        /// </summary>
        public Mesh Mesh { get; private set; } = null!;

        /// <summary>
        /// Invoked after the <see cref="Mesh"/> is updated and all optional collision and NavMesh baking completes.
        /// </summary>
        public event Action? OnMeshRefreshed;

        /// <summary>
        /// Invoked <i>immediately</i> after the <see cref="Mesh"/> is updated.
        /// </summary>
        public event Action? OnMeshDataUpdated;

        /// <summary>
        /// Invoked after the <see cref="Mesh"/> is updated and before optional collision baking is started.
        /// </summary>
        public event Action? OnBeforeColliderBuild;

        /// <summary>
        /// Invoked after the <see cref="Mesh"/> is updated and before optional NavMesh baking is started.
        /// </summary>
        public event Action? OnBeforeNavMeshBuild;

        #region Editor Settings
        [Header("Mesher Settings")]
        [SerializeField, Tooltip("The compute shader containing kernels for volume updates and surface nets meshing.")]
        private ComputeShader _shader = null!;

        [SerializeField, Tooltip("The dimensions of the TSDF volume grid (width x height x depth). Higher resolutions will increase scanned volume but increase memory usage and compute cost.")]
        private Vector3Int _volumeSize = new(256, 64, 256);

        [SerializeField, Min(0.0f), Tooltip("The real-world size represented by each voxel (in meters). Smaller values yield finer detail but require larger volumes.")]
        private float _metersPerVoxel = 0.1f;

        [SerializeField, Min(0.0f), Tooltip("The minimum distance from the camera at which depth data is considered for meshing (ignores closer user-occluded data).")]
        private float _minViewDistance = 1f;

        [SerializeField, Min(0.0f), Tooltip("The maximum distance from the camera at which depth data is considered for meshing.")]
        private float _maxViewDistance = 4f;

        [SerializeField, Tooltip("The maximum number of triangles allowed in the generated mesh (caps GPU memory usage).")]
        private int _trianglesBudget = 64 * 64 * 64;

        /// <summary>The target update frequency for the TSDF volume (in Hz). Higher rates improve responsiveness but increase GPU load; lower rates reduce overhead in stable scenes.</summary>
        [Min(0.0f), Tooltip("The target update frequency for the TSDF volume (in Hz). Higher rates improve responsiveness but increase GPU load; lower rates reduce overhead in stable scenes.")]
        public float TargetVolumeUpdateRateHertz = 45;

        /// <summary>The target refresh frequency for the generated mesh (in Hz). Lower rates reduce CPU overhead for stable scenes.</summary>
        [Min(0.0f), Tooltip("The target refresh frequency for the generated mesh (in Hz). Lower rates reduce CPU overhead for stable scenes.")]
        public float TargetMeshRefreshRateHertz = 1;

        [SerializeField, Tooltip("The OVRCameraRig providing the eye poses and tracking space. If not assigned, auto-finds via FindAnyObjectByType.")]
        private OVRCameraRig _cameraRig = null!;

        [Space, Header("Mesh Consumers")]
        [SerializeField, Tooltip("The MeshFilter to assign the generated mesh to for rendering.")]
        private MeshFilter? _meshFilterConsumer;

        [SerializeField, Tooltip("The MeshCollider to assign the generated mesh to for physics collisions.")]
        private MeshCollider? _meshColliderConsumer;

        [Space, Header("Collider Baking Options")]
        [SerializeField, Tooltip("If enabled, bakes the mesh into the MeshCollider for optimized physics queries.")]
        private bool _bakeCollision = true;

        [Space, Header("NavMesh Baking Options")]
        [SerializeField, Tooltip("If enabled, dynamically bakes a NavMesh surface from the generated mesh for AI pathfinding.")]
        private bool _bakeNavMesh = true;
        
        [SerializeField, Min(1), Tooltip("Bake the NavMesh once every N mesh updates. A value of 1 bakes after every mesh update.")]
        private int _navMeshBakeSubfrequency = 1;

        [SerializeField, Tooltip("The NavMeshSurface component to bake the mesh into.")]
        private NavMeshSurface? _navMeshSurface;
        
        [SerializeField, Tooltip("Uses an optimized NavMesh baking path that updates the NavMesh using only the generated depth mesh. " +
                                 "This can be faster than a full NavMeshSurface bake, but all other NavMesh build sources are ignored.")]
        private bool _useFastNavMeshBake;

        [SerializeField, Min(0), Tooltip("Maximum number of worker jobs Unity may use when performing a fast NavMesh bake. " +
                                         "Higher values can reduce bake time on CPUs with more cores, but may increase CPU usage and " +
                                         "compete with other jobs. Set to 1 to minimize background CPU contention.")]
        private uint _fastNavMeshBakeWorkers = 1;
        #endregion
    
        #region Shader Kernels and Buffers
        private ComputeShaderKernel _volumeClearKernel;
        private ComputeShaderKernel _updateVoxelsKernel;
        private ComputeShaderKernel _viBufferClearKernel;
        private ComputeShaderKernel _sfVertexPassKernel;
        private ComputeShaderKernel _sfTrianglePassKernel;

        // cached points within viewspace depth frustum 
        // like a 3D lookup table
        private GraphicsBuffer? _frustumVolume;
        private GraphicsBuffer _frustumUpdateDataBuffer = null!;
        private GraphicsBuffer _validVertCounterBuffer  = null!;
        private GraphicsBuffer _validTriCounterBuffer   = null!;
        private GraphicsBuffer _counterCopyBuffer       = null!;
        private GraphicsBuffer _vertexIndexBuffer       = null!;
        private GraphicsBuffer _vertexBuffer            = null!;
        private GraphicsBuffer _indexBuffer             = null!;
        
        private NativeArray<uint> _resourceCountArray;
        private NativeArray<Vector3> _verticesArray;
        private NativeArray<uint> _indicesArray;
        #endregion

        private CancellationTokenSource? _updateCancellation;
        private DepthPreprocessor? _depthPreprocessor;
        private Transform _trackingSpace = null!;
        private int _numMeshUpdatesSinceNavMeshUpdate;
        private bool _awakeSuccessful;
        private bool _startCalled;

#if UNITY_6000_3_OR_NEWER
        private EntityId? _meshIdV2;
#else
        private int? _meshId;
#endif

        private JobHandle? _collisionBakeJob;
        private bool _warnedConsumerPositions;

        protected override void Awake()
        {
            base.Awake();
            if (_shader == null)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Compute shader is not assigned. Meshing will fail.");
                return;
            }

            if (_cameraRig == null)
            {
                _cameraRig = FindAnyObjectByType<OVRCameraRig>();
                if (_cameraRig == null)
                {
                    Debug.LogError($"{nameof(DepthMesher)}: Could not find camera rig.");
                    return;
                }
            }

            if (_bakeNavMesh && _navMeshSurface == null)
            {
                Debug.LogWarning($"{nameof(DepthMesher)}: NavMesh baking is enabled but a NavMeshSurface has not been assigned. Baking has been disabled.");
                _bakeNavMesh = false;
            }

            _trackingSpace = _cameraRig.trackingSpace;

            InitializeKernels();
            InitializeVolume();
            InitializeMeshData();
            InitializeCounters();
            InitializeFrustumUpdateDataBuffer();
                    
            OVRManager.display.RecenteredPose += Clear;
            _awakeSuccessful = true;
        }

        protected void Start()
        {
            if (!DepthPreprocessor.TryGetInstance(out _depthPreprocessor))
            {
                Debug.LogError($"{nameof(DepthMesher)}: {nameof(DepthPreprocessor)} was not found in the current scene.");
                enabled = false;
            }

            _startCalled = true;
        }

        protected void OnEnable()
        {
            if (!_awakeSuccessful)
                return;

            _updateCancellation = new CancellationTokenSource();
            RunVolumeUpdateLoopAsync(_updateCancellation.Token).Forget();
            RunMeshRefreshLoopAsync(_updateCancellation.Token).Forget();
        }

        protected void OnDisable()
        {
            if (!_awakeSuccessful)
                return;

            _updateCancellation?.Cancel();
            _updateCancellation?.Dispose();
            _updateCancellation = null;
        }

        protected void OnDestroy()
        {
            if (!_awakeSuccessful)
                return;

            OVRManager.display.RecenteredPose -= Clear;
            
            _collisionBakeJob?.Complete();
            Volume.Release();
            Destroy(Volume);

            Destroy(Mesh);

            _frustumVolume?.Dispose();
            _frustumVolume = null;
            
            _frustumUpdateDataBuffer.Dispose();

            _validVertCounterBuffer.Dispose();
            _validTriCounterBuffer.Dispose();
            _counterCopyBuffer.Dispose();
            _vertexIndexBuffer.Dispose();
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            
            _resourceCountArray.Dispose();
            _verticesArray.Dispose();
            _indicesArray.Dispose();
        }

        /// <summary>
        /// Clears the TSDF volume and vertex-index buffer, essentially resetting the system.
        /// </summary>
        public void Clear()
        {
            _volumeClearKernel.Dispatch(_volumeSize);
            _viBufferClearKernel.Dispatch(_vertexIndexBuffer.count);
        }

        private async Task RunVolumeUpdateLoopAsync(CancellationToken token)
        {
            while (!_startCalled)
                await Awaitable.NextFrameAsync();

            DepthPreprocessor? preprocessor = _depthPreprocessor;
            if (preprocessor == null || Mathf.Approximately(TargetVolumeUpdateRateHertz, 0f))
                return;

            do
            {
                if (!preprocessor.IsDataAvailable)
                {
                    await Awaitable.NextFrameAsync();
                    continue;
                }
                
                if (_frustumVolume == null)
                    InitializeFrustumVolume(preprocessor);

                NativeArray<FrustumUpdateData> strideParams = _frustumUpdateDataBuffer.LockBufferForWrite<FrustumUpdateData>(0, 1);
                strideParams[0] = new FrustumUpdateData(
                    _trackingSpace.localToWorldMatrix * preprocessor.DepthViewMatrices[0].inverse,
                    _trackingSpace.localToWorldMatrix * preprocessor.DepthViewMatrices[1].inverse,
                    _trackingSpace.worldToLocalMatrix,
                    _trackingSpace.localToWorldMatrix
                );

                _frustumUpdateDataBuffer.UnlockBufferAfterWrite<FrustumUpdateData>(1);
                _updateVoxelsKernel.Dispatch(_frustumVolume!.count);
                
                await Awaitable.WaitForSecondsAsync(1f / TargetVolumeUpdateRateHertz);
            } while (!token.IsCancellationRequested);
        }

        private async Task RunMeshRefreshLoopAsync(CancellationToken token)
        {
            while (!_startCalled)
                await Awaitable.NextFrameAsync();
                
            DepthPreprocessor? preprocessor = _depthPreprocessor;
            if (preprocessor == null || Mathf.Approximately(TargetMeshRefreshRateHertz, 0f))
                return;

            do
            {
                if (!preprocessor.IsDataAvailable)
                {
                    await Awaitable.NextFrameAsync();
                    continue;
                }
                
                _validTriCounterBuffer.SetCounterValue(0);
                _validVertCounterBuffer.SetCounterValue(0);
                _sfVertexPassKernel.Dispatch(_volumeSize);
                _sfTrianglePassKernel.Dispatch(_volumeSize);
                Mesh.bounds = new Bounds(_trackingSpace.TransformPoint(Vector3.zero), (Vector3)_volumeSize * _metersPerVoxel);

                await ProcessMeshDataCPU();
                await Awaitable.WaitForSecondsAsync(1f / TargetMeshRefreshRateHertz);
            } while (!token.IsCancellationRequested);
        }
        
        private async Awaitable ProcessMeshDataCPU()
        {
            GraphicsBuffer.CopyCount(_validTriCounterBuffer, _counterCopyBuffer, 0);

            AsyncGPUReadbackRequest vtcResult = await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref _resourceCountArray, _counterCopyBuffer);
            if (vtcResult.hasError)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Could not process mesh data due to GPU readback error for valid triangles count.");
                return;
            }

            int triangleCount = Mathf.Min((int)_resourceCountArray[0], _trianglesBudget);
            int indexCount = triangleCount * 3;

            GraphicsBuffer.CopyCount(_validVertCounterBuffer, _counterCopyBuffer, 0);
            vtcResult = await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref _resourceCountArray, _counterCopyBuffer);
            if (vtcResult.hasError)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Could not process mesh data due to GPU readback error for valid vertices count.");
                return;
            }

            int vertexCount = (int)_resourceCountArray[0];
            if (triangleCount == 0)
                return;
            
            (Awaitable<AsyncGPUReadbackRequest> vResultTask, Awaitable<AsyncGPUReadbackRequest> iResultTask) = (
                AsyncGPUReadback.RequestIntoNativeArrayAsync(ref _verticesArray, _vertexBuffer, sizeof(float) * 3 * vertexCount, 0),
                AsyncGPUReadback.RequestIntoNativeArrayAsync(ref _indicesArray, _indexBuffer, sizeof(uint) * indexCount, 0)
            );

            (AsyncGPUReadbackRequest vResult, AsyncGPUReadbackRequest iResult) = (await vResultTask, await iResultTask);
            if (vResult.hasError || iResult.hasError)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Could not process mesh data due to GPU readback error for vertex or index buffer.");
                return;
            }

            Mesh.SetVertexBufferData(_verticesArray, 0, 0, vertexCount, flags: MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            Mesh.SetIndexBufferData(_indicesArray, 0, 0, indexCount, flags: MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            Mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);

            UpdateMeshFilterIfNeeded();
            OnMeshDataUpdated?.Invoke();

            await BakeCollisionIfNeededAsync();
            UpdateMeshColliderIfNeeded();
            
            await BakeNavMeshIfNeededAsync();
            OnMeshRefreshed?.Invoke();
        }

        private async Awaitable BakeNavMeshIfNeededAsync()
        {
            if (!_bakeNavMesh || _navMeshSurface == null)
                return;

            _numMeshUpdatesSinceNavMeshUpdate++;
            if (_numMeshUpdatesSinceNavMeshUpdate < _navMeshBakeSubfrequency)
                return;

            _numMeshUpdatesSinceNavMeshUpdate = 0;
            await Awaitable.EndOfFrameAsync();
            OnBeforeNavMeshBuild?.Invoke();
            
            if (_navMeshSurface.navMeshData == null)
                _navMeshSurface.navMeshData = new NavMeshData();
                
            _navMeshSurface.AddData();
            
            bool useFastBake = _useFastNavMeshBake;
            if (_navMeshSurface.useGeometry == NavMeshCollectGeometry.PhysicsColliders
                && _meshColliderConsumer == null)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Fast NavMesh baking enabled with Physics colliders, but no MeshCollider consumer was set. Defaulting to slow baking.");
                useFastBake = false;
            }
            else if (_navMeshSurface.useGeometry == NavMeshCollectGeometry.RenderMeshes
                     && _meshFilterConsumer == null)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Fast NavMesh baking enabled with Render meshes, but no MeshFilter consumer was set. Defaulting to slow baking.");
                useFastBake = false;
            }
            
            if (!useFastBake)
            {
                await _navMeshSurface.UpdateNavMesh(_navMeshSurface.navMeshData);
                _useFastNavMeshBake = useFastBake;
                return;
            }

            Transform root = _navMeshSurface.useGeometry == NavMeshCollectGeometry.PhysicsColliders
                ? _meshColliderConsumer!.transform : _meshFilterConsumer!.transform;
            
            List<NavMeshBuildMarkup> markups = new(1);
            if (root.TryGetComponent(out NavMeshModifier modifier))
            {
                markups.Add(new NavMeshBuildMarkup()
                {
                    root = modifier.transform,
                    overrideArea = modifier.overrideArea,
                    area = modifier.area,
                    ignoreFromBuild = false,
                    applyToChildren = false,
                    overrideGenerateLinks = modifier.overrideGenerateLinks,
                    generateLinks = modifier.generateLinks,
                });
            }
            
            List<NavMeshBuildSource> sources = new(1);
            NavMeshBuilder.CollectSources(root, _navMeshSurface.layerMask, _navMeshSurface.useGeometry,
                _navMeshSurface.defaultArea, markups, sources);

            if (sources.Count == 0)
            {
                Debug.LogWarning($"{nameof(DepthMesher)}: {root.name} could not be detected as a NavMesh build source.");
                return;
            }

            if (sources.Count > 1)
                Debug.LogWarning($"{nameof(DepthMesher)}: Did not expect more than one NavMesh build source from {root.name}, bounds calculation will only account for scanned Mesh.");
            
            Matrix4x4 worldToSurface = Matrix4x4.TRS(
                _navMeshSurface.transform.position,
                _navMeshSurface.transform.rotation,
                Vector3.one).inverse;

            Bounds navMeshBounds = new();
            navMeshBounds.Encapsulate(GetWorldBounds(worldToSurface * sources[0].transform, Mesh.bounds));
            navMeshBounds.Expand(0.1f);
            
            NavMeshBuildSettings settings = _navMeshSurface.GetBuildSettings();
            settings.maxJobWorkers = _fastNavMeshBakeWorkers;
            
            await NavMeshBuilder.UpdateNavMeshDataAsync(_navMeshSurface.navMeshData, settings, sources, navMeshBounds);
        }
        
        // Abs and GetWorldBounds are based on https://github.com/Unity-Technologies/NavMeshComponents,
        // licensed under the MIT License:
        // The MIT License (MIT)
        // 
        // Copyright (c) 2016, Unity Technologies
        // 
        // Permission is hereby granted, free of charge, to any person obtaining a copy
        // of this software and associated documentation files (the "Software"), to deal
        // in the Software without restriction, including without limitation the rights
        // to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        // copies of the Software, and to permit persons to whom the Software is
        // furnished to do so, subject to the following conditions:
        // 
        // The above copyright notice and this permission notice shall be included in
        // all copies or substantial portions of the Software.
        // 
        // THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        // IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        // FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        // AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        // LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        // OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
        // THE SOFTWARE.
        private static Vector3 Abs(Vector3 v) =>
            new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static Bounds GetWorldBounds(Matrix4x4 mat, Bounds bounds)
        {
            Vector3 absAxisX = Abs(mat.MultiplyVector(Vector3.right));
            Vector3 absAxisY = Abs(mat.MultiplyVector(Vector3.up));
            Vector3 absAxisZ = Abs(mat.MultiplyVector(Vector3.forward));
            Vector3 worldPosition = mat.MultiplyPoint(bounds.center);
            Vector3 worldSize = (absAxisX * bounds.size.x) + (absAxisY * bounds.size.y) + (absAxisZ * bounds.size.z);
            return new Bounds(worldPosition, worldSize);
        }

        private void UpdateMeshColliderIfNeeded()
        {
            if (_meshColliderConsumer == null)
                return;

            if (!_warnedConsumerPositions
                && _meshColliderConsumer.transform.position != Vector3.zero)
            {
                Debug.LogWarning($"{nameof(DepthMesher)}: Mesh filter and collider consumers must be at world origin for correct alignment.");
                _warnedConsumerPositions = true;
            }
            
            if (_bakeCollision && _meshColliderConsumer.cookingOptions != PhysicsDefaultCookingOptions)
            {
                Debug.LogWarning($"{nameof(DepthMesher)}: Mesh collider consumer updates are disabled because the consumer is using non-default cooking options.");
                _meshColliderConsumer = null;
                return;
            }
                
            _meshColliderConsumer.sharedMesh = Mesh;
        }

    #if UNITY_6000_3_OR_NEWER
        private async Awaitable BakeCollisionIfNeededAsync()
        {
            if (!_bakeCollision || !_meshIdV2.HasValue)
                return;

            OnBeforeColliderBuild?.Invoke();

            _collisionBakeJob?.Complete();
            _collisionBakeJob = new MeshColliderBakeJobV2(_meshIdV2.Value).Schedule();

            while (!_collisionBakeJob.Value.IsCompleted)
                await Awaitable.NextFrameAsync();

            _collisionBakeJob.Value.Complete();
            _collisionBakeJob = null;
        }
    #else
        private async Awaitable BakeCollisionIfNeededAsync()
        {
            if (!_bakeCollision || !_meshId.HasValue)
                return;
            
            OnBeforeColliderBuild?.Invoke();

            _collisionBakeJob?.Complete();
            _collisionBakeJob = new MeshColliderBakeJob(_meshId.Value).Schedule();

            while (!_collisionBakeJob.Value.IsCompleted)
                await Awaitable.NextFrameAsync();

            _collisionBakeJob.Value.Complete();
            _collisionBakeJob = null;
        }
    #endif

        private void UpdateMeshFilterIfNeeded()
        {
            if (_meshFilterConsumer == null)
                return;
            
            if (!_warnedConsumerPositions
                && _meshFilterConsumer.transform.position != Vector3.zero)
            {
                Debug.LogWarning($"{nameof(DepthMesher)}: Mesh filter and collider consumers must be at world origin for correct alignment.");
                _warnedConsumerPositions = true;
            }
            
            _meshFilterConsumer.mesh = Mesh;
        }

        // InitializeFrustumVolume is based on https://github.com/anaglyphs/lasertag,
        // licensed under the MIT License:
        // MIT License
        // 
        // Copyright (c) 2024 Julian Triveri & Hazel Roeder
        // 
        // Permission is hereby granted, free of charge, to any person obtaining a copy
        // of this software and associated documentation files (the "Software"), to deal
        // in the Software without restriction, including without limitation the rights
        // to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        // copies of the Software, and to permit persons to whom the Software is
        // furnished to do so, subject to the following conditions:
        // 
        // The above copyright notice and this permission notice shall be included in all
        // copies or substantial portions of the Software.
        // 
        // THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        // IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        // FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        // AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        // LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        // OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        // SOFTWARE.
        private void InitializeFrustumVolume(DepthPreprocessor preprocessor)
        {
            List<Vector3> positions = new(200000);

            FrustumPlanes planes = preprocessor.DepthProjectionMatrices[0].decomposeProjection;
            planes.zFar = _maxViewDistance;

            // slopes 
            float ls = planes.left / planes.zNear;
            float rs = planes.right / planes.zNear;
            float ts = planes.top / planes.zNear;
            float bs = planes.bottom / planes.zNear;

            for (float z = planes.zNear; z < planes.zFar; z += _metersPerVoxel)
            {
                float xMin = (ls * z) + _metersPerVoxel;
                float xMax = (rs * z) - _metersPerVoxel;

                float yMin = (bs * z) + _metersPerVoxel;
                float yMax = (ts * z) - _metersPerVoxel;

                for (float x = xMin; x < xMax; x += _metersPerVoxel)
                {
                    for (float y = yMin; y < yMax; y += _metersPerVoxel)
                    {
                        Vector3 v = new(x, y, -z);

                        if (v.magnitude > _minViewDistance && v.magnitude < _maxViewDistance)
                            positions.Add(v);
                    }
                }
            }

            _frustumVolume = new GraphicsBuffer(GraphicsBuffer.Target.Structured, positions.Count, sizeof(float) * 3);
            _frustumVolume.SetData(positions);

            _updateVoxelsKernel.SetBuffer(MC_FrustumVolume, _frustumVolume);
            Debug.Log($"{nameof(DepthMesher)}: Frustum volume positions initialized.");
        }

        private void InitializeKernels()
        {
            _volumeClearKernel = new ComputeShaderKernel(_shader, "Clear");
            _updateVoxelsKernel = new ComputeShaderKernel(_shader, "UpdateVoxels");
            _viBufferClearKernel = new ComputeShaderKernel(_shader, "VertexIndexBufferClear");
            _sfVertexPassKernel = new ComputeShaderKernel(_shader, "SurfaceNetsVertexPass");
            _sfTrianglePassKernel = new ComputeShaderKernel(_shader, "SurfaceNetsTrianglePass");
        }

        private void InitializeVolume()
        {
            Volume = new RenderTexture(_volumeSize.x, _volumeSize.y, 0, GraphicsFormat.R8_SNorm)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = _volumeSize.z,
                enableRandomWrite = true,
            };

            Volume.Create();

            _shader.SetInts(MC_VolumeSize, _volumeSize.x, _volumeSize.y, _volumeSize.z);
            _shader.SetFloat(MC_MetersPerVoxel, _metersPerVoxel);

            _volumeClearKernel.SetTexture(MC_Volume, Volume);
            _updateVoxelsKernel.SetTexture(MC_Volume, Volume);
            _sfVertexPassKernel.SetTexture(MC_Volume, Volume);
            _sfTrianglePassKernel.SetTexture(MC_Volume, Volume);

            _volumeClearKernel.Dispatch(_volumeSize);
        }

        private void InitializeMeshData()
        {
            Mesh = new Mesh();

#if UNITY_6000_3_OR_NEWER
            _meshIdV2 = Mesh.GetEntityId();
#else
            _meshId = Mesh.GetInstanceID();
#endif

            int vertexCount = _trianglesBudget * 3;
            Mesh.SetVertexBufferParams(vertexCount, new VertexAttributeDescriptor(VertexAttribute.Position));
            Mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt32);

            _vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertexCount, sizeof(float) * 3);
            _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _trianglesBudget, sizeof(uint) * 3);
            
            _sfVertexPassKernel.SetBuffer(MC_VertexBuffer, _vertexBuffer);
            _sfTrianglePassKernel.SetBuffer(MC_IndexBuffer, _indexBuffer);

            _shader.SetInt(MC_MaxTriangles, _trianglesBudget);

            _vertexIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _volumeSize.x * _volumeSize.y * _volumeSize.z, sizeof(uint));
            _viBufferClearKernel.SetBuffer(MC_VertexIndexBuffer, _vertexIndexBuffer);
            _sfVertexPassKernel.SetBuffer(MC_VertexIndexBuffer, _vertexIndexBuffer);
            _sfTrianglePassKernel.SetBuffer(MC_VertexIndexBuffer, _vertexIndexBuffer);
            
            _viBufferClearKernel.Dispatch(_vertexIndexBuffer.count);

            _resourceCountArray = new NativeArray<uint>(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _verticesArray = new NativeArray<Vector3>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _indicesArray = new NativeArray<uint>(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        private void InitializeCounters()
        {
            _counterCopyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 1, sizeof(uint));

            _validVertCounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Counter ,1, sizeof(uint));
            _sfVertexPassKernel.SetBuffer(MC_ValidVertexCounter, _validVertCounterBuffer);

            _validTriCounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Counter, 1, sizeof(uint));
            _sfTrianglePassKernel.SetBuffer(MC_ValidTriangleCounter, _validTriCounterBuffer);
        }

        private void InitializeFrustumUpdateDataBuffer()
        {
            _frustumUpdateDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, 1, FrustumUpdateData.Size);
            _shader.SetConstantBuffer(MC_FrustumUpdateData, _frustumUpdateDataBuffer, 0, FrustumUpdateData.Size);
        }
    }
}