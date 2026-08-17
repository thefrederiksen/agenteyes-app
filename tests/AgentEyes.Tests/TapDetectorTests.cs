using AgentEyes.Input;
using Xunit;

namespace AgentEyes.Tests
{
    public class TapDetectorTests
    {
        private static (TapDetector det, System.Func<int> count) Make(int window = 400)
        {
            var d = new TapDetector(window);
            int fired = 0;
            d.Triggered += () => fired++;
            return (d, () => fired);
        }

        private static void Tap(TapDetector d, long ms)
        {
            d.TriggerDown(ms);
            d.TriggerUp(ms);
        }

        [Fact]
        public void DoubleTap_WithinWindow_Fires()
        {
            var (d, count) = Make();
            Tap(d, 0);
            Tap(d, 200);
            Assert.Equal(1, count());
        }

        [Fact]
        public void DoubleTap_TooFarApart_DoesNotFire()
        {
            var (d, count) = Make(400);
            Tap(d, 0);
            Tap(d, 700);
            Assert.Equal(0, count());
        }

        [Fact]
        public void Typing_BetweenTaps_BreaksTheDoubleTap()
        {
            var (d, count) = Make();
            Tap(d, 0);
            d.OtherKey();          // a letter between the two taps
            Tap(d, 150);
            Assert.Equal(0, count());
        }

        [Fact]
        public void Modifier_Use_DoesNotCountAsTap()
        {
            var (d, count) = Make();
            // Ctrl+C twice quickly: each is down -> other key -> up (dirty), so no clean taps.
            d.TriggerDown(0); d.OtherKey(); d.TriggerUp(10);
            d.TriggerDown(50); d.OtherKey(); d.TriggerUp(60);
            Assert.Equal(0, count());
        }

        [Fact]
        public void CleanTap_ThenModifierTap_DoesNotFire()
        {
            var (d, count) = Make();
            Tap(d, 0);                                  // clean tap
            d.TriggerDown(100); d.OtherKey(); d.TriggerUp(120);   // Ctrl+key, dirty
            Assert.Equal(0, count());
        }

        [Fact]
        public void TripleTap_FiresOnce_ThenRestarts()
        {
            var (d, count) = Make();
            Tap(d, 0);
            Tap(d, 100);    // fires (1)
            Tap(d, 200);    // this is a fresh first tap after consume - no fire yet
            Assert.Equal(1, count());
            Tap(d, 300);    // pairs with the 200 tap -> fires (2)
            Assert.Equal(2, count());
        }

        [Fact]
        public void AutoRepeatDown_WhileHeld_DoesNotBreakTap()
        {
            var (d, count) = Make();
            Tap(d, 0);
            d.TriggerDown(100);
            d.TriggerDown(110);   // auto-repeat, ignored
            d.TriggerUp(120);
            Assert.Equal(1, count());
        }
    }
}
