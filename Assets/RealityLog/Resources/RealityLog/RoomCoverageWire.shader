// Faint triangle grid over the Space Setup room mesh (RoomCoverageGuide), Hyperscape-style: vertex alpha is
// the detail still missing there, so the grid disappears as an area gets captured. Edges come from
// barycentric coordinates in UV0 (the mesh is unwelded), anti-aliased with fwidth; no geometry shader.
// Pass 1 writes depth only, so grid lines behind furniture or walls are hidden instead of piling up.
// Stereo macros: Quest renders single-pass (multiview / instanced); without them one eye would be empty.
Shader "RealityLog/RoomCoverageWire"
{
    Properties
    {
        _LineColor ("Line color", Color) = (0.85, 0.93, 1.0, 0.35)
        _LineWidth ("Line width (pixels)", Float) = 1.1
        _FillAlpha ("Fill alpha (missing areas)", Range(0, 0.2)) = 0.03
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            ColorMask 0
            ZWrite On
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return 0;
            }
            ENDCG
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _LineColor;
            float _LineWidth;
            float _FillAlpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float3 bary : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.bary = float3(v.uv, 1.0 - v.uv.x - v.uv.y);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 width = fwidth(i.bary) * _LineWidth;
                float3 inside = smoothstep(float3(0, 0, 0), width, i.bary);
                float edge = 1.0 - min(min(inside.x, inside.y), inside.z);
                float missing = i.color.a;  // 1 = nothing captured here yet, 0 = captured
                float alpha = missing * max(edge * _LineColor.a, _FillAlpha);
                return fixed4(lerp(_LineColor.rgb, i.color.rgb, 0.5), alpha);
            }
            ENDCG
        }
    }
}
