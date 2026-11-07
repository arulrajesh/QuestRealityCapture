#nullable enable

namespace RealityLog.Streaming
{
    /// <summary>
    /// One line of scan guidance for the HUD, written by the room coverage guide (RealityLog.Recording, which this
    /// assembly cannot reference) and shown by <see cref="LiveFeedbackRenderer"/>. Main thread only.
    /// </summary>
    public static class CaptureGuidance
    {
        public static string Summary { get; set; } = string.Empty;
    }
}
