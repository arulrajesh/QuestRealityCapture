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
        public int colorQueueSize = 8;
        /// <summary>Parallel JPEG encoders. One managed ~13 JPEGs/s at 1728².</summary>
        public int encoderThreads = 3;
        public int depthQueueSize = 4;
        public bool showHud = true;
        public bool showSurfaceOverlay = false;  // laptop live mesh; fidelity mode uses the headset's own coverage instead
        /// <summary>Overlay coloring: "confidence" (green = solid, red = needs more looking) or "color" (camera colors).</summary>
        public string overlayMode = "confidence";
        public float overlayOpacity = 0.45f;
        // Headset-side color gate (fidelity mode, 2026-11-07): dense views, never while turning fast.
        // The laptop's compaction still keeps only the sharpest frame per short window.
        public float colorMinTranslationM = 0.01f;
        public float colorMinRotationDeg = 1f;
        public float colorMaxFps = 15f;  // stereo pairs per second; view count was the strongest splat-quality lever
        public float colorMaxIntervalS = 1.5f;
        /// <summary>Frames are not sent above this head speed: photo-to-model error was 5 px at 25-50 deg/s, 1.3 px below 10.</summary>
        public float maxAngularSpeedDps = 30f;
        /// <summary>The HUD shows "slow down" above this head speed (deg/s).</summary>
        public float slowDownDps = 20f;
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
