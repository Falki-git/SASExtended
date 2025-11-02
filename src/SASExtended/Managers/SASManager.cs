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

    public double RefreshInterval = 0;
    public double RefreshInterval_short = 0.02;
    public double RefreshInterval_mid = 0.05;
    public double RefreshInterval_long = 0.08;
    public double AngleToRotation_small = 10;
    public double AngleToRotation_large = 30;

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
        if (AttitudeMode != AttitudeMode.None && (_UT - _lastRefreshTime > RefreshInterval /*DebugUI.Instance.RefreshInterval*/))
        {
            SetRotation();
            _vessel.Autopilot.SAS.LockRotation(_rotation);
            _lastRefreshTime = _UT;

            SetRefreshInterval();
        }
    }

    public void SetRotation()
    {
        //double x, y, z;
        //if (!double.TryParse(_x, out x) || !double.TryParse(_y, out y) || !double.TryParse(_z, out z))
        //    return;

        var orbitPrograde = Vector.normalize(_vessel._telemetryComponent.OrbitalMovementVelocity);
        var orbitRetrograde = Vector.negate(orbitPrograde);
        var orbitNormal = _vessel._telemetryComponent.OrbitMovementNormal;
        var orbitAntiNormal = Vector.negate(orbitNormal);
        var orbitRadialIn = _vessel._telemetryComponent.OrbitMovementRadialIn;
        var orbitRadialOut = Vector.negate(orbitRadialIn);

        var horizon = _vessel._telemetryComponent.HorizonNorth;
        var surfacePrograde = Vector.normalize(_vessel._telemetryComponent.SurfaceMovementPrograde);
        var surfaceRetrograde = Vector.negate(surfacePrograde);
        var target = Vector.normalize(_vessel._telemetryComponent.TargetDirection);
        var antiTarget = Vector.negate(target);
        

        var maneuver = Vector.normalize(_vessel._telemetryComponent.ManeuverDirection);

        //var currentAttitude = _vessel.transform.coordinateSystem.ToLocalVector(_vessel.MOI.coordinateSystem.up).normalized;
        //var angle = Vector3d.Angle(orbitPrograde.vector, vessel.transform.coordinateSystem.ToLocalVector(vessel.MOI.coordinateSystem.up).normalized);
        //var sun = vessel._telemetryComponent.SOIFrameBody.transform.parent.forward;
        
        var sunBody = GetParentStar(_vessel);
        var sun = Vector.normalize(Position.Delta(sunBody.Position, _vessel._telemetryComponent.RootPosition));
        var antiSun = Vector.negate(sun);


        var upwards = Vector.normalize(Position.Delta(_vessel._telemetryComponent.RootPosition, _vessel._telemetryComponent.SOIPosition));

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
            case AttitudeMode.OrbitNormal:
                _rotation = Rotation.LookRotation(orbitNormal, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.OrbitAntiNormal:
                _rotation = Rotation.LookRotation(orbitAntiNormal, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.OrbitRadialIn:
                _rotation = Rotation.LookRotation(orbitRadialIn, upwards);
                _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-Y, X, Z) * QuaternionD.Euler(90, 0, 0);
                break;
            case AttitudeMode.OrbitRadialOut:
                _rotation = Rotation.LookRotation(orbitRadialOut, upwards);
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
    public void SetOrbitRetrograde()
    {
        AttitudeMode = AttitudeMode.OrbitRetrograde;

        _vessel.Autopilot.SetActive(true);
    }

    public void SetOrbitNormal()
    {
        AttitudeMode = AttitudeMode.OrbitNormal;

        _vessel.Autopilot.SetActive(true);
    }
    public void SetOrbitAntiNormal()
    {
        AttitudeMode = AttitudeMode.OrbitAntiNormal;
        
        _vessel.Autopilot.SetActive(true);
    }
    public void SetOrbitRadialIn()
    {
        AttitudeMode = AttitudeMode.OrbitRadialIn;
        
        _vessel.Autopilot.SetActive(true);
    }

    public void SetOrbitRadialOut()
    {
        AttitudeMode = AttitudeMode.OrbitRadialOut;
        
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

    private double GetAngleToRotation()
    {
        var currentAttitude = _vessel.transform.coordinateSystem.ToLocalVector(_vessel.MOI.coordinateSystem.up).normalized;
        var currentRotation = _rotation.localRotation * Vector3d.up;
        return Vector3d.Angle(currentAttitude, currentRotation);
    }

    private void SetRefreshInterval()
    {
        var angleToRotation = GetAngleToRotation();
        if (angleToRotation > AngleToRotation_large)
            RefreshInterval = RefreshInterval_short;
        else if (angleToRotation > AngleToRotation_small)
            RefreshInterval = RefreshInterval_mid;
        else
            RefreshInterval = RefreshInterval_long;
    }


}
