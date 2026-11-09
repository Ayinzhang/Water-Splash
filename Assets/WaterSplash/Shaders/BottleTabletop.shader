Shader "WaterSplash/Bottle Tabletop"
{
    Properties
    {
        _BaseColor("Base color", Color) = (.38,.25,.15,1)
        _GrainColor("Grain color", Color) = (.15,.085,.04,1)
        _Grain("Grain strength", Range(0,1)) = .18
        _PlankWidth("Plank width", Float) = .62
        _Gloss("Oil gloss", Range(0,1)) = .55
        [NoScaleOffset] _AOTex("Baked contact occlusion", 2D) = "white" {}
        _AOExtent("AO world half-extent", Float) = 3.5
        _WaterLevel("Water level relative to bottle axis", Float) = -.05
        _WaterTint("Water shadow tint", Color) = (.42,.78,.74,1)
        _Caustics("Caustic strength", Range(0,3)) = 1.3
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor, _GrainColor, _WaterTint;
            half _Grain, _Gloss, _Caustics;
            float _PlankWidth, _AOExtent, _WaterLevel;
            CBUFFER_END
            TEXTURE2D(_AOTex); SAMPLER(sampler_AOTex);
            TEXTURE2D(_VoyageBottleProfile); SAMPLER(sampler_VoyageBottleProfile);
            TEXTURECUBE(_VoyageReflection); SAMPLER(sampler_VoyageReflection);
            float4 _VoyageBottle;

            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normal:TEXCOORD1; };
            Varyings Vert(float3 p:POSITION,float3 n:NORMAL)
            {
                Varyings o;
                o.positionWS=TransformObjectToWorld(p);
                o.positionCS=TransformWorldToHClip(o.positionWS);
                o.normal=TransformObjectToWorldNormal(n);
                return o;
            }

            float Hash(float x) { return frac(sin(x*127.1)*43758.5453); }
            float Noise(float2 p)
            {
                float2 c=floor(p), f=frac(p); f=f*f*(3-2*f);
                float a=Hash(dot(c,float2(1,57))), b=Hash(dot(c+float2(1,0),float2(1,57)));
                float d=Hash(dot(c+float2(0,1),float2(1,57))), e=Hash(dot(c+1,float2(1,57)));
                return lerp(lerp(a,b,f.x),lerp(d,e,f.x),f.y);
            }

            // Grain runs along world x; returns the ring intensity for a given plank.
            float GrainAt(float2 p, float plank)
            {
                float2 q=p/6.7+float2(plank*3.7, plank*.31);
                float warp=sin(q.x*7+q.y*5)*.8+sin(q.x*19-q.y*3)*.2+Noise(q*float2(1.5,9))*.6;
                return pow(.5+.5*sin(q.y*170+warp*5),9);
            }

            // Signed shadow of the horizontal glass bottle, computed analytically
            // from its lathe profile. Returns rgb transmission.
            half3 BottleShadow(float3 positionWS, float3 toLight)
            {
                float axis=_VoyageBottle.w;
                if (abs(toLight.z)<.05) return 1;
                float3 center=_VoyageBottle.xyz;
                float t=(center.z-positionWS.z)/toLight.z;
                if (t<=0) return 1;
                float3 q=positionWS+toLight*t;
                float profileX=(q.x-center.x)*axis;
                if (profileX<-2.05 || profileX>2.05) return 1;
                float radius=SAMPLE_TEXTURE2D_LOD(_VoyageBottleProfile,sampler_VoyageBottleProfile,float2((profileX+2)*.25,.5),0).r;
                float lyz=length(toLight.yz);
                float scale=abs(toLight.z)/lyz;
                float offset=(q.y-center.y)*scale;
                float soft=.012+.03*t;
                float inside=1-smoothstep(radius-soft,radius+soft,abs(offset));
                if (inside<=0) return 1;
                // Chord of the light ray through the circular cross-section.
                float2 d=toLight.yz/lyz;
                float2 n=float2(-d.y,d.x);
                float half_=sqrt(max(radius*radius-offset*offset,0));
                float y0=offset*n.x-half_*abs(d.x), y1=offset*n.x+half_*abs(d.x);
                float chord=2*half_;
                float wet=saturate((_WaterLevel-y0)/max(y1-y0,1e-3))*chord;
                half3 transmission=lerp(1,_WaterTint.rgb,saturate(wet*1.6))*(.86-.18*saturate(wet));
                // Refraction through the moving surface focuses light into caustic filaments.
                float crossing=saturate((_WaterLevel-y0)*8)*saturate((y1-_WaterLevel)*8);
                float time=_Time.y;
                float2 cp=float2(profileX*5.5,offset*9);
                float c1=Noise(cp+float2(time*.7,time*.35)), c2=Noise(cp*1.7-float2(time*.45,time*.6));
                float filament=pow(saturate(1-abs(c1-c2)*3.2),5);
                float lens=exp(-pow((offset/max(radius,1e-3)+.15)*3.2,2));
                transmission+=_Caustics*(filament*crossing*.9+lens*.35*saturate(wet*2))*_WaterTint.rgb;
                // Glass walls are thickest at the silhouette, darkening the shadow edge.
                transmission*=1-.35*smoothstep(.75,1,abs(offset)/max(radius,1e-3));
                return lerp(1,transmission,inside);
            }

            half4 Frag(Varyings i):SV_Target
            {
                float3 p=i.positionWS;
                half3 n=normalize(i.normal);
                bool top=n.y>.6;
                float plank=floor(p.z/_PlankWidth+.5);
                float seamDistance=abs(p.z-plank*_PlankWidth+_PlankWidth*.5);
                seamDistance=min(seamDistance,abs(_PlankWidth-seamDistance));
                float seam=top?1-smoothstep(.002,.007,seamDistance):0;
                float grain=GrainAt(p.xz,plank);
                float grainZ=GrainAt(p.xz+float2(0,.002),plank);
                half tint=Hash(plank+11)*.16-.08;
                half3 albedo=lerp(_BaseColor.rgb*(1+tint),_GrainColor.rgb,grain*_Grain);
                if (!top) albedo=lerp(albedo,_GrainColor.rgb,.25);
                albedo*=1-seam*.55;
                if (top)
                {
                    float bump=(grainZ-grain)*.9+Noise(p.xz*float2(3,40))*.04;
                    n=normalize(n+half3(0,0,-bump)+half3(0,0,sign(p.z-plank*_PlankWidth)*seam*.35));
                }
                float2 aoUV=p.xz/(2*_AOExtent)+.5;
                half ao=SAMPLE_TEXTURE2D(_AOTex,sampler_AOTex,aoUV).r;
                Light light=GetMainLight(TransformWorldToShadowCoord(p));
                half3 l=light.direction;
                half3 v=normalize(GetWorldSpaceViewDir(p));
                half3 bottle=top?BottleShadow(p,l):1;
                half ndl=saturate(dot(n,l));
                // Keep the original painterly wrap so the walnut stays warm instead of going grey-dark.
                half3 direct=light.color*(.2+.8*ndl*light.shadowAttenuation*bottle);
                half3 h=normalize(l+v);
                half gloss=_Gloss*(1-grain*.5)*(1-seam);
                half spec=pow(saturate(dot(n,h)),lerp(16,96,gloss))*lerp(.02,.18,gloss)*ndl*light.shadowAttenuation;
                half fresnel=.04+.5*pow(1-saturate(dot(n,v)),5);
                half3 env=SAMPLE_TEXTURECUBE_LOD(_VoyageReflection,sampler_VoyageReflection,reflect(-v,n),4).rgb;
                half3 color=albedo*(SampleSH(n)*ao+direct*lerp(.75,1,ao))
                    +spec*light.color*bottle*2.2+env*lerp(albedo*2,1,.5)*fresnel*gloss*ao*.45;
                return half4(color,1);
            }
            ENDHLSL
        }
    }
}
