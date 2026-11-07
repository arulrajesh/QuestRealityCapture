#nullable enable

using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealityLog.Recording
{
    /// <summary>
    /// A miniature of the room (walls, floor, furniture boxes, doors and windows) at the bottom of the view, like
    /// RoomPlan-style apps (user request, 2026-11-07). It keeps the real room's orientation, so "ahead" in the
    /// miniature is ahead in the room; it is tilted toward the eye, shows a marker for the operator, and each
    /// element is tinted by how much of it has been captured (red -> green). The chosen entrance door is green.
    /// This is the same geometry the laptop exports as the SketchUp-style room model.
    /// </summary>
    internal sealed class MiniRoomModel
    {
        private const float WidthMeters = 0.24f;  // size of the whole miniature in front of the eyes
        private const float Distance = 0.55f;
        private const float Drop = 0.30f;
        private const float TiltDegrees = -35f;  // far side raised toward the eye

        private readonly GameObject root;
        private readonly Transform content;
        private readonly Transform marker;
        private readonly Mesh mesh;
        private readonly Dictionary<MRUKAnchor, (int Start, int Count, Color32 Base)> ranges = new();
        private Color32[] colors = System.Array.Empty<Color32>();
        private readonly float scale;
        private readonly Vector3 center;
        private readonly float floorY;

        public bool Visible
        {
            get => root.activeSelf;
            set => root.SetActive(value);
        }

        public MiniRoomModel(MRUKRoom room, Material material, string? entranceUuid)
        {
            root = new GameObject("Mini Room Model");
            content = new GameObject("Content").transform;
            content.SetParent(root.transform, false);

            var vertices = new List<Vector3>();
            var vertexColors = new List<Color32>();
            var indices = new List<int>();
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            void Add(MRUKAnchor anchor, Vector3[] corners, int[] triangles, Color32 color)
            {
                var start = vertices.Count;
                foreach (var c in corners)
                {
                    vertices.Add(c);
                    vertexColors.Add(color);
                    lo = Vector3.Min(lo, c);
                    hi = Vector3.Max(hi, c);
                }
                foreach (var t in triangles) indices.Add(start + t);
                ranges[anchor] = (start, corners.Length, color);
            }

            foreach (var anchor in room.Anchors)
            {
                if (anchor == null) continue;
                var m = anchor.transform.localToWorldMatrix;
                if (anchor.VolumeBounds.HasValue)
                {
                    var b = anchor.VolumeBounds.Value;
                    var corners = new Vector3[8];
                    for (var i = 0; i < 8; i++)
                    {
                        corners[i] = m.MultiplyPoint3x4(new Vector3(
                            (i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
                    }
                    Add(anchor, corners, BoxTriangles, new Color32(200, 200, 200, 220));
                }
                else if (anchor.PlaneRect.HasValue && !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.CEILING))
                {
                    var r = anchor.PlaneRect.Value;
                    var corners = new[]
                    {
                        m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMin, 0f)), m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMin, 0f)),
                        m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0f)), m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMax, 0f)),
                    };
                    var isDoor = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME);
                    var isWindow = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WINDOW_FRAME);
                    var isFloor = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR);
                    var color = isDoor
                        ? (entranceUuid != null && RoomCoverageGuide.UuidOf(anchor) == entranceUuid ? new Color32(70, 220, 110, 255) : new Color32(90, 140, 255, 255))
                        : isWindow ? new Color32(150, 210, 255, 200)
                        : isFloor ? new Color32(150, 150, 150, 150)
                        : new Color32(235, 235, 235, 90);
                    Add(anchor, corners, QuadTriangles, color);
                }
            }

            mesh = new Mesh { name = "Mini room", indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            colors = vertexColors.ToArray();
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            var body = new GameObject("Room");
            body.transform.SetParent(content, false);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = body.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Operator marker: a flat arrow on the floor plane, pointing where the head faces.
            marker = new GameObject("You").transform;
            marker.SetParent(content, false);
            var arrow = new Mesh { name = "Mini marker" };
            arrow.SetVertices(new[] { new Vector3(0f, 0f, 0.35f), new Vector3(-0.2f, 0f, -0.2f), new Vector3(0.2f, 0f, -0.2f) });
            arrow.SetColors(new[] { new Color32(255, 200, 40, 255), new Color32(255, 200, 40, 255), new Color32(255, 200, 40, 255) });
            arrow.SetTriangles(new[] { 0, 1, 2 }, 0);
            marker.gameObject.AddComponent<MeshFilter>().sharedMesh = arrow;
            var markerRenderer = marker.gameObject.AddComponent<MeshRenderer>();
            markerRenderer.sharedMaterial = material;
            markerRenderer.shadowCastingMode = ShadowCastingMode.Off;

            center = vertices.Count > 0 ? (lo + hi) * 0.5f : Vector3.zero;
            var extent = vertices.Count > 0 ? Mathf.Max(hi.x - lo.x, hi.z - lo.z, 1f) : 1f;
            scale = WidthMeters / extent;
            root.transform.localScale = Vector3.one * scale;
            content.localPosition = -center;
            marker.localScale = Vector3.one * Mathf.Clamp(extent * 0.08f, 0.25f, 0.6f);  // room units (meters)
            floorY = vertices.Count > 0 ? lo.y + 0.02f : 0f;
        }

        /// <summary>Tint each element by its captured fraction (0..1); doors and windows keep their colour.</summary>
        public void SetCoverage(IReadOnlyDictionary<MRUKAnchor, float> captured)
        {
            foreach (var pair in ranges)
            {
                if (!captured.TryGetValue(pair.Key, out var fraction)) continue;
                if (pair.Key.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME)) continue;
                var tint = (Color32)Color.Lerp(new Color(1f, 0.3f, 0.25f), new Color(0.3f, 0.95f, 0.45f), fraction);
                tint.a = pair.Value.Base.a;
                for (var i = 0; i < pair.Value.Count; i++) colors[pair.Value.Start + i] = tint;
            }
            mesh.SetColors(colors);
        }

        /// <summary>Head-locked placement (below the view), room-aligned orientation, operator marker.</summary>
        public void Follow(Transform head)
        {
            var forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward);
            root.transform.position = head.position + forward * Distance + Vector3.down * Drop;
            root.transform.rotation = Quaternion.AngleAxis(TiltDegrees, right);
            marker.localPosition = new Vector3(head.position.x, floorY, head.position.z);
            marker.localRotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        public void Destroy()
        {
            Object.Destroy(root);
        }

        private static readonly int[] QuadTriangles = { 0, 1, 2, 0, 2, 3 };

        private static readonly int[] BoxTriangles =
        {
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        };
    }
}
