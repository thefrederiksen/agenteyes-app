using System;
using AgentEyes.Preview;

namespace AgentEyes.App
{
    /// <summary>
    /// Every decision the recording HUD's live preview makes, with no WPF in it (issue #33).
    ///
    /// It exists so the questions that actually go wrong in this feature - which layers are drawn,
    /// whether the camera controls may be touched at all, what reaches manifest.json, and when a
    /// picture has gone stale enough to be a lie - are answered by something a test can call. The
    /// window above it does layout and nothing else.
    ///
    /// TWO RULES SHAPE IT.
    ///
    /// It never silently rewrites the person's choice. A recording with no camera track leaves a
    /// stored "camera" or "both" mode exactly where it is and says out loud that this recording has
    /// no camera (<see cref="UnavailableMessage"/>). Coercing the mode to "screen" would look tidier
    /// and would quietly lose the setting the next recording wanted.
    ///
    /// Staleness is judged on a PRESENCE and fails closed. <see cref="IsStale"/> is true when no
    /// frame has EVER arrived, not just when an old one has aged out - "we have not seen a frame" is
    /// the reading that must reach the person, and treating never-arrived as fine is exactly how a
    /// dead preview would pass for a live one showing a very still desk.
    /// </summary>
    internal sealed class HudPreviewState
    {
        /// <summary>
        /// How long a published frame stays credible. Frames arrive at
        /// <see cref="AgentEyes.Video.FfmpegArgs.PreviewFps"/> (10/s), so two seconds is twenty
        /// missed frames - far past a hiccup and still fast enough that a person notices the panel
        /// telling them the truth rather than showing them a photograph.
        /// </summary>
        public const double StaleAfterSeconds = 2.0;

        public HudPreviewState(
            bool visible, PreviewMode mode, PreviewCorner corner,
            bool feedAvailable, bool cameraAvailable)
        {
            Visible = visible;
            Mode = mode;
            Corner = corner;
            FeedAvailable = feedAvailable;
            CameraAvailable = cameraAvailable && feedAvailable;
        }

        /// <summary>Whether the preview panel is showing. False on a fresh config (AC1).</summary>
        public bool Visible { get; private set; }

        /// <summary>What the person asked to see - preserved even when this recording cannot show it.</summary>
        public PreviewMode Mode { get; private set; }

        /// <summary>Which corner the camera is inset into in <see cref="PreviewMode.Both"/>.</summary>
        public PreviewCorner Corner { get; private set; }

        /// <summary>
        /// Whether THIS recording carries a live preview feed at all.
        ///
        /// A feed is a second output on the recording's own ffmpeg, and ffmpeg's outputs are fixed
        /// when the process starts - so a recording begun with the preview switched off has no feed
        /// and cannot grow one without restarting the ffmpeg that is writing the recording. That
        /// trade is deliberate: it is what keeps a recording made without the preview identical to
        /// what it was before this feature (AC11). The panel SAYS SO when there is no feed rather
        /// than showing an empty rectangle.
        /// </summary>
        public bool FeedAvailable { get; }

        /// <summary>Whether THIS recording has a camera track to preview. A recording without one can
        /// only ever show the screen.</summary>
        public bool CameraAvailable { get; }

        /// <summary>Draw the screen layer.</summary>
        public bool ShowScreenLayer =>
            Visible && FeedAvailable && Mode is PreviewMode.Screen or PreviewMode.Both;

        /// <summary>Draw the camera layer. Requires a camera track: there is no camera picture to
        /// draw for a recording that is not recording one.</summary>
        public bool ShowCameraLayer =>
            Visible && CameraAvailable && Mode is PreviewMode.Camera or PreviewMode.Both;

        /// <summary>The camera is inset into a corner of the screen picture, rather than filling the
        /// panel on its own.</summary>
        public bool CameraIsInset => ShowCameraLayer && Mode == PreviewMode.Both;

        /// <summary>Whether the four corner controls do anything right now.</summary>
        public bool CornerControlsEnabled => Visible && CameraAvailable && Mode == PreviewMode.Both;

        /// <summary>Whether the camera-bearing modes may be chosen at all.</summary>
        public bool CameraModesEnabled => CameraAvailable;

        /// <summary>What the toggle reads. The control's UI Automation name stays "Show preview"
        /// throughout so it can be found by name in either state; this is the visible label.</summary>
        public string ToggleLabel => Visible ? "Hide preview" : "Show preview";

        /// <summary>
        /// Why the panel is showing no picture, or null when it should be showing one. A recording
        /// with no camera says so rather than presenting an empty rectangle.
        /// </summary>
        public string? UnavailableMessage
        {
            get
            {
                if (!Visible) return null;
                if (!FeedAvailable)
                    return "Live preview starts with your NEXT recording. The recorder's preview feed "
                         + "is set up when a recording begins, and this one was started with the "
                         + "preview switched off.";
                if (!CameraAvailable && Mode == PreviewMode.Camera)
                    return "This recording has no camera track.";
                if (!CameraAvailable && Mode == PreviewMode.Both)
                    return "This recording has no camera track - showing the screen only.";
                return null;
            }
        }

        /// <summary>
        /// The corner to record in manifest.json, or null when no overlay framing happened (issue
        /// #33, AC5). Non-null needs all three: the panel is showing, the mode is the overlay mode,
        /// and there is a camera to be inset. A screen-only or camera-only preview frames nothing.
        /// </summary>
        public string? ManifestCorner =>
            Visible && CameraAvailable && Mode == PreviewMode.Both ? PreviewNames.Text(Corner) : null;

        /// <summary>Whether the preview should be ARMED for the next recording. It is simply the
        /// person's current choice: turning the preview on is what asks for a feed, and the feed is
        /// created when a recording starts.</summary>
        public bool ArmNextRecording => Visible;

        /// <summary>Show or hide the panel. Returns the new visibility.</summary>
        public bool ToggleVisible()
        {
            Visible = !Visible;
            return Visible;
        }

        /// <summary>
        /// Choose what the preview shows. Returns false - and changes NOTHING - when the mode needs a
        /// camera this recording does not have, so the caller can say why instead of appearing to
        /// accept a choice it will not honour.
        /// </summary>
        public bool TrySetMode(PreviewMode mode)
        {
            if (!CameraAvailable && mode is PreviewMode.Camera or PreviewMode.Both) return false;
            Mode = mode;
            return true;
        }

        /// <summary>Choose the overlay corner. Always accepted and always stored: the choice outlives
        /// this recording even if this one cannot show it.</summary>
        public void SetCorner(PreviewCorner corner) => Corner = corner;

        /// <summary>
        /// Whether the last frame is too old to be shown as live. TRUE WHEN NOTHING HAS EVER ARRIVED
        /// (<paramref name="lastFrameUtc"/> null) - the absence of a frame is the failure this is
        /// looking for, not an exemption from it.
        /// </summary>
        public static bool IsStale(DateTime? lastFrameUtc, DateTime nowUtc) =>
            lastFrameUtc is not { } last || (nowUtc - last).TotalSeconds >= StaleAfterSeconds;
    }
}
