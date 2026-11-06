#nullable enable

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RealityLog.Streaming
{
    /// <summary>Builders for headset → laptop messages (headers match specs/LIVE_STREAMING.md).</summary>
    public static class LiveStreamMessages
    {
        public static byte[] Hello(string device, string appVersion)
        {
            var header = "{\"device\":" + LiveStreamProtocol.Str(device) + ",\"app_version\":" + LiveStreamProtocol.Str(appVersion) + "}";
            return LiveStreamProtocol.Encode(LiveStreamProtocol.Hello, header, null);
        }

        public static byte[] SessionStart(string sessionName, bool resume, string? leftIntrinsicsJson, string? rightIntrinsicsJson)
        {
            var sb = new StringBuilder();
            sb.Append("{\"session_name\":").Append(LiveStreamProtocol.Str(sessionName));
            sb.Append(",\"resume\":").Append(LiveStreamProtocol.Bool(resume));
            sb.Append(",\"intrinsics\":{");
            var first = true;
            if (!string.IsNullOrEmpty(leftIntrinsicsJson))
            {
                sb.Append("\"left\":").Append(leftIntrinsicsJson);
                first = false;
            }
            if (!string.IsNullOrEmpty(rightIntrinsicsJson))
            {
                if (!first) sb.Append(',');
                sb.Append("\"right\":").Append(rightIntrinsicsJson);
            }
            sb.Append("}}");
            return LiveStreamProtocol.Encode(LiveStreamProtocol.SessionStart, sb.ToString(), null);
        }

        public static byte[] Color(ColorJob job, byte[] jpeg, string rowOrder)
        {
            var header = "{\"side\":\"" + LiveStreamProtocol.SideName(job.Left) + "\"" +
                         ",\"frame_index\":" + LiveStreamProtocol.Num(job.FrameIndex) +
                         ",\"timestamp_us_realtime\":" + LiveStreamProtocol.Num(job.TimestampUs) +
                         ",\"unix_time_ms\":" + LiveStreamProtocol.Num(job.UnixTimeMs) +
                         ",\"width\":" + LiveStreamProtocol.Num(job.Width) +
                         ",\"height\":" + LiveStreamProtocol.Num(job.Height) +
                         ",\"encoding\":\"jpeg\",\"row_order\":" + LiveStreamProtocol.Str(rowOrder) +
                         ",\"pose\":" + LiveStreamProtocol.PoseJson(job.Position, job.Rotation) +
                         ",\"pose_ok\":" + LiveStreamProtocol.Bool(job.PoseOk) + "}";
            return LiveStreamProtocol.Encode(LiveStreamProtocol.Color, header, jpeg);
        }

        public static byte[] Depth(DepthJob job, string encoding)
        {
            byte[] payload;
            if (encoding == "f32")
            {
                payload = new byte[job.Ndc.Length * 4];
                Buffer.BlockCopy(job.Ndc, 0, payload, 0, payload.Length);
            }
            else
            {
                encoding = "u16_ndc";
                payload = new byte[job.Ndc.Length * 2];
                for (var i = 0; i < job.Ndc.Length; ++i)
                {
                    var v = job.Ndc[i];
                    var q = float.IsNaN(v) ? 0 : (int)Math.Round(Mathf.Clamp01(v) * 65535f);
                    payload[2 * i] = (byte)(q & 0xFF);
                    payload[2 * i + 1] = (byte)(q >> 8);
                }
            }
            var header = "{\"side\":\"" + LiveStreamProtocol.SideName(job.Left) + "\"" +
                         ",\"timestamp_ms\":" + LiveStreamProtocol.Num(job.TimestampMs) +
                         ",\"ovr_timestamp\":" + LiveStreamProtocol.Num(job.OvrTimestamp) +
                         ",\"width\":" + LiveStreamProtocol.Num(job.Width) +
                         ",\"height\":" + LiveStreamProtocol.Num(job.Height) +
                         ",\"encoding\":" + LiveStreamProtocol.Str(encoding) +
                         ",\"create_pose\":" + LiveStreamProtocol.PoseJson(job.Position, job.Rotation) +
                         ",\"fov\":{\"left\":" + LiveStreamProtocol.Num(job.FovLeft) + ",\"right\":" + LiveStreamProtocol.Num(job.FovRight) +
                         ",\"top\":" + LiveStreamProtocol.Num(job.FovTop) + ",\"down\":" + LiveStreamProtocol.Num(job.FovDown) + "}" +
                         ",\"near_z\":" + LiveStreamProtocol.Num(job.NearZ) +
                         ",\"far_z\":" + (float.IsInfinity(job.FarZ) ? "null" : LiveStreamProtocol.Num(job.FarZ)) + "}";
            return LiveStreamProtocol.Encode(LiveStreamProtocol.Depth, header, payload);
        }

        public static byte[] Heartbeat(long timeMs, long queued, long dropped)
        {
            var header = "{\"time_ms\":" + LiveStreamProtocol.Num(timeMs) +
                         ",\"queued\":{\"all\":" + LiveStreamProtocol.Num(queued) + "}" +
                         ",\"dropped\":{\"all\":" + LiveStreamProtocol.Num(dropped) + "}}";
            return LiveStreamProtocol.Encode(LiveStreamProtocol.Heartbeat, header, null);
        }

        public static byte[] SessionEnd(string reason, long dropped)
        {
            var header = "{\"reason\":" + LiveStreamProtocol.Str(reason) + ",\"dropped\":{\"all\":" + LiveStreamProtocol.Num(dropped) + "}}";
            return LiveStreamProtocol.Encode(LiveStreamProtocol.SessionEnd, header, null);
        }

        public static byte[] Bye(string reason)
        {
            return LiveStreamProtocol.Encode(LiveStreamProtocol.Bye, "{\"reason\":" + LiveStreamProtocol.Str(reason) + "}", null);
        }
    }

    /// <summary>A framed message read from the laptop.</summary>
    public sealed class IncomingMessage
    {
        public ushort Type;
        public string HeaderJson = string.Empty;
        public byte[] Payload = Array.Empty<byte>();

        public static IncomingMessage? Read(Stream stream)
        {
            var prefix = ReadExact(stream, 16, allowEof: true);
            if (prefix == null)
            {
                return null;
            }
            if (prefix[0] != 'M' || prefix[1] != 'Q' || prefix[2] != 'L' || prefix[3] != 'S')
            {
                throw new IOException("Bad magic from laptop");
            }
            var version = (ushort)(prefix[4] | (prefix[5] << 8));
            if (version != LiveStreamProtocol.Version)
            {
                throw new IOException($"Laptop speaks protocol version {version}, headset speaks {LiveStreamProtocol.Version}");
            }
            var type = (ushort)(prefix[6] | (prefix[7] << 8));
            var headerLength = BitConverter.ToInt32(prefix, 8);
            var payloadLength = BitConverter.ToInt32(prefix, 12);
            if (headerLength < 0 || headerLength > 64 * 1024 || payloadLength < 0 || payloadLength > 64 * 1024 * 1024)
            {
                throw new IOException("Invalid frame lengths from laptop");
            }
            var header = ReadExact(stream, headerLength, allowEof: false) ?? Array.Empty<byte>();
            var payload = payloadLength > 0 ? ReadExact(stream, payloadLength, allowEof: false) ?? Array.Empty<byte>() : Array.Empty<byte>();
            return new IncomingMessage { Type = type, HeaderJson = Encoding.UTF8.GetString(header), Payload = payload };
        }

        private static byte[]? ReadExact(Stream stream, int size, bool allowEof)
        {
            var buffer = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = stream.Read(buffer, offset, size - offset);
                if (read <= 0)
                {
                    if (allowEof && offset == 0) return null;
                    throw new IOException("Connection closed mid-message");
                }
                offset += read;
            }
            return buffer;
        }
    }

    [Serializable]
    public sealed class PoseJson
    {
        public float[] position = new float[3];
        public float[] rotation = { 0f, 0f, 0f, 1f };

        public Vector3 Position => new(position[0], position[1], position[2]);
        public Quaternion Rotation => new(rotation[0], rotation[1], rotation[2], rotation[3]);
    }

    [Serializable]
    public sealed class KeyframeNoticeJson
    {
        public long timestamp_us;
        public bool depth_kept;
        public bool color_wanted;
        public string reason = string.Empty;
        public float novelty;
        public float info_gain;
        public PoseJson pose = new();
    }

    [Serializable]
    public sealed class SurfaceMeshJson
    {
        public int sequence;
        public int segment;
        public float[] origin = new float[3];
        public float step_m;
        public int vertex_count;
        public int triangle_count;
        public int index_bits = 32;
    }

    [Serializable]
    public sealed class StatusJson
    {
        public string session = string.Empty;
        public bool recording;
        public int depth_frames;
        public int keyframes;
        public int color_keyframes;
        public int triangles;
        public float surface_m2;
        public float median_sigma_mm;
        public int dropped_live;
        public int dropped_headset;
        public int tracking_jumps;
        public int segment;
    }

    [Serializable]
    public sealed class DiscoveryReplyJson
    {
        public string service = string.Empty;
        public int protocol_version;
        public int port;
        public string host = string.Empty;
    }
}
