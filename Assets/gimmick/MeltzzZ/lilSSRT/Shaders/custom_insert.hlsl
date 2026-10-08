#define LILSSRT_PI 3.14159265359
#define LILSSRT_TWO_PI (LILSSRT_PI * 2.0)
#define LILSSRT_GOLDEN_ANGLE 2.39996322973
#define LILSSRT_EPSILON 0.0001

float lilSSRT_SafeDenominator(float value)
{
    if(abs(value) >= LILSSRT_EPSILON) return value;
    return value < 0.0 ? -LILSSRT_EPSILON : LILSSRT_EPSILON;
}

#if defined(UNITY_PASS_SHADOWCASTER) || defined(UNITY_PASS_META) || defined(LIL_PASS_SHADOWCASTER_INCLUDED) || defined(LIL_PASS_META_INCLUDED)

void ApplySSRTLighting(inout lilFragData fd)
{
}

void lilSSRT_ApplyAO(inout lilFragData fd, float4 ssrtData)
{
}

#else

struct LilSSRTContext
{
    float2 screenUV;
    float2 screenPositionCS;
    float centerDepth;
    float3 viewNormal;
    float radius;
    float invW;
    float2 csRadius;
    float sampleLoad;
    float jitter;
    float distanceFade;
};

bool lilSSRT_HasDepthTexture()
{
    return LIL_ENABLED_DEPTH_TEX;
}

float lilSSRT_DepthMissingAO()
{
    return 1.0;
}

float lilSSRT_SampleLinearDepthCS(float2 positionCS)
{
    float rawDepth = LIL_GET_DEPTH_TEX_CS(positionCS);
    return LIL_TO_LINEARDEPTH(rawDepth, positionCS);
}

bool lilSSRT_IsMirrorRendering()
{
#if defined(LIL_IS_MIRROR)
    return LIL_IS_MIRROR || _VRChatMirrorMode > 0.5;
#else
    return _VRChatMirrorMode > 0.5;
#endif
}

float lilSSRT_SurfaceLinearDepth(float4 positionCS)
{
    return max(lilSSRT_SampleLinearDepthCS(positionCS.xy), LILSSRT_EPSILON);
}

float2 lilSSRT_GetScreenUV(lilFragData fd)
{
    return saturate(lilCStoGrabUV(fd.positionCS));
}

float lilSSRT_InterleavedNoise(float2 coord)
{
    const float2 noiseBasis = float2(0.06711056, 0.00583715);
    return frac(52.9829189 * frac(dot(coord, noiseBasis)));
}

float lilSSRT_Noise(float2 coord)
{
    return lilSSRT_InterleavedNoise(coord);
}

float lilSSRT_FovScale()
{
    float referenceFov = rcp(max(tan(radians(_LilSSRTAOFOV * 0.5)), LILSSRT_EPSILON));
    float rawFov = abs(LIL_MATRIX_P._m11) / max(referenceFov, LILSSRT_EPSILON);
    return lerp(1.0, clamp(rawFov, 0.1, 10.0), saturate(_LilSSRTAOFOVAdjust));
}

float lilSSRT_DistanceFade(float centerDepth)
{
    if(_LilSSRTAODistanceFadeMax <= _LilSSRTAODistanceFadeMin) return 1.0;
    return 1.0 - smoothstep(_LilSSRTAODistanceFadeMin, _LilSSRTAODistanceFadeMax, centerDepth);
}

bool lilSSRT_UsesDistanceFade()
{
    return _LilSSRTAODistanceFadeMax > _LilSSRTAODistanceFadeMin;
}

float lilSSRT_AdaptiveDistanceDetail(float viewDistance)
{
    if(!lilSSRT_UsesDistanceFade()) return 1.0;

    float fadeRange = max(_LilSSRTAODistanceFadeMax - _LilSSRTAODistanceFadeMin, LILSSRT_EPSILON);
    float detailStart = max(0.0, _LilSSRTAODistanceFadeMin - fadeRange * 1.25);
    float detailEnd = lerp(_LilSSRTAODistanceFadeMin, _LilSSRTAODistanceFadeMax, 0.35);
    return 1.0 - smoothstep(detailStart, detailEnd, viewDistance);
}

float3 lilSSRT_DepthNormal(float2 screenPositionCS, float centerDepth, float radius, float3 fallbackNormal, float useValidation)
{
    float depthR = lilSSRT_SampleLinearDepthCS(screenPositionCS + float2(1.0, 0.0));
    float depthL = lilSSRT_SampleLinearDepthCS(screenPositionCS - float2(1.0, 0.0));
    float depthU = lilSSRT_SampleLinearDepthCS(screenPositionCS + float2(0.0, 1.0));
    float depthD = lilSSRT_SampleLinearDepthCS(screenPositionCS - float2(0.0, 1.0));

    float deltaR = depthR - centerDepth;
    float deltaL = centerDepth - depthL;
    float deltaU = depthU - centerDepth;
    float deltaD = centerDepth - depthD;
    float2 gradient = float2(abs(deltaR) < abs(deltaL) ? deltaR : deltaL, abs(deltaU) < abs(deltaD) ? deltaU : deltaD);
    gradient /= max(radius, LILSSRT_EPSILON);

    float3 depthNormal = normalize(float3(-gradient.x, -gradient.y, 1.0));
    depthNormal = dot(depthNormal, fallbackNormal) < 0.0 ? -depthNormal : depthNormal;

    float maxNeighborDelta = max(max(abs(deltaR), abs(deltaL)), max(abs(deltaU), abs(deltaD)));
    float validationWidth = lerp(radius * 3.0, radius * 1.5, saturate(useValidation));
    float neighborValid = 1.0 - smoothstep(radius * 0.5, validationWidth + max(_LilSSRTSSRTAOThickness, LILSSRT_EPSILON), maxNeighborDelta);
    return normalize(lerp(fallbackNormal, depthNormal, neighborValid));
}

