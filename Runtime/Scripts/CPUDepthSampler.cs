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
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Uralstech.Utils.Singleton;

#nullable enable
namespace Uralstech.UXR.QuestMeshing
{
    /// <summary>
    /// Specifies the eye for depth sampling operations.
    /// </summary>
    public enum DepthEye
    {
        /// <summary>
        /// The left eye.
        /// </summary>
        Left = 0,

        /// <summary>
        /// The right eye.
        /// </summary>
        Right = 1
    }

    /// <summary>
    /// A utility for asynchronously sampling world-space positions from normalized device coordinates (NDC) in the depth texture
    /// using a compute shader. This class batches requests for efficiency and ensures main-thread safety for XR frame access.
    /// </summary>
    /// <remarks>
    /// This sampler relies on <see cref="DepthPreprocessor"/> for depth texture and reprojection data. It is designed for CPU-side
    /// depth queries, such as raycasting against environment depth or occlusion checks in custom XR interactions.
    /// 
    /// <para>
    /// Usage Notes:
    /// <list type="bullet">
    /// <item><description>Requests are batched and dispatched at the end of the frame via <see cref="DispatchForSamplingAsync"/> to minimize GPU overhead.</description></item>
    /// <item><description>All public sampling methods require main-thread execution for XR subsystem access; they yield to the main thread if needed.</description></item>
    /// <item><description>Invalid NDC positions (outside [0,1]) will return invalid results—validate with <see cref="IsValidNDC"/> before sampling.</description></item>
    /// <item><description>Errors (e.g., GPU readback failures) return <see langword="null"/>; check return values before use.</description></item>
    /// </list>
    /// </para>
    /// 
    /// <example>
    /// Single Sample:
    /// <code><![CDATA[
    /// if (CPUDepthSampler.Instance.ConvertToNDCPosition(worldPos, DepthEye.Left, out Vector2? ndc)
    ///     && CPUDepthSampler.IsValidNDC(ndc.Value))
    /// {
    ///     Vector3? depthPos = await CPUDepthSampler.Instance.SampleDepthAsync(ndc.Value);
    ///     if (depthPos.HasValue) { /* Process data */ }
    /// }
    /// ]]></code>
    /// </example>
    /// 
    /// <example>
    /// Batch Sample:
    /// <code><![CDATA[
    /// ArraySegment<Vector3>? results = await sampler.BatchSampleDepthAsync(new Vector2[] { ndc1, ndc2 });
    /// if (results.HasValue)
    /// {
    ///     foreach (Vector3 pos in results.Value) { /* Process data */ }
    /// }
    /// ]]></code>
    /// </example>
    /// </remarks>
    [AddComponentMenu("Uralstech/UXR/QuestMeshing/CPU Depth Sampler")]
    public class CPUDepthSampler : DontCreateNewSingleton<CPUDepthSampler>
    {
        private static readonly Vector3 s_vector3Half = Vector3.one / 2f;

#pragma warning disable IDE1006 // Naming Styles
        private static readonly int CS_PositionSampleRequests = Shader.PropertyToID("PositionSampleRequests");
        private static readonly int CS_TotalPositionSampleRequests = Shader.PropertyToID("TotalPositionSampleRequests");
#pragma warning restore IDE1006 // Naming Styles

        [Tooltip("The compute shader containing the 'SampleDepthPosition' kernel for unprojecting NDC to world positions.")]
        [SerializeField] private ComputeShader _shader;

        private DepthPreprocessor _preprocessor;
        private ComputeShaderKernel _sampleKernel;

        private readonly List<Vector3> _sampleRequests = new();
        private Task<Vector3[]?>? _sampleRequest;

        protected override void Awake()
        {
            base.Awake();
            if (_shader == null)
            {
                Debug.LogError($"{nameof(CPUDepthSampler)}: Compute shader is not assigned. Sampling will fail.");
                return;
            }

            _sampleKernel = new ComputeShaderKernel(_shader, "SampleDepthPosition");
        }

        protected void Start()
        {
            if (!DepthPreprocessor.HasInstance)
            {
                Debug.LogError($"{nameof(CPUDepthSampler)}: No instance of {nameof(DepthPreprocessor)} found in scene.");
                enabled = false;
                return;
            }

            _preprocessor = DepthPreprocessor.Instance;
        }

