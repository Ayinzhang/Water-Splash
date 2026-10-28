Shader "Hidden/WaterSplash/Surface Particles"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "MlsMpmSurfaceCommon.hlsl"
        #include "MlsMpmWhitewater.hlsl"

        struct Particle
        {
            float4 position;
            float4 velocity;
            float4 affineX;
            float4 affineY;
            float4 affineZ;
        };
        StructuredBuffer<Particle> _Particles;
        float3 _Origin;
        float _CellSize;
        float _Radius;
        float _ThicknessScale;
        StructuredBuffer<Whitewater> _Whitewater;
        StructuredBuffer<uint> _WhitewaterIndices;
        TEXTURE2D(_FluidDepth);
        float _WhitewaterRadius;
        float _WhitewaterDepthTolerance;
        float _ContinuousFoam;

        struct ParticleVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disk : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
        };

        ParticleVaryings ParticleVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            const float2 corners[6] = {
                float2(-1,-1), float2(-1,1), float2(1,1),
                float2(-1,-1), float2(1,1), float2(1,-1)
            };
            ParticleVaryings output;
            output.centerVS = TransformWorldToView(_Origin + _CellSize * _Particles[instance].position.xyz);
            output.disk = corners[vertex];
            output.positionCS = mul(UNITY_MATRIX_P,
                float4(output.centerVS + float3(output.disk * _Radius, 0), 1));
            if (-output.centerVS.z < _ProjectionParams.y + _Radius)
                output.positionCS = float4(0, 0, 2, 1);
            return output;
        }

        struct DepthOutput
        {
            float depth : SV_Target;
            float deviceDepth : SV_Depth;
        };

        DepthOutput ParticleDepth(ParticleVaryings input)
        {
            float r2 = dot(input.disk, input.disk);
            clip(1.0 - r2);
            float3 surface = input.centerVS + float3(input.disk * _Radius, sqrt(1.0 - r2) * _Radius);
            float eyeDepth = -surface.z;
            float2 uv = input.positionCS.xy * _FluidBufferSize.zw;
            clip(SceneEyeDepth(uv) - eyeDepth);
            DepthOutput output;
            output.depth = eyeDepth;
            output.deviceDepth = FluidDeviceDepth(surface);
            return output;
        }

        float ParticleThickness(ParticleVaryings input) : SV_Target
        {
            float r2 = dot(input.disk, input.disk);
            clip(1.0 - r2);
            float halfChord = sqrt(1.0 - r2) * _Radius;
            float front = -input.centerVS.z - halfChord;
            float back = -input.centerVS.z + halfChord;
            float2 uv = input.positionCS.xy * _FluidBufferSize.zw;
            return max(0, min(back, SceneEyeDepth(uv)) - front) * _ThicknessScale;
        }

        struct WhitewaterVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disk : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
            float2 radiusFade : TEXCOORD2;
        };

        WhitewaterVaryings WhitewaterVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            const float2 corners[6] = {
                float2(-1,-1), float2(-1,1), float2(1,1),
                float2(-1,-1), float2(1,1), float2(1,-1)
            };
            uint id = _WhitewaterIndices[instance];
            Whitewater p = _Whitewater[id];
            WhitewaterVaryings output = (WhitewaterVaryings)0;
            output.positionCS = float4(0, 0, 2, 1);
            if (p.position.w <= 0) return output;
            float random = WhitewaterRandom(id);
            bool foam = p.normalType.w > 0.5;
            float radius = _WhitewaterRadius * (foam ?
                lerp(lerp(1.4, 2.8, random), lerp(2.8, 4.3, random), _ContinuousFoam) : lerp(0.25, 0.6, random));
            output.centerVS = TransformWorldToView(_Origin + _CellSize * p.position.xyz);
            output.disk = corners[vertex];
            output.radiusFade = float2(radius, saturate((p.velocity.w - p.position.w) / 0.1) *
                saturate(p.position.w / 0.5));
            if (-output.centerVS.z < _ProjectionParams.y + radius) return output;
            float3 billboard = float3(output.disk * radius, 0);
            if (foam && _ContinuousFoam > 0.5)
            {
                // Surface-oriented elliptical footprints overlap into a density field.
                // A grazing-angle floor prevents subpixel flicker on steep waves.
                float3 n = normalize(p.normalType.xyz + float3(0, 1e-5, 0));
                float3 flow = p.velocity.xyz - n * dot(n, p.velocity.xyz);
                float3 tangent = dot(flow, flow) > 0.01 ? normalize(flow) :
                    normalize(cross(n, abs(n.y) < .9 ? float3(0,1,0) : float3(1,0,0)));
                float3 bitangent = cross(n, tangent);
                float3 surfaceOffset = mul((float3x3)UNITY_MATRIX_V,
                    radius * (output.disk.x * tangent * 1.65 + output.disk.y * bitangent * .85));
                billboard = lerp(billboard * .7, surfaceOffset, .85);
            }
            output.positionCS = mul(UNITY_MATRIX_P, float4(output.centerVS + billboard, 1));
            return output;
        }

        float FoamMask(WhitewaterVaryings input) : SV_Target
        {
            float r2 = dot(input.disk, input.disk);
            clip(1.0 - r2);
            float2 uv = input.positionCS.xy * _FluidBufferSize.zw;
            float depth = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv).r;
            clip(depth - 1e-5);
            float depthFade = saturate(1.0 - abs(-input.centerVS.z - depth) / _WhitewaterDepthTolerance);
            clip(depthFade - 1e-4);
            clip(SceneEyeDepth(uv) + input.centerVS.z + input.radiusFade.x);
            float falloff = saturate(1.0 - r2);
            float kernel = lerp(falloff * falloff * .55, exp(-3.5 * r2) * .42, _ContinuousFoam);
            return input.radiusFade.y * depthFade * kernel;
        }

        struct SprayOutput
        {
            half4 color : SV_Target;
            float depth : SV_Depth;
        };

        SprayOutput Spray(WhitewaterVaryings input)
        {
            float r2 = dot(input.disk, input.disk);
            clip(1.0 - r2);
            float hemisphere = sqrt(1.0 - r2);
            float3 surface = input.centerVS + input.radiusFade.x * float3(input.disk, hemisphere);
            SprayOutput output;
            output.color = half4(float3(0.92, 0.96, 1.0) * (0.7 + 0.3 * hemisphere),
                input.radiusFade.y * smoothstep(1.0, 0.15, r2) * 0.85);
            output.depth = FluidDeviceDepth(surface);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "Fluid Depth"
            ZTest LEqual
            ZWrite On
            Blend Off
            HLSLPROGRAM
            #pragma vertex ParticleVertex
            #pragma fragment ParticleDepth
            ENDHLSL
        }
        Pass
        {
            Name "Fluid Thickness"
            ZTest Always
            ZWrite Off
            Blend One One
            HLSLPROGRAM
            #pragma vertex ParticleVertex
            #pragma fragment ParticleThickness
            ENDHLSL
        }
        Pass
        {
            Name "Advected Foam"
            ZTest Always
            ZWrite Off
            Blend One One
            HLSLPROGRAM
            #pragma vertex WhitewaterVertex
            #pragma fragment FoamMask
            ENDHLSL
        }
        Pass
        {
            Name "Ballistic Spray"
            ZTest LEqual
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex WhitewaterVertex
            #pragma fragment Spray
            ENDHLSL
        }
    }
}
