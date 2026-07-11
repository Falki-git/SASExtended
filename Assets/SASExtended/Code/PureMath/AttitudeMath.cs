namespace SASExtended.PureMath
{
    // Pure quaternion math shared by every "point the nose at X" attitude mode
    // (see SASManager.BuildPointingRotation/GetCurrentOffsetAngles). Deliberately kept free of
    // KSP.Sim's frame-aware Vector/Rotation wrappers (which need a live ICoordinateSystem) so this
    // can be validated with edit-mode tests instead of only via an in-game build - see
    // hover_mode_fixes.md for the debugging cost that motivated this split.
    //
    // Namespace deliberately isn't "SASExtended.Math" - nested-namespace lookup would then shadow
    // System.Math for every unqualified "Math.*" call anywhere under the SASExtended.* namespace
    // tree (not just files with a `using` for it), breaking Math.Cos/Math.Abs/etc. everywhere else
    // in the mod.
    public static class AttitudeMath
    {
        // Solves `currentRotation = look * offset * Euler(90,0,0)` for `offset` and decomposes it
        // back into heading/pitch/roll using the same convention ComposePointingRotation builds it
        // with. Used to make a disabled H/P/R axis free-track the vessel's actual current angle
        // instead of snapping to 0 - see the field-level comment on SASManager.GetCurrentOffsetAngles.
        public static (double heading, double pitch, double roll) GetCurrentOffsetAngles(QuaternionD look, QuaternionD currentRotation)
        {
            var offset = QuaternionD.Inverse(look) * currentRotation * QuaternionD.Inverse(QuaternionD.Euler(90, 0, 0));
            var euler = ((UnityEngine.Quaternion)offset).eulerAngles;
            return (NormalizeAngle(euler.y), NormalizeAngle(-euler.x), NormalizeAngle(euler.z));
        }

        // Picks the commanded heading/pitch/roll offset for each axis: the fixed user-set value when
        // enabled, or the vessel's current live offset (via GetCurrentOffsetAngles) when disabled, so
        // a disabled axis drifts freely instead of being pinned - see the field-level comment on
        // SASManager.GetCurrentOffsetAngles for why.
        public static void ResolveAppliedOffsets(
            bool xEnabled, double x,
            bool yEnabled, double y,
            bool zEnabled, double z,
            QuaternionD look, QuaternionD currentRotation,
            out double appliedX, out double appliedY, out double appliedZ)
        {
            appliedX = xEnabled ? x : 0;
            appliedY = yEnabled ? y : 0;
            appliedZ = zEnabled ? z : 0;
            if (!xEnabled || !yEnabled || !zEnabled)
            {
                var current = GetCurrentOffsetAngles(look, currentRotation);
                if (!xEnabled) appliedX = current.heading;
                if (!yEnabled) appliedY = current.pitch;
                if (!zEnabled) appliedZ = current.roll;
            }
        }

        // Aligns local "up" (the vessel's nose axis - hence the trailing Euler(90,0,0)) with `look`'s
        // forward, then applies the heading/pitch/roll offsets on top.
        public static QuaternionD ComposePointingRotation(QuaternionD look, double appliedX, double appliedY, double appliedZ) =>
            look * QuaternionD.Euler(-appliedY, appliedX, appliedZ) * QuaternionD.Euler(90, 0, 0);

        // Wrap 0-360 to -180..180.
        public static float NormalizeAngle(float angle)
        {
            if (angle > 180f)
                angle -= 360f;

            if (angle < -180f)
                angle += 360f;

            return angle;
        }
    }
}
