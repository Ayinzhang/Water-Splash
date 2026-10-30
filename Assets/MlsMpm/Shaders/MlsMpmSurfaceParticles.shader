Shader "Hidden/WaterSplash/Surface Particles"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "MlsMpmSurfaceCommon.hlsl"
        #include "MlsMpmWhitewater.hlsl"

        #define MPM_PARTICLES_READ_ONLY
        #include "MlsMpmParticleState.hlsl"
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

        struct ParticleVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disk : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
        };

        ParticleVaryings ParticleVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
        {
            const float2 corners[6] = {
                float2(-1,-1), float2(-1,1), float2(1,1),
                float2(-1,-1), float2(1,1), float2(1,-1)
            };
            ParticleVaryings output;
            output.centerVS = TransformWorldToView(_Origin + _CellSize * ParticlePosition(instance).xyz);
            output.disk = corners[vertex];
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
            const float2 corners[6] = {
                float2(-1,-1), float2(-1,1), float2(1,1),
                float2(-1,-1), float2(1,1), float2(1,-1)
            };
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
            output.disk = corners[vertex];
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
    }
}
