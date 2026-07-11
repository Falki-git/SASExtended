using NUnit.Framework;
using SASExtended.PureMath;

namespace SASExtended.Tests
{
    public class AttitudeMathTests
    {
        [Test]
        public void ComposePointingRotation_WithZeroOffsets_PointsNoseAtLookTarget()
        {
            var look = QuaternionD.LookRotation(Vector3d.right, Vector3d.up);

            var composed = AttitudeMath.ComposePointingRotation(look, 0, 0, 0);

            // The nose axis is local "up" (see BuildPointingRotation's trailing Euler(90,0,0)) -
            // it should align exactly with whatever LookRotation's forward argument was.
            var nose = composed * Vector3d.up;
            Assert.That(Vector3d.Distance(nose, Vector3d.right), Is.LessThan(1e-6));
        }

        [Test]
        public void GetCurrentOffsetAngles_WhenAlignedWithLook_ReturnsZero()
        {
            var look = QuaternionD.LookRotation(Vector3d.forward, Vector3d.up);
            var current = AttitudeMath.ComposePointingRotation(look, 0, 0, 0);

            var (heading, pitch, roll) = AttitudeMath.GetCurrentOffsetAngles(look, current);

            Assert.That(heading, Is.EqualTo(0).Within(1e-3));
            Assert.That(pitch, Is.EqualTo(0).Within(1e-3));
            Assert.That(roll, Is.EqualTo(0).Within(1e-3));
        }

        [Test]
        public void GetCurrentOffsetAngles_RoundTripsComposePointingRotation()
        {
            var look = QuaternionD.LookRotation(Vector3d.forward, Vector3d.up);
            const double heading = 15, pitch = -8, roll = 42;
            var current = AttitudeMath.ComposePointingRotation(look, heading, pitch, roll);

            var decoded = AttitudeMath.GetCurrentOffsetAngles(look, current);

            Assert.That(decoded.heading, Is.EqualTo(heading).Within(1e-3));
            Assert.That(decoded.pitch, Is.EqualTo(pitch).Within(1e-3));
            Assert.That(decoded.roll, Is.EqualTo(roll).Within(1e-3));
        }

        [Test]
        public void ResolveAppliedOffsets_EnabledAxis_UsesFixedValue()
        {
            var look = QuaternionD.LookRotation(Vector3d.forward, Vector3d.up);
            var current = AttitudeMath.ComposePointingRotation(look, 0, 0, 30);

            AttitudeMath.ResolveAppliedOffsets(
                xEnabled: true, x: 10,
                yEnabled: true, y: 20,
                zEnabled: true, z: 5,
                look, current,
                out var appliedX, out var appliedY, out var appliedZ);

            Assert.That(appliedX, Is.EqualTo(10));
            Assert.That(appliedY, Is.EqualTo(20));
            Assert.That(appliedZ, Is.EqualTo(5));
        }

        [Test]
        public void ResolveAppliedOffsets_DisabledAxis_FreeTracksCurrentOffsetInsteadOfSnappingToZero()
        {
            var look = QuaternionD.LookRotation(Vector3d.forward, Vector3d.up);
            // Vessel is currently rolled 30 degrees relative to the look rotation.
            var current = AttitudeMath.ComposePointingRotation(look, 0, 0, 30);

            AttitudeMath.ResolveAppliedOffsets(
                xEnabled: true, x: 0,
                yEnabled: true, y: 0,
                zEnabled: false, z: 0, // roll disabled - should free-track the current 30deg roll, not snap to 0
                look, current,
                out var appliedX, out var appliedY, out var appliedZ);

            Assert.That(appliedX, Is.EqualTo(0).Within(1e-3));
            Assert.That(appliedY, Is.EqualTo(0).Within(1e-3));
            Assert.That(appliedZ, Is.EqualTo(30).Within(1e-3));
        }
    }
}
