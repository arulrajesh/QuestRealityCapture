#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace RealityLog.Streaming
{
    /// <summary>
    /// Streams capture frames to the MQ3DRecon laptop receiver and receives live feedback.
    /// Threads: network (connect, send, heartbeat), reader (feedback), and <see cref="LiveStreamSettings.encoderThreads"/>
    /// encoders (JPEG / u16 depth). One encoder managed only ~13 JPEGs/s at 1728² (6.6 stereo pairs/s, 2026-11-07).
    /// Public methods are main-thread safe and never block on the network.
    /// </summary>
    public sealed class LiveStreamClient : MonoBehaviour
    {
        public static LiveStreamClient? Active { get; private set; }

        public LiveStreamSettings Settings { get; private set; } = new();
        public bool IsConnected => connected;
        public string Endpoint { get; private set; } = string.Empty;
        public string StatusText { get; private set; } = "searching for laptop";
        public long FramesSent => Interlocked.Read(ref framesSent);
        public long FramesDropped => colorJobs.Dropped + depthJobs.Dropped + colorOut.Dropped + depthOut.Dropped;
        /// <summary>Color frames intentionally not sent (no head motion, or too fast to be sharp). Not a loss.</summary>
        public long FramesGated => Interlocked.Read(ref gatedFrames);
        public bool SessionActive => sessionName != null;
        /// <summary>Head (left camera) angular speed at the last sampled color frame, deg/s. Main thread.</summary>
        public float AngularSpeedDps { get; private set; }
        /// <summary>True while turning too fast for sharp frames (<see cref="LiveStreamSettings.slowDownDps"/>).</summary>
        public bool TooFast => AngularSpeedDps > Settings.slowDownDps;

        /// <summary>Feedback from the laptop, drained on the main thread by <see cref="LiveFeedbackRenderer"/>.</summary>
        public readonly ConcurrentQueue<IncomingMessage> Incoming = new();

        private DropOldestQueue<ColorJob> colorJobs = new(4, job => job.Dispose());
        private readonly DropOldestQueue<DepthJob> depthJobs = new(8);
        private readonly DropOldestQueue<byte[]> colorOut = new(8);
        private readonly DropOldestQueue<byte[]> depthOut = new(8);
        private readonly ConcurrentQueue<byte[]> control = new();
        private readonly SemaphoreSlim encodeWork = new(0);
        private readonly AutoResetEvent sendSignal = new(false);
        private Thread? networkThread;
        private Thread[] encodeThreads = Array.Empty<Thread>();
        private volatile bool stopping;
        private volatile bool connected;
        private string dataDirectory = string.Empty;
        private string deviceName = "Quest";
        private string appVersion = "unknown";
        private string? sessionName;
        private string? leftIntrinsics;
        private string? rightIntrinsics;
        private volatile string? pendingEndReason;
        private string? everAnnounced;
        private long framesSent;
        private long gatedFrames;
        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation = Quaternion.identity;
        private long lastSentColorUs;
        private long lastColorPoseUs;
        private Quaternion lastColorRotation = Quaternion.identity;

        public static LiveStreamClient Ensure(GameObject host)
        {
            if (Active != null) return Active;
            var existing = host.GetComponent<LiveStreamClient>();  // Unity fake-null: compare, do not use ??
            return existing != null ? existing : host.AddComponent<LiveStreamClient>();
        }

        public void Configure(LiveStreamSettings settings)
        {
            Settings = settings;
            dataDirectory = Application.persistentDataPath;
            deviceName = SystemInfo.deviceModel;
            appVersion = Application.version;
            if (settings.enabled && networkThread == null)
            {
                colorJobs = new DropOldestQueue<ColorJob>(Math.Max(2, settings.colorQueueSize), job => job.Dispose());
                networkThread = new Thread(NetworkLoop) { IsBackground = true, Name = "LiveStreamNetwork" };
                encodeThreads = new Thread[Math.Clamp(settings.encoderThreads, 1, 4)];
                for (var i = 0; i < encodeThreads.Length; ++i)
                {
                    encodeThreads[i] = new Thread(EncodeLoop)
                    {
                        IsBackground = true, Name = $"LiveStreamEncode{i}", Priority = System.Threading.ThreadPriority.BelowNormal,
                    };
                }
                networkThread.Start();
                foreach (var thread in encodeThreads) thread.Start();
            }
        }

        private void Awake()
        {
            Active = this;
        }

        private void OnDestroy()
        {
            stopping = true;
            encodeWork.Release(Math.Max(1, encodeThreads.Length));
            sendSignal.Set();
            colorJobs.Clear();
            if (Active == this) Active = null;
        }

        // ---------------------------------------------------------------- session API (main thread)

        public void BeginSession(string name, string? leftIntrinsicsJson, string? rightIntrinsicsJson)
        {
            sessionName = name;
            leftIntrinsics = leftIntrinsicsJson;
            rightIntrinsics = rightIntrinsicsJson;
            pendingEndReason = null;
            lastSentColorUs = 0;
            sendSignal.Set();  // the network thread announces the session (see RunConnection)
        }

        public void EndSession(string reason)
        {
            if (sessionName == null) return;
            pendingEndReason = reason;  // sent by the network thread once queued frames have drained
            sessionName = null;
            sendSignal.Set();
        }

        /// <summary>
        /// Main thread, before copying any pixels: should this color frame be sent? Stateful (it records the
        /// decision), so call it exactly once per sampled frame, left eye first. Copy the pixels and call
        /// <see cref="EnqueueColor"/> only when it returns true.
        /// </summary>
        public bool WantsColor(bool left, long timestampUs, Vector3 position, Quaternion rotation)
        {
            if (left) UpdateAngularSpeed(timestampUs, rotation);
            if (sessionName == null || !connected) return false;
            if (ShouldSendColor(left, timestampUs, position, rotation)) return true;
            Interlocked.Increment(ref gatedFrames);
            return false;
        }

        /// <summary>Hands over a copy of the MRUK pixels (bottom-up RGBA32) that <see cref="WantsColor"/> accepted. Ownership passes to the client.</summary>
        public void EnqueueColor(ColorJob job)
        {
            if (sessionName == null || !connected)
            {
                job.Dispose();
                return;
            }
            colorJobs.Enqueue(job);
            encodeWork.Release();
        }

        public void EnqueueDepth(DepthJob job)
        {
            if (sessionName == null || !connected) return;
            depthJobs.Enqueue(job);
            encodeWork.Release();
        }

        /// <summary>
        /// Headset-side gate (cheap; the laptop makes the real Kalman keyframe decision):
        /// send a color pair only after real head motion, never during very fast turns.
        /// The right eye follows the left eye's decision through the shared timestamp.
        /// </summary>
        private bool ShouldSendColor(bool left, long timestampUs, Vector3 position, Quaternion rotation)
        {
            if (!left)
            {
                return lastSentColorUs != 0 && Math.Abs(timestampUs - lastSentColorUs) <= 20_000;  // stereo pair
            }
            if (AngularSpeedDps > Settings.maxAngularSpeedDps)
            {
                return false;  // motion-blurred: photo-to-model error was 5 px at 25-50 deg/s vs 1.3 px below 10
            }
            var sinceLast = (timestampUs - lastSentColorUs) / 1e6f;
            var moved = Vector3.Distance(position, lastSentPosition) >= Settings.colorMinTranslationM
                        || Quaternion.Angle(rotation, lastSentRotation) >= Settings.colorMinRotationDeg;
            if (lastSentColorUs != 0 && sinceLast < 1f / Mathf.Max(0.1f, Settings.colorMaxFps)) return false;
            if (lastSentColorUs != 0 && !moved && sinceLast < Settings.colorMaxIntervalS) return false;
            lastSentColorUs = timestampUs;
            lastSentPosition = position;
            lastSentRotation = rotation;
            return true;
        }

        private void UpdateAngularSpeed(long timestampUs, Quaternion rotation)
        {
            var dt = lastColorPoseUs > 0 ? (timestampUs - lastColorPoseUs) / 1e6f : 0f;
            if (dt > 0f && dt < 0.5f)
            {
                var instant = Quaternion.Angle(lastColorRotation, rotation) / dt;
                AngularSpeedDps = Mathf.Lerp(AngularSpeedDps, instant, 0.6f);  // light smoothing over ~2 frames
            }
            lastColorPoseUs = timestampUs;
            lastColorRotation = rotation;
        }

        // ---------------------------------------------------------------- encoder thread

        /// <summary>Runs on every encoder thread. Frames may leave slightly out of order; the laptop orders by timestamp.</summary>
        private void EncodeLoop()
        {
            while (!stopping)
            {
                encodeWork.Wait(500);
                while (!stopping && depthJobs.TryDequeue(out var depth))
                {
                    depthOut.Enqueue(LiveStreamMessages.Depth(depth, Settings.depthEncoding));
                    sendSignal.Set();
                }
                if (!stopping && colorJobs.TryDequeue(out var color))
                {
                    try
                    {
                        using var jpeg = ImageConversion.EncodeNativeArrayToJPG(
                            color.Pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)color.Width, (uint)color.Height, 0, Settings.jpegQuality);
                        colorOut.Enqueue(LiveStreamMessages.Color(color, jpeg.ToArray(), Settings.jpegRowOrder));
                        sendSignal.Set();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] JPEG encode failed: {ex.Message}");
                    }
                    finally
                    {
                        color.Dispose();
                    }
                }
            }
        }

        // ---------------------------------------------------------------- network thread

        private void NetworkLoop()
        {
            while (!stopping)
            {
                TcpClient? tcp = null;
                try
                {
                    tcp = ConnectToLaptop();
                    if (tcp == null)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    RunConnection(tcp);
                }
                catch (Exception ex)
                {
                    StatusText = $"link lost: {ex.Message}";
                }
                finally
                {
                    connected = false;
                    tcp?.Close();
                }
                Thread.Sleep(500);
            }
        }

        private void RunConnection(TcpClient tcp)
        {
            tcp.NoDelay = true;
            tcp.SendBufferSize = 4 * 1024 * 1024;
            var stream = tcp.GetStream();
            Write(stream, LiveStreamMessages.Hello(deviceName, appVersion));
            string? announced = null;  // session announced on this connection
            connected = true;
            StatusText = $"connected to {Endpoint}";
            var reader = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "LiveStreamReader" };
            reader.Start();
            var nextHeartbeat = DateTime.UtcNow;
            while (!stopping && tcp.Connected)
            {
                sendSignal.WaitOne(200);
                var current = sessionName;
                if (current != null && announced != current)
                {
                    // Resume if an earlier connection already announced this session (reconnect after a drop).
                    Write(stream, LiveStreamMessages.SessionStart(current, everAnnounced == current, leftIntrinsics, rightIntrinsics));
                    announced = everAnnounced = current;
                }
                while (control.TryDequeue(out var message)) Write(stream, message);
                while (depthOut.TryDequeue(out var depth)) Write(stream, depth);
                // Drain every encoded color frame: with several encoders, signals coalesce, so sending one per wake
                // would cap the rate. Depth goes in between so it never waits behind a burst of color.
                while (!stopping && colorOut.TryDequeue(out var color))
                {
                    Write(stream, color);
                    while (depthOut.TryDequeue(out var between)) Write(stream, between);
                }
                if (pendingEndReason != null && colorOut.Count == 0 && depthOut.Count == 0 && colorJobs.Count == 0 && depthJobs.Count == 0)
                {
                    Write(stream, LiveStreamMessages.SessionEnd(pendingEndReason, FramesDropped));
                    pendingEndReason = null;
                    announced = null;
                }
                if (DateTime.UtcNow >= nextHeartbeat)
                {
                    nextHeartbeat = DateTime.UtcNow.AddSeconds(1);
                    var queued = colorOut.Count + depthOut.Count + colorJobs.Count + depthJobs.Count;
                    Write(stream, LiveStreamMessages.Heartbeat(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), queued, FramesDropped));
                }
                if (!reader.IsAlive) throw new IOException("laptop closed the connection");
            }
        }

        private void Write(NetworkStream stream, byte[] frame)
        {
            stream.Write(frame, 0, frame.Length);
            Interlocked.Increment(ref framesSent);
        }

        private void ReadLoop(NetworkStream stream)
        {
            try
            {
                while (!stopping)
                {
                    var message = IncomingMessage.Read(stream);
                    if (message == null) return;
                    Incoming.Enqueue(message);
                    while (Incoming.Count > 64 && Incoming.TryDequeue(out _)) { }  // never let feedback pile up
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Feedback reader stopped: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- endpoint resolution (spec order)

        private TcpClient? ConnectToLaptop()
        {
            var candidates = new List<(string host, int port, string how)>();
            if (!string.IsNullOrWhiteSpace(Settings.host)) candidates.Add((Settings.host, Settings.port, "config"));
            var endpointFile = LiveStreamEndpointFile.TryRead(dataDirectory);
            if (endpointFile != null) candidates.Add((endpointFile.host, endpointFile.port, "adb endpoint"));
            candidates.Add(("127.0.0.1", Settings.port, "adb reverse"));
            foreach (var (host, port, how) in candidates)
            {
                var tcp = TryConnect(host, port);
                if (tcp != null)
                {
                    Endpoint = $"{host}:{port} ({how})";
                    return tcp;
                }
            }
            StatusText = "searching for laptop (UDP)";
            var discovered = Discover();
            if (discovered != null)
            {
                var tcp = TryConnect(discovered.Value.host, discovered.Value.port);
                if (tcp != null)
                {
                    Endpoint = $"{discovered.Value.host}:{discovered.Value.port} (discovery)";
                    return tcp;
                }
            }
            StatusText = "laptop not found: run 'mq3drecon live' on the same Wi-Fi";
            return null;
        }

        private static TcpClient? TryConnect(string host, int port)
        {
            var tcp = new TcpClient();
            try
            {
                var task = tcp.ConnectAsync(host, port);
                if (task.Wait(1500) && tcp.Connected) return tcp;
            }
            catch (Exception)
            {
                // fall through
            }
            tcp.Close();
            return null;
        }

        private (string host, int port)? Discover()
        {
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                udp.Client.ReceiveTimeout = 1000;
                var probe = Encoding.ASCII.GetBytes(LiveStreamProtocol.DiscoveryProbe);
                udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, Settings.discoveryPort));
                var from = new IPEndPoint(IPAddress.Any, 0);
                var reply = udp.Receive(ref from);
                var parsed = JsonUtility.FromJson<DiscoveryReplyJson>(Encoding.UTF8.GetString(reply));
                if (parsed != null && parsed.service == LiveStreamProtocol.ServiceName && parsed.protocol_version == LiveStreamProtocol.Version)
                {
                    return (from.Address.ToString(), parsed.port);
                }
            }
            catch (Exception)
            {
                // no laptop answered within the timeout
            }
            return null;
        }
    }
}
