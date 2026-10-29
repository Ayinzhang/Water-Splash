Shader "WaterSplash/Bottle Sunset"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            Varyings Vert(float3 p:POSITION,float2 uv:TEXCOORD0)
            {
                Varyings o; o.positionCS=TransformObjectToHClip(p); o.uv=uv; return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                float2 uv=i.uv;
                half3 color=lerp(half3(.94,.77,.60),half3(.59,.62,.75),smoothstep(.2,1,uv.y));
                float2 sun=(uv-float2(.71,.64))*float2(1.4,1);
                float distance=length(sun);
                color=lerp(color,half3(1,.9,.70),exp(-distance*distance*22)*.65);
                color=lerp(color,half3(1,.96,.81),1-smoothstep(.046,.049,distance));
                float2 shadow=(uv-float2(.5,.30))/float2(.26,.025);
                color*=1-.18*exp(-dot(shadow,shadow)*2);
                float grain=frac(sin(dot(floor(uv*1800),float2(127.1,311.7)))*43758.5453);
                return half4(color+(grain-.5)*.009,1);
            }
            ENDHLSL
        }
    }
}
