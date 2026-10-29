Shader "Hidden/WaterSplash/Continuous Surface"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "MlsMpmSurfaceCommon.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

        TEXTURE2D(_FluidSource);
        TEXTURE2D(_FluidDepth);
        TEXTURE2D(_FluidThickness);
        TEXTURE2D(_FluidNormals);
        TEXTURE2D(_FluidFoam);
        float2 _FluidBlurDirection;
        float4 _FluidProjectionScale;
        float _FluidFilterScale;
        float _FluidFirstFilter;
        float4 _FluidAbsorption;
        float4 _FluidWaterColor;
        float _FluidRefraction;
        float _ContinuousFoam;
        float _DetailedWhitewater;

        float FoamNoise(float2 p)
        {
            float2 cell = floor(p), f = frac(p); f = f * f * (3 - 2 * f);
            float4 h = frac(sin(float4(dot(cell, float2(127.1,311.7)),
                dot(cell + float2(1,0), float2(127.1,311.7)),
                dot(cell + float2(0,1), float2(127.1,311.7)),
                dot(cell + 1, float2(127.1,311.7)))) * 43758.5453);
            return lerp(lerp(h.x, h.y, f.x), lerp(h.z, h.w, f.x), f.y);
        }

        float FoamCoverage(float2 uv, float depth, float3 positionWS)
        {
            float2 field = SAMPLE_TEXTURE2D(_FluidFoam, sampler_LinearClamp, uv).rg;
            float density = field.r;
            if (_DetailedWhitewater > .5)
            {
                float detail = saturate(field.g / max(density, 1e-4));
                float packed = smoothstep(.4, 1.3, density);
                float structure = lerp(saturate(detail * 2.2), .62 + .38 * detail, packed);
                return (1 - exp(-3.2 * density)) * structure;
            }
            if (_ContinuousFoam < .5) return 1 - exp(-1.5 * density);
            float sum = density * 4, weight = 4;
            const float2 offsets[4] = { float2(-1,0), float2(1,0), float2(0,-1), float2(0,1) };
            [unroll] for (int i = 0; i < 4; i++)
            {
                float2 sampleUv = uv + offsets[i] * _FluidBufferSize.zw * 1.5;
                float otherDepth = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, sampleUv).r;
                float valid = otherDepth > 0 ? saturate(1 - abs(otherDepth - depth) / _FluidDepthRange) : 0;
                sum += valid * SAMPLE_TEXTURE2D(_FluidFoam, sampler_LinearClamp, sampleUv).r; weight += valid;
            }
            density = sum / weight;
            // Detail breaks up the common foam field rather than outlining every seed.
            float2 p = (positionWS.xz + positionWS.y * float2(.31,.47)) * 18;
            float grain = lerp(FoamNoise(p), FoamNoise(p * 2.13), .3);
            float coverage = 1 - exp(-3.8 * density);
            return saturate(coverage * lerp(.78, 1.12, grain));
        }

        struct SurfaceVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        SurfaceVaryings SurfaceVertex(uint vertex : SV_VertexID)
        {
            SurfaceVaryings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(vertex);
            output.uv = GetFullScreenTriangleTexCoord(vertex);
            return output;
        }

        float2 FilterPoint(float2 uv)
        {
            float2 data = SAMPLE_TEXTURE2D(_FluidSource, sampler_PointClamp, uv).rg;
            if (_FluidFirstFilter > 0.5)
                data.y = SAMPLE_TEXTURE2D(_FluidThickness, sampler_PointClamp, uv).r;
            return data;
        }

        float2 FilterSample(float2 uv, float reference)
        {
            // Interpolate along the filter axis without interpolating empty space or another layer.
            float fraction = frac(dot(uv * _FluidBufferSize.xy - 0.5, abs(_FluidBlurDirection)));
            float2 firstUv = uv - _FluidBlurDirection * _FluidBufferSize.zw * fraction;
            float2 a = FilterPoint(firstUv);
            float2 b = FilterPoint(firstUv + _FluidBlurDirection * _FluidBufferSize.zw);
            float wa = a.x > 0 && abs(a.x - reference) < _FluidDepthRange * 2 ? 1.0 - fraction : 0;
            float wb = b.x > 0 && abs(b.x - reference) < _FluidDepthRange * 2 ? fraction : 0;
            float weights = wa + wb;
            return weights > 1e-5 ? (wa * a + wb * b) / weights : 0;
        }

        float2 SmoothDepth(SurfaceVaryings input) : SV_Target
        {
            float2 center = FilterPoint(input.uv);
            if (center.x <= 0) return 0;
            float distance = lerp(1.0, center.x, _FluidProjectionScale.z);
            float projectedRadius = _FluidRadius * dot(_FluidProjectionScale.xy, abs(_FluidBlurDirection)) / distance;
            float stride = clamp(_FluidFilterScale * projectedRadius, 1.0, 24.0) / 4.0;
            float2 sum = center;
            float weightSum = 1;
            [unroll]
            for (int tap = -4; tap <= 4; tap++)
            {
                if (tap == 0) continue;
                float2 uv = input.uv + _FluidBlurDirection * _FluidBufferSize.zw * (tap * stride);
                float2 data = FilterSample(uv, center.x);
                if (data.x <= 0) continue;
                float difference = (data.x - center.x) / _FluidDepthRange;
                if (abs(difference) > 2.0) continue;
                float weight = exp(-0.125 * tap * tap - 2.0 * difference * difference);
                sum += data * weight;
                weightSum += weight;
            }
            return sum / weightSum;
        }

        float2 SurfaceData(float2 uv, out float3 normal)
        {
            float2 pixel = uv * _FluidBufferSize.xy - 0.5;
            float2 fraction = frac(pixel);
            float2 origin = (floor(pixel) + 0.5) * _FluidBufferSize.zw;
            float2 sum = 0;
            normal = 0;
            float weights = 0;
            float reference = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv).r;
            [unroll]
            for (int y = 0; y < 2; y++)
            [unroll]
            for (int x = 0; x < 2; x++)
            {
                float2 sampleUv = origin + float2(x, y) * _FluidBufferSize.zw;
                float2 data = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, sampleUv).rg;
                float weight = (x == 0 ? 1.0 - fraction.x : fraction.x) *
                               (y == 0 ? 1.0 - fraction.y : fraction.y);
                if (data.x <= 0 || (reference > 0 && abs(data.x - reference) > _FluidDepthRange * 2)) continue;
                sum += weight * data;
                normal += weight * UnpackNormalOctQuadEncode(
                    SAMPLE_TEXTURE2D(_FluidNormals, sampler_PointClamp, sampleUv).rg);
                weights += weight;
            }
            return weights >= 0.35 ? sum / weights : 0;
        }

        float3 SurfaceDerivative(float2 uv, float depth, float3 position, float2 offset)
        {
            float minus = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv - offset).r;
            float plus = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv + offset).r;
            bool validMinus = minus > 0 && abs(minus - depth) < _FluidDepthRange * 2;
            bool validPlus = plus > 0 && abs(plus - depth) < _FluidDepthRange * 2;
            if (validMinus && validPlus)
                return FluidViewPosition(uv + offset, plus) - FluidViewPosition(uv - offset, minus);
            if (validPlus) return FluidViewPosition(uv + offset, plus) - position;
            if (validMinus) return position - FluidViewPosition(uv - offset, minus);
            return FluidViewPosition(uv + offset, depth) - position;
        }

        float2 BuildNormal(SurfaceVaryings input) : SV_Target
        {
            float depth = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, input.uv).r;
            if (depth <= 0) return 0;
            float3 position = FluidViewPosition(input.uv, depth);
            float3 dx = SurfaceDerivative(input.uv, depth, position, float2(_FluidBufferSize.z, 0));
            float3 dy = SurfaceDerivative(input.uv, depth, position, float2(0, _FluidBufferSize.w));
            float3 normal = SafeNormalize(cross(dx, dy));
            if (dot(normal, -position) < 0) normal = -normal;
            return PackNormalOctQuadEncode(normal);
        }

        struct WaterOutput
        {
            half4 color : SV_Target;
            float depth : SV_Depth;
        };

        WaterOutput ShadeWater(SurfaceVaryings input)
        {
            float3 normalVS;
            float2 data = SurfaceData(input.uv, normalVS);
            float depth = data.x;
            clip(depth - 1e-5);
            clip(SceneEyeDepth(input.uv) - depth);
            float3 positionVS = FluidViewPosition(input.uv, depth);
            normalVS = SafeNormalize(normalVS);
            float3 viewVS = normalize(-positionVS);
            if (dot(normalVS, viewVS) < 0) normalVS = -normalVS;
            float3 normalWS = mul((float3x3)UNITY_MATRIX_I_V, normalVS);
            float3 viewWS = mul((float3x3)UNITY_MATRIX_I_V, viewVS);
            float3 reflected = reflect(-viewWS, normalWS);
            float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(normalVS, viewVS)), 5.0);

            float thickness = data.y;
            float2 refractionDirection = normalVS.xy;
            #if UNITY_UV_STARTS_AT_TOP
                refractionDirection.y = -refractionDirection.y;
            #endif
            float2 refractedUv = saturate(input.uv + refractionDirection *
                (_FluidRefraction * min(thickness, 1.0) / max(depth, 1.0)));
            // Reject foreground samples rather than refracting an occluder over the water.
            if (SceneEyeDepth(refractedUv) < depth) refractedUv = input.uv;
            float3 background = SampleSceneColor(refractedUv);
            float3 transmittance = exp(-_FluidAbsorption.rgb * thickness);
            float3 transmitted = background * transmittance + _FluidWaterColor.rgb * (1.0 - transmittance);
            float3 environment = lerp(float3(0.13, 0.22, 0.3), float3(0.62, 0.79, 0.95),
                                     saturate(reflected.y * 0.7 + 0.3));
            float3 light = normalize(float3(-0.5, 0.75, 0.4));
            environment += 2.0 * pow(saturate(dot(reflected, light)), 48.0);
            float highlight = pow(saturate(dot(normalWS, normalize(light + viewWS))), 256.0);
            WaterOutput output;
            float coverage = FoamCoverage(input.uv, depth, mul(UNITY_MATRIX_I_V, float4(positionVS, 1)).xyz);
            float3 water = lerp(transmitted, environment, fresnel) + highlight * 0.4;
            float3 foam = float3(0.9, 0.96, 1.0) * (0.75 + 0.25 * saturate(dot(normalWS, light)));
            output.color = half4(lerp(water, foam, coverage * 0.95), 1);
            output.depth = FluidDeviceDepth(positionVS);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "Bilateral Depth"
            ZTest Always
            ZWrite Off
            Blend Off
            HLSLPROGRAM
            #pragma vertex SurfaceVertex
            #pragma fragment SmoothDepth
            ENDHLSL
        }
        Pass
        {
            Name "Water Composite"
            ZTest LEqual
            ZWrite On
            Blend Off
            HLSLPROGRAM
            #pragma vertex SurfaceVertex
            #pragma fragment ShadeWater
            ENDHLSL
        }
        Pass
        {
            Name "Surface Normals"
            ZTest Always
            ZWrite Off
            Blend Off
            HLSLPROGRAM
            #pragma vertex SurfaceVertex
            #pragma fragment BuildNormal
            ENDHLSL
        }
    }
}