float3 lilSSRT_AONormal(lilFragData fd, float2 screenPositionCS, float centerDepth, float radius)
{
    float3 fallback = normalize(mul((float3x3)UNITY_MATRIX_V, fd.N));
    if(_LilSSRTAOReconstructNormal < 0.5) return fallback;

    float modeBlend = step(0.5, _LilSSRTAOReconstructNormal) * (1.0 - step(1.5, _LilSSRTAOReconstructNormal));
    float modeDepth = step(1.5, _LilSSRTAOReconstructNormal);
    float strength = saturate(_LilSSRTAOReconstructNormalStrength);
    if(modeDepth < 0.5 && strength <= LILSSRT_EPSILON) return fallback;

    float3 depthNormal = lilSSRT_DepthNormal(screenPositionCS, centerDepth, radius, fallback, modeDepth);
    return normalize(lerp(fallback, depthNormal, saturate(modeBlend * strength * 0.6 + modeDepth * 0.8)));
}

void lilSSRT_GetVogelSample(int index, int count, float jitter, out float radius, out float angle)
{
    float t = ((float)index + 0.5) / max((float)count, 1.0);
    radius = sqrt(t);
    angle = (float)index * LILSSRT_GOLDEN_ANGLE + jitter * LILSSRT_TWO_PI;
}

void lilSSRT_BuildOrthonormalBasis(float3 normal, out float3 tangent, out float3 bitangent)
{
    float3 helperAxis = lerp(float3(0.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), step(0.999, abs(normal.y)));
    tangent = normalize(cross(helperAxis, normal));
    bitangent = normalize(cross(normal, tangent));
}

float lilSSRT_AOScreenLoad(float invW, float primaryCount, float stepCount)
{
    float referenceFov = rcp(max(tan(radians(_LilSSRTAOFOV * 0.5)), LILSSRT_EPSILON));
    float rawFov = abs(LIL_MATRIX_P._m11) / max(referenceFov, LILSSRT_EPSILON);
    float fovFactor = lerp(1.0, clamp(rawFov, 0.1, 10.0), saturate(_LilSSRTAOFOVAdjust));
    float projectedRadiusPx = _LilSSRTAORadius * invW * fovFactor * min(LIL_SCREENPARAMS.x, LIL_SCREENPARAMS.y);
    float load = smoothstep(180.0, 580.0, projectedRadiusPx);
    return load * load;
}

float lilSSRT_AOMinRadiusFade(float invW)
{
    float projectedRadiusPx = _LilSSRTAORadius * invW * lilSSRT_FovScale() * min(LIL_SCREENPARAMS.x, LIL_SCREENPARAMS.y);
    return smoothstep(0.0, 0.5, projectedRadiusPx);
}

float lilSSRT_AOSameSurfaceWeight(float delta, float radius, float centerDepth)
{
    float absDelta = abs(delta);
    float depthScaledBias = centerDepth * 0.00035;
    float radiusScaledBias = radius * 0.005;
    float thicknessScaledBias = max(_LilSSRTSSRTAOThickness, LILSSRT_EPSILON) * 0.20;
    float minDelta = max(max(depthScaledBias, radiusScaledBias), thicknessScaledBias);
    return smoothstep(minDelta, minDelta * 3.0 + LILSSRT_EPSILON, absDelta);
}

float lilSSRT_SkipNoise(float2 screenUV, float blockSize, float salt)
{
    float2 pixel = floor(screenUV * LIL_SCREENPARAMS.xy);
    float2 block = floor(pixel / max(blockSize, 1.0));
    return lilSSRT_InterleavedNoise(block + salt);
}

float lilSSRT_CompleteSkipRate(float sampleLoad)
{
    return smoothstep(0.75, 1.0, sampleLoad) * 0.10;
}

float lilSSRT_PrimarySkipRate(float sampleLoad, float primaryCount)
{
    float countRoom = saturate((primaryCount - 2.0) * 0.5);
    return sampleLoad * countRoom * 0.20;
}

float lilSSRT_StepSkipRate(float sampleLoad, float stepCount, float stepT, float fastMode)
{
    float stepRoom = saturate((stepCount - 2.0) * 0.5);
    float farStepWeight = smoothstep(0.4, 1.0, stepT);
    return sampleLoad * stepRoom * farStepWeight * 0.3;
}

bool lilSSRT_ShouldSkipAOPrimary(float2 screenUV, int primaryIndex, int primaryCount, float sampleLoad, float salt)
{
    if(primaryCount <= 2) return false;
    if(primaryIndex == 0 || primaryIndex == max(1, primaryCount / 2)) return false;

    float rate = lilSSRT_PrimarySkipRate(sampleLoad, (float)primaryCount);
    float noise = lilSSRT_SkipNoise(screenUV, 2.0, salt + (float)primaryIndex * 19.17);
    return noise < rate;
}

bool lilSSRT_ShouldSkipAOStep(float2 screenUV, int primaryIndex, int stepIndex, int stepCount, float stepT, float sampleLoad, float salt, float fastMode)
{
    if(stepCount <= 2 || stepIndex <= 2) return false;

    float rate = lilSSRT_StepSkipRate(sampleLoad, (float)stepCount, stepT, fastMode);
    float noise = lilSSRT_SkipNoise(screenUV, 4.0, salt + (float)primaryIndex * 17.13 + (float)stepIndex * 37.71);
    return noise < rate;
}

float lilSSRT_DepthReject(float delta, float radius)
{
    float thickness = max(_LilSSRTSSRTAOThickness, LILSSRT_EPSILON);
    float hitSpread = saturate(_LilSSRTAOHitSpread);
    float spreadThickness = thickness * (1.0 + hitSpread * 6.0) + radius * hitSpread * 0.25;

    if(_LilSSRTAODepthReject <= LILSSRT_EPSILON)
    {
        return 1.0 - smoothstep(radius, radius + spreadThickness, delta);
    }

    if(_LilSSRTAODepthReject >= 1.0 - LILSSRT_EPSILON)
    {
        return 1.0 - smoothstep(radius * lerp(0.45, 0.65, hitSpread), radius + spreadThickness * 0.5, delta);
    }

    float softWeight = 1.0 - smoothstep(radius, radius + spreadThickness, delta);
    float strictWeight = 1.0 - smoothstep(radius * lerp(0.45, 0.65, hitSpread), radius + spreadThickness * 0.5, delta);
    return lerp(softWeight, strictWeight, saturate(_LilSSRTAODepthReject));
}

