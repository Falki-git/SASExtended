using System;

namespace SASExtended.PureMath
{
    // Pure ballistic/Keplerian coast math used by LandingPredictionManager to predict "where do I
    // land if I cut thrust now" - RK4 integration, coarse/fine impact bracketing, and bisection
    // refinement. The only game coupling in the original code was CelestialBodyComponent.
    // GetAltitudeFromTerrain (needs a live ICoordinateSystem, so it can't live here) - that's
    // injected into MarchToImpact as altitudeAt: a de-rotated local-frame position in, a
    // terrain-relative altitude out (already including ground correction - see
    // LandingPredictionManager.RecomputeTrajectory's local AltitudeAt function) - so the actual
    // math is fully testable with edit-mode tests instead of only via an in-game build. See
    // .claude/landing_prediction_fixes.md for the ten-round in-game debugging history this split is
    // meant to avoid repeating - every one of those rounds required launching the game; every bug in
    // this file is reproducible at the desk instead.
    public static class LandingPredictionMath
    {
        // Bisection refinement of the final impact point within the bracketing pair of samples found
        // by MarchToImpact - see the call site for why a single linear-interpolation guess isn't
        // enough. 20 halvings shrinks the bracket by ~2^20, far finer than the visual marker's size.
        public const int ImpactBisectionIterations = 20;

        // Integrates up to maxSteps of size dt from (startPos, startVel) looking for the first
        // terrain crossing, recording every sample - de-rotated (see Derotate) and expressed as an
        // OFFSET from startPos, not an absolute position - into `offsets` (index 0 = start, always
        // zero) if it isn't null. Returns false if no crossing is found in maxSteps * dt seconds.
        // Storing offsets rather than absolute positions is what lets LandingPredictionManager anchor
        // rendering to the vessel's current position every frame instead of a cached absolute one -
        // see its field comment on _sampleOffsets.
        public static bool MarchToImpact(
            Func<Vector3d, double> altitudeAt, Vector3d omegaAxis, double omegaMag, double mu,
            Vector3d startPos, Vector3d startVel, double startAlt, double dt, int maxSteps,
            Vector3d[] offsets,
            out int sampleCount, out Vector3d impactOffset, out double impactSpan)
        {
            sampleCount = 1;
            if (offsets != null)
                offsets[0] = Vector3d.zero;

            impactOffset = Vector3d.zero;
            impactSpan = 0.0;

            if (startAlt <= 0.0)
                return true; // already at/under the ground this instant

            Vector3d pos = startPos, vel = startVel;
            double prevAlt = startAlt;
            Vector3d prevOffset = Vector3d.zero;

            for (int i = 1; i <= maxSteps; i++)
            {
                IntegrateStep(ref pos, ref vel, mu, dt);

                double t = i * dt;
                Vector3d derotated = Derotate(pos, omegaAxis, omegaMag, t);
                Vector3d offset = derotated - startPos;
                if (offsets != null)
                    offsets[sampleCount] = offset;
                sampleCount++;

                double alt = altitudeAt(derotated);
                if (alt <= 0.0)
                {
                    // Terrain height isn't guaranteed to vary linearly between the last two samples
                    // (a boulder or ridge can sit between them), so a single linear-interpolation
                    // guess can land "impact" on the far side of that feature, embedded underground -
                    // see landing_prediction_fixes.md round 13. Bisect instead, re-querying the
                    // actual terrain altitude at each candidate.
                    Vector3d hiOffset = offset;
                    double loAlt = prevAlt, hiAlt = alt;
                    double loFrac = 0.0, hiFrac = 1.0;
                    for (int bisect = 0; bisect < ImpactBisectionIterations; bisect++)
                    {
                        double frac = loFrac + (hiFrac - loFrac) * (loAlt / (loAlt - hiAlt));
                        frac = Math.Min(Math.Max(frac, loFrac + 1e-6), hiFrac - 1e-6);
                        Vector3d candidateOffset = Vector3d.Lerp(prevOffset, offset, frac);
                        double candidateAlt = altitudeAt(startPos + candidateOffset);
                        if (candidateAlt > 0.0)
                        {
                            loAlt = candidateAlt;
                            loFrac = frac;
                        }
                        else
                        {
                            hiOffset = candidateOffset;
                            hiAlt = candidateAlt;
                            hiFrac = frac;
                        }
                    }
                    // The hi side is always at-or-under the surface, so ending there (rather than
                    // the midpoint) never renders the marker floating visibly above the ground.
                    impactOffset = hiOffset;
                    impactSpan = (i - 1 + hiFrac) * dt;
                    return true;
                }

                prevAlt = alt;
                prevOffset = offset;
            }

            return false;
        }

        // Classical two-body (pure gravity, no drag) RK4 step - airless bodies only, see the plan.
        public static void IntegrateStep(ref Vector3d pos, ref Vector3d vel, double mu, double dt)
        {
            Vector3d k1V = Acceleration(pos, mu);
            Vector3d k1X = vel;

            Vector3d k2X = vel + k1V * (dt * 0.5);
            Vector3d k2V = Acceleration(pos + k1X * (dt * 0.5), mu);

            Vector3d k3X = vel + k2V * (dt * 0.5);
            Vector3d k3V = Acceleration(pos + k2X * (dt * 0.5), mu);

            Vector3d k4X = vel + k3V * dt;
            Vector3d k4V = Acceleration(pos + k3X * dt, mu);

            pos += (dt / 6.0) * (k1X + 2.0 * k2X + 2.0 * k3X + k4X);
            vel += (dt / 6.0) * (k1V + 2.0 * k2V + 2.0 * k3V + k4V);
        }

        public static Vector3d Acceleration(Vector3d pos, double mu)
        {
            double r = pos.magnitude;
            return pos * (-mu / (r * r * r));
        }

        // Rotates a future inertial-frame position backward by the angle the body will have swept
        // forward over `elapsedTime` seconds - i.e. "where would this point be if the ground hadn't
        // moved since now". GetAltitudeFromTerrain (and rendering the point at all, via the current
        // floating-origin conversion) only ever reflects the body's *current* orientation, so both
        // the terrain check and anything stored for display need this correction applied
        // consistently - otherwise a vessel with near-zero horizontal (surface-relative) velocity
        // gets predicted to land far to the side, since the body's own rotation shows up as
        // inertial-frame horizontal velocity that only cancels out if every future position/check
        // shares this same correction.
        public static Vector3d Derotate(Vector3d localPos, Vector3d omegaAxis, double omegaMag, double elapsedTime)
        {
            if (omegaMag <= 0.0 || elapsedTime == 0.0)
                return localPos;

            var backRotation = QuaternionD.AngleAxis(-omegaMag * elapsedTime * (180.0 / Math.PI), omegaAxis);
            return backRotation * localPos;
        }
    }
}
