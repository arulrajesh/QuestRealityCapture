#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using RealityLog.Depth;
using RealityLog.Streaming;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealityLog.Recording
{
    /// <summary>
    /// Hyperscape-style scan guidance (user requests, 2026-11-07). The room from Meta's Space Setup (MRUK global
    /// mesh) is drawn as a faint triangle grid that disappears where enough sharp, close views have been captured,
    /// so whatever is still visible is what needs scanning, including places not looked at yet. Walls, floor,
    /// ceiling and furniture boxes are outlined; a miniature of the room sits at the bottom of the view (B hides
    /// it). The HUD shows room and per-object completion.
    /// Controls (right hand): trigger at a door = make it the entrance (visitors start there); A = "detail here"
    /// (2 s of every frame, all kept). Y (not while recording) = edit the room in Space Setup (trace walls, add
    /// doors, windows and furniture by hand), then reload. The room, with the entrance, is sent to the laptop,
    /// which builds the SketchUp-style model from it.
    /// Replaces the voxel dots (LiveDepthCoverageVisualizer): up to 80k particles filled the view and lagged.
    /// </summary>
    public sealed class RoomCoverageGuide : MonoBehaviour
    {
        // A vertex is captured with >= GoodViews distinct slow viewpoints, one of them as close as a visitor can
        // ever get: CloseMeters for surfaces near eye height (~0.7 mm per camera pixel at 1728^2), and for high or
        // low surfaces (ceiling, crown moulding, floor) their vertical distance from a standing eye.
        private const float GoodViews = 6f;
        private const float CloseMeters = 0.8f;
        private const float DetailFalloffMeters = 1.2f;  // views beyond close + this add no close-up detail
        private const float EyeHeightMeters = 1.6f;
        private const float UpdateIntervalS = 1f / 6f;
        private const float ReadbackIntervalS = 0.5f;
        private const float NewViewpointMeters = 0.05f;
        private const float NewViewpointDegrees = 5f;
        private const float DetailSeconds = 2f;
        private const int EyeIndex = 0;  // left depth eye, as the voxel coverage
        private const bool FlipVerticalProjection = true;  // liveFeedback.coverage.flipVerticalProjection default
        private const float MinDepthMeters = 0.2f;
        private const float MaxDepthMeters = 4.0f;

        private ComputeShader? compute;
        private int resetKernel = -1;
        private int updateKernel = -1;
        private Material? gridMaterial;
        private Material? outlineMaterial;
        private Material? miniMaterial;
        private DepthFrameProvider? depthProvider;
        private bool usingDepth;
        private GameObject? gridObject;
        private GameObject? outlineObject;
        private Mesh? gridMesh;
        private ComputeBuffer? vertexBuffer;
        private ComputeBuffer? coverageBuffer;
        private int vertexCount;
        private Color32[] colors = Array.Empty<Color32>();
        private float[] vertexArea = Array.Empty<float>();
        private float[] needClose = Array.Empty<float>();
        private readonly List<(MRUKAnchor Anchor, string Label, bool Furniture, int[] Vertices)> parts = new();
        private readonly Dictionary<MRUKAnchor, float> captured = new();
        private MRUKRoom? room;
        private MiniRoomModel? mini;
        private string? entranceUuid;
        private float nextUpdate;
        private float nextReadback;
        private bool readbackPending;
        private bool wasRecording;
        private bool editing;
        private string flash = string.Empty;
        private float flashUntil;
        private string roomSummary = string.Empty;
        private Vector3 lastCountedPosition = new(float.MaxValue, 0f, 0f);
        private Quaternion lastCountedRotation = Quaternion.identity;

        private static bool Recording => LiveStreamClient.Active != null && LiveStreamClient.Active.SessionActive;

        private async void Start()
        {
            compute = Resources.Load<ComputeShader>("RealityLog/RoomMeshCoverage");
            var gridShader = Resources.Load<Shader>("RealityLog/RoomCoverageWire");
            var overlayShader = Resources.Load<Shader>("RealityLog/LiveSurfaceOverlay");
            if (compute == null || gridShader == null || overlayShader == null)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Room coverage guide disabled: shaders not found in Resources/RealityLog.");
                return;
            }
            resetKernel = compute.FindKernel("ResetCoverage");
            updateKernel = compute.FindKernel("UpdateCoverage");
            gridMaterial = new Material(gridShader);
            outlineMaterial = new Material(overlayShader) { renderQueue = (int)RenderQueue.Transparent - 20 };
            outlineMaterial.SetFloat("_Opacity", 0.55f);
            miniMaterial = new Material(overlayShader) { renderQueue = (int)RenderQueue.Transparent - 30 };
            miniMaterial.SetFloat("_Opacity", 0.9f);
            depthProvider = FindAnyObjectByType<DepthFrameProvider>();
            if (depthProvider != null)
            {
                depthProvider.BeginDepthUsage();
                usingDepth = true;
            }
            await LoadRoom(requestSpaceSetupIfMissing: true);
        }

        private async Task LoadRoom(bool requestSpaceSetupIfMissing)
        {
            SetSummary("loading room…");
            try
            {
                if (MRUK.Instance == null)
                {
                    gameObject.AddComponent<MRUK>();  // its Awake starts loading the room from the device
                }
                var mruk = MRUK.Instance!;
                var deadline = Time.realtimeSinceStartup + 20f;
                while (!mruk.IsInitialized && Time.realtimeSinceStartup < deadline)
                {
                    await Task.Yield();
                }
                if (CurrentRoom(mruk) == null || !requestSpaceSetupIfMissing)
                {
                    await mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: requestSpaceSetupIfMissing);
                }
                room = CurrentRoom(mruk);
                var anchor = room != null ? room.GlobalMeshAnchor : null;
                var mesh = anchor != null ? anchor.GlobalMesh : null;
                if (room == null || anchor == null || mesh == null || mesh.vertexCount == 0)
                {
                    SetSummary("no room mesh: press Y to run Space Setup");
                    return;
                }
                if (entranceUuid != null && !room.Anchors.Exists(a => a != null && UuidOf(a) == entranceUuid)) entranceUuid = null;
                BuildGrid(mesh, anchor.transform.localToWorldMatrix, room);
                RebuildOverlays();
                SendRoomModel();
                SetSummary("room loaded: scan until the grid is gone");
                Debug.Log($"[{Constants.LOG_TAG}] Room coverage guide: {vertexCount / 3} triangles, {parts.Count} elements.");
            }
            catch (Exception ex)
            {
                SetSummary("room guide unavailable");
                Debug.LogWarning($"[{Constants.LOG_TAG}] Room coverage guide failed to load the room: {ex.Message}");
            }
        }

        private static MRUKRoom? CurrentRoom(MRUK mruk)
        {
            var current = mruk.GetCurrentRoom();
            if (current != null) return current;
            var rooms = mruk.Rooms;
            return rooms != null && rooms.Count > 0 ? rooms[0] : null;
        }

        internal static string UuidOf(MRUKAnchor anchor)
        {
            var uuid = anchor.Anchor.Uuid;
            return uuid != Guid.Empty ? uuid.ToString() : anchor.name;
        }

        /// <summary>Unwelded copy of the room mesh in world space, with barycentric UVs for the grid shader.</summary>
        private void BuildGrid(Mesh source, Matrix4x4 localToWorld, MRUKRoom loaded)
        {
            ReleaseGrid();
            var sourceVertices = source.vertices;
            var triangles = source.triangles;
            vertexCount = triangles.Length;
            var positions = new Vector3[vertexCount];
            var bary = new Vector2[vertexCount];
            var indices = new int[vertexCount];
            vertexArea = new float[vertexCount];
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var a = localToWorld.MultiplyPoint3x4(sourceVertices[triangles[t]]);
                var b = localToWorld.MultiplyPoint3x4(sourceVertices[triangles[t + 1]]);
                var c = localToWorld.MultiplyPoint3x4(sourceVertices[triangles[t + 2]]);
                positions[t] = a;
                positions[t + 1] = b;
                positions[t + 2] = c;
                bary[t] = new Vector2(1f, 0f);
                bary[t + 1] = new Vector2(0f, 1f);
                bary[t + 2] = new Vector2(0f, 0f);
                var area = Vector3.Cross(b - a, c - a).magnitude / 6f;  // a third of the triangle per vertex
                vertexArea[t] = vertexArea[t + 1] = vertexArea[t + 2] = area;
                indices[t] = t;
                indices[t + 1] = t + 1;
                indices[t + 2] = t + 2;
            }
            colors = new Color32[vertexCount];
            for (var i = 0; i < vertexCount; i++) colors[i] = new Color32(255, 255, 255, 255);

            // How close a sharp view must be: as close as a standing visitor's eyes can get to that height.
            var floorY = loaded.FloorAnchor != null ? loaded.FloorAnchor.transform.position.y : MinY(positions);
            var eyeY = floorY + EyeHeightMeters;
            needClose = new float[vertexCount];
            for (var i = 0; i < vertexCount; i++) needClose[i] = Mathf.Max(CloseMeters, Mathf.Abs(positions[i].y - eyeY));

            gridMesh = new Mesh { name = "Room coverage grid", indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            gridMesh.SetVertices(positions);
            gridMesh.SetUVs(0, bary);
            gridMesh.SetColors(colors);
            gridMesh.SetIndices(indices, MeshTopology.Triangles, 0);
            gridMesh.RecalculateBounds();
            gridObject = new GameObject("Room Coverage Grid");
            gridObject.AddComponent<MeshFilter>().sharedMesh = gridMesh;
            var renderer = gridObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = gridMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            vertexBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
            vertexBuffer.SetData(positions);
            coverageBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 2);
            ResetCoverage();

            // Which grid vertices belong to each wall, floor, ceiling and furniture volume (HUD and miniature).
            parts.Clear();
            foreach (var anchor in loaded.Anchors)
            {
                if (anchor == null) continue;
                var worldToLocal = anchor.transform.worldToLocalMatrix;
                var members = new List<int>();
                if (anchor.VolumeBounds.HasValue)
                {
                    var bounds = anchor.VolumeBounds.Value;
                    bounds.Expand(0.1f);
                    for (var i = 0; i < vertexCount; i++)
                    {
                        if (bounds.Contains(worldToLocal.MultiplyPoint3x4(positions[i]))) members.Add(i);
                    }
                    if (members.Count > 0) parts.Add((anchor, LabelOf(anchor), true, members.ToArray()));
                }
                else if (anchor.PlaneRect.HasValue && !anchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME))
                {
                    var rect = anchor.PlaneRect.Value;
                    for (var i = 0; i < vertexCount; i++)
                    {
                        var p = worldToLocal.MultiplyPoint3x4(positions[i]);
                        if (Mathf.Abs(p.z) < 0.12f && p.x >= rect.xMin - 0.05f && p.x <= rect.xMax + 0.05f && p.y >= rect.yMin - 0.05f && p.y <= rect.yMax + 0.05f)
                        {
                            members.Add(i);
                        }
                    }
                    if (members.Count > 0) parts.Add((anchor, LabelOf(anchor), false, members.ToArray()));
                }
            }
        }

        private static float MinY(Vector3[] points)
        {
            var y = float.MaxValue;
            foreach (var p in points) y = Mathf.Min(y, p.y);
            return y == float.MaxValue ? 0f : y;
        }

        private static string LabelOf(MRUKAnchor anchor)
        {
            var text = anchor.Label.ToString();
            var comma = text.IndexOf(',');
            if (comma >= 0) text = text.Substring(0, comma);
            return text.Replace('_', ' ').ToLowerInvariant();
        }

        private void RebuildOverlays()
        {
            if (room == null) return;
            BuildOutlines(room);
            var wasVisible = mini == null || mini.Visible;
            mini?.Destroy();
            mini = miniMaterial != null ? new MiniRoomModel(room, miniMaterial, entranceUuid) { Visible = wasVisible } : null;
            if (mini != null && captured.Count > 0) mini.SetCoverage(captured);
        }

        /// <summary>Wall, floor and ceiling rectangles, doors/windows and furniture boxes as thin lines.</summary>
        private void BuildOutlines(MRUKRoom loaded)
        {
            if (outlineObject != null) Destroy(outlineObject);
            var points = new List<Vector3>();
            var lineColors = new List<Color32>();
            void Segment(Vector3 a, Vector3 b, Color32 color)
            {
                points.Add(a);
                points.Add(b);
                lineColors.Add(color);
                lineColors.Add(color);
            }
            var plain = new Color32(235, 245, 255, 255);
            foreach (var anchor in loaded.Anchors)
            {
                if (anchor == null) continue;
                var m = anchor.transform.localToWorldMatrix;
                if (anchor.VolumeBounds.HasValue)
                {
                    var b = anchor.VolumeBounds.Value;
                    var corners = new Vector3[8];
                    for (var i = 0; i < 8; i++)
                    {
                        var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                        corners[i] = m.MultiplyPoint3x4(c);
                    }
                    foreach (var (i, j) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                    {
                        Segment(corners[i], corners[j], plain);
                    }
                }
                else if (anchor.PlaneRect.HasValue)
                {
                    var color = plain;
                    if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME))
                    {
                        color = UuidOf(anchor) == entranceUuid ? new Color32(70, 230, 110, 255) : new Color32(110, 160, 255, 255);
                    }
                    var r = anchor.PlaneRect.Value;
                    var a = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMin, 0f));
                    var b = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMin, 0f));
                    var c = m.MultiplyPoint3x4(new Vector3(r.xMax, r.yMax, 0f));
                    var d = m.MultiplyPoint3x4(new Vector3(r.xMin, r.yMax, 0f));
                    Segment(a, b, color);
                    Segment(b, c, color);
                    Segment(c, d, color);
                    Segment(d, a, color);
                }
            }
            if (points.Count == 0) return;
            var mesh = new Mesh { name = "Room outlines", indexFormat = points.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(points);
            mesh.SetColors(lineColors);
            var indices = new int[points.Count];
            for (var i = 0; i < indices.Length; i++) indices[i] = i;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            outlineObject = new GameObject("Room Outlines");
            outlineObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = outlineObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = outlineMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        // ---------------------------------------------------------------- room model for the laptop

        /// <summary>
        /// The room as JSON for the laptop (room_model.json; schema in metaquest-3d-reconstruction
        /// processing/web_location/room_model.py): per anchor the labels, Unity world pose, plane rectangle and
        /// outline (local XY, normal +Z) and volume bounds (local); plus the entrance. Unity world = the frame of
        /// the camera poses, so the laptop lines it up with the scan.
        /// </summary>
        private void SendRoomModel()
        {
            var client = LiveStreamClient.Active;
            if (client == null || room == null) return;
            var sb = new StringBuilder();
            sb.Append("{\"version\":1,\"coordinate_system\":\"unity_world\",\"anchors\":[");
            var first = true;
            foreach (var anchor in room.Anchors)
            {
                if (anchor == null || anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH)) continue;
                if (!first) sb.Append(',');
                first = false;
                var t = anchor.transform;
                sb.Append("{\"uuid\":").Append(LiveStreamProtocol.Str(UuidOf(anchor)));
                sb.Append(",\"labels\":[");
                var labels = anchor.Label.ToString().Split(',');
                for (var i = 0; i < labels.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(LiveStreamProtocol.Str(labels[i].Trim()));
                }
                sb.Append("],\"pose\":").Append(LiveStreamProtocol.PoseJson(t.position, t.rotation));
                if (anchor.PlaneRect.HasValue)
                {
                    var r = anchor.PlaneRect.Value;
                    sb.Append(",\"plane_rect\":[").Append(F(r.xMin)).Append(',').Append(F(r.yMin)).Append(',').Append(F(r.xMax)).Append(',').Append(F(r.yMax)).Append(']');
                    var boundary = anchor.PlaneBoundary2D;
                    if (boundary != null && boundary.Count >= 3)
                    {
                        sb.Append(",\"plane_boundary\":[");
                        for (var i = 0; i < boundary.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append('[').Append(F(boundary[i].x)).Append(',').Append(F(boundary[i].y)).Append(']');
                        }
                        sb.Append(']');
                    }
                }
                if (anchor.VolumeBounds.HasValue)
                {
                    var b = anchor.VolumeBounds.Value;
                    sb.Append(",\"volume\":{\"min\":[").Append(F(b.min.x)).Append(',').Append(F(b.min.y)).Append(',').Append(F(b.min.z))
                      .Append("],\"max\":[").Append(F(b.max.x)).Append(',').Append(F(b.max.y)).Append(',').Append(F(b.max.z)).Append("]}");
                }
                sb.Append('}');
            }
            sb.Append("],\"entrance\":");
            sb.Append(entranceUuid != null ? "{\"anchor_uuid\":" + LiveStreamProtocol.Str(entranceUuid) + "}" : "null");
            sb.Append('}');
            client.SetRoomModel(sb.ToString());
        }

        private static string F(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- input and per-frame work

        private void Update()
        {
            HandleInput();
            if (mini != null && UnityEngine.Camera.main != null) mini.Follow(UnityEngine.Camera.main.transform);
            if (coverageBuffer == null || compute == null) return;

            var recording = Recording;
            if (recording && !wasRecording)
            {
                ResetCoverage();  // every recording starts with the full grid
                SendRoomModel();  // and the laptop gets the room for this session
            }
            wasRecording = recording;

            var now = Time.realtimeSinceStartup;
            if (recording && now >= nextUpdate)
            {
                nextUpdate = now + UpdateIntervalS;
                DispatchUpdate();
            }
            if (!readbackPending && now >= nextReadback)
            {
                nextReadback = now + ReadbackIntervalS;
                readbackPending = true;
                AsyncGPUReadback.Request(coverageBuffer, OnReadback);
            }
            if (flash.Length > 0 && now > flashUntil)
            {
                flash = string.Empty;
                PublishSummary();
            }
        }

        private void HandleInput()
        {
            if (OVRInput.GetDown(OVRInput.Button.Four) && !Recording && !editing)
            {
                EditRoom();
            }
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) && mini != null)
            {
                mini.Visible = !mini.Visible;
            }
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            {
                var client = LiveStreamClient.Active;
                Flash(client != null && client.RequestDetail(DetailSeconds)
                    ? "DETAIL: hold it in view, 2 s of frames kept"
                    : "detail: start recording first");
            }
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                PickEntrance();
            }
        }

        /// <summary>Right-trigger ray at a door frame: that door becomes the entrance where visitors start.</summary>
        private void PickEntrance()
        {
            if (room == null) return;
            var rig = FindAnyObjectByType<OVRCameraRig>();
            var hand = rig != null ? rig.rightControllerAnchor : null;
            if (hand == null) return;
            var ray = new Ray(hand.position, hand.forward);
            if (room.Raycast(ray, 15f, LabelFilter.Included(MRUKAnchor.SceneLabels.DOOR_FRAME), out _, out var door) && door != null)
            {
                entranceUuid = UuidOf(door);
                RebuildOverlays();
                SendRoomModel();
                Flash("entrance set: visitors start at this door");
            }
            else
            {
                Flash("point at a door to make it the entrance (no doors? Y: add them in Space Setup)");
            }
        }

        private async void EditRoom()
        {
            editing = true;
            SetSummary("opening Space Setup…");
            try
            {
                await OVRScene.RequestSpaceSetup();
                await LoadRoom(requestSpaceSetupIfMissing: false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Space Setup failed: {ex.Message}");
            }
            finally
            {
                editing = false;
            }
        }

        private void ResetCoverage()
        {
            if (compute == null || coverageBuffer == null) return;
            compute.SetBuffer(resetKernel, "_Coverage", coverageBuffer);
            compute.SetInt("_VertexCount", vertexCount);
            compute.Dispatch(resetKernel, Mathf.Max(1, Mathf.CeilToInt(vertexCount / 64f)), 1, 1);
            lastCountedPosition = new Vector3(float.MaxValue, 0f, 0f);
        }

        /// <summary>Count one view for every visible grid vertex: only with a slow head and a new viewpoint.</summary>
        private void DispatchUpdate()
        {
            if (compute == null || vertexBuffer == null || coverageBuffer == null || depthProvider == null) return;
            if (!depthProvider.TryGetLatestFrame(out var depthTexture, out var descriptors) || descriptors.Length <= EyeIndex) return;
            var descriptor = descriptors[EyeIndex];
            var client = LiveStreamClient.Active;
            if (client != null && client.AngularSpeedDps > client.Settings.maxAngularSpeedDps) return;
            var cameraToWorld = Matrix4x4.TRS(descriptor.createPoseLocation, descriptor.createPoseRotation, new Vector3(1f, 1f, -1f));
            Vector3 position = cameraToWorld.GetColumn(3);
            var moved = Vector3.Distance(position, lastCountedPosition) >= NewViewpointMeters
                        || Quaternion.Angle(descriptor.createPoseRotation, lastCountedRotation) >= NewViewpointDegrees;
            if (!moved) return;
            lastCountedPosition = position;
            lastCountedRotation = descriptor.createPoseRotation;

            var infiniteFar = float.IsInfinity(descriptor.farZ) || float.IsNaN(descriptor.farZ) || descriptor.farZ < descriptor.nearZ;
            compute.SetTexture(updateKernel, "_DepthTexture", depthTexture);
            compute.SetBuffer(updateKernel, "_Vertices", vertexBuffer);
            compute.SetBuffer(updateKernel, "_Coverage", coverageBuffer);
            compute.SetInt("_VertexCount", vertexCount);
            compute.SetInt("_Width", depthTexture.width);
            compute.SetInt("_Height", depthTexture.height);
            compute.SetInt("_EyeIndex", EyeIndex);
            compute.SetInt("_UseInfiniteFar", infiniteFar ? 1 : 0);
            compute.SetInt("_FlipVerticalProjection", FlipVerticalProjection ? 1 : 0);
            compute.SetFloat("_FovLeftTan", descriptor.fovLeftAngleTangent);
            compute.SetFloat("_FovRightTan", descriptor.fovRightAngleTangent);
            compute.SetFloat("_FovTopTan", descriptor.fovTopAngleTangent);
            compute.SetFloat("_FovDownTan", descriptor.fovDownAngleTangent);
            compute.SetFloat("_NearZ", descriptor.nearZ);
            compute.SetFloat("_FarZ", infiniteFar ? 0f : descriptor.farZ);
            compute.SetFloat("_MinDepthMeters", MinDepthMeters);
            compute.SetFloat("_MaxDepthMeters", MaxDepthMeters);
            compute.SetMatrix("_WorldToDepthCamera", cameraToWorld.inverse);
            compute.Dispatch(updateKernel, Mathf.Max(1, Mathf.CeilToInt(vertexCount / 64f)), 1, 1);
        }

        private static float Quality(float sharpViews, float closestMeters, float close)
        {
            var detail = closestMeters >= 999f ? 0f : Mathf.InverseLerp(close + DetailFalloffMeters, close, closestMeters);
            return Mathf.Min(detail, Mathf.Clamp01(sharpViews / GoodViews));
        }

        private void OnReadback(AsyncGPUReadbackRequest request)
        {
            readbackPending = false;
            if (request.hasError || gridMesh == null || this == null) return;
            var data = request.GetData<Vector2>();
            if (data.Length != vertexCount || needClose.Length != vertexCount) return;
            var quality = new float[vertexCount];
            double total = 0, done = 0;
            for (var i = 0; i < vertexCount; i++)
            {
                var q = Quality(data[i].x, data[i].y, needClose[i]);
                quality[i] = q;
                colors[i].a = (byte)Mathf.RoundToInt(255f * (1f - q));
                total += vertexArea[i];
                done += vertexArea[i] * q;
            }
            gridMesh.SetColors(colors);

            var objects = new List<(string Label, float Percent)>();
            captured.Clear();
            foreach (var (anchor, label, furniture, vertices) in parts)
            {
                double area = 0, got = 0;
                foreach (var i in vertices)
                {
                    area += vertexArea[i];
                    got += vertexArea[i] * quality[i];
                }
                if (area <= 0) continue;
                var fraction = (float)(got / area);
                captured[anchor] = fraction;
                if (furniture) objects.Add((label, fraction * 100f));
            }
            mini?.SetCoverage(captured);
            objects.Sort((a, b) => a.Percent.CompareTo(b.Percent));
            var summary = $"room {(total > 0 ? 100.0 * done / total : 0):0}% captured";
            var shown = 0;
            foreach (var (label, percent) in objects)
            {
                if (percent >= 90f || shown >= 3) break;
                summary += $" · {label} {percent:0}%";
                shown++;
            }
            if (!Recording) summary += "  (Y: edit room · trigger at a door: entrance)";
            SetSummary(summary);
        }

        private void SetSummary(string text)
        {
            roomSummary = text;
            PublishSummary();
        }

        private void Flash(string text)
        {
            flash = text;
            flashUntil = Time.realtimeSinceStartup + 3f;
            PublishSummary();
        }

        private void PublishSummary()
        {
            CaptureGuidance.Summary = flash.Length > 0 ? $"{flash}\n{roomSummary}" : roomSummary;
        }

        private void ReleaseGrid()
        {
            vertexBuffer?.Release();
            vertexBuffer = null;
            coverageBuffer?.Release();
            coverageBuffer = null;
            if (gridObject != null) Destroy(gridObject);
            gridObject = null;
            if (gridMesh != null) Destroy(gridMesh);
            gridMesh = null;
            vertexCount = 0;
        }

        private void OnDestroy()
        {
            ReleaseGrid();
            if (outlineObject != null) Destroy(outlineObject);
            mini?.Destroy();
            if (usingDepth && depthProvider != null) depthProvider.EndDepthUsage();
        }
    }
}
