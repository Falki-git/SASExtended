using NUnit.Framework;
using SASExtended.PureMath;

namespace SASExtended.Tests
{
    public class HoverThrottleMathTests
    {
        private static HoverThrottleState InitialState(float throttle = 0.5f) => new HoverThrottleState
        {
            ThrottleIntegral = throttle,
            ThrottleIntegralKnown = false,
            LastVerticalSpeedForDerivative = 0,
            FilteredThrustDrivenAccel = 0,
            Throttle = throttle,
        };

        [Test]
        public void Step_FirstTick_MarksIntegralKnown()
        {
            var result = HoverThrottleMath.Step(
                InitialState(), dt: 0.02, actualVerticalSpeed: 0, targetVerticalSpeed: 0,
                gravityMagnitude: 9.81, hoverCosTilt: 1.0,
                kp: 0.05, ki: 0.06, kd: 0.08, accelFilterTime: 0.2, maxRate: 2.0);

            Assert.That(result.State.ThrottleIntegralKnown, Is.True);
        }

        [Test]
        public void Step_FallingBelowTarget_IncreasesThrottleIntegralOverTime()
        {
            var state = InitialState(throttle: 0.5f);
            // Vessel descending at 5 m/s against a 0 m/s target - the integral term should climb.
            for (int i = 0; i < 50; i++)
            {
                var result = HoverThrottleMath.Step(
                    state, dt: 0.02, actualVerticalSpeed: -5, targetVerticalSpeed: 0,
                    gravityMagnitude: 9.81, hoverCosTilt: 1.0,
                    kp: 0.05, ki: 0.06, kd: 0.08, accelFilterTime: 0.2, maxRate: 2.0);
                state = result.State;
            }

            Assert.That(state.ThrottleIntegral, Is.GreaterThan(0.5));
        }

        [Test]
        public void Step_ThrottleNeverLeavesZeroToOneRange()
        {
            var state = InitialState(throttle: 1f);
            HoverThrottleStepResult result = default;
            // A huge downward error should saturate, not overshoot past 1.0.
            for (int i = 0; i < 100; i++)
            {
                result = HoverThrottleMath.Step(
                    state, dt: 0.02, actualVerticalSpeed: -1000, targetVerticalSpeed: 0,
                    gravityMagnitude: 9.81, hoverCosTilt: 1.0,
                    kp: 0.05, ki: 0.06, kd: 0.08, accelFilterTime: 0.2, maxRate: 2.0);
                state = result.State;
            }

            Assert.That(state.Throttle, Is.LessThanOrEqualTo(1f));
            Assert.That(state.Throttle, Is.GreaterThanOrEqualTo(0f));
            Assert.That(result.ClampedThrottle, Is.EqualTo(1.0).Within(1e-6));
        }

        [Test]
        public void Step_RateLimitsThrottleChangePerTick()
        {
            // Starting throttle 0, a demand that would otherwise jump straight to 1.0 in one tick
            // must be capped by maxRate * dt.
            var state = InitialState(throttle: 0f);
            const double maxRate = 2.0;
            const double dt = 0.02;

            var result = HoverThrottleMath.Step(
                state, dt, actualVerticalSpeed: -1000, targetVerticalSpeed: 0,
                gravityMagnitude: 9.81, hoverCosTilt: 1.0,
                kp: 0.05, ki: 0.06, kd: 0.08, accelFilterTime: 0.2, maxRate: maxRate);

            Assert.That(result.State.Throttle, Is.LessThanOrEqualTo((float)(maxRate * dt) + 1e-6f));
        }

        [Test]
        public void Step_AtTargetSpeedWithNoIntegral_HoldsThrottleSteady()
        {
            var state = InitialState(throttle: 0f);

            var result = HoverThrottleMath.Step(
                state, dt: 0.02, actualVerticalSpeed: 0, targetVerticalSpeed: 0,
                gravityMagnitude: 0, hoverCosTilt: 1.0,
                kp: 0.05, ki: 0.06, kd: 0.08, accelFilterTime: 0.2, maxRate: 2.0);

            Assert.That(result.ClampedThrottle, Is.EqualTo(0).Within(1e-6));
        }
    }
}
