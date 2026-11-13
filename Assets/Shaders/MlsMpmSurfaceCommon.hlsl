#ifndef WATER_SPLASH_SURFACE_COMMON
#define WATER_SPLASH_SURFACE_COMMON

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

float4 _FluidBufferSize;
float4x4 _FluidInverseProjection;
float _FluidRadius;
float _FluidDepthRange;

float SceneEyeDepth(float2 uv)
{
    float raw = SampleSceneDepth(uv);
    if (unity_OrthoParams.w > 0.5)
    {
        #if UNITY_REVERSED_Z
            raw = 1.0 - raw;
        #endif
        return lerp(_ProjectionParams.y, _ProjectionParams.z, raw);
    }
    return LinearEyeDepth(raw, _ZBufferParams);
}

float3 FluidViewPosition(float2 uv, float eyeDepth)
{
    float4 ray = mul(_FluidInverseProjection, ComputeClipSpacePosition(uv, UNITY_NEAR_CLIP_VALUE));
    ray.xyz /= ray.w;
    if (unity_OrthoParams.w > 0.5) return float3(ray.xy, -eyeDepth);
    return ray.xyz * (eyeDepth / max(-ray.z, 1e-6));
}

float FluidDeviceDepth(float3 viewPosition)
{
    float4 projected = mul(UNITY_MATRIX_P, float4(viewPosition, 1));
    float depth = projected.z / projected.w;
    #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES3)
        depth = depth * 0.5 + 0.5;
    #endif
    return depth;
}

#endif
