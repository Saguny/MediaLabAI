//----------------------------------------------------------------------------------------------------------------------
// lilSSRT custom shader properties

#define LIL_CUSTOM_PROPERTIES \
    float _LilSSRTAO; \
    float _LilSSRTAOStrength; \
    float _LilSSRTAOContrast; \
    float _LilSSRTAORadius; \

    float _LilSSRTAODepthBias; \
    float _LilSSRTAOSpread; \
    float _LilSSRTAOJitter; \
    float _LilSSRTAOInvertMask; \
    float _LilSSRTAOFOVAdjust; \
    float _LilSSRTAOFOV; \
    float _LilSSRTAODistanceFadeMin; \
    float _LilSSRTAODistanceFadeMax; \
    float4 _LilSSRTAOColor; \
    float _LilSSRTAODebugMode; \
    float _LilSSRTAOAlgorithm; \
    float _LilSSRTSSRTAORays; \
    float _LilSSRTXeGTAOSlices; \
    float _LilSSRTSSRTAOSteps; \
    float _LilSSRTSSRTAOThickness; \
    float _LilSSRTSSRTAOTransparentReduce; \
    float _LilSSRTSSRTAOHitMode; \
    float _LilSSRTSSRTAOIntensity; \
    float _LilSSRTAOReconstructNormal; \
    float _LilSSRTAOReconstructNormalStrength; \
    float _LilSSRTAODepthReject; \
    float _LilSSRTAONoiseStability; \
    float _LilSSRTAOHitSpread; \
    float _VRChatMirrorMode; \
    float _FakeBounceLight; \
    float _FakeBouncePreventBlowout; \
    float _FakeBounceStrength; \
    float _FakeBounceBlur; \
    float _FakeBounceBlendMode; \
    float _FakeBounceInvertMask; \
    float _FakeBouncePower; \
    float4 _FakeBounceColor;

#define LIL_CUSTOM_TEXTURES \
    TEXTURE2D(_LilSSRTAOMask); \
    TEXTURE2D(_FakeBounceMask); \
    TEXTURE2D(_FakeBounceTexDown); \
    TEXTURE2D(_FakeBounceTexRight); \
    TEXTURE2D(_FakeBounceTexLeft); \
    TEXTURE2D(_FakeBounceTexFront); \
    TEXTURE2D(_FakeBounceTexBack);

// Add vertex shader input
//#define LIL_REQUIRE_APP_POSITION
//#define LIL_REQUIRE_APP_TEXCOORD0
//#define LIL_REQUIRE_APP_TEXCOORD1
//#define LIL_REQUIRE_APP_TEXCOORD2
//#define LIL_REQUIRE_APP_TEXCOORD3
//#define LIL_REQUIRE_APP_TEXCOORD4
//#define LIL_REQUIRE_APP_TEXCOORD5
//#define LIL_REQUIRE_APP_TEXCOORD6
//#define LIL_REQUIRE_APP_TEXCOORD7
//#define LIL_REQUIRE_APP_COLOR
//#define LIL_REQUIRE_APP_NORMAL
//#define LIL_REQUIRE_APP_TANGENT
//#define LIL_REQUIRE_APP_VERTEXID

// Add vertex shader output
//#define LIL_V2F_FORCE_TEXCOORD0
//#define LIL_V2F_FORCE_TEXCOORD1
//#define LIL_V2F_FORCE_POSITION_OS
//#define LIL_V2F_FORCE_POSITION_WS
//#define LIL_V2F_FORCE_POSITION_SS
//#define LIL_V2F_FORCE_NORMAL
//#define LIL_V2F_FORCE_TANGENT
//#define LIL_V2F_FORCE_BITANGENT

#define LIL_CUSTOM_V2F_MEMBER(id0,id1,id2,id3,id4,id5,id6,id7) \
    float4 ssrtData : TEXCOORD##id0;

#if defined(UNITY_PASS_SHADOWCASTER) || defined(UNITY_PASS_META) || defined(LIL_PASS_SHADOWCASTER_INCLUDED) || defined(LIL_PASS_META_INCLUDED)
    #define LIL_CUSTOM_VERT_COPY \
        LIL_V2F_OUT_BASE.ssrtData = 0.0;
#else
    #define LIL_CUSTOM_VERT_COPY \
        LIL_V2F_OUT_BASE.ssrtData = float4(vertexInput.positionWS - _WorldSpaceCameraPos, 0.0);
#endif

// Inserting a process into the vertex shader
//#define LIL_CUSTOM_VERTEX_OS
//#define LIL_CUSTOM_VERTEX_WS

// Inserting a process into pixel shader
#define BEFORE_FOG \
    ApplySSRTLighting(fd); \
    lilSSRT_ApplyAO(fd, input.ssrtData);