float lilSSRT_HitSpreadWeight(float hitT, float stepT)
{
    float hitSpread = saturate(_LilSSRTAOHitSpread);
    float width = lerp(20.0, 3.0, hitSpread);
    return rcp(1.0 + abs(stepT - hitT) * width);
}

float lilSSRT_FastDepthReject(float delta, float radius)
{
    float hitSpread = saturate(_LilSSRTAOHitSpread);
    float thickness = max(_LilSSRTSSRTAOThickness, LILSSRT_EPSILON);
    float softRange = radius + thickness * lerp(2.0, 7.0, hitSpread);
    float reject = saturate(delta / max(softRange, LILSSRT_EPSILON));
    return lerp(1.0 - reject, saturate(1.0 - reject * reject), 1.0 - saturate(_LilSSRTAODepthReject));
}

float lilSSRT_AOContactWindow(float delta, float radius, float3 viewNormal)
{
    if(!lilSSRT_IsMirrorRendering()) return 1.0;

    float hitSpread = saturate(_LilSSRTAOHitSpread);
    float thickness = max(_LilSSRTSSRTAOThickness, LILSSRT_EPSILON);
    float grazing = 1.0 - saturate(abs(viewNormal.z));
    float nearLimit = max(thickness * 0.25, radius * 0.01);
    float farLimit = radius * lerp(0.35, 0.75, hitSpread) + thickness * lerp(0.75, 1.75, hitSpread);
    farLimit *= lerp(1.0, 0.45, grazing * grazing);
    float lower = smoothstep(nearLimit, nearLimit * 3.0 + LILSSRT_EPSILON, delta);
    float upper = 1.0 - smoothstep(farLimit, farLimit * 1.6 + LILSSRT_EPSILON, delta);
    return lower * upper;
}

float lilSSRT_DistanceWeight(float stepT)
{
    const float falloff = 0.8;
    float linearWeight = saturate(1.0 - stepT);
    float softWeight = linearWeight * (2.0 - linearWeight);
    return lerp(linearWeight * linearWeight, softWeight, falloff);
}

float2 lilSSRT_SpreadGTADirection(float2 dir, float2 bentNormal, float normalScreenWeight)
{
    float spread = saturate(_LilSSRTAOSpread) * saturate(normalScreenWeight);
    float normalSide = dot(bentNormal, dir) < 0.0 ? -1.0 : 1.0;
    float2 spreadTarget = bentNormal * normalSide;
    float2 spreadDir = lerp(dir, spreadTarget, spread);
    return normalize(spreadDir + dir * LILSSRT_EPSILON);
}

float lilSSRT_ContactBoost(float primaryCount, float stepCount)
{
    float sparsePrimary = saturate((4.0 - primaryCount) * 0.25);
    float sparseStep = saturate((4.0 - stepCount) * 0.25);
    return 1.0 + (sparsePrimary + sparseStep) * 0.35 + saturate(_LilSSRTAOHitSpread) * 0.35;
}

float lilSSRT_AdaptiveSampleRadius(float sampleRadius, float sampleLoad)
{
    float loadBias = lerp(1.0, 0.78, sampleLoad);
    float radius = saturate(sampleRadius);
    float curvedRadius = lerp(sqrt(radius), radius, 0.35);
    return curvedRadius * loadBias;
}

float lilSSRT_LightweightAOWeight(float delta, float radius, float sampleRadius, float depthBias, float centerDepth)
{
    float thickness = max(_LilSSRTSSRTAOThickness, LILSSRT_EPSILON);
    float biasedDelta = delta - depthBias;
    float sameSurfaceWeight = 1.0;
    float nearT = saturate(biasedDelta / max(radius * 0.35, LILSSRT_EPSILON));
    float nearWeight = nearT * nearT * (3.0 - 2.0 * nearT);
    float contactRange = radius * lerp(0.28, 0.55, saturate(_LilSSRTAOHitSpread)) + thickness;
    float farT = saturate(biasedDelta / max(contactRange, LILSSRT_EPSILON));
    float farWeight = 1.0 - farT * farT * (3.0 - 2.0 * farT);
    float distanceWeight = lerp(0.55, 1.0, 1.0 - sampleRadius);
    return nearWeight * farWeight * distanceWeight * sameSurfaceWeight;
}

LilSSRTContext lilSSRT_CreateAOContext(lilFragData fd, float4 ssrtData, float primaryCount, float stepCount)
{
    LilSSRTContext ctx;
    ctx.invW = rcp(lilSSRT_SafeDenominator(fd.positionCS.w));
    ctx.screenUV = lilSSRT_GetScreenUV(fd);
    ctx.screenPositionCS = fd.positionCS.xy;
    ctx.centerDepth = lilSSRT_SurfaceLinearDepth(fd.positionCS);
    ctx.radius = max(_LilSSRTAORadius, LILSSRT_EPSILON);
    ctx.viewNormal = lilSSRT_AONormal(fd, ctx.screenPositionCS, ctx.centerDepth, ctx.radius);
    ctx.csRadius = max(ctx.radius * ctx.invW * lilSSRT_FovScale(), LILSSRT_EPSILON) * LIL_SCREENPARAMS.xy;
    ctx.sampleLoad = lilSSRT_AOScreenLoad(ctx.invW, primaryCount, stepCount);
    ctx.distanceFade = lilSSRT_DistanceFade(length(ssrtData.xyz));

    float2 noiseCoord = ctx.screenUV * LIL_SCREENPARAMS.xy;
    ctx.jitter = (lilSSRT_Noise(noiseCoord) * 2.0 - 1.0) * _LilSSRTAOJitter * (1.0 - saturate(_LilSSRTAONoiseStability) * 0.85);
    return ctx;
}

float lilSSRT_FinalAO(float occlusion, LilSSRTContext ctx, float calibration, float algorithmIntensity)
{
    occlusion = saturate(occlusion * calibration * algorithmIntensity);
    occlusion *= ctx.distanceFade;
    return 1.0 - occlusion;
}

float lilSSRT_AlgorithmIntensity()
{
    return saturate(_LilSSRTSSRTAOIntensity);
}

