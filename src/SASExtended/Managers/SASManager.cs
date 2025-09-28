using BepInEx.Logging;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended.Models;
using UnityEngine;

namespace SASExtended.Managers;

public class SASManager : MonoBehaviour
{
    private SASManager() { }

    public static SASManager Instance { get; set; }

    //public double X = 90, Y = 90, Z = 90;
    public double X = 0, Y = 0, Z = 0;
    public bool XEnabled = true, YEnabled = true, ZEnabled = true;
    public AttitudeMode AttitudeMode = AttitudeMode.None;

    private static readonly ManualLogSource _LOGGER = BepInEx.Logging.Logger.CreateLogSource("SASExtended.SASManager");
    private static double _UT => GameManager.Instance.Game?.UniverseModel?.UniverseTime ?? 0;
    private double _lastRefreshTime = 0;

    private VesselComponent _vessel => GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
    private Rotation _rotation;

    private void Start()
    {
        Instance = this;
    }

    private void Update()
    {
        if (AttitudeMode != AttitudeMode.None && (_UT - _lastRefreshTime > DebugUI.Instance.RefreshInterval))
        {
            SetRotation();
            
            _vessel.Autopilot.SAS.LockRotation(_rotation);
            _lastRefreshTime = _UT;
        }
    }

    public void SetRotation()
    {
        var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

        //double x, y, z;
        //if (!double.TryParse(_x, out x) || !double.TryParse(_y, out y) || !double.TryParse(_z, out z))
        //    return;

        var orbitPrograde = Vector.normalize(vessel._telemetryComponent.OrbitalMovementVelocity);
        var orbitRetrograde = Vector.negate(orbitPrograde);
        var horizon = vessel._telemetryComponent.HorizonNorth;
        var surfacePrograde = Vector.normalize(vessel._telemetryComponent.SurfaceMovementPrograde);
        var surfaceRetrograde = Vector.negate(surfacePrograde);
        var target = Vector.normalize(vessel._telemetryComponent.TargetDirection);
        var antiTarget = Vector.negate(target);

        //var sun = vessel._telemetryComponent.SOIFrameBody.transform.parent.forward;

        var sunBody = GetParentStar(vessel);
        var sun = Vector.normalize(Position.Delta(sunBody.Position, vessel._telemetryComponent.RootPosition));
        var antiSun = Vector.negate(sun);


        var upwards = Vector.normalize(Position.Delta(vessel._telemetryComponent.RootPosition, vessel._telemetryComponent.SOIPosition));

        switch (AttitudeMode)
        {
            case AttitudeMode.OrbitPrograde:
                _rotation = Rotation.LookRotation(orbitPrograde, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.OrbitRetrograde:
                _rotation = Rotation.LookRotation(orbitRetrograde, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.SurfaceSurf:
                _rotation = Rotation.LookRotation(horizon, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.SurfaceSvelPlus:
                _rotation = Rotation.LookRotation(surfacePrograde, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.SurfaceSvelMinus:
                _rotation = Rotation.LookRotation(surfaceRetrograde, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.TargetPlus:
                _rotation = Rotation.LookRotation(target, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.SpecialStarPlus:
                _rotation = Rotation.LookRotation(sun, upwards); // doesn't work
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.SpecialStarMinus:
                _rotation = Rotation.LookRotation(antiSun, upwards); // doesn't work
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.SurfaceUp:
                _rotation = Rotation.LookRotation(horizon, upwards); // maybe we can simplify this
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-90, 0, 0) * QuaternionD.Euler(90, 0, 0);
                break;

            default: // horizon
                _rotation = Rotation.LookRotation(horizon, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
        }
    }

    public void SetSASOff()
    {
        AttitudeMode = AttitudeMode.None;
        _vessel.Autopilot.SetActive(false);
    }

    public void SetSASKillrot()
    {
        AttitudeMode = AttitudeMode.KillRot;
        
        _vessel.Autopilot.SetActive(true);
    }

    public void SetSASManeuver()
    {
        AttitudeMode = AttitudeMode.Maneuver;

        _vessel.Autopilot.SetActive(true);
    }

    public void SetOrbitPrograde()
    {
        AttitudeMode = AttitudeMode.OrbitPrograde;

        _vessel.Autopilot.SetActive(true);
    }

    private CelestialBodyComponent GetParentStar(VesselComponent vessel)
    {
        var body = vessel.mainBody;

        try
        {
            while (!body.IsStar)
            {
                body = body.referenceBody;
            }
        }
        catch (Exception ex)
        {
            _LOGGER.LogError($"Unable to fetch parent star for vessel {vessel.Name}. How is this possible?! Exception: {ex.Message}");
        }

        return body;
    }


}
