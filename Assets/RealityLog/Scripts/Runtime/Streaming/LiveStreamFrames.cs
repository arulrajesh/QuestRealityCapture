#nullable enable

using System;
using Unity.Collections;
using UnityEngine;

namespace RealityLog.Streaming
{
    [Serializable]
    public sealed class LiveStreamSettings
    {
        public bool enabled = true;
        public string host = string.Empty;
        public int port = LiveStreamProtocol.DefaultPort;
        public int discoveryPort = LiveStreamProtocol.DefaultDiscoveryPort;
        public bool recordOnHeadset = false;
        public int jpegQuality = 90;
        /// <summary>Row order of the JPEG Unity produces from the bottom-up MRUK buffer. Flip here if images arrive upside down.</summary>
        public string jpegRowOrder = "top_down";
        public string depthEncoding = "u16_ndc";
        public int colorQueueSize = 4;
        public int depthQueueSize = 4;
        public bool showHud = true;
        public bool showSurfaceOverlay = true;
        /// <summary>Overlay coloring: "confidence" (green = solid, red = needs more looking) or "color" (camera colors).</summary>
        public string overlayMode = "confidence";
        public float overlayOpacity = 0.45f;
        // Headset-side color gate (the laptop's Kalman selection decides what is finally kept).
        public float colorMinTranslationM = 0.03f;
        public float colorMinRotationDeg = 3f;
        public float colorMaxFps = 5f;
        public float colorMaxIntervalS = 1.5f;
        public float maxAngularSpeedDps = 150f;
    }

    /// <summary>Color frame captured on the main thread. Owns <see cref="Pixels"/> (bottom-up RGBA32) until encoded.</summary>
    public sealed class ColorJob : IDisposable
    {
        public bool Left;
        public int FrameIndex;
        public long TimestampUs;
        public long UnixTimeMs;
        public int Width;
        public int Height;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool PoseOk;
        public NativeArray<Color32> Pixels;

        public void Dispose()
        {
            if (Pixels.IsCreated)
            {
                Pixels.Dispose();
            }
        }
    }

    /// <summary>One eye of a Depth API frame: raw NDC floats, row-major as stored in .raw files.</summary>
    public sealed class DepthJob
    {
        public bool Left;
        public long TimestampMs;
        public double OvrTimestamp;
        public int Width;
        public int Height;
        public Vector3 Position;
        public Quaternion Rotation;
        public float FovLeft;
        public float FovRight;
        public float FovTop;
        public float FovDown;
        public float NearZ;
        public float FarZ;
        public float[] Ndc = Array.Empty<float>();
    }
}