float lilSSRT_AOAlgorithmStrengthScale()
{
    if(_LilSSRTAOAlgorithm < 0.5) return 5.0;
    if(_LilSSRTAOAlgorithm < 1.5) return 4.0;
    if(_LilSSRTAOAlgorithm < 2.5) return 5.0;
    return 4.0;
}

float lilSSRT_AOOcclusionDeadZone()
{
    if(_LilSSRTAOAlgorithm < 2.5) return 0.01;
    return 0.002;
}

float lilSSRT_ApplyAOContrast(float occlusion)
{
    occlusion = saturate(occlusion);
    float contrast = saturate(_LilSSRTAOContrast * 0.5) * 2.0;
    float softened = sqrt(occlusion);
    float sharpened = occlusion * occlusion * (3.0 - 2.0 * occlusion);
    float contrasted = contrast < 1.0 ? lerp(softened, occlusion, contrast) : lerp(occlusion, sharpened, contrast - 1.0);
    float lowOcclusionBlend = smoothstep(0.0, 0.05, occlusion);
    return lerp(occlusion, contrasted, lowOcclusionBlend);
}

float lilSSRT_DepthDelta(float centerDepth, float sampleDepth)
{
    return centerDepth - sampleDepth;
}

float lilSSRT_SampleDepthAlongRay(float sampleDepth, float rayZ, float radius, float sampleT)
{
    return sampleDepth;
}

float lilSSRT_DepthBias()
{
    return saturate(_LilSSRTAODepthBias) * max(_LilSSRTAORadius, LILSSRT_EPSILON);
}

float2 lilSSRT_AONoiseCoord(float2 screenUV, float2 mainUV)
{
    return screenUV * LIL_SCREENPARAMS.xy;
}

float lilSSRT_DebugDepthValue(float depth)
{
    float tone = 1.0 - smoothstep(0.05, 2.0, depth);
    tone = pow(saturate(tone), 1.35);
    tone = lerp(0.16, 0.74, tone);
    tone = (tone - 0.45) * 2.1 + 0.45;
    return saturate(tone);
}

float lilSSRT_DebugDepth(float2 positionCS)
{
    return lilSSRT_DebugDepthValue(lilSSRT_SampleLinearDepthCS(positionCS));
}

float lilSSRT_ReadMaskFactor(float2 uv)
{
    float sampledMask = LIL_SAMPLE_2D(_LilSSRTAOMask, lil_sampler_linear_repeat, uv).r;
    return _LilSSRTAOInvertMask > 0.5 ? 1.0 - sampledMask : sampledMask;
}

float lilSSRT_EvaluateSSRTAO(lilFragData fd, float4 ssrtData)
{
    if(!lilSSRT_HasDepthTexture()) return lilSSRT_DepthMissingAO();

    int rayCount = min(8, max(2, (int)max(2.0, _LilSSRTSSRTAORays)));
    int stepCount = min(12, max(2, (int)max(2.0, _LilSSRTSSRTAOSteps)));
    LilSSRTContext ctx = lilSSRT_CreateAOContext(fd, ssrtData, (float)rayCount, (float)stepCount);

    float invRadius = rcp(ctx.radius);
    const float falloff = 0.8;
    float ao = 0.0;
    float validRays = 0.0;
    float3 tangent;
    float3 bitangent;
    lilSSRT_BuildOrthonormalBasis(ctx.viewNormal, tangent, bitangent);

    [loop]
    for(int ray = 0; ray < 8; ray++)
    {
        if(ray >= rayCount) break;
        if(lilSSRT_ShouldSkipAOPrimary(ctx.screenUV, ray, rayCount, ctx.sampleLoad, 11.73)) continue;

        validRays += 1.0;

        float rayRadius;
        float angle;
        lilSSRT_GetVogelSample(ray, rayCount, ctx.jitter, rayRadius, angle);

        float s;
        float c;
        sincos(angle, s, c);
        float3 rayDir = normalize(lerp(tangent * c + bitangent * s, ctx.viewNormal, _LilSSRTAOSpread));

        float rayOcclusion = 0.0;
        float bestHit = 0.0;
        float bestHitT = 0.0;
        float sumHit = 0.0;
        float validSteps = 0.0;
        float spreadHit = 0.0;

        [loop]
        for(int stepIndex = 1; stepIndex <= 12; stepIndex++)
        {
            if(stepIndex > stepCount) break;

            float stepT = (float)stepIndex / (float)stepCount;
            float scaledT = stepT * max(rayRadius, LILSSRT_EPSILON);
            float2 samplePositionCS = ctx.screenPositionCS + rayDir.xy * ctx.csRadius * scaledT;
            if(any(samplePositionCS < 0.0) || any(samplePositionCS > LIL_SCREENPARAMS.xy)) break;
            if(lilSSRT_ShouldSkipAOStep(ctx.screenUV, ray, stepIndex, stepCount, stepT, ctx.sampleLoad, 23.41, 0.0)) continue;

            float sampleDepth = lilSSRT_SampleLinearDepthCS(samplePositionCS);
            sampleDepth = lilSSRT_SampleDepthAlongRay(sampleDepth, rayDir.z, ctx.radius, scaledT);
            float delta = lilSSRT_DepthDelta(ctx.centerDepth, sampleDepth) - lilSSRT_DepthBias();
            float frontHit = step(0.0, delta);
            float depthReject = lilSSRT_DepthReject(delta, ctx.radius);
            float linearWeight = saturate(1.0 - stepT);
            float softWeight = linearWeight * (2.0 - linearWeight);
            float rangeWeight = lerp(linearWeight * linearWeight, softWeight, falloff);
            float sameSurfaceWeight = lilSSRT_AOSameSurfaceWeight(delta, ctx.radius, ctx.centerDepth);
            float contactWindow = lilSSRT_AOContactWindow(delta, ctx.radius, ctx.viewNormal);
            float hitAmount = saturate(delta * invRadius) * frontHit * depthReject * rangeWeight * sameSurfaceWeight * contactWindow;

            if(hitAmount > bestHit)
            {
                bestHit = hitAmount;
                bestHitT = stepT;
            }

            if(_LilSSRTAOHitSpread > LILSSRT_EPSILON)
            {
                spreadHit += hitAmount * lilSSRT_HitSpreadWeight(bestHitT, stepT);
            }

            rayOcclusion = max(rayOcclusion, hitAmount);
            sumHit += hitAmount;
            validSteps += 1.0;

            if(rayOcclusion > 0.98 && _LilSSRTSSRTAOHitMode > 0.5) break;
        }

        if(_LilSSRTAOHitSpread > LILSSRT_EPSILON)
        {
            float spreadAverage = spreadHit / max(validSteps * 0.35, 1.0);
            rayOcclusion = lerp(rayOcclusion, saturate(max(rayOcclusion, spreadAverage)), saturate(_LilSSRTAOHitSpread));
        }

        float averageHit = sumHit / max(validSteps, 1.0);
        float maxHitWeight = lerp(0.25, 0.65, step(0.5, _LilSSRTSSRTAOHitMode));
        rayOcclusion = lerp(averageHit, rayOcclusion, maxHitWeight);
        ao += rayOcclusion;
    }

    ao = 1.0 - saturate((ao / max(validRays, 1.0)) * lilSSRT_AlgorithmIntensity() * 0.62);
    return ao;
}

