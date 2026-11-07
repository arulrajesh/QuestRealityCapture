#nullable enable
using System;
using System.IO;
using RealityLog.Streaming;
using Unity.Collections;
using UnityEngine;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "encode") return Encode(args[1]);
        if (args.Length == 2 && args[0] == "decode") return Decode(args[1]);
        Console.WriteLine("usage: encode <out.bin> | decode <in.bin>");
        return 2;
    }

    private static int Encode(string path)
    {
        using var file = File.Create(path);
        void Put(byte[] frame) => file.Write(frame, 0, frame.Length);
        var intrinsics = "{\"backend\":\"MRUK\",\"focalLength\":{\"x\":870.07,\"y\":870.07},\"principalPoint\":{\"x\":642.97,\"y\":639.24},\"resolution\":{\"width\":4,\"height\":2}}";
        Put(LiveStreamMessages.Hello("Quest 3 \"stub\"", "1.6.0-live"));
        Put(LiveStreamMessages.SessionStart("20261106_130000", false, intrinsics, intrinsics));
        var color = new ColorJob
        {
            Left = true, FrameIndex = 1, TimestampUs = 1793946809031963, UnixTimeMs = 1793946809031, Width = 4, Height = 2,
            Position = new Vector3(-0.03f, 1.70f, 0.07f), Rotation = new Quaternion(0.0027f, 0.0216f, -0.0042f, 0.9997f), PoseOk = true,
            Pixels = new NativeArray<Color32>(8, Allocator.Persistent),
        };
        Put(LiveStreamMessages.Color(color, new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 }, "top_down"));
        var depth = new DepthJob
        {
            Left = false, TimestampMs = 1793946808971, OvrTimestamp = 941.138303, Width = 3, Height = 2,
            Position = new Vector3(-0.034f, 1.71f, 0.004f), Rotation = new Quaternion(0.0934f, -0.0142f, 0.0034f, -0.9955f),
            FovLeft = 1.376f, FovRight = 0.839f, FovTop = 0.966f, FovDown = 1.428f, NearZ = 0.1f, FarZ = float.PositiveInfinity,
            Ndc = new[] { 0f, 0.5f, 0.98f, 1f, float.NaN, 0.25f },
        };
        Put(LiveStreamMessages.Depth(depth, "u16_ndc"));
        Put(LiveStreamMessages.Depth(depth, "f32"));
        Put(LiveStreamMessages.Heartbeat(1793946809100, 3, 1));
        Put(LiveStreamMessages.SessionEnd("user_stop", 1));
        Put(LiveStreamMessages.Bye("app_quit"));
        Put(LiveStreamMessages.RoomModel("{\"version\":1,\"anchors\":[{\"uuid\":\"d1\",\"labels\":[\"DOOR_FRAME\"]}],\"entrance\":{\"anchor_uuid\":\"d1\"}}"));
        Put(LiveStreamMessages.DetailMarker(1793946809031963, 1793946811031963));
        Console.WriteLine($"encoded 8 messages to {path}");
        return 0;
    }

    private static int Decode(string path)
    {
        using var file = File.OpenRead(path);
        int keyframes = 0, meshes = 0, statuses = 0;
        while (true)
        {
            var message = IncomingMessage.Read(file);
            if (message == null) break;
            switch (message.Type)
            {
                case LiveStreamProtocol.KeyframeNotice:
                    var k = JsonUtility.FromJson<KeyframeNoticeJson>(message.HeaderJson);
                    Console.WriteLine($"KEYFRAME ts={k.timestamp_us} kept={k.depth_kept} color={k.color_wanted} reason={k.reason} pose=({k.pose.Position.x:0.###},{k.pose.Position.y:0.###},{k.pose.Position.z:0.###})");
                    keyframes++;
                    break;
                case LiveStreamProtocol.SurfaceMesh:
                    var m = JsonUtility.FromJson<SurfaceMeshJson>(message.HeaderJson);
                    var expected = m.vertex_count * 10 + m.triangle_count * 3 * (m.index_bits == 16 ? 2 : 4);
                    Console.WriteLine($"SURFACE_MESH seq={m.sequence} verts={m.vertex_count} tris={m.triangle_count} bits={m.index_bits} payload_ok={message.Payload.Length == expected}");
                    var first = BitConverter.ToUInt16(message.Payload, 0);
                    Console.WriteLine($"  first vertex x = {m.origin[0] + m.step_m * first:0.###}");
                    meshes++;
                    break;
                case LiveStreamProtocol.Status:
                    var s = JsonUtility.FromJson<StatusJson>(message.HeaderJson);
                    Console.WriteLine($"STATUS session={s.session} recording={s.recording} keyframes={s.keyframes} surface={s.surface_m2}");
                    statuses++;
                    break;
                default:
                    Console.WriteLine($"other type {message.Type}");
                    break;
            }
        }
        Console.WriteLine($"decoded keyframes={keyframes} meshes={meshes} statuses={statuses}");
        return keyframes > 0 && meshes > 0 && statuses > 0 ? 0 : 1;
    }
}
