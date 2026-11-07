Shader "RealityLog/DepthCoveragePoints"
{
    Properties
    {
        _PointColor ("Point Color", Color) = (0.2, 0.8, 1.0, 0.45)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _CoveragePoints;
            StructuredBuffer<float4> _CoverageMetadata;
            StructuredBuffer<int> _VoxelOccupancy;
            float _PointSizeMeters;
            float _CurrentSegmentId;
            float _PreviousSegmentAlpha;
            float _MinDepthMeters;
            float _MaxDepthMeters;
            float4 _PointColor;
            float _GoodViews;
            float _CloseMeters;
            float _FarMeters;

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                uint pointIndex = v.vertexID / 6u;
                uint cornerIndex = v.vertexID % 6u;

                float2 corner = float2(-1.0, -1.0);
                if (cornerIndex == 1u) corner = float2(1.0, -1.0);
                else if (cornerIndex == 2u) corner = float2(1.0, 1.0);
                else if (cornerIndex == 4u) corner = float2(1.0, 1.0);
                else if (cornerIndex == 5u) corner = float2(-1.0, 1.0);

                int occupied = _VoxelOccupancy[pointIndex];
                float4 coveragePoint = _CoveragePoints[pointIndex];
                float4 metadata = _CoverageMetadata[pointIndex];
                float3 cameraRight = normalize(float3(unity_CameraToWorld[0][0], unity_CameraToWorld[1][0], unity_CameraToWorld[2][0]));
                float3 cameraUp = normalize(float3(unity_CameraToWorld[0][1], unity_CameraToWorld[1][1], unity_CameraToWorld[2][1]));
                corner *= _PointSizeMeters;
                float3 world = coveragePoint.xyz + cameraRight * corner.x + cameraUp * corner.y;

                o.pos = UnityWorldToClipPos(world);
                // Capture quality, as in DepthCoverageParticleRenderer.Quality: red missing, yellow partial, green captured.
                float detail = metadata.y >= 999.0 ? 0.0 : saturate((_FarMeters - metadata.y) / max(0.001, _FarMeters - _CloseMeters));
                float quality = min(detail, saturate(metadata.z / max(1.0, _GoodViews)));
                float3 missing = float3(1.0, 0.2, 0.15);
                float3 partial = float3(1.0, 0.8, 0.1);
                float3 captured = float3(0.2, 0.95, 0.4);
                float3 rgb = quality < 0.5 ? lerp(missing, partial, quality * 2.0) : lerp(partial, captured, (quality - 0.5) * 2.0);
                float segmentAlpha = abs(metadata.x - _CurrentSegmentId) < 0.5 ? 1.0 : _PreviousSegmentAlpha;
                o.color = float4(rgb, occupied == 1 ? segmentAlpha * lerp(0.9, 0.3, quality) : 0.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
