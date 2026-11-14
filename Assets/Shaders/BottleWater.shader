Shader "WaterSplash/Bottle Water"
{
    Properties
    {
        _SlowColor("Slow Color", Color) = (0.025, 0.28, 0.65, 1)
        _FastColor("Fast Color", Color) = (0.18, 0.9, 1, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

        float4 _FluidBufferSize;
        float4x4 _FluidInverseProjection;
        float _FluidRadius;
        float _FluidDepthRange;

        float SceneEyeDepth(float2 uv)
        {
            float raw = SampleSceneDepth(uv);
            if (unity_OrthoParams.w > 0.5)
            {
                #if UNITY_REVERSED_Z
                    raw = 1.0 - raw;
                #endif
                return lerp(_ProjectionParams.y, _ProjectionParams.z, raw);
            }
            return LinearEyeDepth(raw, _ZBufferParams);
        }

        float3 FluidViewPosition(float2 uv, float eyeDepth)
        {
            float4 ray = mul(_FluidInverseProjection, ComputeClipSpacePosition(uv, UNITY_NEAR_CLIP_VALUE));
            ray.xyz /= ray.w;
            if (unity_OrthoParams.w > 0.5) return float3(ray.xy, -eyeDepth);
            return ray.xyz * (eyeDepth / max(-ray.z, 1e-6));
        }

        float FluidDeviceDepth(float3 viewPosition)
        {
            float4 projected = mul(UNITY_MATRIX_P, float4(viewPosition, 1));
            float depth = projected.z / projected.w;
            #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES3)
                depth = depth * 0.5 + 0.5;
            #endif
            return depth;
        }

        #define MPM_PARTICLES_READ_ONLY
        #include "BottleFluidData.hlsl"
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
        float _DetailedWhitewater;

        static const float2 BillboardCorners[6] = {
            float2(-1,-1), float2(-1,1), float2(1,1),
            float2(-1,-1), float2(1,1), float2(1,-1)
        };

        struct ParticleVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disk : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
            float3 color : TEXCOORD2;
        };

        ParticleVaryings ParticleVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            ParticleVaryings output = (ParticleVaryings)0;
            output.centerVS = TransformWorldToView(_Origin + _CellSize * ParticlePosition(instance).xyz);
            output.disk = BillboardCorners[vertex];
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
            float4 detail : TEXCOORD3;
            float3 billboardVS : TEXCOORD4;
        };

        WhitewaterVaryings WhitewaterVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            uint id = _WhitewaterIndices[instance];
            Whitewater p = _Whitewater[id];
            WhitewaterVaryings output = (WhitewaterVaryings)0;
            output.positionCS = float4(0, 0, 2, 1);
            if (p.position.w <= 0) return output;
            float random = WhitewaterRandom(id);
            bool foam = p.normalType.w > 0.5;
            float radius = _WhitewaterRadius * (foam ?
                lerp(lerp(1.4, 2.8, random), lerp(2.8, 4.3, random), _ContinuousFoam) : lerp(0.25, 0.6, random));
            if (!foam && _DetailedWhitewater > .5) radius *= .75;
            output.centerVS = TransformWorldToView(_Origin + _CellSize * p.position.xyz);
            output.disk = BillboardCorners[vertex];
            output.detail = float4(random * 31, WhitewaterRandom(id + 193u) * 31,
                saturate(1 - p.position.w / max(p.velocity.w, .001)), random);
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
                if (_DetailedWhitewater > .5)
                {
                    // A seeded surface frame carries detail with the seed instead of swimming through world noise.
                    tangent = normalize(cross(n, abs(n.y) < .9 ? float3(0,1,0) : float3(1,0,0)));
                    bitangent = cross(n, tangent);
                    float angle = random * 6.2831853;
                    tangent = tangent * cos(angle) + bitangent * sin(angle);
                    bitangent = cross(n, tangent);
                }
                float3 offset = radius * (output.disk.x * tangent * 1.65 + output.disk.y * bitangent * .85);
                if (_DetailedWhitewater > .5)
                {
                    float speed = length(flow) * _CellSize;
                    float3 direction = flow / max(length(flow), .1);
                    offset += direction * dot(offset, direction) * saturate(speed * .3);
                }
                float3 surfaceOffset = mul((float3x3)UNITY_MATRIX_V,
                    offset);
                billboard = lerp(billboard * .7, surfaceOffset, .85);
            }
            else if (!foam && _DetailedWhitewater > .5)
            {
                float3 velocityVS = mul((float3x3)UNITY_MATRIX_V, p.velocity.xyz * _CellSize);
                float speed = length(velocityVS.xy);
                float2 direction = velocityVS.xy / max(speed, .001);
                billboard.xy += direction * dot(billboard.xy, direction) * min(speed * .65, 2.5);
            }
            output.billboardVS = billboard;
            output.positionCS = mul(UNITY_MATRIX_P, float4(output.centerVS + billboard, 1));
            return output;
        }

        float BubbleRims(float2 p, float age)
        {
            float2 cell = floor(p), f = frac(p);
            float nearest = 10;
            [unroll] for (int y = -1; y <= 1; y++)
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float2 offset = float2(x, y);
                float3 hash = frac(float3(cell + offset, (cell.x + offset.x) + (cell.y + offset.y)) *
                    float3(.1031, .1030, .0973));
                hash += dot(hash, hash.yxz + 33.33);
                float2 random = frac((hash.xx + hash.yz) * hash.zy);
                float2 delta = offset + .25 + random * .5 - f;
                nearest = min(nearest, length(delta) / lerp(.28, .46, random.x));
            }
            float aa = max(fwidth(nearest), .04);
            float hole = lerp(.55, .91, age);
            float ring = smoothstep(hole - aa, hole + aa, nearest) *
                (1 - smoothstep(1.06 - aa, 1.06 + aa, nearest));
            // Unresolved bubbles become average coverage rather than sparkling subpixel rings.
            return lerp(ring, .32, saturate((aa - .25) * 2));
        }

        float2 FoamMask(WhitewaterVaryings input) : SV_Target
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
            float density = input.radiusFade.y * depthFade * kernel;
            float detail = 1;
            if (_DetailedWhitewater > .5)
            {
                float2 p = input.disk * lerp(2.8, 4.2, input.detail.w) + input.detail.xy;
                detail = lerp(BubbleRims(p, input.detail.z),
                    BubbleRims(p * 2.17 + 7.3, input.detail.z), .35);
            }
            return float2(density, density * detail);
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
            if (_DetailedWhitewater > .5)
            {
                surface = input.centerVS + input.billboardVS + float3(0, 0, hemisphere * input.radiusFade.x);
                float eyeDepth = -surface.z;
                float2 uv = GetNormalizedScreenSpaceUV(input.positionCS);
                float softRange = max(input.radiusFade.x * 2, .003);
                float soft = saturate((SceneEyeDepth(uv) - eyeDepth) / softRange);
                float waterDepth = SAMPLE_TEXTURE2D(_FluidDepth, sampler_PointClamp, uv).r;
                if (waterDepth > 0) soft *= saturate((waterDepth - eyeDepth) / softRange);
                float3 normal = normalize(float3(input.disk, hemisphere));
                float3 view = normalize(-surface);
                float fresnel = .02 + .98 * pow(1 - saturate(dot(normal, view)), 5);
                float3 light = normalize(mul((float3x3)UNITY_MATRIX_V, float3(-.5, .75, .4)));
                float glint = pow(saturate(dot(normal, normalize(light + view))), 64);
                output.color = half4(lerp(float3(.45,.64,.70), float3(.88,.96,1), fresnel) + glint * .6,
                    input.radiusFade.y * soft * smoothstep(1, .15, r2) *
                    (.16 + .42 * fresnel + .25 * glint));
                output.depth = FluidDeviceDepth(surface);
            }
            return output;
        }
        TEXTURE2D(_FluidSource);
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
        TEXTURECUBE(_VoyageReflection); SAMPLER(sampler_VoyageReflection);
        float _VoyageReflectionStrength;

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
            // Scenes that publish a baked environment (the bottle voyage) use it and the real sun.
            if (_VoyageReflectionStrength > 0)
            {
                environment = SAMPLE_TEXTURECUBE_LOD(_VoyageReflection, sampler_VoyageReflection, reflected, 1).rgb
                    * _VoyageReflectionStrength;
                light = _MainLightPosition.xyz;
            }
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

        float4 _SlowColor, _FastColor;
        ParticleVaryings DebugVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            ParticleVaryings output = ParticleVertex(vertex, instance);
            output.color = lerp(_SlowColor.rgb, _FastColor.rgb,
                saturate(length(ParticleVelocity(instance).xyz) * _CellSize / 5));
            return output;
        }
        WaterOutput DebugFragment(ParticleVaryings input)
        {
            float r2 = dot(input.disk, input.disk);
            clip(1.0 - r2);
            float3 normalVS = float3(input.disk, sqrt(saturate(1.0 - r2)));
            float diffuse = saturate(dot(normalVS, normalize(float3(-0.4, 0.7, 0.6))));
            float highlight = pow(saturate(dot(normalVS, normalize(float3(-0.3, 0.5, 1)))), 48.0);
            WaterOutput output;
            output.depth = FluidDeviceDepth(input.centerVS + normalVS * _Radius);
            output.color = half4(input.color * (0.3 + diffuse * 0.7) + highlight * 0.65, 1);
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

        Pass
        {
            Name "Raw Particles"
            ZTest LEqual
            ZWrite On
            Blend Off
            HLSLPROGRAM
            #pragma vertex DebugVertex
            #pragma fragment DebugFragment
            ENDHLSL
        }
    }
}
