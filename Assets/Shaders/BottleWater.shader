Shader "Bottle/Water"
{
    Properties
    {
        _BaseColor("Color", Color) = (.05, .5, .8, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _Particles;

            float3 _Origin;
            float _CellSize;
            float _Radius;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            static const float2 Corners[6] = {float2(-1, -1), float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(1, -1)};

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 diskCoord : TEXCOORD0;
            };

            Varyings Vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                float3 positionWS = _Origin + _Particles[instance].xyz * _CellSize;
                float3 positionVS = TransformWorldToView(positionWS);

                Varyings output;
                output.diskCoord = Corners[vertex];

                positionVS.xy += output.diskCoord * _Radius;
                output.positionCS = mul(UNITY_MATRIX_P, float4(positionVS, 1));

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float r2 = dot(input.diskCoord, input.diskCoord);
                clip(1 - r2);

                half shade = .4 + .6 * sqrt(saturate(1 - r2));
                return half4(_BaseColor.rgb * shade, 1);
            }
            ENDHLSL
        }
    }
}