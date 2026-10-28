using NUnit.Framework;
using UnityEngine;

namespace WaterSplash.Tests
{
    public sealed class MlsMpmMobileValidationTests
    {
        [TestCase(2608, 833, 1f)]
        [TestCase(2400, 720, .75f)]
        [TestCase(1080, 1600, .5f)]
        public void AdaptiveTargetMatchesViewportRatherThanFixedAspect(int w,int h,float scale)
        {
            var size=MlsMpmMobileValidation.AdaptiveSize(new Vector2(w,h),scale);
            Assert.That(size.x%2,Is.Zero);Assert.That(size.y%2,Is.Zero);
            Assert.That(size.x,Is.EqualTo(w*scale).Within(1.01f));
            Assert.That(size.y,Is.EqualTo(h*scale).Within(1.01f));
        }
        [Test]
        public void MinimizedViewportStillCreatesValidTarget()
        {
            Assert.That(MlsMpmMobileValidation.AdaptiveSize(Vector2.zero,1),Is.EqualTo(new Vector2Int(2,2)));
        }
    }
}
