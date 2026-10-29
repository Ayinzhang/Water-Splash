Shader "WaterSplash/Bottle Still Life"
{
    Properties
    {
        _BaseColor("Base color", Color) = (.35,.22,.13,1)
        _GrainColor("Grain color", Color) = (.20,.10,.045,1)
        _Grain("Grain strength", Range(0,1)) = .3
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor, _GrainColor;
            half _Grain;
            CBUFFER_END
            struct Varyings { float4 positionCS:SV_POSITION; float3 local:TEXCOORD0; half3 normal:TEXCOORD1; };
            Varyings Vert(float3 p:POSITION,float3 n:NORMAL)
            {
                Varyings o;o.positionCS=TransformObjectToHClip(p);o.local=p;o.normal=TransformObjectToWorldNormal(n);return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                float3 p=i.local;
                float warp=sin(p.x*7+p.z*5)*.8+sin(p.x*19-p.z*3)*.2;
                half grain=pow(.5+.5*sin(p.z*170+warp*5),9);
                half3 albedo=lerp(_BaseColor.rgb,_GrainColor.rgb,grain*_Grain);
                Light light=GetMainLight();
                half3 n=normalize(i.normal);
                half3 illumination=SampleSH(n)+light.color*(.25+.75*saturate(dot(n,light.direction)));
                return half4(albedo*illumination,1);
            }
            ENDHLSL
        }
    }
}
