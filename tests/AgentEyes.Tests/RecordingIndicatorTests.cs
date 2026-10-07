using System;
using AgentEyes.AlwaysOn;
using AgentEyes.App;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #98: the recording HUD can be hidden while the recording carries on, the tray offers it
    /// back, and the tray icon carries a red dot for the whole of a normal recording.
    /// </summary>
    public class RecordingIndicatorTests
    {
        private sealed class FakeHud
        {
            public int Hides;
            public int Shows;
        }

        private static (RecordingIndicator indicator, FakeHud hud, object owner) Attached()
        {
            var indicator = new RecordingIndicator();
            var hud = new FakeHud();
            var owner = new object();
            indicator.Attach(owner, () => hud.Hides++, () => hud.Shows++);
            return (indicator, hud, owner);
        }

        // ---- the indicator state ---------------------------------------------

        [Fact]
        public void Attach_NewIndicator_StartsShownAndNotOffered()
        {
            var (indicator, hud, _) = Attached();

            Assert.False(indicator.IsHidden);
            Assert.False(indicator.CanShow);
            Assert.Equal(0, hud.Hides);
        }

        [Fact]
        public void Hide_AttachedIndicator_HidesTheWindowAndOffersShow()
        {
            var (indicator, hud, _) = Attached();

            indicator.Hide();

            Assert.Equal(1, hud.Hides);
            Assert.True(indicator.IsHidden);
            Assert.True(indicator.CanShow);
        }

        [Fact]
        public void Show_HiddenIndicator_ShowsTheWindowAndStopsOffering()
        {
            var (indicator, hud, _) = Attached();
            indicator.Hide();

            indicator.Show();

            Assert.Equal(1, hud.Shows);
            Assert.False(indicator.IsHidden);
            Assert.False(indicator.CanShow);
        }

        [Fact]
        public void Show_IndicatorAlreadyShowing_DoesNothing()
        {
            var (indicator, hud, _) = Attached();

            indicator.Show();

            Assert.Equal(0, hud.Shows);
        }

        [Fact]
        public void Hide_NothingAttached_DoesNothingAndOffersNothing()
        {
            var indicator = new RecordingIndicator();

            indicator.Hide();

            Assert.False(indicator.IsHidden);
            Assert.False(indicator.CanShow);
        }

        [Fact]
        public void Attach_AfterARecordingThatEndedHidden_NewRecordingStartsShown()
        {
            // The owner's decision: hiding is for ONE recording. The next one always starts on screen.
            var (indicator, _, first) = Attached();
            indicator.Hide();
            indicator.Detach(first);

            var next = new FakeHud();
            indicator.Attach(new object(), () => next.Hides++, () => next.Shows++);

            Assert.False(indicator.IsHidden);
            Assert.False(indicator.CanShow);
        }

        [Fact]
        public void Attach_WhileThePreviousIndicatorIsHidden_NewOneStartsShown()
        {
            // A new HUD can be attached before the old one's Closed arrives.
            var (indicator, _, _) = Attached();
            indicator.Hide();

            indicator.Attach(new object(), () => { }, () => { });

            Assert.False(indicator.IsHidden);
        }

        [Fact]
        public void Detach_HiddenIndicatorClosedAtStop_StopsOfferingShow()
        {
            var (indicator, _, owner) = Attached();
            indicator.Hide();

            indicator.Detach(owner);

            Assert.False(indicator.IsHidden);
            Assert.False(indicator.CanShow);
        }

        [Fact]
        public void Detach_StaleIndicator_LeavesTheCurrentOneAlone()
        {
            var (indicator, _, old) = Attached();
            var current = new FakeHud();
            indicator.Attach(new object(), () => current.Hides++, () => current.Shows++);
            indicator.Hide();

            indicator.Detach(old);   // the previous recording's late Closed

            Assert.True(indicator.CanShow);
            indicator.Show();
            Assert.Equal(1, current.Shows);
        }

        [Fact]
        public void Changed_HideAndShow_RaisedForEach()
        {
            var (indicator, _, _) = Attached();
            int changes = 0;
            indicator.Changed += () => changes++;

            indicator.Hide();
            indicator.Show();

            Assert.Equal(2, changes);
        }

        // ---- the tray icon ---------------------------------------------------

        [Theory]
        [InlineData(AlwaysOnState.Off)]
        [InlineData(AlwaysOnState.Listening)]
        [InlineData(AlwaysOnState.Keeping)]
        [InlineData(AlwaysOnState.Paused)]
        [InlineData(AlwaysOnState.Retrying)]
        [InlineData(null)]
        public void Choose_NormalRecording_RedDotWhateverAlwaysOnIsDoing(string? alwaysOn)
        {
            Assert.Equal(TrayIconKind.Recording, TrayDot.Choose(recording: true, alwaysOn));
        }

        [Theory]
        [InlineData(AlwaysOnState.Off, "Off")]
        [InlineData(null, "Off")]
        [InlineData(AlwaysOnState.Listening, "Listening")]
        [InlineData(AlwaysOnState.Keeping, "Keeping")]
        [InlineData(AlwaysOnState.Paused, "Paused")]
        [InlineData(AlwaysOnState.Retrying, "Paused")]
        public void Choose_NoRecording_AlwaysOnDotAsBefore(string? alwaysOn, string expected)
        {
            Assert.Equal(expected, TrayDot.Choose(recording: false, alwaysOn).ToString());
        }

        [Fact]
        public void RecordingTooltip_IndicatorShowing_SaysRecordingAndTime()
        {
            Assert.Equal("AgentEyes - Recording 04:27", TrayDot.RecordingTooltip(267.4, indicatorHidden: false));
        }

        [Fact]
        public void RecordingTooltip_IndicatorHidden_SaysSoAndHowToShowIt()
        {
            string text = TrayDot.RecordingTooltip(5, indicatorHidden: true);

            Assert.Equal("AgentEyes - Recording 00:05\nIndicator hidden - right-click to show it", text);
            Assert.True(text.Length <= TrayDot.MaxTooltip);
        }
    }
}