float lilSSRT_EvaluateFastSSRTAO(lilFragData fd, float4 ssrtData)
{
    if(!lilSSRT_HasDepthTexture()) return lilSSRT_DepthMissingAO();

    int rayCount = min(8, max(2, (int)max(2.0, _LilSSRTSSRTAORays)));
    int configuredSteps = min(12, max(2, (int)max(2.0, _LilSSRTSSRTAOSteps)));
    int stepCount = min(configuredSteps, max(2, (int)ceil((float)configuredSteps * 0.6)));
    LilSSRTContext ctx = lilSSRT_CreateAOContext(fd, ssrtData, (float)rayCount, (float)configuredSteps);

    float invRadius = rcp(ctx.radius);
    float contactBoost = lilSSRT_ContactBoost((float)rayCount, (float)configuredSteps) * ((float)configuredSteps / (float)stepCount);
    float ao = 0.0;
    float validRays = 0.0;
    float3 tangent;
    float3 bitangent;
    lilSSRT_BuildOrthonormalBasis(ctx.viewNormal, tangent, bitangent);

    [loop]
    for(int ray = 0; ray < 8; ray++)
    {
        if(ray >= rayCount) break;
        if(lilSSRT_ShouldSkipAOPrimary(ctx.screenUV, ray, rayCount, ctx.sampleLoad, 11.73)) continue;

        validRays += 1.0;

        float rayRadius;
        float angle;
        lilSSRT_GetVogelSample(ray, rayCount, ctx.jitter, rayRadius, angle);

        float s;
        float c;
        sincos(angle, s, c);
        float3 rayDir = normalize(lerp(tangent * c + bitangent * s, ctx.viewNormal, _LilSSRTAOSpread));
        float rayOcclusion = 0.0;
        float sumHit = 0.0;
        float validSteps = 0.0;

        [loop]
        for(int stepIndex = 1; stepIndex <= 8; stepIndex++)
        {
            if(stepIndex > stepCount) break;

            float stepT = (float)stepIndex / (float)stepCount;
            float scaledT = stepT * lerp(1.0, max(rayRadius, LILSSRT_EPSILON), 0.65);
            float2 samplePositionCS = ctx.screenPositionCS + rayDir.xy * ctx.csRadius * scaledT;
            if(any(samplePositionCS < 0.0) || any(samplePositionCS > LIL_SCREENPARAMS.xy)) break;
            if(lilSSRT_ShouldSkipAOStep(ctx.screenUV, ray, stepIndex, stepCount, stepT, ctx.sampleLoad, 23.41, 1.0)) continue;

            float sampleDepth = lilSSRT_SampleLinearDepthCS(samplePositionCS);
            sampleDepth = lilSSRT_SampleDepthAlongRay(sampleDepth, rayDir.z, ctx.radius, scaledT);
            float delta = lilSSRT_DepthDelta(ctx.centerDepth, sampleDepth) - lilSSRT_DepthBias();
            float sameSurfaceWeight = lilSSRT_AOSameSurfaceWeight(delta, ctx.radius, ctx.centerDepth);
            float contactWindow = lilSSRT_AOContactWindow(delta, ctx.radius, ctx.viewNormal);
            float hitAmount = saturate(delta * invRadius * contactBoost);
            hitAmount *= step(0.0, delta) * lilSSRT_FastDepthReject(delta, ctx.radius) * lilSSRT_DistanceWeight(stepT) * sameSurfaceWeight * contactWindow;

            rayOcclusion = max(rayOcclusion, hitAmount);
            sumHit += hitAmount;
            validSteps += 1.0;

            if(rayOcclusion > 0.92 && _LilSSRTSSRTAOHitMode > 0.5) break;
        }

        float averageHit = sumHit / max(validSteps, 1.0);
        float stableWeight = lerp(0.65, 0.35, step(0.5, _LilSSRTSSRTAOHitMode));
        rayOcclusion = lerp(rayOcclusion, averageHit, stableWeight);
        ao += rayOcclusion;
    }

    ao = 1.0 - saturate((ao / max(validRays, 1.0)) * lilSSRT_AlgorithmIntensity() * 0.58);
    return ao;
}

