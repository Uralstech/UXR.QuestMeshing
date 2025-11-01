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

uniform Texture2D<float4> _EnvironmentDepthWorldPositionsTexture;
uniform Texture2D<float4> _EnvironmentDepthWorldNormalsTexture;
uniform float4x4 _EnvironmentDepthReprojectionMatrices[2];

SamplerState PointClampSampler;

float3 WorldToNDCPos(float3 worldPos)
{
    const float4 hcsPos = mul(_EnvironmentDepthReprojectionMatrices[0], float4(worldPos, 1));
    return (hcsPos.xyz / hcsPos.w) * 0.5 + 0.5;
}

float3 SampleDepthPosition(float2 uv)
{
    return _EnvironmentDepthWorldPositionsTexture.SampleLevel(PointClampSampler, uv, 0).xyz;
}

float3 SampleDepthNormal(float2 uv)
{
    return _EnvironmentDepthWorldNormalsTexture.SampleLevel(PointClampSampler, uv, 0).xyz;
}