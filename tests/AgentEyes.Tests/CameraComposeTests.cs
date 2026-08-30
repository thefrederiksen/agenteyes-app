using System;
using System.IO;
using System.Linq;
using AgentEyes;
using AgentEyes.Preview;
using AgentEyes.Video;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #47: the framing a person chose has to reach the video they end up with.
    ///
    /// Before this, shape/corner/inset drove the live preview and were written to the manifest as
    /// edit metadata for "a later edit" that did not exist, so camera.mp4 sat beside recording.mp4
    /// forever and the corner setting did nothing to the output.
    /// </summary>
    public class CameraComposeTests
    {
        private const int ScreenW = 1920;
        private const int ScreenH = 1080;
        private const int CamW = 1920;
        private const int CamH = 1080;

        private static CameraOverlaySettings Overlay(
            string corner = "bottom-right", string shape = "circle", double inset = 0.30) => new()
            {
                Corner = corner,
                Shape = shape,
                InsetFraction = inset,
                Circle = new CameraOverlayCircle { CentreX = 0.50, CentreY = 0.42, Diameter = 0.60 },
            };

        // ---- geometry ----------------------------------------------------------

        [Fact]
        public void Composition_puts_a_circle_in_the_bottom_right_at_the_chosen_size()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());

            Assert.True(c.Circular);
            Assert.Equal(576, c.InsetWidth);          // 30% of 1920
            Assert.Equal(576, c.InsetHeight);         // a circle's bounds are square
            int margin = (int)Math.Round(ScreenW * CameraComposition.MarginFraction);
            Assert.Equal(ScreenW - 576 - margin, c.X);
            Assert.Equal(ScreenH - 576 - margin, c.Y);
        }

        [Theory]
        [InlineData("top-left")]
        [InlineData("top-right")]
        [InlineData("bottom-left")]
        [InlineData("bottom-right")]
        public void Composition_keeps_the_inset_fully_inside_the_frame_in_every_corner(string corner)
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(corner: corner));

            Assert.True(c.X >= 0 && c.Y >= 0, $"{corner} placed the inset off the top/left");
            Assert.True(c.Right <= ScreenW, $"{corner} ran past the right edge");
            Assert.True(c.Bottom <= ScreenH, $"{corner} ran past the bottom edge");
        }

        [Fact]
        public void Composition_puts_each_corner_where_its_name_says()
        {
            var tl = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(corner: "top-left"));
            var br = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(corner: "bottom-right"));
            var tr = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(corner: "top-right"));
            var bl = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(corner: "bottom-left"));

            Assert.True(tl.X < br.X && tl.Y < br.Y);
            Assert.True(tr.X > tl.X && tr.Y == tl.Y);
            Assert.True(bl.X == tl.X && bl.Y > tl.Y);
        }

        [Fact]
        public void Composition_crops_a_circle_to_the_stored_framing_not_the_whole_frame()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());

            // Diameter 0.60 of a 1080-tall frame = 648px, square in PIXELS (not per-axis fractions,
            // which would make it an ellipse on a 16:9 camera).
            Assert.Equal(648, Math.Round(c.CameraCrop.Width));
            Assert.Equal(648, Math.Round(c.CameraCrop.Height));
            Assert.True(c.CameraCrop.Width < CamW, "a circle must not crop the whole frame width");
        }

        [Fact]
        public void Composition_of_a_rectangle_uses_the_whole_camera_frame_and_keeps_its_shape()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(shape: "rectangle"));

            Assert.False(c.Circular);
            Assert.Equal(CamW, Math.Round(c.CameraCrop.Width));
            Assert.Equal(CamH, Math.Round(c.CameraCrop.Height));
            // 16:9 in, 16:9 out - 576 wide means 324 tall.
            Assert.Equal(324, c.InsetHeight);
        }

        [Fact]
        public void Composition_keeps_a_four_three_camera_from_becoming_an_ellipse()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, 640, 480, Overlay());

            // Still a square inset, and the crop out of the 4:3 frame is still square in pixels.
            Assert.Equal(c.InsetWidth, c.InsetHeight);
            Assert.Equal(Math.Round(c.CameraCrop.Width), Math.Round(c.CameraCrop.Height));
        }

        [Fact]
        public void Composition_sizes_and_offsets_are_always_even()
        {
            // yuv420p cannot express odd sizes or odd overlay offsets.
            foreach (var inset in new[] { 0.15, 0.23, 0.31, 0.47, 0.60 })
            foreach (var corner in new[] { "top-left", "top-right", "bottom-left", "bottom-right" })
            {
                var c = CameraComposition.For(1919, 1079, 1280, 720, Overlay(corner: corner, inset: inset));
                Assert.True(c.InsetWidth % 2 == 0, $"odd width at {inset}/{corner}");
                Assert.True(c.InsetHeight % 2 == 0, $"odd height at {inset}/{corner}");
                Assert.True(c.X % 2 == 0, $"odd x at {inset}/{corner}");
                Assert.True(c.Y % 2 == 0, $"odd y at {inset}/{corner}");
            }
        }

        [Fact]
        public void Composition_never_lets_the_inset_exceed_the_screen()
        {
            // A huge inset fraction against a tiny screen: the inset must still fit.
            var c = CameraComposition.For(320, 240, 1920, 1080, Overlay(inset: 0.60));

            Assert.True(c.InsetWidth <= 320 && c.InsetHeight <= 240);
            Assert.True(c.X >= 0 && c.Y >= 0 && c.Right <= 320 && c.Bottom <= 240);
        }

        [Fact]
        public void Composition_rejects_an_impossible_frame_size()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CameraComposition.For(0, 1080, CamW, CamH, Overlay()));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CameraComposition.For(ScreenW, ScreenH, CamW, 0, Overlay()));
        }

        // ---- the ffmpeg command ------------------------------------------------

        private static string Graph(System.Collections.Generic.List<string> args)
        {
            int at = args.IndexOf("-filter_complex");
            Assert.True(at >= 0, "the compose command must have a filtergraph");
            return args[at + 1];
        }

        [Fact]
        public void CameraInset_masks_the_circle_with_an_image_not_a_per_pixel_expression()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());
            var args = ComposeArgs.CameraInset("screen.mp4", "camera.mp4", "mask.png", "out.mp4", c, -0.855, 23);
            string g = Graph(args);

            Assert.Contains("alphamerge", g);
            // geq would be ~1.6 billion per-pixel evaluations on a take this size.
            Assert.DoesNotContain("geq", g);
            Assert.Contains($"overlay={c.X}:{c.Y}", g);
        }

        [Fact]
        public void CameraInset_skips_the_camera_head_when_the_camera_started_first()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());
            var args = ComposeArgs.CameraInset("screen.mp4", "camera.mp4", "mask.png", "out.mp4", c, -0.855, 23);

            // Input seek, and BEFORE the camera input so it seeks rather than decode-and-discard.
            int ss = args.IndexOf("-ss");
            Assert.True(ss >= 0, "a camera that started early must be seeked");
            Assert.Equal("0.855", args[ss + 1]);
            Assert.True(args.IndexOf("camera.mp4") > ss);
        }

        [Fact]
        public void CameraInset_does_not_round_a_sub_second_offset_away()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());
            var args = ComposeArgs.CameraInset("s.mp4", "c.mp4", "m.png", "o.mp4", c, -0.4, 23);

            // Rounding 0.4 to "0" would silently drop the alignment this feature exists to apply.
            Assert.Equal("0.4", args[args.IndexOf("-ss") + 1]);
        }

        [Fact]
        public void CameraInset_delays_the_inset_when_the_camera_started_late()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());
            var args = ComposeArgs.CameraInset("s.mp4", "c.mp4", "m.png", "o.mp4", c, 1.25, 23);

            Assert.DoesNotContain("-ss", args);
            Assert.Contains("tpad=start_duration=1.25", Graph(args));
        }

        [Fact]
        public void CameraInset_needs_no_mask_for_a_rectangle()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay(shape: "rectangle"));
            var args = ComposeArgs.CameraInset("s.mp4", "c.mp4", null, "o.mp4", c, 0, 23);
            string g = Graph(args);

            Assert.DoesNotContain("alphamerge", g);
            Assert.DoesNotContain("[2:v]", g);
        }

        [Fact]
        public void CameraInset_refuses_a_circle_with_no_mask_rather_than_drawing_a_square()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());

            var ex = Assert.Throws<UsageException>(
                () => ComposeArgs.CameraInset("s.mp4", "c.mp4", null, "o.mp4", c, 0, 23));
            Assert.Contains("mask", ex.Message);
        }

        [Fact]
        public void CameraInset_copies_the_audio_instead_of_re_encoding_it()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());
            var args = ComposeArgs.CameraInset("s.mp4", "c.mp4", "m.png", "o.mp4", c, 0, 23);

            // Composing is a video operation. Re-encoding here would undo the clean-voice chain's work.
            int map = args.IndexOf("0:a?");
            Assert.True(map >= 0, "the screen recording's audio must be mapped through");
            Assert.Equal("-c:a", args[map + 1]);
            Assert.Equal("copy", args[map + 2]);
        }

        [Fact]
        public void CameraInset_lets_the_screen_outlive_a_camera_that_stopped_early()
        {
            var c = CameraComposition.For(ScreenW, ScreenH, CamW, CamH, Overlay());
            var args = ComposeArgs.CameraInset("s.mp4", "c.mp4", "m.png", "o.mp4", c, 0, 23);

            // Without this the screen would freeze or end when the camera track ran out.
            Assert.Contains("eof_action=pass", Graph(args));
        }

        // ---- when the stage runs -----------------------------------------------

        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "compose-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Write(string dir, Action<Manifest> build)
        {
            var m = new Manifest { Mode = "video", VideoFile = "recording.mp4" };
            build(m);
            ManifestStore.Replace(dir, m);
        }

        [Fact]
        public void NeedsCompose_is_true_for_a_camera_recording_with_a_framing()
        {
            string dir = NewDir();
            Write(dir, m =>
            {
                m.CameraFile = "camera.mp4";
                m.PreviewOverlayCorner = "bottom-right";
                m.PreviewOverlayShape = "circle";
            });

            Assert.True(PostRecordingPlan.NeedsCompose(dir));
        }

        [Fact]
        public void NeedsCompose_is_false_without_a_camera()
        {
            string dir = NewDir();
            Write(dir, m => { m.PreviewOverlayCorner = "bottom-right"; m.PreviewOverlayShape = "circle"; });

            Assert.False(PostRecordingPlan.NeedsCompose(dir));
        }

        [Fact]
        public void NeedsCompose_is_false_when_no_framing_was_recorded()
        {
            // Every recording made before the framing was persisted is in this state.
            string dir = NewDir();
            Write(dir, m => m.CameraFile = "camera.mp4");

            Assert.False(PostRecordingPlan.NeedsCompose(dir));
        }

        [Fact]
        public void NeedsCompose_is_false_once_it_has_been_composed()
        {
            string dir = NewDir();
            Write(dir, m =>
            {
                m.CameraFile = "camera.mp4";
                m.PreviewOverlayCorner = "bottom-right";
                m.PreviewOverlayShape = "circle";
                m.ComposedCamera = true;
            });

            // A composed recording must never be composed again - that would inset the camera twice.
            Assert.False(PostRecordingPlan.NeedsCompose(dir));
        }

        [Fact]
        public void Compose_runs_after_the_mux_and_before_the_thumbnail()
        {
            // The order is the point: compose needs the final media the mux writes, and the
            // thumbnail must be made from the video people actually get.
            var all = PostStage.All.ToList();
            Assert.True(all.IndexOf(PostStage.Mux) < all.IndexOf(PostStage.Compose));
            Assert.True(all.IndexOf(PostStage.Compose) < all.IndexOf(PostStage.Thumbnail));
        }

        [Fact]
        public void NeedsCompose_does_not_throw_on_a_stranded_directory_with_no_manifest()
        {
            // Regression. The recovery scan calls Outstanding() on every directory it finds,
            // including a recording stranded before its manifest was ever written. A predicate that
            // throws there takes down the whole scan, not just this one directory.
            string dir = NewDir();

            Assert.False(PostRecordingPlan.NeedsCompose(dir));
            Assert.Empty(PostRecordingPlan.Outstanding(dir));
            Assert.False(PostRecordingPlan.HasUnfinishedWork(dir));
        }

        [Fact]
        public void NeedsCompose_does_not_throw_on_a_directory_that_is_not_there()
        {
            Assert.False(PostRecordingPlan.NeedsCompose(
                Path.Combine(Path.GetTempPath(), "compose-tests", Guid.NewGuid().ToString("N"))));
        }

        [Fact]
        public void Compose_is_listed_as_outstanding_work()
        {
            string dir = NewDir();
            Write(dir, m =>
            {
                m.CameraFile = "camera.mp4";
                m.PreviewOverlayCorner = "bottom-right";
                m.PreviewOverlayShape = "circle";
            });

            Assert.Contains(PostStage.Compose, PostRecordingPlan.Outstanding(dir));
        }
    }
}