float lilSSRT_EvaluateXeGTAO(lilFragData fd, float4 ssrtData)
{
    if(!lilSSRT_HasDepthTexture()) return lilSSRT_DepthMissingAO();

    int sliceCount = min(10, max(2, (int)max(2.0, _LilSSRTXeGTAOSlices)));
    int stepCount = min(12, max(2, (int)max(2.0, _LilSSRTSSRTAOSteps)));
    LilSSRTContext ctx = lilSSRT_CreateAOContext(fd, ssrtData, (float)sliceCount, (float)stepCount);

    float invRadius = rcp(ctx.radius);
    float2 normalXY = ctx.viewNormal.xy;
    float normalXYLen = max(length(normalXY), LILSSRT_EPSILON);
    float2 bentNormal = normalXY / normalXYLen;
    float normalScreenWeight = saturate(normalXYLen);
    const float falloff = 0.8;
    float2 noiseCoord = ctx.screenUV * LIL_SCREENPARAMS.xy;
    float noise = lilSSRT_Noise(noiseCoord);
    float jitterAmount = _LilSSRTAOJitter * (1.0 - saturate(_LilSSRTAONoiseStability) * 0.85);
    float jitter = (noise - 0.5) * jitterAmount;
    float ao = 0.0;
    float validSlices = 0.0;

    [loop]
    for(int slice = 0; slice < 10; slice++)
    {
        if(slice >= sliceCount) break;
        if(lilSSRT_ShouldSkipAOPrimary(ctx.screenUV, slice, sliceCount, ctx.sampleLoad, 31.37)) continue;

        validSlices += 1.0;

        float angle = ((float)slice + jitter) * LILSSRT_TWO_PI / (float)sliceCount;
        float s;
        float c;
        sincos(angle, s, c);
        float2 sliceDir = float2(c, s);
        float sliceStepJitter = (frac(noise + (float)slice * 0.61803398875) - 0.5) * saturate(jitterAmount);

        float sliceOcclusion = 0.0;

        [loop]
        for(int side = 0; side < 2; side++)
        {
            float sideSign = side == 0 ? -1.0 : 1.0;
            float2 dir = sliceDir * sideSign;
            float2 sampleDir = lilSSRT_SpreadGTADirection(dir, bentNormal, normalScreenWeight);
            float horizon = 0.0;
            float bestHorizon = 0.0;
            float bestHorizonT = 0.0;
            float sumHorizon = 0.0;
            float validSteps = 0.0;
            float spreadHorizon = 0.0;
            float normalWeight = lerp(1.0, saturate(dot(bentNormal, sampleDir) * 0.5 + 0.5), normalScreenWeight);

            [loop]
            for(int stepIndex = 1; stepIndex <= 12; stepIndex++)
            {
                if(stepIndex > stepCount) break;

                float stepT = saturate(((float)stepIndex + sliceStepJitter) / (float)stepCount);
                float2 samplePositionCS = ctx.screenPositionCS + sampleDir * ctx.csRadius * stepT;
                if(any(samplePositionCS < 0.0) || any(samplePositionCS > LIL_SCREENPARAMS.xy)) break;

                int primaryIndex = slice * 2 + side;
                if(lilSSRT_ShouldSkipAOStep(ctx.screenUV, primaryIndex, stepIndex, stepCount, stepT, ctx.sampleLoad, 67.19, 0.0)) continue;

                float sampleDepth = lilSSRT_SampleLinearDepthCS(samplePositionCS);
                sampleDepth = lilSSRT_SampleDepthAlongRay(sampleDepth, ctx.viewNormal.z * normalWeight, ctx.radius, stepT);
                float delta = lilSSRT_DepthDelta(ctx.centerDepth, sampleDepth) - lilSSRT_DepthBias();
                float frontHit = step(0.0, delta);
                float depthReject = lilSSRT_DepthReject(delta, ctx.radius);
                float linearWeight = saturate(1.0 - stepT);
                float softWeight = linearWeight * (2.0 - linearWeight);
                float distanceWeight = lerp(linearWeight * linearWeight, softWeight, falloff);
                float sameSurfaceWeight = lilSSRT_AOSameSurfaceWeight(delta, ctx.radius, ctx.centerDepth);
                float contactWindow = lilSSRT_AOContactWindow(delta, ctx.radius, ctx.viewNormal);
                float hitAmount = saturate(delta * invRadius) * frontHit * depthReject * distanceWeight * normalWeight * sameSurfaceWeight * contactWindow;

                if(hitAmount > bestHorizon)
                {
                    bestHorizon = hitAmount;
                    bestHorizonT = stepT;
                }

                if(_LilSSRTAOHitSpread > LILSSRT_EPSILON)
                {
                    spreadHorizon += hitAmount * lilSSRT_HitSpreadWeight(bestHorizonT, stepT);
                }

                horizon = max(horizon, hitAmount);
                sumHorizon += hitAmount;
                validSteps += 1.0;

                if(horizon > 0.98 && _LilSSRTSSRTAOHitMode > 0.5) break;
            }

            if(_LilSSRTAOHitSpread > LILSSRT_EPSILON)
            {
                float spreadAverage = spreadHorizon / max(validSteps * 0.35, 1.0);
                horizon = lerp(horizon, saturate(max(horizon, spreadAverage)), saturate(_LilSSRTAOHitSpread));
            }

            float averageHorizon = sumHorizon / max(validSteps, 1.0);
            float maxHitWeight = lerp(0.25, 0.65, step(0.5, _LilSSRTSSRTAOHitMode));
            horizon = lerp(averageHorizon, horizon, maxHitWeight);
            sliceOcclusion += horizon;
        }

        ao += sliceOcclusion * 0.5;
    }

    ao = 1.0 - saturate((ao / max(validSlices, 1.0)) * lilSSRT_AlgorithmIntensity() * 0.58);
    return ao;
}

