Shader "WaterSplash/Bottle Seaweed"
{
    Properties { _BaseColor("Leaf color",Color)=(.16,.33,.23,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            CBUFFER_END
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            Varyings Vert(float3 p:POSITION,float2 uv:TEXCOORD0)
            {
                Varyings o;
                float3 world=TransformObjectToWorld(p);
                world.x+=sin(_Time.y*1.1+world.x*4+uv.y*3)*.025*uv.y*uv.y;
                world.z+=sin(_Time.y*.8+world.z*3+uv.y*4)*.016*uv.y*uv.y;
                o.positionCS=TransformWorldToHClip(world);o.uv=uv;return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                half vein=1-smoothstep(.02,.075,abs(i.uv.x-.5));
                half3 color=_BaseColor.rgb*lerp(.65,1.35,i.uv.y)+vein*.07;
                return half4(color*(SampleSH(half3(0,1,0))+GetMainLight().color*.45),1);
            }
            ENDHLSL
        }
    }
}
