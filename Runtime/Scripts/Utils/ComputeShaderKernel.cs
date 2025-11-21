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

using UnityEngine;

#nullable enable
namespace Uralstech.UXR.QuestMeshing
{
    /// <summary>
    /// A light wrapper for a compute shader kernel.
    /// </summary>
    public readonly struct ComputeShaderKernel
    {
        /// <summary>
        /// The index of the kernel.
        /// </summary>
        public readonly int KernelIndex;

        /// <summary>
        /// The thread group sizes of the kernel as defined in its numthreads attribute.
        /// </summary>
        public readonly (uint x, uint y, uint z) ThreadGroupSizes;

        private readonly ComputeShader _shader;

        public ComputeShaderKernel(ComputeShader shader, string name)
        {
            _shader = shader;
            KernelIndex = _shader.FindKernel(name)!;
            _shader.GetKernelThreadGroupSizes(KernelIndex, out ThreadGroupSizes.x, out ThreadGroupSizes.y, out ThreadGroupSizes.z);
        }

        /// <summary>
        /// Dispatches the shader kernel.
        /// </summary>
        /// <param name="threadsX">The total threads for computation in the X dimension.</param>
        /// <param name="threadsY">The total threads for computation in the Y dimension.</param>
        /// <param name="threadsZ">The total threads for computation in the Z dimension.</param>
        public void Dispatch(int threadsX, int threadsY = 1, int threadsZ = 1)
        {
            _shader.Dispatch(KernelIndex,
                Mathf.CeilToInt((float)threadsX / ThreadGroupSizes.x),
                Mathf.CeilToInt((float)threadsY / ThreadGroupSizes.y),
                Mathf.CeilToInt((float)threadsZ / ThreadGroupSizes.z));
        }

        /// <summary>
        /// Dispatches the shader kernel.
        /// </summary>
        /// <param name="threads">The total threads for computation in 3 dimensions.</param>
        public void Dispatch(Vector3Int threads) => Dispatch(threads.x, threads.y, threads.z);

        /// <summary>
        /// Sets a texture for the kernel.
        /// </summary>
        /// <param name="id">The parameter ID of the texture as defined in the shader.</param>
        /// <param name="texture">The texture to set.</param>
        public void SetTexture(int id, Texture texture) => _shader.SetTexture(KernelIndex, id, texture);

        /// <summary>
        /// Sets a buffer for the kernel.
        /// </summary>
        /// <param name="id">The parameter ID of the buffer as defined in the shader.</param>
        /// <param name="buffer">The buffer to set.</param>
        public void SetBuffer(int id, ComputeBuffer buffer) => _shader.SetBuffer(KernelIndex, id, buffer);

        /// <summary>
        /// Sets a buffer for the kernel.
        /// </summary>
        /// <param name="id">The parameter ID of the buffer as defined in the shader.</param>
        /// <param name="buffer">The buffer to set.</param>
        public void SetBuffer(int id, GraphicsBuffer buffer) => _shader.SetBuffer(KernelIndex, id, buffer);
    }
}