float lilSSRT_EvaluateFastXeGTAO(lilFragData fd, float4 ssrtData)
{
    if(!lilSSRT_HasDepthTexture()) return lilSSRT_DepthMissingAO();

    int sliceCount = min(10, max(2, (int)max(2.0, _LilSSRTXeGTAOSlices)));
    int configuredSteps = min(12, max(2, (int)max(2.0, _LilSSRTSSRTAOSteps)));
    int stepCount = min(configuredSteps, max(2, (int)ceil((float)configuredSteps * 0.6)));
    LilSSRTContext ctx = lilSSRT_CreateAOContext(fd, ssrtData, (float)sliceCount, (float)configuredSteps);

    float invRadius = rcp(ctx.radius);
    float2 normalXY = ctx.viewNormal.xy;
    float normalScreenWeight = saturate(length(normalXY));
    float2 bentNormal = normalXY / max(length(normalXY), LILSSRT_EPSILON);
    float2 noiseCoord = ctx.screenUV * LIL_SCREENPARAMS.xy;
    float noise = lilSSRT_Noise(noiseCoord);
    float jitterAmount = _LilSSRTAOJitter * (1.0 - saturate(_LilSSRTAONoiseStability) * 0.7);
    float jitter = (noise - 0.5) * jitterAmount;
    float stepCompensation = sqrt((float)configuredSteps / (float)stepCount);
    float contactBoost = lilSSRT_ContactBoost((float)sliceCount, (float)configuredSteps) * stepCompensation;
    float ao = 0.0;
    float validSlices = 0.0;

    [loop]
    for(int slice = 0; slice < 10; slice++)
    {
        if(slice >= sliceCount) break;
        if(lilSSRT_ShouldSkipAOPrimary(ctx.screenUV, slice, sliceCount, ctx.sampleLoad, 31.37)) continue;

        validSlices += 1.0;

        float angle = ((float)slice + jitter) * LILSSRT_TWO_PI / (float)sliceCount;
        float s;
        float c;
        sincos(angle, s, c);
        float2 sliceDir = float2(c, s);
        float sliceJitter = (frac(noise + (float)slice * 0.61803398875) - 0.5) * saturate(jitterAmount);
        float sideSign = slice - (slice / 2) * 2 == 0 ? -1.0 : 1.0;
        float2 dir = sliceDir * sideSign;
        float2 sampleDir = lilSSRT_SpreadGTADirection(dir, bentNormal, normalScreenWeight);
        float horizon = 0.0;
        float sumHorizon = 0.0;
        float validSteps = 0.0;
        float normalWeight = lerp(1.0, saturate(dot(bentNormal, sampleDir) * 0.5 + 0.5), normalScreenWeight * 0.5);

        [loop]
        for(int stepIndex = 1; stepIndex <= 8; stepIndex++)
        {
            if(stepIndex > stepCount) break;

            float stepT = saturate(((float)stepIndex + sliceJitter) / (float)stepCount);
            float2 samplePositionCS = ctx.screenPositionCS + sampleDir * ctx.csRadius * stepT;
            if(any(samplePositionCS < 0.0) || any(samplePositionCS > LIL_SCREENPARAMS.xy)) break;
            if(lilSSRT_ShouldSkipAOStep(ctx.screenUV, slice, stepIndex, stepCount, stepT, ctx.sampleLoad, 43.89, 1.0)) continue;

            float sampleDepth = lilSSRT_SampleLinearDepthCS(samplePositionCS);
            sampleDepth = lilSSRT_SampleDepthAlongRay(sampleDepth, ctx.viewNormal.z * normalWeight, ctx.radius, stepT);
            float delta = lilSSRT_DepthDelta(ctx.centerDepth, sampleDepth) - lilSSRT_DepthBias();
            float sameSurfaceWeight = lilSSRT_AOSameSurfaceWeight(delta, ctx.radius, ctx.centerDepth);
            float contactWindow = lilSSRT_AOContactWindow(delta, ctx.radius, ctx.viewNormal);
            float hitAmount = saturate(delta * invRadius * contactBoost);
            hitAmount *= step(0.0, delta) * lilSSRT_FastDepthReject(delta, ctx.radius) * lilSSRT_DistanceWeight(stepT) * normalWeight * sameSurfaceWeight * contactWindow;

            horizon = max(horizon, hitAmount);
            sumHorizon += hitAmount;
            validSteps += 1.0;

            if(horizon > 0.92 && _LilSSRTSSRTAOHitMode > 0.5) break;
        }

        float averageHorizon = sumHorizon / max(validSteps, 1.0);
        float stableWeight = lerp(0.65, 0.35, step(0.5, _LilSSRTSSRTAOHitMode));
        horizon = lerp(horizon, averageHorizon, stableWeight);
        float contrastHorizon = horizon * horizon * (3.0 - 2.0 * horizon);
        horizon = saturate(lerp(horizon, contrastHorizon, 0.45) * 1.2);
        ao += horizon;
    }

    ao = 1.0 - saturate((ao / max(validSlices, 1.0)) * lilSSRT_AlgorithmIntensity() * 0.35);
    return ao;
}

float3 lilSSRT_FakeBounceNormal(float3 normalWS)
{
    return normalize(mul((float3x3)unity_WorldToObject, normalWS));
}

float3 lilSSRT_SampleFakeBounce(float3 normalFB)
{
    if(_FakeBounceLight < 0.5 || _FakeBounceStrength <= LILSSRT_EPSILON) return 0.0;

    float power = max(_FakeBouncePower, LILSSRT_EPSILON);
    float blur = max(_FakeBounceBlur, 0.0);
    float diagonal = 0.70710678;

    float3 dirDown = float3(0.0, -1.0, 0.0);
    float3 dirRight = float3(diagonal, diagonal, 0.0);
    float3 dirLeft = float3(-diagonal, diagonal, 0.0);
    float3 dirFront = float3(0.0, diagonal, diagonal);
    float3 dirBack = float3(0.0, diagonal, -diagonal);

    float weightDown = pow(saturate(dot(normalFB, dirDown)), power);
    float weightRight = pow(saturate(dot(normalFB, dirRight)), power);
    float weightLeft = pow(saturate(dot(normalFB, dirLeft)), power);
    float weightFront = pow(saturate(dot(normalFB, dirFront)), power);
    float weightBack = pow(saturate(dot(normalFB, dirBack)), power);
    float weightSum = weightDown + weightRight + weightLeft + weightFront + weightBack;
    if(weightSum <= LILSSRT_EPSILON) return 0.0;

    float2 uvDown = saturate(float2(normalFB.x, normalFB.z) * 0.5 + 0.5);
    float2 uvRight = saturate(float2(-normalFB.z, dot(normalFB, float3(-diagonal, diagonal, 0.0))) * 0.5 + 0.5);
    float2 uvLeft = saturate(float2(normalFB.z, dot(normalFB, float3(diagonal, diagonal, 0.0))) * 0.5 + 0.5);
    float2 uvFront = saturate(float2(normalFB.x, dot(normalFB, float3(0.0, diagonal, -diagonal))) * 0.5 + 0.5);
    float2 uvBack = saturate(float2(-normalFB.x, dot(normalFB, float3(0.0, diagonal, diagonal))) * 0.5 + 0.5);

    float3 light = 0.0;
    light += _FakeBounceTexDown.SampleLevel(lil_sampler_linear_repeat, uvDown, blur).rgb * weightDown;
    light += _FakeBounceTexRight.SampleLevel(lil_sampler_linear_repeat, uvRight, blur).rgb * weightRight;
    light += _FakeBounceTexLeft.SampleLevel(lil_sampler_linear_repeat, uvLeft, blur).rgb * weightLeft;
    light += _FakeBounceTexFront.SampleLevel(lil_sampler_linear_repeat, uvFront, blur).rgb * weightFront;
    light += _FakeBounceTexBack.SampleLevel(lil_sampler_linear_repeat, uvBack, blur).rgb * weightBack;
    return max(0.0, light / weightSum) * _FakeBounceColor.rgb * _FakeBounceColor.a * _FakeBounceStrength;
}

