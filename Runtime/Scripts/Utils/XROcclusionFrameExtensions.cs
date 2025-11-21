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

using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

#nullable enable
namespace Uralstech.UXR.QuestMeshing
{
    /// <summary>
    /// Extensions for Unity's XROcclusionFrame.
    /// </summary>
    public static class XROcclusionFrameExtensions
    {
        private static readonly Vector3 s_scalingVector3 = new(1f, 1f, -1f);

        /// <summary>
        /// Uses data from <see cref="GetBaseFrameData(XROcclusionFrame, Matrix4x4[], Matrix4x4[], out Vector4)"/> to construct
        /// reprojection matrices which can be used to convert world space points to points in the depth texture.
        /// </summary>
        /// <param name="worldToLocalMatrix">A matrix used to convert world space points into the local space of the XR session.</param>
        /// <param name="reprojectionMatrices">The array to populate the reprojection matrices in, must be of length 2.</param>
        /// <param name="projectionMatrices">The array to populate the projection matrices in, must be of length 2.</param>
        /// <param name="viewMatrices">The array to populate the view matrices in, must be of length 2.</param>
        /// <param name="zBufferParams">Resulting depth buffer parameters for "_EnvironmentDepthZBufferParams".</param>
        /// <returns>A boolean representing the success of the operation.</returns>
        public static bool TryGetReprojectionMatrices(this XROcclusionFrame frame,
            Matrix4x4 worldToLocalMatrix,
            Matrix4x4[] reprojectionMatrices,
            Matrix4x4[] projectionMatrices,
            Matrix4x4[] viewMatrices,
            out Vector4 zBufferParams)
        {
            if (reprojectionMatrices.Length != projectionMatrices.Length || !frame.GetBaseFrameData(projectionMatrices, viewMatrices, out zBufferParams))
            {
                zBufferParams = Vector4.zero;
                return false;
            }

            for (int i = 0; i < reprojectionMatrices.Length; i++)
                reprojectionMatrices[i] = projectionMatrices[i] * viewMatrices[i] * worldToLocalMatrix;
            return true;
        }

        /// <summary>
        /// Constructs:<br/>
        /// - A perspective projection matrix for each eye<br/>
        /// - The view matrix for each eye from its Pose<br/>
        /// - Parameters used for handling the depth buffer for shaders using "_EnvironmentDepthZBufferParams".<br/>
        /// for a given XROcclusionFrame.
        /// </summary>
        /// <param name="projectionMatrices">The array to populate the projection matrices in, must be of length 2.</param>
        /// <param name="viewMatrices">The array to populate the view matrices in, must be of length 2.</param>
        /// <param name="zBufferParams">Resulting depth buffer parameters for "_EnvironmentDepthZBufferParams".</param>
        /// <returns>A boolean representing the success of the operation.</returns>
        public static bool GetBaseFrameData(this XROcclusionFrame frame,
            Matrix4x4[] projectionMatrices,
            Matrix4x4[] viewMatrices,
            out Vector4 zBufferParams)
        {
            if (projectionMatrices.Length != 2
                || viewMatrices.Length != projectionMatrices.Length
                || !frame.TryGetFovs(out NativeArray<XRFov> fovs)
                || !frame.TryGetPoses(out NativeArray<Pose> poses)
                || !frame.TryGetNearFarPlanes(out XRNearFarPlanes nearFarPlanes))
            {
                zBufferParams = Vector4.zero;
                return false;
            }

            zBufferParams = nearFarPlanes.GetZBufferParams();
            for (int i = 0; i < projectionMatrices.Length; i++)
            {
                float fovLeftAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleLeft));
                float fovRightAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleRight));

                float fovUpAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleUp));
                float fovDownAngleTangent = Mathf.Tan(Mathf.Abs(fovs[i].angleDown));

                float nearZ = nearFarPlanes.nearZ;
                float farZ = nearFarPlanes.farZ;

                float m = 2f / (fovRightAngleTangent + fovLeftAngleTangent);
                float m2 = 2f / (fovUpAngleTangent + fovDownAngleTangent);
                float m3 = (fovRightAngleTangent - fovLeftAngleTangent) / (fovRightAngleTangent + fovLeftAngleTangent);
                float m4 = (fovUpAngleTangent - fovDownAngleTangent) / (fovUpAngleTangent + fovDownAngleTangent);

                (float m5, float m6) = float.IsInfinity(farZ)
                    ? (-1f, -2f * nearZ)
                    : ((0f - (farZ + nearZ)) / (farZ - nearZ), (0f - (2f * farZ * nearZ)) / (farZ - nearZ));

                projectionMatrices[i] = new Matrix4x4
                {
                    m00 = m,
                    m01 = 0f,
                    m02 = m3,
                    m03 = 0f,
                    m10 = 0f,
                    m11 = m2,
                    m12 = m4,
                    m13 = 0f,
                    m20 = 0f,
                    m21 = 0f,
                    m22 = m5,
                    m23 = m6,
                    m30 = 0f,
                    m31 = 0f,
                    m32 = -1f,
                    m33 = 0f
                };

                viewMatrices[i] = Matrix4x4.TRS(poses[i].position, poses[i].rotation, s_scalingVector3).inverse;
            }

            return true;
        }

        /// <summary>
        /// Calculates parameters used for handling the depth buffer for shaders using "_EnvironmentDepthZBufferParams".
        /// </summary>
        /// <returns>The depth buffer parameters.</returns>
        public static Vector4 GetZBufferParams(this XRNearFarPlanes planes)
        {
            float near = planes.nearZ, far = planes.farZ;
            (float invDepthFactor, float depthOffset) = far < near || float.IsInfinity(far)
                ? (-2.0f * near, -1.0f)
                : (-2.0f * far * near / (far - near), -(far + near) / (far - near));

            return new Vector4(invDepthFactor, depthOffset, 0, 0);
        }
    }
}