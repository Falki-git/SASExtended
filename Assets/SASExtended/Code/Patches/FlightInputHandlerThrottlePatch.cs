using HarmonyLib;
using KSP.Sim.impl;
using KSP.Sim.State;
using SASExtended.Managers;
using SASExtended.Models;

namespace SASExtended.Patches
{
    /// <summary>
    /// Lets the hover controller own the throttle. The stock <see cref="FlightInputHandler"/> keeps a
    /// persistent <c>_flightCtrlState.mainThrottle</c> and pushes it to the active vessel every
    /// FixedUpdate, so simply calling <c>SetFlightControlState</c> from our Update loop would be
    /// overwritten. We instead overwrite that field right after the handler has applied player input
    /// (in <c>UpdateFlightControlState</c>) but before the autopilot rotation pass and the push to the
    /// vessel — making the commanded hover throttle stick.
    /// </summary>
    [HarmonyPatch(typeof(FlightInputHandler), "UpdateFlightControlState")]
    internal static class FlightInputHandlerThrottlePatch
    {
        // ____flightCtrlState (four underscores) injects the private instance field `_flightCtrlState`.
        private static void Postfix(ref FlightCtrlState ____flightCtrlState)
        {
            var manager = SASManager.Instance;
            if (manager != null && manager.AttitudeMode == AttitudeMode.Hover)
            {
                ____flightCtrlState.mainThrottle = manager.HoverThrottle;
            }
        }
    }
}
