using wisland.Models;
using Xunit;

namespace wisland.Tests
{
    public sealed class SessionPickerOverlayDismissMotionTests
    {
        [Fact]
        public void FromKind_ReturnsPassiveDismissConfig()
        {
            SessionPickerOverlayDismissMotion motion = SessionPickerOverlayDismissMotion.FromKind(
                SessionPickerOverlayDismissKind.Passive);

            Assert.Equal(IslandConfig.SessionPickerOverlayPassiveDismissDurationMs, motion.DurationMs);
            Assert.Equal((float)IslandConfig.SessionPickerOverlayPassiveDismissTargetOpacity, motion.TargetOpacity);
            Assert.Equal((float)IslandConfig.SessionPickerOverlayPassiveDismissOffsetY, motion.OffsetY);
        }

        [Fact]
        public void FromKind_ReturnsSelectionDismissConfig()
        {
            SessionPickerOverlayDismissMotion motion = SessionPickerOverlayDismissMotion.FromKind(
                SessionPickerOverlayDismissKind.Selection);

            Assert.Equal(IslandConfig.SessionPickerOverlaySelectionDismissDurationMs, motion.DurationMs);
            Assert.Equal((float)IslandConfig.SessionPickerOverlaySelectionDismissTargetOpacity, motion.TargetOpacity);
            Assert.Equal((float)IslandConfig.SessionPickerOverlaySelectionDismissOffsetY, motion.OffsetY);
        }

        [Fact]
        public void FromKind_ReturnsToggleDismissConfig()
        {
            SessionPickerOverlayDismissMotion motion = SessionPickerOverlayDismissMotion.FromKind(
                SessionPickerOverlayDismissKind.Toggle);

            Assert.Equal(IslandConfig.SessionPickerOverlayToggleDismissDurationMs, motion.DurationMs);
            Assert.Equal((float)IslandConfig.SessionPickerOverlayToggleDismissTargetOpacity, motion.TargetOpacity);
            Assert.Equal((float)IslandConfig.SessionPickerOverlayToggleDismissOffsetY, motion.OffsetY);
        }
    }
}
