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
using System.Threading;
using Unity.AI.Navigation;
using Unity.Burst;
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
        private static readonly int MC_ViewToWorldMatrices = Shader.PropertyToID("ViewToWorldMatrices");
        private static readonly int MC_UserWorldPos = Shader.PropertyToID("UserWorldPos");
        private static readonly int MC_WorldToTrackingMatrix = Shader.PropertyToID("WorldToTrackingMatrix");
        private static readonly int MC_TrackingToWorldMatrix = Shader.PropertyToID("TrackingToWorldMatrix");
        private static readonly int MC_MaxTriangles = Shader.PropertyToID("MaxTriangles");
        private static readonly int MC_VertexBuffer = Shader.PropertyToID("VertexBuffer");
        private static readonly int MC_IndexBuffer = Shader.PropertyToID("IndexBuffer");
        private static readonly int MC_ValidTriangleCounter = Shader.PropertyToID("ValidTriangleCounter");
        private static readonly int MC_ValidVertexCounter = Shader.PropertyToID("ValidVertexCounter");
        private static readonly int MC_VertexIndexBuffer = Shader.PropertyToID("VertexIndexBuffer");
        private static readonly int MC_MaxMeshUpdateDistance = Shader.PropertyToID("MaxMeshUpdateDistance");
#pragma warning restore IDE1006 // Naming Styles
        #endregion

        [BurstCompile]
        public readonly struct MeshColliderBakeJob : IJob
        {
            public readonly int MeshId;
            public MeshColliderBakeJob(int meshId)
            {
                MeshId = meshId;
            }

            public void Execute() => Physics.BakeMesh(MeshId, false);
        }
    
        /// <summary>
        /// The TSDF volume used for the surface nets operation.
        /// </summary>
        public RenderTexture Volume { get; private set; }

        /// <summary>
        /// The generated mesh.
        /// </summary>
        public Mesh Mesh { get; private set; }

        /// <summary>
        /// Called after <see cref="Mesh"/> is updated and all collision and NavMesh baking is completed.
        /// </summary>
        public event Action? OnMeshRefreshed;

        #region Editor Settings
        [Header("Mesher Settings")]
        [SerializeField, Tooltip("The compute shader containing kernels for volume updates and surface nets meshing.")]
        private ComputeShader _shader;

        [SerializeField, Tooltip("The dimensions of the TSDF volume grid (X: width, Y: height, Z: depth). Higher resolutions improve detail but increase memory and compute cost.")]
        private Vector3Int _volumeSize = new(256, 64, 256);

        [SerializeField, Min(0.0f), Tooltip("The real-world size represented by each voxel (in meters). Smaller values yield finer detail but require larger volumes.")]
        private float _metersPerVoxel = 0.1f;

        [SerializeField, Min(0.0f), Tooltip("The minimum distance from the camera at which depth data is considered for meshing (ignores closer user-occluded data).")]
        private float _minViewDistance = 1f;

        [SerializeField, Min(0.0f), Tooltip("The maximum distance from the camera at which depth data is considered for meshing.")]
        private float _maxViewDistance = 4f;

        [SerializeField, Min(0.0f), Tooltip("The maximum distance from the user's position to update the mesh (optimizes for local changes).")]
        private float _maxMeshUpdateDistance = 4f;

        [SerializeField, Tooltip("The maximum number of triangles allowed in the generated mesh (caps GPU memory usage).")]
        private int _trianglesBudget = 64 * 64 * 64;

        /// <summary>The target update frequency for the TSDF volume (in Hz). Higher rates improve responsiveness but increase GPU load.</summary>
        [Min(0.0f), Tooltip("The target update frequency for the TSDF volume (in Hz). Higher rates improve responsiveness but increase GPU load.")]
        public float TargetVolumeUpdateRateHertz = 45;

        /// <summary>The target refresh frequency for the generated mesh (in Hz). Lower rates reduce CPU overhead for stable scenes.</summary>
        [Min(0.0f), Tooltip("The target refresh frequency for the generated mesh (in Hz). Lower rates reduce CPU overhead for stable scenes.")]
        public float TargetMeshRefreshRateHertz = 1;

        [SerializeField, Tooltip("The OVRCameraRig providing eye poses and tracking space. If not assigned, auto-finds via FindAnyObjectByType.")]
        private OVRCameraRig _cameraRig;

        [Space, Header("Mesh Consumers")]
        [SerializeField, Tooltip("The MeshFilter to assign the generated mesh to for rendering.")]
        private MeshFilter _meshFilterConsumer;

        [SerializeField, Tooltip("The MeshCollider to assign the generated mesh to for physics collisions.")]
        private MeshCollider _meshColliderConsumer;

        [Space, Header("Collider Baking Options")]
        [SerializeField, Tooltip("If enabled, bakes the mesh into the MeshCollider for optimized physics queries.")]
        private bool _bakeCollision = true;

        [Space, Header("NavMesh Baking Options")]
        [SerializeField, Tooltip("If enabled, dynamically bakes a NavMesh surface from the generated mesh for AI pathfinding.")]
        private bool _bakeNavMesh = true;

        [SerializeField, Tooltip("The NavMeshSurface component to bake the mesh into.")]
        private NavMeshSurface _navMeshSurface;
        #endregion
    
        #region Shader Kernels and Buffers
        private ComputeShaderKernel _volumeClearKernel;
        private ComputeShaderKernel _updateVoxelsKernel;
        private ComputeShaderKernel _viBufferClearKernel;
        private ComputeShaderKernel _sfVertexPassKernel;
        private ComputeShaderKernel _sfTrianglePassKernel;

        // cached points within viewspace depth frustum 
        // like a 3D lookup table
        private ComputeBuffer? _frustumVolume;
        private ComputeBuffer _validVertCounterBuffer;
        private ComputeBuffer _validTriCounterBuffer;
        private ComputeBuffer _counterCopyBuffer;
        private ComputeBuffer _vertexIndexBuffer;
        private ComputeBuffer _vertexBuffer;
        private ComputeBuffer _indexBuffer;
        #endregion

        private readonly Matrix4x4[] _viewToWorldMatrices = new Matrix4x4[2];
        private CancellationTokenSource? _updateCancellation;
        private DepthPreprocessor? _depthPreprocessor;
        private Transform _centerEyeAnchor;
        private Transform _trackingSpace;
        private bool _awakeSuccessful;
        private bool _startCalled;
        private int? _meshId;

        private NavMeshDataInstance? _navMeshDataInstance;
        private JobHandle? _collisionBakeJob;

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

            _centerEyeAnchor = _cameraRig.centerEyeAnchor;
            _trackingSpace = _cameraRig.trackingSpace;

            InitializeKernels();
            InitializeVolume();
            InitializeMeshData();
            InitializeCounters();
                    
            OVRManager.display.RecenteredPose += Clear;
            _awakeSuccessful = true;
        }

        protected void Start()
        {
            if (DepthPreprocessor.Instance == null)
            {
                Debug.LogError($"{nameof(DepthMesher)}: {nameof(DepthPreprocessor)} was not found in the current scene.");
                enabled = false;
            }

            _depthPreprocessor = DepthPreprocessor.Instance;
            _startCalled = true;
        }

        protected void OnEnable()
        {
            if (!_awakeSuccessful)
                return;

            _updateCancellation = new CancellationTokenSource();
            RunVolumeUpdateLoopAsync(_updateCancellation.Token);
            RunMeshRefreshLoopAsync(_updateCancellation.Token);
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
            if (_navMeshDataInstance.HasValue)
            {
                _navMeshDataInstance.Value.Remove();
                _navMeshDataInstance = null;
            }
            
            _collisionBakeJob?.Complete();
            Volume.Release();
            Destroy(Volume);

            Destroy(Mesh);

            _frustumVolume?.Dispose();
            _frustumVolume = null;

            _validVertCounterBuffer.Dispose();
            _validTriCounterBuffer.Dispose();
            _counterCopyBuffer.Dispose();
            _vertexIndexBuffer.Dispose();
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
        }

        public void Clear()
        {
            _volumeClearKernel.Dispatch(Volume.width, Volume.height, Volume.volumeDepth);
            if (_vertexIndexBuffer != null)
                _viBufferClearKernel.Dispatch(_vertexIndexBuffer.count, 1, 1);
        }

        private async void RunVolumeUpdateLoopAsync(CancellationToken token)
        {
            while (!_startCalled)
                await Awaitable.NextFrameAsync();

            DepthPreprocessor? preprocessor = _depthPreprocessor;
            if (preprocessor == null)
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

                _viewToWorldMatrices[0] = _trackingSpace.localToWorldMatrix * preprocessor.DepthViewMatrices[0].inverse;
                _viewToWorldMatrices[1] = _trackingSpace.localToWorldMatrix * preprocessor.DepthViewMatrices[1].inverse;
                _shader.SetMatrixArray(MC_ViewToWorldMatrices, _viewToWorldMatrices);

                Vector3 playerPos = _centerEyeAnchor.position;
                _shader.SetFloats(MC_UserWorldPos, playerPos.x, playerPos.y, playerPos.z);

                _shader.SetMatrix(MC_WorldToTrackingMatrix, _trackingSpace.worldToLocalMatrix);
                _shader.SetMatrix(MC_TrackingToWorldMatrix, _trackingSpace.localToWorldMatrix);

                _updateVoxelsKernel.Dispatch(_frustumVolume!.count, 1, 1);
                await Awaitable.WaitForSecondsAsync(1f / TargetVolumeUpdateRateHertz);
            } while (!token.IsCancellationRequested);
        }

        private async void RunMeshRefreshLoopAsync(CancellationToken token)
        {
            while (!_startCalled)
                await Awaitable.NextFrameAsync();
                
            DepthPreprocessor? preprocessor = _depthPreprocessor;
            if (preprocessor == null)
                return;

            do
            {
                if (!preprocessor.IsDataAvailable)
                {
                    await Awaitable.NextFrameAsync();
                    continue;
                }
                
                _validTriCounterBuffer!.SetCounterValue(0);
                _validVertCounterBuffer!.SetCounterValue(0);
                _sfVertexPassKernel.Dispatch(Volume!.width, Volume.height, Volume.volumeDepth);
                _sfTrianglePassKernel.Dispatch(Volume!.width, Volume.height, Volume.volumeDepth);
                Mesh!.bounds = new Bounds(_trackingSpace.TransformPoint(Vector3.zero), (Vector3)_volumeSize * _metersPerVoxel);

                await ProcessMeshDataCPU();
                await Awaitable.WaitForSecondsAsync(1f / TargetMeshRefreshRateHertz);
            } while (!token.IsCancellationRequested);
        }

        private async Awaitable ProcessMeshDataCPU()
        {
            NativeArray<uint> countBuf = new(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            ComputeBuffer.CopyCount(_validTriCounterBuffer, _counterCopyBuffer, 0);

            AsyncGPUReadbackRequest vtcResult = await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref countBuf, _counterCopyBuffer);
            if (vtcResult.hasError)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Could not process mesh data due to GPU readback error for valid triangles count.");
                countBuf.Dispose();
                return;
            }

            int triangleCount = Mathf.Min((int)countBuf[0], _trianglesBudget);
            int indexCount = triangleCount * 3;

            ComputeBuffer.CopyCount(_validVertCounterBuffer, _counterCopyBuffer, 0);
            vtcResult = await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref countBuf, _counterCopyBuffer);
            if (vtcResult.hasError)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Could not process mesh data due to GPU readback error for valid vertices count.");
                countBuf.Dispose();
                return;
            }

            int vertexCount = (int)countBuf[0];
            countBuf.Dispose();

            if (triangleCount == 0)
                return;

            NativeArray<Vector3> verticesBuf = new(vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            NativeArray<uint> indicesBuf = new(indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            (Awaitable<AsyncGPUReadbackRequest> vResultTask, Awaitable<AsyncGPUReadbackRequest> iResultTask) = (
                AsyncGPUReadback.RequestIntoNativeArrayAsync(ref verticesBuf, _vertexBuffer, sizeof(float) * 3 * vertexCount, 0),
                AsyncGPUReadback.RequestIntoNativeArrayAsync(ref indicesBuf, _indexBuffer, sizeof(uint) * indexCount, 0)
            );

            (AsyncGPUReadbackRequest vResult, AsyncGPUReadbackRequest iResult) = (await vResultTask, await iResultTask);
            if (vResult.hasError || iResult.hasError)
            {
                Debug.LogError($"{nameof(DepthMesher)}: Could not process mesh data due to GPU readback error for vertex or index buffer.");
                verticesBuf.Dispose();
                indicesBuf.Dispose();
                return;
            }

            if (_meshColliderConsumer != null && !_bakeCollision)
                _meshColliderConsumer.sharedMesh = null;

            Mesh!.SetVertexBufferData(verticesBuf, 0, 0, vertexCount, flags: MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            Mesh.SetIndexBufferData(indicesBuf, 0, 0, indexCount, flags: MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            Mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);

            verticesBuf.Dispose();
            indicesBuf.Dispose();

            if (_bakeCollision && _meshId.HasValue)
            {
                _collisionBakeJob?.Complete();
                _collisionBakeJob = new MeshColliderBakeJob(_meshId.Value).Schedule();

                while (!_collisionBakeJob.Value.IsCompleted)
                    await Awaitable.NextFrameAsync();

                _collisionBakeJob.Value.Complete();
                _collisionBakeJob = null;
            }

            if (_meshColliderConsumer != null)
                _meshColliderConsumer.sharedMesh = Mesh;

            if (_bakeNavMesh)
            {
                await Awaitable.EndOfFrameAsync();
                if (_navMeshDataInstance?.valid == true)
                    _navMeshDataInstance.Value.Remove();

                NavMeshData navMeshData = new();
                await _navMeshSurface.UpdateNavMesh(navMeshData);

                NavMeshDataInstance navMeshDataInstance = NavMesh.AddNavMeshData(navMeshData);
                navMeshDataInstance.owner = this;

                _navMeshDataInstance = navMeshDataInstance;
            }

            OnMeshRefreshed?.Invoke();
        }
        
        // InitializeFrustumVolume is based on https://github.com/anaglyphs/lasertag
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

            _frustumVolume = new(positions.Count, sizeof(float) * 3);
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

            _shader.SetInts(MC_VolumeSize, Volume.width, Volume.height, Volume.volumeDepth);
            _shader.SetFloat(MC_MetersPerVoxel, _metersPerVoxel);

            _volumeClearKernel.SetTexture(MC_Volume, Volume);
            _updateVoxelsKernel.SetTexture(MC_Volume, Volume);
            _sfVertexPassKernel.SetTexture(MC_Volume, Volume);
            _sfTrianglePassKernel.SetTexture(MC_Volume, Volume);

            _volumeClearKernel.Dispatch(Volume.width, Volume.height, Volume.volumeDepth);
        }

        private void InitializeMeshData()
        {
            Mesh = new Mesh();
            _meshId = Mesh.GetInstanceID();

            int vertexCount = _trianglesBudget * 3;
            Mesh.SetVertexBufferParams(vertexCount, new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
            Mesh.SetIndexBufferParams(vertexCount, IndexFormat.UInt32);

            _vertexBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3, ComputeBufferType.Structured);
            _indexBuffer = new ComputeBuffer(_trianglesBudget, sizeof(uint) * 3, ComputeBufferType.Structured);
            
            _sfVertexPassKernel.SetBuffer(MC_VertexBuffer, _vertexBuffer);
            _sfTrianglePassKernel.SetBuffer(MC_IndexBuffer, _indexBuffer);

            _shader.SetFloat(MC_MaxMeshUpdateDistance, _maxMeshUpdateDistance);
            _shader.SetInt(MC_MaxTriangles, _trianglesBudget);

            _vertexIndexBuffer = new ComputeBuffer(_volumeSize.x * _volumeSize.y * _volumeSize.z, sizeof(uint));
            _viBufferClearKernel.SetBuffer(MC_VertexIndexBuffer, _vertexIndexBuffer);
            _sfVertexPassKernel.SetBuffer(MC_VertexIndexBuffer, _vertexIndexBuffer);
            _sfTrianglePassKernel.SetBuffer(MC_VertexIndexBuffer, _vertexIndexBuffer);
            
            _viBufferClearKernel.Dispatch(_vertexIndexBuffer.count, 1, 1);

            if (_meshFilterConsumer != null)
                _meshFilterConsumer.mesh = Mesh;
        }

        private void InitializeCounters()
        {
            _counterCopyBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);

            _validVertCounterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Counter);
            _sfVertexPassKernel.SetBuffer(MC_ValidVertexCounter, _validVertCounterBuffer);

            _validTriCounterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Counter);
            _sfTrianglePassKernel.SetBuffer(MC_ValidTriangleCounter, _validTriCounterBuffer);
        }
    }
}