Shader "WaterSplash/MLS-MPM Particles"
{
    Properties
    {
        _SlowColor("Slow Color", Color) = (0.025, 0.28, 0.65, 1)
        _FastColor("Fast Color", Color) = (0.18, 0.9, 1, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
            float4 _SlowColor;
            float4 _FastColor;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 disk : TEXCOORD0;
                float3 centerVS : TEXCOORD1;
                float3 color : TEXCOORD2;
            };

            Varyings Vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                const float2 corners[6] = {
                    float2(-1,-1), float2(-1,1), float2(1,1),
                    float2(-1,-1), float2(1,1), float2(1,-1)
                };
                Particle p = _Particles[instance];
                float3 center = TransformWorldToView(_Origin + _CellSize * p.position.xyz);
                Varyings output;
                output.disk = corners[vertex];
                output.centerVS = center;
                output.positionCS = mul(UNITY_MATRIX_P, float4(center + float3(output.disk * _Radius, 0), 1));
                output.color = lerp(_SlowColor.rgb, _FastColor.rgb,
                                    saturate(length(p.velocity.xyz) * _CellSize / 5.0));
                return output;
            }

            struct FragmentOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            FragmentOutput Frag(Varyings input)
            {
                float r2 = dot(input.disk, input.disk);
                clip(1.0 - r2);
                float3 normalVS = float3(input.disk, sqrt(saturate(1.0 - r2)));
                float diffuse = saturate(dot(normalVS, normalize(float3(-0.4, 0.7, 0.6))));
                float highlight = pow(saturate(dot(normalVS, normalize(float3(-0.3, 0.5, 1)))), 48.0);
                float3 surfaceVS = input.centerVS + normalVS * _Radius;
                float4 clipPosition = mul(UNITY_MATRIX_P, float4(surfaceVS, 1));
                FragmentOutput output;
                output.depth = clipPosition.z / clipPosition.w;
                #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES3)
                    output.depth = output.depth * 0.5 + 0.5;
                #endif
                output.color = half4(input.color * (0.3 + diffuse * 0.7) + highlight * 0.65, 1);
                return output;
            }
            ENDHLSL
        }
    }
}
