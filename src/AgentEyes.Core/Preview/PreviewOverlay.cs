using System;

namespace AgentEyes.Preview
{
    /// <summary>
    /// What the HUD preview is showing (issue #33). Preview only - it never changes what is
    /// recorded. The recorded outputs stay two separate files exactly as issue #28 leaves them.
    /// </summary>
    internal enum PreviewMode
    {
        /// <summary>The screen being recorded.</summary>
        Screen,

        /// <summary>The camera being recorded.</summary>
        Camera,

        /// <summary>The screen with the camera inset in one corner.</summary>
        Both,
    }

    /// <summary>
    /// Which corner of the preview the camera is inset into in <see cref="PreviewMode.Both"/>
    /// (issue #33, assumption C4).
    ///
    /// IT IS INTENT, NOT COMPOSITION. Nothing composites the camera into a recorded file - the
    /// corner exists so the framing the person actually wanted survives the session, is written into
    /// manifest.json, and is there for the later edit (or a future auto-compose) to start from
    /// instead of being guessed months afterwards.
    /// </summary>
    internal enum PreviewCorner
    {
        BottomRight,
        BottomLeft,
        TopLeft,
        TopRight,
    }

    /// <summary>The wire spelling of the two choices above - what goes into config and manifest.json.
    /// Strings, so a human opening either file can read them.</summary>
    internal static class PreviewNames
    {
        public const string Screen = "screen";
        public const string Camera = "camera";
        public const string Both = "both";

        public const string BottomRight = "bottom-right";
        public const string BottomLeft = "bottom-left";
        public const string TopLeft = "top-left";
        public const string TopRight = "top-right";

        // Issue #36: the overlay SHAPE. Circle is the default and is spelled out here with the rest
        // of the wire vocabulary, so config.json, presets.json and manifest.json cannot each invent
        // their own spelling of the same choice.
        public const string Circle = "circle";
        public const string Rectangle = "rectangle";

        public static string Text(PreviewMode mode) => mode switch
        {
            PreviewMode.Screen => Screen,
            PreviewMode.Camera => Camera,
            PreviewMode.Both => Both,
            // No default that guesses: a mode nobody added here breaks the build's own tests rather
            // than quietly becoming one of the three.
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown preview mode"),
        };

        public static string Text(PreviewCorner corner) => corner switch
        {
            PreviewCorner.BottomRight => BottomRight,
            PreviewCorner.BottomLeft => BottomLeft,
            PreviewCorner.TopLeft => TopLeft,
            PreviewCorner.TopRight => TopRight,
            _ => throw new ArgumentOutOfRangeException(nameof(corner), corner, "unknown preview corner"),
        };

        /// <summary>Parse a stored mode. An unknown or absent value reads as
        /// <see cref="PreviewMode.Screen"/> - the mode every recording can show, since a recording
        /// need not have a camera.</summary>
        public static PreviewMode Mode(string? text) => text switch
        {
            Camera => PreviewMode.Camera,
            Both => PreviewMode.Both,
            _ => PreviewMode.Screen,
        };

        /// <summary>Parse a stored corner. An unknown or absent value reads as
        /// <see cref="PreviewCorner.BottomRight"/>, the documented default (issue #33, AC5).</summary>
        public static PreviewCorner Corner(string? text) => text switch
        {
            BottomLeft => PreviewCorner.BottomLeft,
            TopLeft => PreviewCorner.TopLeft,
            TopRight => PreviewCorner.TopRight,
            _ => PreviewCorner.BottomRight,
        };

        public static string Text(CameraOverlayShape shape) => shape switch
        {
            CameraOverlayShape.Circle => Circle,
            CameraOverlayShape.Rectangle => Rectangle,
            // No default that guesses, for the same reason as the mode above.
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown camera overlay shape"),
        };

        /// <summary>Parse a stored overlay shape. An unknown or absent value reads as
        /// <see cref="CameraOverlayShape.Circle"/>, the documented default (issue #36, AC1) - which
        /// is also what a preset written before this field existed deserializes to.</summary>
        public static CameraOverlayShape Shape(string? text) => text switch
        {
            Rectangle => CameraOverlayShape.Rectangle,
            _ => CameraOverlayShape.Circle,
        };
    }
}
