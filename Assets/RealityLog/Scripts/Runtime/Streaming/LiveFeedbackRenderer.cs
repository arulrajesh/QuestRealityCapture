#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealityLog.Streaming
{
    /// <summary>
    /// Shows the laptop's live feedback in the headset: the generated surfaces (confidence or camera
    /// colors), a marker at every keyframe, and a head-locked status HUD.
    /// </summary>
    public sealed class LiveFeedbackRenderer : MonoBehaviour
    {
        private const int MaxMarkers = 300;
        private const float MarkerSize = 0.06f;

        private LiveStreamClient? client;
        private Material? material;
        private MeshFilter? surfaceFilter;
        private Mesh? surfaceMesh;
        private TextMesh? hud;
        private readonly Queue<LineRenderer> markers = new();
        private StatusJson status = new();
        private float keyframeFlashUntil;
        private int lastSegment = -1;

        public static LiveFeedbackRenderer Ensure(GameObject host, LiveStreamClient client)
        {
            var renderer = host.GetComponent<LiveFeedbackRenderer>();
            if (renderer == null) renderer = host.AddComponent<LiveFeedbackRenderer>();
            renderer.client = client;
            return renderer;
        }

        private void Start()
        {
            var shader = Resources.Load<Shader>("RealityLog/LiveSurfaceOverlay");
            if (shader == null)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] LiveSurfaceOverlay shader missing from Resources; feedback overlay disabled.");
                enabled = false;
                return;
            }
            material = new Material(shader);
            var surface = new GameObject("LiveSurfaceOverlay");  // world origin: the mesh arrives in Unity world coordinates
            surfaceFilter = surface.AddComponent<MeshFilter>();
            surface.AddComponent<MeshRenderer>().sharedMaterial = material;
            surfaceMesh = new Mesh { name = "LiveSurface" };
            surfaceMesh.MarkDynamic();
            surfaceFilter.sharedMesh = surfaceMesh;
            CreateHud();
        }

        private void CreateHud()
        {
            // Fully qualified: inside RealityLog.*, "Camera" resolves to the RealityLog.Camera namespace.
            var mainCamera = UnityEngine.Camera.main;
            var cameraTransform = mainCamera != null ? mainCamera.transform : null;
            if (cameraTransform == null) return;
            var go = new GameObject("LiveStreamHud");
            go.transform.SetParent(cameraTransform, false);
            go.transform.localPosition = new Vector3(0f, -0.20f, 0.7f);
            hud = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            hud.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            hud.fontSize = 48;
            hud.characterSize = 0.004f;
            hud.anchor = TextAnchor.MiddleCenter;
            hud.alignment = TextAlignment.Center;
        }

        private void Update()
        {
            if (client == null || material == null) return;
            material.SetFloat("_Opacity", client.Settings.overlayOpacity);
            if (surfaceFilter != null) surfaceFilter.gameObject.SetActive(client.Settings.showSurfaceOverlay);
            for (var i = 0; i < 8 && client.Incoming.TryDequeue(out var message); ++i)
            {
                try
                {
                    Handle(message);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] Bad feedback message {message.Type}: {ex.Message}");
                }
            }
            UpdateHud();
        }

        private void Handle(IncomingMessage message)
        {
            switch (message.Type)
            {
                case LiveStreamProtocol.KeyframeNotice:
                    var notice = JsonUtility.FromJson<KeyframeNoticeJson>(message.HeaderJson);
                    if (notice.depth_kept) AddMarker(notice);
                    break;
                case LiveStreamProtocol.SurfaceMesh:
                    ApplySurface(JsonUtility.FromJson<SurfaceMeshJson>(message.HeaderJson), message.Payload);
                    break;
                case LiveStreamProtocol.Status:
                    status = JsonUtility.FromJson<StatusJson>(message.HeaderJson);
                    break;
            }
        }

        private void AddMarker(KeyframeNoticeJson notice)
        {
            keyframeFlashUntil = Time.time + 0.35f;
            LineRenderer line;
            if (markers.Count >= MaxMarkers)
            {
                line = markers.Dequeue();
            }
            else
            {
                var go = new GameObject("KeyframeMarker");
                line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = material!;
                line.widthMultiplier = 0.003f;
                line.useWorldSpace = false;
                line.positionCount = 9;
                // Camera frustum outline: apex, then the four corners of the image plane, back and forth.
                var s = MarkerSize;
                line.SetPositions(new[]
                {
                    Vector3.zero, new Vector3(-s, -s, 2 * s), new Vector3(s, -s, 2 * s), Vector3.zero,
                    new Vector3(s, s, 2 * s), new Vector3(-s, s, 2 * s), Vector3.zero,
                    new Vector3(-s, -s, 2 * s), new Vector3(-s, s, 2 * s),
                });
            }
            var color = notice.color_wanted ? new Color(0.2f, 1f, 0.4f, 1f) : new Color(0.3f, 0.8f, 1f, 1f);
            line.startColor = line.endColor = color;
            line.transform.SetPositionAndRotation(notice.pose.Position, notice.pose.Rotation);
            markers.Enqueue(line);
        }

        private void ApplySurface(SurfaceMeshJson header, byte[] payload)
        {
            if (surfaceMesh == null) return;
            var n = header.vertex_count;
            var m = header.triangle_count;
            var indexBytes = header.index_bits == 16 ? 2 : 4;
            if (payload.Length != n * 10 + m * 3 * indexBytes) throw new ArgumentException("SURFACE_MESH payload size mismatch");
            if (header.segment != lastSegment)
            {
                ClearMarkers();  // a new tracking segment: old markers are in another frame
                lastSegment = header.segment;
            }
            var origin = new Vector3(header.origin[0], header.origin[1], header.origin[2]);
            var vertices = new Vector3[n];
            var colors = new Color32[n];
            var colorMode = client != null && client.Settings.overlayMode == "color";
            for (var i = 0; i < n; ++i)
            {
                var o = i * 6;
                vertices[i] = origin + header.step_m * new Vector3(
                    BitConverter.ToUInt16(payload, o), BitConverter.ToUInt16(payload, o + 2), BitConverter.ToUInt16(payload, o + 4));
                var c = n * 6 + i * 4;
                colors[i] = colorMode
                    ? new Color32(payload[c], payload[c + 1], payload[c + 2], 255)
                    : ConfidenceColor(payload[c + 3]);
            }
            var triangles = new int[m * 3];
            var t0 = n * 10;
            for (var i = 0; i < triangles.Length; ++i)
            {
                triangles[i] = indexBytes == 2 ? BitConverter.ToUInt16(payload, t0 + 2 * i) : BitConverter.ToInt32(payload, t0 + 4 * i);
            }
            surfaceMesh.Clear();
            surfaceMesh.indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            surfaceMesh.SetVertices(vertices);
            surfaceMesh.SetColors(colors);
            surfaceMesh.SetTriangles(triangles, 0, calculateBounds: true);
        }

        /// <summary>Red (uncertain) → yellow → green (solid).</summary>
        private static Color32 ConfidenceColor(byte confidence)
        {
            var c = confidence / 255f;
            var color = c < 0.5f ? Color.Lerp(new Color(1f, 0.2f, 0.2f), new Color(1f, 0.9f, 0.2f), c * 2f)
                                 : Color.Lerp(new Color(1f, 0.9f, 0.2f), new Color(0.2f, 1f, 0.4f), (c - 0.5f) * 2f);
            return color;
        }

        private void ClearMarkers()
        {
            while (markers.Count > 0)
            {
                var line = markers.Dequeue();
                if (line != null) Destroy(line.gameObject);
            }
        }

        private void UpdateHud()
        {
            if (hud == null || client == null) return;
            hud.gameObject.SetActive(client.Settings.showHud);
            var link = client.IsConnected ? $"<color=#66ff99>● laptop</color> {client.Endpoint}" : $"<color=#ff6666>● {client.StatusText}</color>";
            var flash = Time.time < keyframeFlashUntil ? "  <color=#66ff99>KEYFRAME ✓</color>" : string.Empty;
            var session = client.SessionActive ? $"REC {status.session}" : "not recording (☰ to start)";
            hud.richText = true;
            hud.text = $"{link}\n{session}{flash}\n" +
                       $"keyframes {status.keyframes}  color {status.color_keyframes}  surface {status.surface_m2:0.0} m²  σ {status.median_sigma_mm:0} mm\n" +
                       $"sent {client.FramesSent}  gated {client.FramesGated}  dropped {client.FramesDropped + status.dropped_live}" +
                       (status.tracking_jumps > 0 ? $"  tracking resets {status.tracking_jumps}" : string.Empty);
        }
    }
}
