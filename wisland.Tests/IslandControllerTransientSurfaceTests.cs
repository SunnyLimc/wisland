using wisland.Models;
using wisland.Services;
using Xunit;

namespace wisland.Tests
{
    public sealed class IslandControllerTransientSurfaceTests
    {
        [Fact]
        public void TransientSurfaceKeepsExpandedTargetsWithoutPointerHover()
        {
            IslandController controller = new();
            controller.InitializePosition(centerX: 200, y: 0, isDocked: true);
            controller.IsTransientSurfaceOpen = true;

            controller.UpdateTargetState();
            controller.Tick(1.0);

            Assert.True(controller.Current.Width > IslandConfig.CompactWidth);
            Assert.True(controller.Current.ExpandedOpacity > 0.5);
        }

        [Fact]
        public void ClosingTransientSurfaceReturnsToCompactTargets()
        {
            IslandController controller = new();
            controller.InitializePosition(centerX: 200, y: 0, isDocked: true);
            controller.IsTransientSurfaceOpen = true;
            controller.UpdateTargetState();
            controller.Tick(1.0);

            controller.IsTransientSurfaceOpen = false;
            controller.UpdateTargetState();
            controller.Tick(1.0);

            Assert.True(controller.Current.Width < IslandConfig.ExpandedWidth);
            Assert.True(controller.Current.ExpandedOpacity < 0.5);
        }
    }
}
