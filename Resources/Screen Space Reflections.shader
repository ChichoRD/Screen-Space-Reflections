Shader "Hidden/Screen Space Reflections"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"

        #pragma multi_compile_local_fragment _ DEFERRED_GBUFFERS_AVAILABLE
        #pragma multi_compile_local_fragment _ REFLECT_SKYBOX

        uniform TEXTURE2D(_MainTex);
        uniform SAMPLER(sampler_MainTex);
        float4 _MainTex_TexelSize;

        uniform TEXTURE2D(_GBuffer0);
        uniform SAMPLER(sampler_GBuffer0);

        uniform TEXTURE2D(_GBuffer1);
        uniform SAMPLER(sampler_GBuffer1);

        uniform TEXTURE2D(_GBuffer2);
        uniform SAMPLER(sampler_GBuffer2);

        uniform float _RayStep;
        uniform float _RayMinStep;
        uniform uint _RayMaxSteps;
        uniform float _RayMaxDistance;
        uniform uint _BinaryHitSearchSteps;

        uniform float _HitDepthDifferenceThreshold;
        uniform float _ReflectionIntensity;
        uniform float _ReflectivityBias;
        uniform float _FresnelBias;

        uniform float _VignetteRadius;
        uniform float _VignetteSoftness;

        uniform float _BlurRadius;

        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct v2f
        {
            float2 uv : TEXCOORD0;
            float4 vertex : SV_POSITION;
        };

        v2f vert(appdata v)
        {
            v2f o;
            o.vertex = TransformObjectToHClip(v.vertex);
            o.uv = v.uv;
            return o;
        }

        float3 TransformUVToPositionVS(float2 uv, float rawDepth)
        {
            return ComputeViewSpacePosition(uv, rawDepth, UNITY_MATRIX_I_P);
        }

        float2 TransformPositionVSToUV(float3 positionVS)
        {
            float2 uv = ComputeNormalizedDeviceCoordinates(positionVS, unity_CameraProjection);
            uv.x = 1.0f - uv.x;
            return uv;
        }

        float SampleRawDepth(float2 uv)
        {
            #if UNITY_REVERSED_Z
		    return SampleSceneDepth(uv);
		    #else
		        // Adjust Z to match NDC for OpenGL ([-1, 1])
		    return lerp(UNITY_NEAR_CLIP_VALUE, 1, SampleSceneDepth(uv));
		    #endif
        }

        float2 BinarySearchHitUV(inout float3 stepVS, inout float3 hitPositionVS, inout float depthDifference, inout float rawHitDepth)
		{
            float2 hitUV = 0.0f;

            [loop]
            for (uint i = 0; i < _BinaryHitSearchSteps; i++)
            {
                hitUV = TransformPositionVSToUV(hitPositionVS);

                rawHitDepth = SampleRawDepth(hitUV); 
                float linearEyeHitDepth = LinearEyeDepth(rawHitDepth, _ZBufferParams);
                depthDifference = linearEyeHitDepth - hitPositionVS.z;

                if (abs(depthDifference) < _HitDepthDifferenceThreshold) break;

                stepVS *= 0.5f;
                hitPositionVS += stepVS * sign(depthDifference);
            }

            return TransformPositionVSToUV(hitPositionVS);
		}

        float2 GetRaycastHitUV(in float3 directionVS, inout float3 hitPositionVS, out float depthDifference, out float rawHitDepth, out uint performedIterations)
        {
            float3 stepVS = directionVS * _RayStep * max(_RayMinStep, hitPositionVS.z);
            float2 hitUV = 0.0f;
            performedIterations = 0;

            [loop]
            for (uint i = 0; i < _RayMaxSteps; i++)
            {
                hitPositionVS += stepVS;
                hitUV = TransformPositionVSToUV(hitPositionVS);

                rawHitDepth = SampleRawDepth(hitUV);
                float linearEyeHitDepth = LinearEyeDepth(rawHitDepth, _ZBufferParams);
                depthDifference = linearEyeHitDepth - hitPositionVS.z;

                if (linearEyeHitDepth + 1.0f > _ProjectionParams.z) continue;

                if (abs(depthDifference) < _HitDepthDifferenceThreshold) break;

                if (depthDifference < 0.0f)
                    return BinarySearchHitUV(stepVS, hitPositionVS, depthDifference, rawHitDepth);

                performedIterations = i;
            }

            return hitUV;
        }

        #define OVERLAY(base, top) lerp(2.0f * base * top, 1.0f - 2.0f * (1.0f - base) * (1.0f - top), step(0.5f, base));

        ENDHLSL

        Pass
        {
            Name "Screen Space Reflections"
            //Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_TARGET
            {
                float3 normalWS = SampleSceneNormals(i.uv);
                float3 normalVS = normalize(mul((float3x3)unity_WorldToCamera, normalWS));

                float rawDepth = SampleRawDepth(i.uv);

                float3 positionVS = ComputeViewSpacePosition(i.uv, rawDepth, UNITY_MATRIX_I_P);
                float3 viewDirectionVS = normalize(positionVS);
                float3 reflectedViewDirectionVS = normalize(reflect(viewDirectionVS, normalVS));

                float3 hitPositionVS = positionVS;
                float depthDifference = 1.0f;
                float rawHitDepth = SampleRawDepth(i.uv);
                uint performedIterations;

                float4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                if (Linear01Depth(rawHitDepth, _ZBufferParams) > 0.9999f) return float4(color.rgb, 0.0f);

                float2 hitUV = GetRaycastHitUV(reflectedViewDirectionVS, hitPositionVS, depthDifference, rawHitDepth, performedIterations);
                float4 hitColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, hitUV);

                #if DEFERRED_GBUFFERS_AVAILABLE
                    BRDFData brdfData = BRDFDataFromGbuffer(SAMPLE_TEXTURE2D(_GBuffer0, sampler_GBuffer0, i.uv),
                                                            SAMPLE_TEXTURE2D(_GBuffer1, sampler_GBuffer1, i.uv),
														    SAMPLE_TEXTURE2D(_GBuffer2, sampler_GBuffer2, i.uv));  
                    const float BOTTOM022 = 0.22f;
                    //grazingTerm is in [0.22, 1] range, we want to map it to [0, 1] range
                    float reflectivity = saturate((brdfData.grazingTerm - BOTTOM022) / (1.0f - BOTTOM022));
                #else
                    float3 dNormalDxy = fwidth(normalWS);
                    float surfaceUniformity = saturate(1.0f - length(dNormalDxy));
                    float luminance = Luminance(color);
                    float reflectivity = OVERLAY(luminance, surfaceUniformity);
                #endif
                //return reflectivity;

                float centerDistance = distance(i.uv, 0.5f);
                float vignette = smoothstep(_VignetteRadius, _VignetteRadius + _VignetteSoftness, centerDistance);
                float reverseVignetteFactor = 1.0f - vignette;

                float viewNormalAlignment = saturate(dot(normalVS, float3(0.0f, 0.0f, -1.0f)));
                float fresnelFactor = sqrt(1.0f - viewNormalAlignment);

                float hitProbability = (
                                        smoothstep(_HitDepthDifferenceThreshold, 0, abs(depthDifference))
                                        #if REFLECT_SKYBOX
                                            + step(0.9999f, abs(Linear01Depth(rawHitDepth, _ZBufferParams)))
                                        #endif
                                        )
                                        * (reflectivity + _ReflectivityBias)
                                        * reverseVignetteFactor
                                        * (fresnelFactor + _FresnelBias)
                                        * _ReflectionIntensity;

                return float4(hitColor.rgb, hitProbability);
            }

            ENDHLSL
        }

        Pass
        {
            Name "Kawase Blur"

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_TARGET
            {
                #define NUM_OFFSETS 4
                static const float2 offsets[NUM_OFFSETS] =
                {
                    float2(-0.5f, 0.5f),
                    float2(0.5f, 0.5f),
                    float2(-0.5f, -0.5f),
                    float2(0.5f, -0.5f),
				};

                float4 avg = 0.0f;

                [unroll]
                for (uint j = 0; j < NUM_OFFSETS; j++)
                    avg += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex,
                                            i.uv + (offsets[j] * (1.0f + 2.0f * _BlurRadius)) * _MainTex_TexelSize.xy)
                                            / NUM_OFFSETS;

                return avg;
            }

            ENDHLSL
        }

        Pass
        {
            Name "Reflections Composite"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_TARGET
            {
	            float4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                return float4(color.rgb, saturate(color.a));
            }

            ENDHLSL
		}
    }
}
