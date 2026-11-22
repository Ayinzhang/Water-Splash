Shader "Bottle/Water"
{
    Properties
    {
        _SlowColor("Slow Color", Color) = (0.025, 0.28, 0.65, 1)
        _FastColor("Fast Color", Color) = (0.18, 0.9, 1, 1)
        _WaterColor("Water Color", Color) = (0.01, 0.15, 0.32, 1)
        _Absorption("Absorption", Vector) = (2.5, 0.7, 0.3, 0)
        _Refraction("Refraction", Range(0, 0.2)) = 0.08
        _Reflection("Reflection", Cube) = "" {}
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        struct Whitewater { float4 position, velocity, normalType; }; // position.w: life, velocity.w: lifetime, type: 0 spray 1 foam

        StructuredBuffer<float4> _Particles;
        StructuredBuffer<Whitewater> _Whitewater;
        StructuredBuffer<uint> _WhitewaterIndices;
        uint _ParticleCount;
        float3 _Origin;
        float _CellSize, _FluidRadius, _FluidFilterScale, _FluidFirstFilter, _Refraction;
        float2 _FluidBlurDirection, _FluidProjectionScale;
        float4 _FluidBufferSize, _SlowColor, _FastColor, _WaterColor, _Absorption;
        float4x4 _FluidInverseProjection;
        TEXTURE2D(_FluidDepth); TEXTURE2D(_FluidSource); TEXTURE2D(_FluidThickness); TEXTURE2D(_FluidNormals); TEXTURE2D(_FluidFoam);
        TEXTURECUBE(_Reflection); SAMPLER(sampler_Reflection);

        #define DepthRange (_FluidRadius * 4)
        static const float ThicknessScale = 3 / (4 * PI * .95 * .95 * .95);
        static const float2 Corners[6] = { float2(-1,-1), float2(-1,1), float2(1,1), float2(-1,-1), float2(1,1), float2(1,-1) };

        uint WaterHash(uint v) { v ^= v >> 16; v *= 0x7feb352du; v ^= v >> 15; v *= 0x846ca68bu; return v ^ (v >> 16); }
        float WaterRandom(uint seed) { return (WaterHash(seed) & 0xffffffu) / 16777216.0; }
        float SceneEyeDepth(float2 uv)
        {
            float raw = SampleSceneDepth(uv);
            if (unity_OrthoParams.w < .5) return LinearEyeDepth(raw, _ZBufferParams);
            #if UNITY_REVERSED_Z
                raw = 1 - raw;
            #endif
            return lerp(_ProjectionParams.y, _ProjectionParams.z, raw); // ortho depth is linear
        }
        float DeviceDepth(float3 positionVS) { float4 p = mul(UNITY_MATRIX_P, float4(positionVS, 1)); return p.z / p.w; }
        float3 ViewPosition(float2 uv, float eyeDepth)
        {
            float4 ray = mul(_FluidInverseProjection, ComputeClipSpacePosition(uv, UNITY_NEAR_CLIP_VALUE));
            ray.xyz /= ray.w;
            return unity_OrthoParams.w > .5 ? float3(ray.xy, -eyeDepth) : ray.xyz * (eyeDepth / -ray.z);
        }

        struct ParticleVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disk : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
            float3 color : TEXCOORD2;
        };

        ParticleVaryings Billboard(uint vertex, uint instance, float radius)
        {
            ParticleVaryings o = (ParticleVaryings)0;
            o.centerVS = TransformWorldToView(_Origin + _CellSize * _Particles[instance].xyz);
            o.disk = Corners[vertex];
            o.positionCS = -o.centerVS.z < _ProjectionParams.y + radius ? float4(0, 0, 2, 1) : mul(UNITY_MATRIX_P, float4(o.centerVS + float3(o.disk * radius, 0), 1));
            return o;
        }

        ParticleVaryings ParticleVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) { return Billboard(vertex, instance, _FluidRadius); }

        struct DepthOutput { float depth : SV_Target; float deviceDepth : SV_Depth; };
        struct ColorOutput { half4 color : SV_Target; float depth : SV_Depth; };

        DepthOutput ParticleDepth(ParticleVaryings i)
        {
            float r2 = dot(i.disk, i.disk);
            clip(1 - r2);
            float3 surface = i.centerVS + float3(i.disk, sqrt(1 - r2)) * _FluidRadius;
            clip(SceneEyeDepth(i.positionCS.xy * _FluidBufferSize.zw) + surface.z);
            DepthOutput o; o.depth = -surface.z; o.deviceDepth = DeviceDepth(surface);
            return o;
        }

        float ParticleThickness(ParticleVaryings i) : SV_Target
        {
            float r2 = dot(i.disk, i.disk);
            clip(1 - r2);
            float chord = sqrt(1 - r2) * _FluidRadius;
            return max(0, min(-i.centerVS.z + chord, SceneEyeDepth(i.positionCS.xy * _FluidBufferSize.zw)) + i.centerVS.z + chord) * ThicknessScale;
        }

        // Whitewater
        struct WhitewaterVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disk : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
            float2 radiusFade : TEXCOORD2;
            float4 detail : TEXCOORD3; // bubble seed, age, random
            float3 billboardVS : TEXCOORD4;
        };

        WhitewaterVaryings WhitewaterVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            uint id = _WhitewaterIndices[instance];
            Whitewater p = _Whitewater[id];
            WhitewaterVaryings o = (WhitewaterVaryings)0;
            o.positionCS = float4(0, 0, 2, 1);
            float random = WaterRandom(id);
            bool foam = p.normalType.w > .5;
            float radius = _FluidRadius * .4 / .95 * (foam ? lerp(2.8, 4.3, random) : lerp(.25, .6, random) * .75);
            o.centerVS = TransformWorldToView(_Origin + _CellSize * p.position.xyz);
            o.disk = Corners[vertex];
            o.detail = float4(random * 31, WaterRandom(id + 193) * 31, saturate(1 - p.position.w / p.velocity.w), random);
            o.radiusFade = float2(radius, saturate((p.velocity.w - p.position.w) / .1) * saturate(p.position.w / .5));
            if (-o.centerVS.z < _ProjectionParams.y + radius) return o;
            float3 billboard = float3(o.disk * radius, 0);
            if (foam)
            {
                // Seeded ellipse lying on the surface, stretched along the flow
                float3 n = normalize(p.normalType.xyz + float3(0, 1e-5, 0)), flow = p.velocity.xyz - n * dot(n, p.velocity.xyz);
                float3 t = normalize(cross(n, abs(n.y) < .9 ? float3(0, 1, 0) : float3(1, 0, 0))), b = cross(n, t);
                float angle = random * TWO_PI;
                t = t * cos(angle) + b * sin(angle); b = cross(n, t);
                float3 offset = radius * (o.disk.x * t * 1.65 + o.disk.y * b * .85), direction = flow / max(length(flow), .1);
                offset += direction * dot(offset, direction) * saturate(length(flow) * _CellSize * .3);
                billboard = lerp(billboard * .7, mul((float3x3)UNITY_MATRIX_V, offset), .85);
            }
            else
            {
                // Spray streaks along its screen velocity
                float3 v = mul((float3x3)UNITY_MATRIX_V, p.velocity.xyz * _CellSize);
                float speed = length(v.xy);
                float2 direction = v.xy / max(speed, .001);
                billboard.xy += direction * dot(billboard.xy, direction) * min(speed * .65, 2.5);
            }
            o.billboardVS = billboard;
            o.positionCS = mul(UNITY_MATRIX_P, float4(o.centerVS + billboard, 1));
            return o;
        }

        float BubbleRims(float2 p, float age)
        {
            float2 cell = floor(p), f = frac(p);
            float nearest = 10;
            [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 offset = float2(x, y);
                    float3 h = frac(float3(cell + offset, cell.x + offset.x + cell.y + offset.y) * float3(.1031, .1030, .0973));
                    h += dot(h, h.yxz + 33.33);
                    float2 r = frac((h.xx + h.yz) * h.zy);
                    nearest = min(nearest, length(offset + .25 + r * .5 - f) / lerp(.28, .46, r.x));
                }
            float aa = max(fwidth(nearest), .04), hole = lerp(.55, .91, age);
            float ring = smoothstep(hole - aa, hole + aa, nearest) * (1 - smoothstep(1.06 - aa, 1.06 + aa, nearest));
            return lerp(ring, .32, saturate((aa - .25) * 2)); // unresolved bubbles fade to average coverage
        }

        float2 FoamMask(WhitewaterVaryings i) : SV_Target
        {
            float r2 = dot(i.disk, i.disk);
            clip(1 - r2);
            float2 uv = i.positionCS.xy * _FluidBufferSize.zw;
            float depth = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv).r;
            clip(depth - 1e-5);
            float fade = saturate(1 - abs(-i.centerVS.z - depth) / (_FluidRadius * 2.5));
            clip(fade - 1e-4);
            clip(SceneEyeDepth(uv) + i.centerVS.z + i.radiusFade.x);
            float density = i.radiusFade.y * fade * exp(-3.5 * r2) * .42;
            float2 p = i.disk * lerp(2.8, 4.2, i.detail.w) + i.detail.xy;
            return float2(density, density * lerp(BubbleRims(p, i.detail.z), BubbleRims(p * 2.17 + 7.3, i.detail.z), .35));
        }

        ColorOutput Spray(WhitewaterVaryings i)
        {
            float r2 = dot(i.disk, i.disk);
            clip(1 - r2);
            float hemisphere = sqrt(1 - r2);
            float3 surface = i.centerVS + i.billboardVS + float3(0, 0, hemisphere * i.radiusFade.x);
            float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
            float range = max(i.radiusFade.x * 2, .003), soft = saturate((SceneEyeDepth(uv) + surface.z) / range);
            float water = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv).r;
            if (water > 0) soft *= saturate((water + surface.z) / range);
            float3 normal = normalize(float3(i.disk, hemisphere)), view = normalize(-surface);
            float fresnel = .02 + .98 * pow(1 - saturate(dot(normal, view)), 5);
            float glint = pow(saturate(dot(normal, normalize(mul((float3x3)UNITY_MATRIX_V, normalize(float3(-.5, .75, .4))) + view))), 64);
            ColorOutput o;
            o.color = half4(lerp(float3(.45, .64, .70), float3(.88, .96, 1), fresnel) + glint * .6, i.radiusFade.y * soft * smoothstep(1, .15, r2) * (.16 + .42 * fresnel + .25 * glint));
            o.depth = DeviceDepth(surface);
            return o;
        }

        // Screen space surface
        struct SurfaceVaryings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        SurfaceVaryings SurfaceVertex(uint vertex : SV_VertexID)
        {
            SurfaceVaryings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(vertex);
            o.uv = GetFullScreenTriangleTexCoord(vertex);
            return o;
        }

        float2 FilterPoint(float2 uv)
        {
            float2 data = SAMPLE_TEXTURE2D(_FluidSource, sampler_PointClamp, uv).rg;
            if (_FluidFirstFilter > .5) data.y = SAMPLE_TEXTURE2D(_FluidThickness, sampler_PointClamp, uv).r;
            return data;
        }

        float2 FilterSample(float2 uv, float reference) // linear along the axis without blending empty space or another layer
        {
            float fraction = frac(dot(uv * _FluidBufferSize.xy - .5, abs(_FluidBlurDirection)));
            float2 first = uv - _FluidBlurDirection * _FluidBufferSize.zw * fraction;
            float2 a = FilterPoint(first), b = FilterPoint(first + _FluidBlurDirection * _FluidBufferSize.zw);
            float wa = a.x > 0 && abs(a.x - reference) < DepthRange * 2 ? 1 - fraction : 0;
            float wb = b.x > 0 && abs(b.x - reference) < DepthRange * 2 ? fraction : 0;
            return wa + wb > 1e-5 ? (wa * a + wb * b) / (wa + wb) : 0;
        }

        float2 SmoothDepth(SurfaceVaryings i) : SV_Target
        {
            float2 center = FilterPoint(i.uv), sum = center;
            if (center.x <= 0) return 0;
            float stride = clamp(_FluidFilterScale * _FluidRadius * dot(_FluidProjectionScale, abs(_FluidBlurDirection)) / (unity_OrthoParams.w > .5 ? 1 : center.x), 1, 24) / 4, weights = 1;
            [unroll] for (int tap = -4; tap <= 4; tap++)
            {
                if (tap == 0) continue;
                float2 data = FilterSample(i.uv + _FluidBlurDirection * _FluidBufferSize.zw * (tap * stride), center.x);
                float difference = (data.x - center.x) / DepthRange;
                if (data.x <= 0 || abs(difference) > 2) continue;
                float weight = exp(-.125 * tap * tap - 2 * difference * difference);
                sum += data * weight; weights += weight;
            }
            return sum / weights;
        }

        float2 SurfaceData(float2 uv, out float3 normal)
        {
            float2 pixel = uv * _FluidBufferSize.xy - .5, fraction = frac(pixel), origin = (floor(pixel) + .5) * _FluidBufferSize.zw, sum = 0;
            float reference = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv).r, weights = 0;
            normal = 0;
            [unroll] for (int k = 0; k < 4; k++)
            {
                float2 o = float2(k & 1, k >> 1), suv = origin + o * _FluidBufferSize.zw;
                float2 data = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, suv).rg;
                float2 w2 = lerp(1 - fraction, fraction, o);
                if (data.x <= 0 || reference > 0 && abs(data.x - reference) > DepthRange * 2) continue;
                sum += w2.x * w2.y * data; weights += w2.x * w2.y;
                normal += w2.x * w2.y * UnpackNormalOctQuadEncode(SAMPLE_TEXTURE2D(_FluidNormals, sampler_PointClamp, suv).rg);
            }
            return weights >= .35 ? sum / weights : 0;
        }

        float3 Derivative(float2 uv, float depth, float3 position, float2 offset)
        {
            float minus = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv - offset).r, plus = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv + offset).r;
            bool m = minus > 0 && abs(minus - depth) < DepthRange * 2, p = plus > 0 && abs(plus - depth) < DepthRange * 2;
            if (m && p) return ViewPosition(uv + offset, plus) - ViewPosition(uv - offset, minus);
            if (m) return position - ViewPosition(uv - offset, minus);
            return ViewPosition(uv + offset, p ? plus : depth) - position;
        }

        float2 BuildNormal(SurfaceVaryings i) : SV_Target
        {
            float depth = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, i.uv).r;
            if (depth <= 0) return 0;
            float3 position = ViewPosition(i.uv, depth);
            float3 normal = SafeNormalize(cross(Derivative(i.uv, depth, position, float2(_FluidBufferSize.z, 0)), Derivative(i.uv, depth, position, float2(0, _FluidBufferSize.w))));
            return PackNormalOctQuadEncode(dot(normal, position) > 0 ? -normal : normal);
        }

        ColorOutput ShadeWater(SurfaceVaryings i)
        {
            float3 normalVS;
            float2 data = SurfaceData(i.uv, normalVS);
            float depth = data.x, thickness = data.y;
            clip(depth - 1e-5);
            clip(SceneEyeDepth(i.uv) - depth);
            float3 positionVS = ViewPosition(i.uv, depth), viewVS = normalize(-positionVS);
            normalVS = SafeNormalize(normalVS);
            if (dot(normalVS, viewVS) < 0) normalVS = -normalVS;
            float3 normalWS = mul((float3x3)UNITY_MATRIX_I_V, normalVS), viewWS = mul((float3x3)UNITY_MATRIX_I_V, viewVS), reflected = reflect(-viewWS, normalWS), light = _MainLightPosition.xyz;
            float fresnel = .02 + .98 * pow(1 - saturate(dot(normalVS, viewVS)), 5);
            // Refract the opaque background, rejecting foreground occluders
            float2 direction = normalVS.xy;
            #if UNITY_UV_STARTS_AT_TOP
                direction.y = -direction.y;
            #endif
            float2 refracted = saturate(i.uv + direction * (_Refraction * min(thickness, 1) / max(depth, 1)));
            if (SceneEyeDepth(refracted) < depth) refracted = i.uv;
            float3 transmittance = exp(-_Absorption.rgb * thickness);
            float3 transmitted = SampleSceneColor(refracted) * transmittance + _WaterColor.rgb * (1 - transmittance);
            float3 environment = SAMPLE_TEXTURECUBE_LOD(_Reflection, sampler_Reflection, reflected, 1).rgb + 2 * pow(saturate(dot(reflected, light)), 48);
            float3 water = lerp(transmitted, environment, fresnel) + pow(saturate(dot(normalWS, normalize(light + viewWS))), 256) * .4;
            // Foam: density with bubble structure that packs solid where dense
            float2 field = SAMPLE_TEXTURE2D(_FluidFoam, sampler_LinearClamp, i.uv).rg;
            float detail = saturate(field.g / max(field.r, 1e-4));
            float coverage = (1 - exp(-3.2 * field.r)) * lerp(saturate(detail * 2.2), .62 + .38 * detail, smoothstep(.4, 1.3, field.r));
            float3 foam = float3(.9, .96, 1) * (.75 + .25 * saturate(dot(normalWS, light)));
            ColorOutput o;
            o.color = half4(lerp(water, foam, coverage * .95), 1);
            o.depth = DeviceDepth(positionVS);
            return o;
        }

        // Raw particles
        ParticleVaryings DebugVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            ParticleVaryings o = Billboard(vertex, instance, _FluidRadius * .3);
            o.color = lerp(_SlowColor.rgb, _FastColor.rgb, saturate(length(_Particles[instance + _ParticleCount].xyz) * _CellSize / 5));
            return o;
        }

        ColorOutput DebugFragment(ParticleVaryings i)
        {
            float r2 = dot(i.disk, i.disk);
            clip(1 - r2);
            float3 normal = float3(i.disk, sqrt(1 - r2));
            ColorOutput o;
            o.depth = DeviceDepth(i.centerVS + normal * _FluidRadius * .3);
            o.color = half4(i.color * (.3 + .7 * saturate(dot(normal, normalize(float3(-.4, .7, .6))))) + pow(saturate(dot(normal, normalize(float3(-.3, .5, 1)))), 48) * .65, 1);
            return o;
        }
        ENDHLSL

        Pass // 0
        {
            Name "Fluid Depth"
            ZTest LEqual ZWrite On Blend Off
            HLSLPROGRAM
            #pragma vertex ParticleVertex
            #pragma fragment ParticleDepth
            ENDHLSL
        }
        Pass // 1
        {
            Name "Fluid Thickness"
            ZTest Always ZWrite Off Blend One One
            HLSLPROGRAM
            #pragma vertex ParticleVertex
            #pragma fragment ParticleThickness
            ENDHLSL
        }
        Pass // 2
        {
            Name "Bilateral Depth"
            ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma vertex SurfaceVertex
            #pragma fragment SmoothDepth
            ENDHLSL
        }
        Pass // 3
        {
            Name "Water Composite"
            ZTest LEqual ZWrite On Blend Off
            HLSLPROGRAM
            #pragma vertex SurfaceVertex
            #pragma fragment ShadeWater
            ENDHLSL
        }
        Pass // 4
        {
            Name "Surface Normals"
            ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma vertex SurfaceVertex
            #pragma fragment BuildNormal
            ENDHLSL
        }
        Pass // 5
        {
            Name "Raw Particles"
            ZTest LEqual ZWrite On Blend Off
            HLSLPROGRAM
            #pragma vertex DebugVertex
            #pragma fragment DebugFragment
            ENDHLSL
        }
        Pass // 6
        {
            Name "Advected Foam"
            ZTest Always ZWrite Off Blend One One
            HLSLPROGRAM
            #pragma vertex WhitewaterVertex
            #pragma fragment FoamMask
            ENDHLSL
        }
        Pass // 7
        {
            Name "Ballistic Spray"
            ZTest LEqual ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex WhitewaterVertex
            #pragma fragment Spray
            ENDHLSL
        }
    }
}
