// The forest set's one material: colour comes from the mesh's vertex colours, so every tree,
// rock and candy is part of a few combined meshes. Vertex alpha below 1 marks glowing parts
// (lit windows, mushroom caps): alpha 0 glows fully and takes no light. Lambert rather than
// Standard: a physically based surface reflects the sky's orange horizon off every grazing
// face, which on flat-shaded trees reads as red rims.
Shader "Hexe/VertexColorLit"
{
    Properties
    {
        _GlowBoost ("Glow", Float) = 2.2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows addshadow
        #pragma target 3.0

        struct Input { float4 color : COLOR; };
        half _GlowBoost;

        void surf (Input IN, inout SurfaceOutput o)
        {
            half glow = 1 - IN.color.a;
            o.Albedo = IN.color.rgb * (1 - glow);
            o.Emission = IN.color.rgb * glow * _GlowBoost;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
