using Px86.NintendoDS.Emulator;

namespace Px86.NintendoDS.TestProject.Frontend
{
    [TestClass]
    public class TestScreenLayout
    {
        private static readonly ScreenRect Area = new(0, 20, 512, 768); // 20 px menu bar on top

        [TestMethod]
        public void VerticalLayoutFillsTheAreaBelowTheMenu()
        {
            var (top, bottom) = new ScreenLayout().Compute(Area);
            Assert.AreEqual(new ScreenRect(0, 20, 512, 384), top, "top screen");
            Assert.AreEqual(new ScreenRect(0, 404, 512, 384), bottom, "bottom screen");
        }

        [TestMethod]
        public void TouchMapsTheBottomScreenOnly()
        {
            var layout = new ScreenLayout();
            Assert.AreEqual((0, 0), layout.ToTouch(Area, 0, 404), "top-left of the bottom screen");
            Assert.AreEqual((128, 96), layout.ToTouch(Area, 256, 404 + 192), "centre");
            Assert.AreEqual((255, 191), layout.ToTouch(Area, 511.9f, 787.9f), "bottom-right");
            Assert.IsNull(layout.ToTouch(Area, 256, 200), "the top screen is not touchable");
            Assert.IsNull(layout.ToTouch(Area, 256, 10), "the menu bar is not touchable");
        }

        [TestMethod]
        public void SwapPutsTheTouchScreenFirst()
        {
            var layout = new ScreenLayout { Swap = true };
            var (top, bottom) = layout.Compute(Area);
            Assert.IsTrue(bottom.Y < top.Y, "bottom screen drawn above");
            Assert.AreEqual((10, 20), layout.ToTouch(Area, 20, 20 + 40), "touch follows the screen");
        }

        [TestMethod]
        public void HorizontalLayoutWithGap()
        {
            var layout = new ScreenLayout { Arrangement = ScreenArrangement.Horizontal, Gap = 16 };
            var area = new ScreenRect(0, 0, 528, 192);
            var (top, bottom) = layout.Compute(area);
            Assert.AreEqual(new ScreenRect(0, 0, 256, 192), top, "left");
            Assert.AreEqual(new ScreenRect(272, 0, 256, 192), bottom, "right, after the gap");
            Assert.AreEqual((0, 0), layout.ToTouch(area, 272, 0), "touch origin");
            Assert.IsNull(layout.ToTouch(area, 264, 50), "the gap is not touchable");
        }

        [DataTestMethod]
        [DataRow(90)]
        [DataRow(180)]
        [DataRow(270)]
        public void RotatedTouchRoundTripsThroughTheCorners(int rotation)
        {
            var layout = new ScreenLayout { Rotation = rotation };
            var area = new ScreenRect(0, 0, 768, 768);
            var bottom = layout.Compute(area).Bottom;
            Assert.IsFalse(bottom.IsEmpty, "bottom visible");
            // The DS pixel under each window corner of the bottom screen must be a screen corner,
            // and the four corners must all be different.
            var corners = new[]
            {
                layout.ToTouch(area, bottom.X + 0.5f, bottom.Y + 0.5f),
                layout.ToTouch(area, bottom.X + bottom.Width - 0.5f, bottom.Y + 0.5f),
                layout.ToTouch(area, bottom.X + 0.5f, bottom.Y + bottom.Height - 0.5f),
                layout.ToTouch(area, bottom.X + bottom.Width - 0.5f, bottom.Y + bottom.Height - 0.5f),
            };
            foreach (var c in corners)
                Assert.IsTrue(c is { X: 0 or 255, Y: 0 or 191 }, $"{rotation}: corner maps to a DS corner ({c})");
            Assert.AreEqual(4, corners.Distinct().Count(), $"{rotation}: four different corners");

            // The texture corners must agree with the touch mapping: the window's top-left corner
            // shows the same DS pixel (as texture u, v) that a touch there reports.
            var uv = layout.CornerUvs(0f, 1f)[0];
            var touch = corners[0]!.Value;
            Assert.AreEqual(touch.X == 0 ? 0f : 1f, uv.U, $"{rotation}: u of the top-left corner");
            Assert.AreEqual(touch.Y == 0 ? 0f : 1f, uv.V, $"{rotation}: v of the top-left corner");
        }

        [TestMethod]
        public void IntegerScaleUsesWholeMultiples()
        {
            var layout = new ScreenLayout { IntegerScale = true };
            var (top, _) = layout.Compute(new ScreenRect(0, 0, 700, 1000));
            Assert.AreEqual(512, top.Width, "2x, not 2.6x");
        }
    }
}