void ApplySSRTLighting(inout lilFragData fd)
{
    float3 bounceLight = lilSSRT_SampleFakeBounce(lilSSRT_FakeBounceNormal(normalize(fd.N)));
    if(dot(bounceLight, bounceLight) <= LILSSRT_EPSILON) return;

    float mask = LIL_SAMPLE_2D(_FakeBounceMask, lil_sampler_linear_repeat, fd.uvMain).r;
    float maskFactor = _FakeBounceInvertMask > 0.5 ? 1.0 - mask : mask;
    if(maskFactor <= LILSSRT_EPSILON) return;

    float3 baseColor = fd.col.rgb;
    float3 headroom = saturate(1.0 - baseColor);
    float3 result = baseColor + (_FakeBouncePreventBlowout > 0.5 ? min(bounceLight, headroom) : bounceLight);

    if(_FakeBounceBlendMode > 0.5 && _FakeBounceBlendMode < 1.5)
    {
        result = 1.0 - (1.0 - baseColor) * (1.0 - saturate(bounceLight));
    }
    else if(_FakeBounceBlendMode > 1.5 && _FakeBounceBlendMode < 2.5)
    {
        result = baseColor * max(bounceLight, 0.0);
    }
    else if(_FakeBounceBlendMode > 2.5 && _FakeBounceBlendMode < 3.5)
    {
        result = bounceLight;
    }
    else if(_FakeBounceBlendMode > 3.5)
    {
        float3 boost = baseColor * max(bounceLight, 0.0) * 2.0;
        result = baseColor + (_FakeBouncePreventBlowout > 0.5 ? min(boost, headroom) : boost);
    }

    fd.col.rgb = lerp(baseColor, result, saturate(maskFactor));
}

void lilSSRT_ApplyAO(inout lilFragData fd, float4 ssrtData)
{
#if defined(UNITY_PASS_SHADOWCASTER) || defined(UNITY_PASS_META) || defined(LIL_PASS_SHADOWCASTER_INCLUDED) || defined(LIL_PASS_META_INCLUDED)
    return;
#endif

    if(_LilSSRTAO < 0.5)
    {
        return;
    }

    float debugMode = _LilSSRTAODebugMode > 3.5 ? 0.0 : _LilSSRTAODebugMode;

    if(!lilSSRT_HasDepthTexture())
    {
        return;
    }

    if(debugMode > 1.5 && debugMode < 2.5)
    {
        fd.col.rgb = lilSSRT_DebugDepth(fd.positionCS.xy).xxx;
        return;
    }

    if(debugMode > 2.5 && debugMode < 3.5)
    {
        float2 debugUV = lilSSRT_GetScreenUV(fd);
        fd.col.rgb = float3(debugUV, 0.0);
        return;
    }

    float aoScreenLoad = lilSSRT_AOScreenLoad(rcp(max(abs(fd.positionCS.w), LILSSRT_EPSILON)), 1.0, 1.0);
    float completeSkipNoise = lilSSRT_SkipNoise(lilSSRT_GetScreenUV(fd), 2.0, 91.37);
    if(completeSkipNoise < lilSSRT_CompleteSkipRate(aoScreenLoad) && debugMode < 0.5)
    {
        return;
    }

    float maskFactor = lilSSRT_ReadMaskFactor(fd.uvMain);
    if(maskFactor <= LILSSRT_EPSILON && debugMode < 0.5) return;

    float ao = 1.0;
    if(_LilSSRTAOAlgorithm < 0.5)
    {
        ao = lilSSRT_EvaluateSSRTAO(fd, ssrtData);
    }
    else if(_LilSSRTAOAlgorithm < 1.5)
    {
        ao = lilSSRT_EvaluateXeGTAO(fd, ssrtData);
    }
    else if(_LilSSRTAOAlgorithm < 2.5)
    {
        ao = lilSSRT_EvaluateFastSSRTAO(fd, ssrtData);
    }
    else
    {
        ao = lilSSRT_EvaluateFastXeGTAO(fd, ssrtData);
    }

    float occlusionSignal = max(0.0, (1.0 - ao) - lilSSRT_AOOcclusionDeadZone());
    float rawOcclusion = occlusionSignal * maskFactor;
    rawOcclusion *= lilSSRT_AOAlgorithmStrengthScale();
    rawOcclusion *= lerp(1.0, saturate(fd.col.a), saturate(_LilSSRTSSRTAOTransparentReduce));

    if(lilSSRT_UsesDistanceFade())
    {
        float fadeFactor = smoothstep(_LilSSRTAODistanceFadeMin, _LilSSRTAODistanceFadeMax, length(ssrtData.xyz));
        rawOcclusion *= 1.0 - fadeFactor;
    }
    rawOcclusion *= lilSSRT_AOMinRadiusFade(rcp(max(abs(fd.positionCS.w), LILSSRT_EPSILON)));
    rawOcclusion = lilSSRT_ApplyAOContrast(rawOcclusion);

    float debugOcclusion = saturate(rawOcclusion * _LilSSRTAOStrength);

    if(debugMode > 0.5)
    {
        fd.col.rgb = (1.0 - debugOcclusion).xxx;
        return;
    }

    if(_LilSSRTAOStrength <= LILSSRT_EPSILON)
    {
        return;
    }

    float occlusion = saturate(rawOcclusion * _LilSSRTAOStrength);
    float3 aoColor = lerp(float3(1.0, 1.0, 1.0), _LilSSRTAOColor.rgb, saturate(_LilSSRTAOColor.a));
    fd.col.rgb = lerp(fd.col.rgb, fd.col.rgb * aoColor, occlusion);
}

#endif
