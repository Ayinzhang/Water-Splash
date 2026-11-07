Shader "WaterSplash/Bottle Glass"
{
    Properties { _Tint("Glass tint", Color) = (.67,.89,.86,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+20" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _Tint;
            CBUFFER_END
            TEXTURECUBE(_VoyageReflection); SAMPLER(sampler_VoyageReflection);
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normalWS:TEXCOORD1; };
            Varyings Vert(float3 position:POSITION, float3 normal:NORMAL)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(position);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(normal);
                return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                half3 n = normalize(i.normalWS);
                half3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(1 - abs(dot(n,v)), 3);
                half3 reflection = reflect(-v,n);
                half window = pow(saturate(dot(reflection,normalize(half3(-.6,.5,-.4)))), 64);
                half glint = pow(saturate(dot(reflection,normalize(half3(.4,.8,-.25)))), 180);
                // Baked surroundings plus a true sun highlight; the painted window stays as a soft accent.
                half fresnel = .04 + .96 * pow(1 - abs(dot(n,v)), 5);
                half3 environment = SAMPLE_TEXTURECUBE_LOD(_VoyageReflection, sampler_VoyageReflection, reflection, 0).rgb;
                half sun = pow(saturate(dot(reflection, _MainLightPosition.xyz)), 600) * 4;
                half3 color = lerp(_Tint.rgb, environment, saturate(fresnel * 1.5 + .25)) + window * .3
                    + sun * _MainLightColor.rgb + glint * .15;
                half alpha = saturate(.02 + fresnel * .62 + rim * .2 + dot(environment, 1.0 / 3) * .1 + window * .18 + sun * .8);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
