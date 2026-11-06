#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace RealityLog.Streaming
{
    /// <summary>
    /// Wire framing for the MQ3DRecon live streaming protocol, version 1.
    /// Spec: metaquest-3d-reconstruction/specs/LIVE_STREAMING.md.
    /// Frame: "MQLS" | u16 version | u16 type | u32 header length | u32 payload length | JSON header | payload.
    /// All integers little-endian.
    /// </summary>
    public static class LiveStreamProtocol
    {
        public const ushort Version = 1;
        public const int DefaultPort = 9870;
        public const int DefaultDiscoveryPort = 9871;
        public const string DiscoveryProbe = "MQLS?";
        public const string ServiceName = "mq3drecon-live";
        public const string EndpointFileName = "live_stream_endpoint.json";

        public const ushort Hello = 1;
        public const ushort SessionStart = 2;
        public const ushort Color = 3;
        public const ushort Depth = 4;
        public const ushort Heartbeat = 5;
        public const ushort SessionEnd = 6;
        public const ushort Bye = 7;
        public const ushort KeyframeNotice = 20;
        public const ushort SurfaceMesh = 21;
        public const ushort Status = 22;

        private static readonly byte[] Magic = { (byte)'M', (byte)'Q', (byte)'L', (byte)'S' };

        public static byte[] Encode(ushort type, string headerJson, byte[]? payload)
        {
            var header = Encoding.UTF8.GetBytes(headerJson);
            var payloadLength = payload?.Length ?? 0;
            var frame = new byte[16 + header.Length + payloadLength];
            Buffer.BlockCopy(Magic, 0, frame, 0, 4);
            WriteUInt16(frame, 4, Version);
            WriteUInt16(frame, 6, type);
            WriteUInt32(frame, 8, (uint)header.Length);
            WriteUInt32(frame, 12, (uint)payloadLength);
            Buffer.BlockCopy(header, 0, frame, 16, header.Length);
            if (payload != null && payloadLength > 0)
            {
                Buffer.BlockCopy(payload, 0, frame, 16 + header.Length, payloadLength);
            }
            return frame;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        // JSON helpers (invariant culture; Infinity/NaN are written as bare tokens, which the receiver accepts).

        public static string Num(float value)
        {
            if (float.IsPositiveInfinity(value)) return "Infinity";
            if (float.IsNegativeInfinity(value)) return "-Infinity";
            if (float.IsNaN(value)) return "NaN";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Num(double value)
        {
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            if (double.IsNaN(value)) return "NaN";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

        public static string Str(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Bool(bool value) => value ? "true" : "false";

        public static string PoseJson(Vector3 position, Quaternion rotation)
        {
            return "{\"position\":[" + Num(position.x) + "," + Num(position.y) + "," + Num(position.z) +
                   "],\"rotation\":[" + Num(rotation.x) + "," + Num(rotation.y) + "," + Num(rotation.z) + "," + Num(rotation.w) + "]}";
        }

        public static string SideName(bool left) => left ? "left" : "right";
    }

    /// <summary>Reads the endpoint file the laptop pushes over adb: {"host": "...", "port": 9870}.</summary>
    [Serializable]
    public sealed class LiveStreamEndpointFile
    {
        public string host = string.Empty;
        public int port = LiveStreamProtocol.DefaultPort;

        /// <param name="dataDirectory">Application.persistentDataPath, captured on the main thread.</param>
        public static LiveStreamEndpointFile? TryRead(string dataDirectory)
        {
            try
            {
                var path = Path.Combine(dataDirectory, LiveStreamProtocol.EndpointFileName);
                if (!File.Exists(path))
                {
                    return null;
                }
                var parsed = JsonUtility.FromJson<LiveStreamEndpointFile>(File.ReadAllText(path));
                return string.IsNullOrWhiteSpace(parsed?.host) ? null : parsed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Could not read {LiveStreamProtocol.EndpointFileName}: {ex.Message}");
                return null;
            }
        }
    }
}
