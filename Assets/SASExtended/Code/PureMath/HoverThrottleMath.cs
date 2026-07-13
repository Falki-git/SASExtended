using System;

namespace SASExtended.PureMath
{
    // Carries the throttle P+I+D controller's state between ticks (see SASManager's
    // _throttleIntegral/_hoverThrottleIntegralKnown/_lastVerticalSpeedForDerivative/
    // _filteredThrustDrivenAccel/HoverThrottle fields, which this mirrors 1:1 so HoverThrottleMath.Step
    // stays pure and testable without SASManager/Unity).
    public struct HoverThrottleState
    {
        public double ThrottleIntegral;
        public bool ThrottleIntegralKnown;
        public double LastVerticalSpeedForDerivative;
        public double FilteredThrustDrivenAccel;
        public float Throttle;
    }

    public struct HoverThrottleStepResult
    {
        public HoverThrottleState State;
        public double VerticalSpeedError;
        public double VerticalAccel;
        public double ThrustDrivenAccel;
        public double ThrottlePreClamp;
        public double ClampedThrottle;
        public double RateLimitedThrottle;
    }

    // Pure vertical-speed throttle controller (holds a target vertical speed, not altitude - see
    // SASManager's Hover field-block comment). Extracted out of SASManager.UpdateHoverThrottle so the
    // control law can be validated with edit-mode tests instead of only via in-game rounds - see
    // hover_mode_fixes.md for the debugging cost that motivated this split (the offset math above
    // and this law took 11 in-game rounds to get right).
    public static class HoverThrottleMath
    {
        public static HoverThrottleStepResult Step(
            HoverThrottleState state,
            double dt,
            double actualVerticalSpeed,
            double targetVerticalSpeed,
            double gravityMagnitude,
            double hoverCosTilt,
            double kp, double ki, double kd,
            double accelFilterTime,
            double maxRate)
        {
            double verticalSpeedError = targetVerticalSpeed - actualVerticalSpeed;
            // Derivative-on-measurement (not on error) so a future change to the target vertical
            // speed doesn't itself spike this term - only the vessel's own acceleration does.
            double verticalAccel = (actualVerticalSpeed - state.LastVerticalSpeedForDerivative) / dt;
            state.LastVerticalSpeedForDerivative = actualVerticalSpeed;

            // Subtract out the baseline free-fall deceleration so the D term only reacts to
            // thrust/drag-driven acceleration, not to gravity itself - see hover_mode_fixes.md round 4.
            double thrustDrivenAccel = verticalAccel + gravityMagnitude;
            // Low-pass filter before this feeds the D term - see HoverThrottleAccelFilterTime's field
            // comment on SASManager for why.
            double filterAlpha = 1.0 - Math.Exp(-dt / accelFilterTime);
            state.FilteredThrustDrivenAccel += (thrustDrivenAccel - state.FilteredThrustDrivenAccel) * filterAlpha;

            state.ThrottleIntegral = Clamp(state.ThrottleIntegral + verticalSpeedError * ki * dt, 0.0, 1.0);
            // From here on ThrottleIntegral is a real, live PID value - even if it happens to be
            // exactly 0.0 (a normal state, not "no data yet"; see SASManager.SetRotation's Hover case
            // for why that distinction matters).
            state.ThrottleIntegralKnown = true;
            double throttle = (state.ThrottleIntegral + verticalSpeedError * kp - state.FilteredThrustDrivenAccel * kd) / hoverCosTilt;
            double clampedThrottle = Clamp(throttle, 0.0, 1.0);

            // Rate-limit the actuator itself - see HoverThrottleMaxRate's field comment on SASManager
            // for why. This is the last step before the value is published, so it bounds what the
            // engine actually does regardless of how large a jump the P/I/D formula above just asked for.
            double maxDelta = maxRate * dt;
            double rateLimitedThrottle = Clamp(clampedThrottle, state.Throttle - maxDelta, state.Throttle + maxDelta);
            state.Throttle = (float)Clamp(rateLimitedThrottle, 0.0, 1.0);

            return new HoverThrottleStepResult
            {
                State = state,
                VerticalSpeedError = verticalSpeedError,
                VerticalAccel = verticalAccel,
                ThrustDrivenAccel = thrustDrivenAccel,
                ThrottlePreClamp = throttle,
                ClampedThrottle = clampedThrottle,
                RateLimitedThrottle = rateLimitedThrottle,
            };
        }

        private static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);
    }
}
