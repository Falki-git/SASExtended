using System;
using NUnit.Framework;
using SASExtended.PureMath;

namespace SASExtended.Tests
{
    public class LandingPredictionMathTests
    {
        [Test]
        public void Acceleration_PointsTowardOrigin_WithInverseSquareMagnitude()
        {
            var pos = new Vector3d(100, 0, 0);
            const double mu = 10000; // r=100 -> mu/r^2 = 1

            var accel = LandingPredictionMath.Acceleration(pos, mu);

            Assert.That(accel.x, Is.EqualTo(-1.0).Within(1e-9));
            Assert.That(accel.y, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(accel.z, Is.EqualTo(0.0).Within(1e-9));
        }

        [Test]
        public void IntegrateStep_CircularOrbit_StaysApproximatelyCircularOverManyOrbits()
        {
            const double mu = 398600.0;
            const double r = 7000.0;
            double v = Math.Sqrt(mu / r); // circular orbit speed
            var pos = new Vector3d(r, 0, 0);
            var vel = new Vector3d(0, v, 0); // tangential
            const double dt = 1.0;

            for (int i = 0; i < 1000; i++)
                LandingPredictionMath.IntegrateStep(ref pos, ref vel, mu, dt);

            // RK4 isn't symplectic so energy drifts slowly over long integrations, but not fast
            // enough to move the radius this much over 1000 one-second steps of a real orbit.
            Assert.That(pos.magnitude, Is.EqualTo(r).Within(r * 0.01));
        }

        [Test]
        public void Derotate_ZeroElapsedTime_ReturnsUnchanged()
        {
            var pos = new Vector3d(3, 4, 5);

            var result = LandingPredictionMath.Derotate(pos, Vector3d.up, omegaMag: 0.5, elapsedTime: 0.0);

            Assert.That(Vector3d.Distance(result, pos), Is.LessThan(1e-9));
        }

        [Test]
        public void Derotate_ZeroOmega_ReturnsUnchanged()
        {
            var pos = new Vector3d(3, 4, 5);

            var result = LandingPredictionMath.Derotate(pos, Vector3d.up, omegaMag: 0.0, elapsedTime: 10.0);

            Assert.That(Vector3d.Distance(result, pos), Is.LessThan(1e-9));
        }

        [Test]
        public void Derotate_PreservesMagnitude()
        {
            var pos = new Vector3d(7, -2, 3);

            var result = LandingPredictionMath.Derotate(pos, Vector3d.up, omegaMag: 0.3, elapsedTime: 4.0);

            Assert.That(result.magnitude, Is.EqualTo(pos.magnitude).Within(1e-9));
        }

        [Test]
        public void Derotate_FullRotationPeriod_ReturnsToStartingPosition()
        {
            var pos = new Vector3d(10, 0, 0);
            const double omegaMag = 0.5; // rad/s
            double fullPeriod = 2.0 * Math.PI / omegaMag;

            var result = LandingPredictionMath.Derotate(pos, Vector3d.up, omegaMag, fullPeriod);

            Assert.That(Vector3d.Distance(result, pos), Is.LessThan(1e-6));
        }

        [Test]
        public void MarchToImpact_AlreadyAtOrBelowGround_ReturnsTrueImmediately()
        {
            bool found = LandingPredictionMath.MarchToImpact(
                pos => 0.0, Vector3d.up, omegaMag: 0.0, mu: 0.0,
                startPos: Vector3d.zero, startVel: Vector3d.zero, startAlt: 0.0,
                dt: 1.0, maxSteps: 10, offsets: null,
                out int sampleCount, out Vector3d impactOffset, out double impactSpan);

            Assert.That(found, Is.True);
            Assert.That(sampleCount, Is.EqualTo(1));
            Assert.That(impactOffset, Is.EqualTo(Vector3d.zero));
            Assert.That(impactSpan, Is.EqualTo(0.0));
        }

        // No gravity (mu=0) and no body rotation (omegaMag=0) reduces this to pure straight-line
        // kinematics - the terrain sampler represents flat ground that closes 1:1 with the vessel's
        // own downward (-y) travel, so the analytically expected impact time/offset is exactly
        // startAlt / descent speed. Deliberately NOT chosen so the crossing lands exactly on a step
        // boundary (that would make loAlt/hiAlt tie at exactly 0, an edge case of the bisection
        // rather than a representative one).
        private static readonly Vector3d StartPos = new Vector3d(0, 1000, 0);
        private static readonly Vector3d StartVel = new Vector3d(0, -10, 0);
        private const double StartAlt = 97.0;
        private static double FlatGroundAltitudeAt(Vector3d pos) => StartAlt - (StartPos.y - pos.y);

        [Test]
        public void MarchToImpact_ConstantVelocityDescent_FindsImpactAtExpectedTime()
        {
            bool found = LandingPredictionMath.MarchToImpact(
                FlatGroundAltitudeAt, Vector3d.up, omegaMag: 0.0, mu: 0.0,
                StartPos, StartVel, StartAlt,
                dt: 0.5, maxSteps: 40, offsets: null,
                out _, out Vector3d impactOffset, out double impactSpan);

            Assert.That(found, Is.True);
            // 97m at 10 m/s straight down.
            Assert.That(impactSpan, Is.EqualTo(9.7).Within(1e-3));
            Assert.That(impactOffset.y, Is.EqualTo(-97.0).Within(1e-3));
        }

        [Test]
        public void MarchToImpact_NeverCrossesWithinHorizon_ReturnsFalse()
        {
            bool found = LandingPredictionMath.MarchToImpact(
                pos => 1000.0, Vector3d.up, omegaMag: 0.0, mu: 0.0,
                startPos: Vector3d.zero, startVel: new Vector3d(1, 0, 0), startAlt: 1000.0,
                dt: 1.0, maxSteps: 5, offsets: null,
                out _, out _, out _);

            Assert.That(found, Is.False);
        }

        [Test]
        public void MarchToImpact_FillsOffsetsArrayWhenProvided()
        {
            var offsets = new Vector3d[41];

            LandingPredictionMath.MarchToImpact(
                FlatGroundAltitudeAt, Vector3d.up, omegaMag: 0.0, mu: 0.0,
                StartPos, StartVel, StartAlt,
                dt: 0.5, maxSteps: 40, offsets,
                out _, out _, out _);

            Assert.That(offsets[0], Is.EqualTo(Vector3d.zero));
            // One 0.5s step at -10 m/s - the raw (non-bisected) sample recorded before impact.
            Assert.That(offsets[1].y, Is.EqualTo(-5.0).Within(1e-6));
        }
    }
}