        /// <summary>
        /// Converts a world-space position to normalized device coordinates (NDC) in the depth texture for the specified eye.
        /// </summary>
        /// <remarks>
        /// This uses the current frame's reprojection matrix from <see cref="DepthPreprocessor.DepthReprojectionMatrices"/>.
        /// </remarks>
        /// <param name="worldPosition">The world-space position to project.</param>
        /// <param name="eye">The eye for which to compute the NDC (affects reprojection matrix).</param>
        /// <param name="ndcPosition">The resulting NDC position (x,y in [0,1] range), or <see langword="null"/> if preprocessor data is unavailable.</param>
        /// <returns><see langword="true"/> if conversion succeeded (preprocessor data available); otherwise <see langword="false"/>.</returns>
        public bool ConvertToNDCPosition(Vector3 worldPosition, DepthEye eye, [NotNullWhen(true)] out Vector2? ndcPosition)
        {
            if (_preprocessor == null || !_preprocessor.IsDataAvailable)
            {
                ndcPosition = null;
                return false;
            }

            Vector4 hcsPos = _preprocessor.DepthReprojectionMatrices[(int)eye] * new Vector4(worldPosition.x, worldPosition.y, worldPosition.z, 1);
            ndcPosition = ((Vector3)hcsPos / hcsPos.w / 2f) + s_vector3Half;
            return true;
        }

        /// <summary>
        /// Determines if an NDC position is valid for depth texture sampling.
        /// </summary>
        /// <param name="position">The NDC position to validate.</param>
        /// <returns><see langword="true"/> if the position is within the [0,1] range for both x and y; otherwise <see langword="false"/>.</returns>
        public static bool IsValidNDC(Vector2 position) => position.x is >= 0f and <= 1f && position.y is >= 0f and <= 1f;

        /// <summary>
        /// Asynchronously samples the world-space position at the given NDC coordinates in the depth texture.
        /// </summary>
        /// <remarks>
        /// This queues the request and batches it with others for end-of-frame dispatch.
        /// </remarks>
        /// <param name="ndcPosition">The NDC position (x,y in [0,1]) to sample.</param>
        /// <returns>The world-space <see cref="Vector3"/> at the sample point, or <see langword="null"/> if data is unavailable or sampling fails.</returns>
        public async Awaitable<Vector3?> SampleDepthAsync(Vector2 ndcPosition)
        {
            if (_preprocessor == null || !_preprocessor.IsDataAvailable)
                return null;

            await Awaitable.MainThreadAsync();

            int index = _sampleRequests.Count;
            _sampleRequests.Add(ndcPosition);

            _sampleRequest ??= DispatchForSamplingAsync();
            Vector3[]? results = await _sampleRequest;
            return results?[index];
        }

        /// <summary>
        /// Asynchronously samples world-space positions for a batch of NDC coordinates in the depth texture.
        /// </summary>
        /// <remarks>
        /// This queues the batch and dispatches at end-of-frame for efficiency. Ideal for multiple queries (e.g., raycast hits).
        /// The returned segment shares the underlying array.
        /// </remarks>
        /// <param name="ndcPositions">The enumerable of NDC positions (x,y in [0,1]) to sample.</param>
        /// <returns>An ArraySegment of results in input order, or <see langword="null"/> if data is unavailable or sampling fails.</returns>
        public async Awaitable<ArraySegment<Vector3>?> BatchSampleDepthAsync(IEnumerable<Vector2> ndcPositions)
        {
            if (_preprocessor == null || !_preprocessor.IsDataAvailable)
                return null;

            await Awaitable.MainThreadAsync();

            int index = _sampleRequests.Count;
            foreach (Vector3 ndcPosition in ndcPositions)
                _sampleRequests.Add(ndcPosition);
            int count = _sampleRequests.Count - index;

            _sampleRequest ??= DispatchForSamplingAsync();
            Vector3[]? results = await _sampleRequest;

            return results != null ? new ArraySegment<Vector3>(results, index, count) : null;
        }

        private async Task<Vector3[]?> DispatchForSamplingAsync()
        {
            await Awaitable.EndOfFrameAsync();

            int count = _sampleRequests.Count;
            using ComputeBuffer buffer = new(count, sizeof(float) * 3);

            buffer.SetData(_sampleRequests);
            _sampleRequests.Clear();

            _sampleRequest = null;

            _shader.SetInt(CS_TotalPositionSampleRequests, count);
            _sampleKernel.SetBuffer(CS_PositionSampleRequests, buffer);
            _sampleKernel.Dispatch(count, 1, 1);

            NativeArray<Vector3> resultsArray = new(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            AsyncGPUReadbackRequest request = await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref resultsArray, buffer);
            if (request.hasError)
            {
                resultsArray.Dispose();
                return null;
            }

            Vector3[] results = new Vector3[count];
            resultsArray.CopyTo(results);
            resultsArray.Dispose();
            return results;
        }
    }
}
