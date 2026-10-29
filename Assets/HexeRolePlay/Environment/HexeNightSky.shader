// Dusk sky for the forest set: a gradient from a warm horizon to deep blue, stars and a moon.
Shader "Hexe/NightSky"
{
    Properties
    {
        _Top ("Top", Color) = (0.03, 0.04, 0.13, 1)
        _Mid ("Middle", Color) = (0.17, 0.12, 0.32, 1)
        _Horizon ("Horizon", Color) = (0.86, 0.42, 0.33, 1)
        _MoonDir ("Moon direction", Vector) = (-0.75, 0.42, -0.5, 0)
        _MoonColor ("Moon", Color) = (1, 0.95, 0.82, 1)
        _MoonSize ("Moon size", Range(0.0005, 0.02)) = 0.0016
        _Stars ("Stars", Range(0, 2)) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Top, _Mid, _Horizon, _MoonColor;
            float4 _MoonDir;
            float _MoonSize, _Stars;

            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float hash (float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float h = d.y;
                float3 col = lerp(_Horizon.rgb, _Mid.rgb, smoothstep(-0.03, 0.22, h));
                col = lerp(col, _Top.rgb, smoothstep(0.2, 0.75, h));

                float3 cell = floor(d * 700.0);
                float s = hash(cell);
                col += step(0.9985, s) * smoothstep(0.08, 0.35, h) * _Stars * (0.5 + 0.5 * hash(cell + 3.1));

                float md = 1 - dot(d, normalize(_MoonDir.xyz));
                col += _MoonColor.rgb * (smoothstep(_MoonSize, _MoonSize * 0.75, md) * 1.6 + 0.35 * exp(-md * 90.0) + 0.08 * exp(-md * 12.0));
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
