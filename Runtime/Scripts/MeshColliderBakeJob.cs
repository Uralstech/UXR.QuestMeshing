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

using Unity.Burst;
using Unity.Jobs;
using UnityEngine;

#nullable enable
namespace Uralstech.UXR.QuestMeshing
{
#if UNITY_6000_3_OR_NEWER
    [BurstCompile]
    public readonly struct MeshColliderBakeJobV2 : IJob
    {
        public readonly EntityId MeshEntityId;
        public MeshColliderBakeJobV2(EntityId meshEntityId)
        {
            MeshEntityId = meshEntityId;
        }

        public void Execute() => Physics.BakeMesh(MeshEntityId, false);
    }
    
    [System.Obsolete("As Physics.BakeMesh(int meshID, bool convex) has been deprecated in Unity 6.3+, please use MeshColliderBakeJobV2 instead.")]
#endif

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
}
