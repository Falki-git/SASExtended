using BepInEx.Logging;
using KSP.Api;
using KSP.Game;
using KSP.Game.Missions.Definitions;
using KSP.Iteration.UI.Binding;
using KSP.Sim;
using KSP.Sim.impl;
using SASExtended;
using SASExtended.Managers;
using SpaceWarp.API.Game.Extensions;
using SpaceWarp.API.Game.Waypoints;
using SpaceWarp.API.UI;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using static KSP.Api.UIDataPropertyStrings.View;
using static UnityEngine.GraphicsBuffer;


namespace SASExtended
{
    public class DebugUI
    {
        public bool IsDebugWindowOpen;

        private Rect _debugWindowRect = new Rect(1600/*1800*/, 400 /*54*/, 350, 350);
        private GUIStyle _labelStyleLong;
        private GUIStyle _labelStyle;
        private GUIStyle _labelStyleMid;
        private GUIStyle _labelMissionTableStyle;
        private GUIStyle _labelStyleShort;
        private GUIStyle _narrowLabel;
        private GUIStyle _normalButton;
        private GUIStyle _normalSectionButton;
        private GUIStyle _toggledSectionButton;
        private GUIStyle _narrowButton;
        private GUIStyle _midNarrowButton;
        private GUIStyle _normalTextfield;
        private GUIStyle _textfieldStyleShort;
        private readonly ManualLogSource _logger = BepInEx.Logging.Logger.CreateLogSource("SASExtended.DEBUG_UI"); 
        //public string UT;

        public static double UT => GameManager.Instance.Game?.UniverseModel?.UniverseTime ?? 0;
        double _lastRefreshTime = 0;
        public double RefreshInterval = 0.1; // seconds

        private bool _isAutopilotActive = false;
        private Rotation _rotation;
        private string _x = "0";
        private string _y = "0";
        private string _z = "0";
        private bool _headingEnabled = true;
        private bool _pitchEnabled = true;
        private bool _rollEnabled = true;

        private AttitudeReference _attitudeReference = AttitudeReference.Horizon;

        private ITransformFrame _frame;

        private static DebugUI _instance;
        internal static DebugUI Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new DebugUI();

                return _instance;
            }
        }

        public void InitializeStyles()
        {
            _logger.LogInfo("InitializeStyles triggered");

            _labelStyleLong = new GUIStyle(Skins.ConsoleSkin.label) { fixedWidth = 600 };
            _labelStyle = new GUIStyle(Skins.ConsoleSkin.label) { fixedWidth = 200 };
            _labelStyleMid = new GUIStyle(Skins.ConsoleSkin.label) { fixedWidth = 80 };
            _labelStyleShort = new GUIStyle(Skins.ConsoleSkin.label) { fixedWidth = 10 };
            _labelMissionTableStyle = new GUIStyle(Skins.ConsoleSkin.label) { fixedWidth = 100 };
            _narrowLabel = new GUIStyle(Skins.ConsoleSkin.label) { fixedWidth = 20 };
            _normalButton = new GUIStyle(Skins.ConsoleSkin.button);
            _normalSectionButton = new GUIStyle(Skins.ConsoleSkin.button);
            _normalSectionButton.normal.textColor = new Color(120f / 255f, 150f / 255f, 255f / 255f, 1f);
            _toggledSectionButton = new GUIStyle(Skins.ConsoleSkin.button);
            _toggledSectionButton.normal.textColor = Color.gray;
            _narrowButton = new GUIStyle(Skins.ConsoleSkin.button) { fixedWidth = 20 };
            _midNarrowButton = new GUIStyle(Skins.ConsoleSkin.button) { fixedWidth = 40 };
            _normalTextfield = new GUIStyle(Skins.ConsoleSkin.textField) { fixedWidth = 150 };
            _textfieldStyleShort = new GUIStyle(Skins.ConsoleSkin.textField) { fixedWidth = 40 };
        }

        public void InitializeControls()
        {

        }

        public void OnGUI()
        {
            if (_labelStyle == null)
                return;

            GUI.skin = Skins.ConsoleSkin;

            if (IsDebugWindowOpen)
            {
                _debugWindowRect = GUILayout.Window(
                    GUIUtility.GetControlID(FocusType.Passive),
                    _debugWindowRect,
                    FillDebugUI,
                    "// SAS Extended",
                    GUILayout.Width(0),
                    GUILayout.Height(0)
                    );
            }

            

            if (_isAutopilotActive && (UT - _lastRefreshTime > RefreshInterval))
            {
                var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

                SetRotation2();

                // ORIGINAL - WORKS FOR ORBIT
                //SetRotation();
                //var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
                //vessel.Autopilot.SAS.LockRotation(_rotation);
                //_lastRefreshTime = UT;


                // MECHJEB TRY
                //QuaternionD attitude = QuaternionD.AngleAxis((float)x, Vector3.up) * QuaternionD.AngleAxis(-(float)y, Vector3.right) * QuaternionD.AngleAxis(-(float)z, Vector3.forward);
                //var rot = rotRef * attitude * Euler(90, 0, 0);



                //NEW TAKE #1
                //var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
                //var rotRef = QuaternionD.LookRotation(Vector3d.Normalize(vessel._telemetryComponent.surfaceLocalVelocity), vessel._telemetryComponent.surfaceUpRef);

                //double x = 0;
                //double y = 0;
                //double z = 0;

                //double.TryParse(_x, out x);
                //double.TryParse(_y, out y);
                //double.TryParse(_z, out z);
                //var rot = rotRef * QuaternionD.Euler(x, y, z);// * QuaternionD.Euler(90, 0, 0);


                //NEW TAKE #2

                //double x = 0;
                //double y = 0;
                //double z = 0;

                //double.TryParse(_x, out x);
                //double.TryParse(_y, out y);
                //double.TryParse(_z, out z);

                //var velocity = vessel._telemetryComponent.SOIFrameCelestial.motionFrame.ToRelativeVelocity(vessel._telemetryComponent.RootVelocity, vessel._telemetryComponent.RootPosition);
                //var orbitalMovementSpeed = Vector.getMagnitude(vessel._telemetryComponent.OrbitalMovementVelocity);
                //Vector vector = Vector.normalize(vessel._telemetryComponent.OrbitalMovementVelocity);
                //Vector surfaceVector = Vector.normalize(vessel._telemetryComponent.SurfaceMovementPrograde);
                //Vector upwards = Vector.normalize(Position.Delta(vessel._telemetryComponent.RootPosition, vessel._telemetryComponent.SOIPosition));
                ////Vector upwards = Vector.cross(vector, v);
                ////var upwards = v;
                //Rotation rotation = Rotation.LookRotation(surfaceVector, upwards);
                //rotation.localRotation = rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);

                ////rotation.coordinateSystem = vessel.SimulationObject.Telemetry.OrbitMovementFrame;

                //vessel.Autopilot.SAS.LockRotation(rotation);
                //_lastRefreshTime = UT;




                vessel.Autopilot.SAS.LockRotation(_rotation);
                _lastRefreshTime = UT;
            }
        }

        private void FillDebugUI(int _)
        {
            var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"---", GUILayout.Width(200));

            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"X: {SASManager.Instance.X:F2}, Y: {SASManager.Instance.Y:F2}, Z: {SASManager.Instance.Z:F2}", GUILayout.Width(250));
                GUILayout.Label($"Xe: {SASManager.Instance.XEnabled}, Ye: {SASManager.Instance.YEnabled}, Ze: {SASManager.Instance.ZEnabled}", GUILayout.Width(250));
                GUILayout.Label($"Mode: {SASManager.Instance.AttitudeMode}", GUILayout.Width(200));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"---", GUILayout.Width(200));

            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                //GUILayout.Label($"Refresh interval (s): {_refreshInterval:F2}");
                GUILayout.Label($"Refresh interval (s): ", GUILayout.Width(200));
                string intervalText = GUILayout.TextField(RefreshInterval.ToString("F2"), GUILayout.Width(50));

                if (float.TryParse(intervalText, out float parsedValue))
                {
                    RefreshInterval = Mathf.Clamp(parsedValue, 0, 1);
                }

                //_refreshInterval = GUILayout.HorizontalSlider((float)_refreshInterval, 0, 1, GUILayout.Width(100));
                RefreshInterval = GUILayout.HorizontalSlider((float)RefreshInterval, 0, 1, GUILayout.ExpandWidth(true));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"Vessel name: {vessel?.Name}", _labelStyleLong);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"Autopilot mode: {vessel?.Autopilot.AutopilotMode}", _labelStyleLong);

                //var isAutopilotActive = vessel.Autopilot.AutopilotMode == AutopilotMode.Autopilot;
                //var isAutopilotActive = vessel.Autopilot.Enabled;
                if (GUILayout.Button(_isAutopilotActive ? "Deactivate" : "Activate", _normalButton))
                {
                    if (!_isAutopilotActive)
                    {
                        //vessel.Autopilot.Activate(AutopilotMode.Autopilot);
                        vessel.Autopilot.Enabled = true;
                        vessel.Autopilot.SetMode(AutopilotMode.StabilityAssist);
                        _isAutopilotActive = true;
                        _lastRefreshTime = UT;
                    }
                    else
                    {
                        //vessel.Autopilot.Deactivate();
                        vessel.Autopilot.Enabled = false;
                        //vessel.Autopilot.SetMode(AutopilotMode.Navigation);
                        _isAutopilotActive = false;
                    }
                }

            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label($"Attitude Reference: {_attitudeReference.ToString()}", _labelStyleLong);                
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("OrbitPrograde", _normalButton))
                {
                    //_frame = vessel.SimulationObject.Telemetry.OrbitMovementFrame;
                    _attitudeReference = AttitudeReference.OrbitPrograde;
                }
                if (GUILayout.Button("OrbitRetrograde", _normalButton))
                {
                    _attitudeReference = AttitudeReference.OrbitRetrograde;
                }
                if (GUILayout.Button("Horizon", _normalButton))
                {
                    _frame = vessel.SimulationObject.Telemetry.HorizonFrame;
                    _attitudeReference = AttitudeReference.Horizon;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            {
                //if (GUILayout.Button("RootFrameBody", _normalButton))
                //{
                //    _frame = vessel.SimulationObject.Telemetry.RootFrameBody;
                //}
                //if (GUILayout.Button("RootFrameCelestial", _normalButton))
                //{
                //    _frame = vessel.SimulationObject.Telemetry.RootFrameCelestial;
                //}
                //if (GUILayout.Button("SOIFrameBody", _normalButton))
                //{
                //    _frame = vessel.SimulationObject.Telemetry.SOIFrameBody;
                //}
                //if (GUILayout.Button("SOIFrameCelestial", _normalButton))
                //{
                //    //_frame = vessel.SimulationObject.Telemetry.SOIFrameCelestial;
                //}
                
                if (GUILayout.Button("SurfacePrograde", _normalButton))
                {
                    //_frame = vessel.SurfaceVelocity.coordinateSystem.
                    //var v = new RotationWrapper(vessel.SimulationObject.Telemetry.mainBody.Rotation);
                    //var rotationWrapper = new RotationWrapper(new Rotation(vessel.SimulationObject.Telemetry.HorizonFrame, QuaternionD.Euler(-y, x, z)));

                    //var rotationWrapper = new RotationWrapper(new Rotation(_frame, QuaternionD.Euler(-y, x, z)));
                    //_rotation = new Rotation(vessel.mainBody.transform.celestialFrame, vessel.mainBody.transform.celestialFrame.ToLocalRotation(rotationWrapper.Rotation) * QuaternionD.Inverse(QuaternionD.Euler(270, 0, 0)));


                    //var rotRef = QuaternionD.LookRotation(Vector3d.Normalize(vessel._telemetryComponent.surfaceLocalVelocity), vessel._telemetryComponent.surfaceUpRef);

                    //var rot = new Rotation();
                    //rot.localRotation = QuaternionD.Euler(-y, x, z)));

                    //var rot = rotRef * QuaternionD.Euler(0, 0, -90);


                    _attitudeReference = AttitudeReference.SurfacePrograde;
                }
                if (GUILayout.Button("SurfaceRetrograde", _normalButton))
                {
                    _attitudeReference = AttitudeReference.SurfaceRetrograde;
                }
                if (GUILayout.Button("Target", _normalButton))
                {
                    //_frame = vessel.SimulationObject.Telemetry.TargetFrame;
                    _attitudeReference = AttitudeReference.Target;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Star", _normalButton))
                {
                    _attitudeReference = AttitudeReference.SunPrograde;
                }
                if (GUILayout.Button("AntiStar", _normalButton))
                {
                    _attitudeReference = AttitudeReference.SunRetrograde;
                }
                if (GUILayout.Button("Up", _normalButton))
                {
                    _attitudeReference = AttitudeReference.Up;
                }
            }
            GUILayout.EndHorizontal();


            GUILayout.BeginHorizontal();
            {
                GUILayout.Toggle(_headingEnabled, "");
                GUILayout.Label($"Heading: ", _labelStyleMid);
                _x = GUILayout.TextField(_x, _textfieldStyleShort);
                if (GUILayout.Button("-", _narrowButton))
                {
                    double.TryParse(_x, out var value);
                    --value;
                    _x = value.ToString();
                }
                if (GUILayout.Button("+", _narrowButton))
                {
                    double.TryParse(_x, out var value);
                    ++value;
                    _x = value.ToString();
                }
                if (GUILayout.Button("0", _narrowButton))
                {
                    _x = "0";
                }
                if (GUILayout.Button("90", _midNarrowButton))
                {
                    _x = "90";
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Toggle(_pitchEnabled, "");
                GUILayout.Label($"Pitch: ", _labelStyleMid);
                _y = GUILayout.TextField(_y, _textfieldStyleShort);
                if (GUILayout.Button("-", _narrowButton))
                {
                    double.TryParse(_y, out var value);
                    --value;
                    _y = value.ToString();
                }
                if (GUILayout.Button("+", _narrowButton))
                {
                    double.TryParse(_y, out var value);
                    ++value;
                    _y = value.ToString();
                }
                if (GUILayout.Button("0", _narrowButton))
                {
                    _y = "0";
                }
                if (GUILayout.Button("90", _midNarrowButton))
                {
                    _y = "90";
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Toggle(_rollEnabled, "");
                GUILayout.Label($"Roll: ", _labelStyleMid);
                _z = GUILayout.TextField(_z, _textfieldStyleShort);
                if (GUILayout.Button("-", _narrowButton))
                {
                    double.TryParse(_z, out var value);
                    --value;
                    _z = value.ToString();
                }
                if (GUILayout.Button("+", _narrowButton))
                {
                    double.TryParse(_z, out var value);
                    ++value;
                    _z = value.ToString();
                }
                if (GUILayout.Button("0", _narrowButton))
                {
                    _z = "0";
                }
                if (GUILayout.Button("180", _midNarrowButton))
                {
                    _z = "180";
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("Set"))
                {
                    double x = 0;
                    double y = 0;
                    double z = 0;

                    if (!double.TryParse(_x, out x) || !double.TryParse(_y, out y) || !double.TryParse(_z, out z))
                        return;

                    //var vector = new Vector(vessel.transform.coordinateSystem, new Vector3d(x, y, z));

                    /*
                    var coord = new LocalCoordinates(vessel);
                    var attitudeControlOverride = new AttitudeControlOverride();
                    attitudeControlOverride.HorizontalAngle = (int)x;
                    attitudeControlOverride.VerticalAngle = (int)y;

                    var vector = new Vector(vessel.transform.coordinateSystem, coord.ControlVector(attitudeControlOverride));
                    vessel.Autopilot.SAS.SetTargetOrientation(vector, true);
                    */

                    var telemetry = vessel._telemetryComponent;
                    var up = telemetry.HorizonUp;

                    //
                    var direction = QuaternionD.Euler(-y, x, z) * Vector3d.up;
                    //var rotation = QuaternionD.Euler(-y, x, 0);
                    //rotation = QuaternionD.AngleAxis(z, Vector3d.forward) * rotation;
                    //var direction = rotation * Vector3d.forward;

                    //KSP.Sim.impl.VesselVehicle
                    //var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel().vessel
                    

                    var directionVector = new Vector(up.coordinateSystem, direction);
                    vessel.Autopilot.SAS.lockedMode = false;
                    vessel.Autopilot.SAS.SetTargetOrientation(directionVector, false);
                }

                if (GUILayout.Button("Set2"))
                {
                    SetRotation();
                }
            }
            GUILayout.EndHorizontal();


        //vessel.transform.coordinateSystem

        //vessel.Autopilot.SAS.SetTargetOrientation(new KSP.Sim.Vector( vessel.transform.coordinateSystem)




        GUI.DragWindow(new Rect(0, 0, 10000, 10000));
        }

        private void SetRotation()
        {
            //TODO, check if axis are enabled. If they are not, we need to read the current value and use that for the rotation

            var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

            double x = 0;
            double y = 0;
            double z = 0;

            if (!double.TryParse(_x, out x) || !double.TryParse(_y, out y) || !double.TryParse(_z, out z))
                return;

            if (_frame == null) _frame = vessel.SimulationObject.Telemetry.HorizonFrame;

            var b = new Rotation();

            //var v = new RotationWrapper(vessel.SimulationObject.Telemetry.mainBody.Rotation);


            //var rotationWrapper = new RotationWrapper(new Rotation(vessel.SimulationObject.Telemetry.HorizonFrame, QuaternionD.Euler(-y, x, z)));
            var rotationWrapper = new RotationWrapper(new Rotation(_frame, QuaternionD.Euler(-y, x, z)));

            _rotation = new Rotation(vessel.mainBody.transform.celestialFrame, vessel.mainBody.transform.celestialFrame.ToLocalRotation(rotationWrapper.Rotation) * QuaternionD.Inverse(QuaternionD.Euler(270, 0, 0)));

        }

        private void SetRotation2()
        {
            var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

            double x, y, z;
            if (!double.TryParse(_x, out x) || !double.TryParse(_y, out y) || !double.TryParse(_z, out z))
                return;

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

            switch (_attitudeReference)
            {
                case AttitudeReference.OrbitPrograde:
                    _rotation = Rotation.LookRotation(orbitPrograde, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.OrbitRetrograde:
                    _rotation = Rotation.LookRotation(orbitRetrograde, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.Horizon:
                    _rotation = Rotation.LookRotation(horizon, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.SurfacePrograde:
                    _rotation = Rotation.LookRotation(surfacePrograde, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);                    
                    break;
                case AttitudeReference.SurfaceRetrograde:
                    _rotation = Rotation.LookRotation(surfaceRetrograde, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.Target:
                    _rotation = Rotation.LookRotation(target, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.SunPrograde:
                    _rotation = Rotation.LookRotation(sun, upwards); // doesn't work
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.SunRetrograde:
                    _rotation = Rotation.LookRotation(antiSun, upwards); // doesn't work
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
                case AttitudeReference.Up:
                    _rotation = Rotation.LookRotation(horizon, upwards); // maybe we can simplify this
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-90, 0, 0) * QuaternionD.Euler(90, 0, 0);
                    break;

                default: // horizon
                    _rotation = Rotation.LookRotation(horizon, upwards);
                    _rotation.localRotation = _rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);
                    break;
            }

            //Rotation rotation = Rotation.LookRotation(surfacePrograde, upwards);
            //rotation.localRotation = rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);

            //vessel.Autopilot.SAS.LockRotation(rotation);
        }

        public static QuaternionD Euler(double x, double y, double z)
        {
            x = x * Mathf.Deg2Rad;
            y = y * Mathf.Deg2Rad;
            z = z * Mathf.Deg2Rad;

            double cx = Math.Cos(x * 0.5);
            double sx = Math.Sin(x * 0.5);
            double cy = Math.Cos(y * 0.5);
            double sy = Math.Sin(y * 0.5);
            double cz = Math.Cos(z * 0.5);
            double sz = Math.Sin(z * 0.5);

            var q = new QuaternionD
            {
                w = cz * cx * cy + sz * sx * sy,
                x = cz * sx * cy - sz * cx * sy,
                y = cz * cx * sy + sz * sx * cy,
                z = sz * cx * cy - cz * sx * sy
            };

            return q;
        }

        private void WorksForSurfacePrograde()
        {
            if (_isAutopilotActive && (UT - _lastRefreshTime > RefreshInterval))
            {
                var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();

                // ORIGINAL - WORKS FOR ORBIT
                //SetRotation();
                //var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
                //vessel.Autopilot.SAS.LockRotation(_rotation);
                //_lastRefreshTime = UT;


                // MECHJEB TRY
                //QuaternionD attitude = QuaternionD.AngleAxis((float)x, Vector3.up) * QuaternionD.AngleAxis(-(float)y, Vector3.right) * QuaternionD.AngleAxis(-(float)z, Vector3.forward);
                //var rot = rotRef * attitude * Euler(90, 0, 0);



                //NEW TAKE #1
                //var vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
                var rotRef = QuaternionD.LookRotation(Vector3d.Normalize(vessel._telemetryComponent.surfaceLocalVelocity), vessel._telemetryComponent.surfaceUpRef);

                //double x = 0;
                //double y = 0;
                //double z = 0;

                //double.TryParse(_x, out x);
                //double.TryParse(_y, out y);
                //double.TryParse(_z, out z);
                //var rot = rotRef * QuaternionD.Euler(x, y, z);// * QuaternionD.Euler(90, 0, 0);


                //NEW TAKE #2

                double x = 0;
                double y = 0;
                double z = 0;

                double.TryParse(_x, out x);
                double.TryParse(_y, out y);
                double.TryParse(_z, out z);

                //var velocity = vessel._telemetryComponent.SOIFrameCelestial.motionFrame.ToRelativeVelocity(vessel._telemetryComponent.RootVelocity, vessel._telemetryComponent.RootPosition);
                //var orbitalMovementSpeed = Vector.getMagnitude(vessel._telemetryComponent.OrbitalMovementVelocity);
                //Vector vector = Vector.normalize(vessel._telemetryComponent.OrbitalMovementVelocity);
                Vector surfaceVector = Vector.normalize(vessel._telemetryComponent.SurfaceMovementPrograde);
                Vector upwards = Vector.normalize(Position.Delta(vessel._telemetryComponent.RootPosition, vessel._telemetryComponent.SOIPosition));
                //Vector upwards = Vector.cross(vector, v);
                //var upwards = v;
                Rotation rotation = Rotation.LookRotation(surfaceVector, upwards);
                rotation.localRotation = rotation.localRotation * QuaternionD.Euler(-y, x, z) * QuaternionD.Euler(90, 0, 0);

                //rotation.coordinateSystem = vessel.SimulationObject.Telemetry.OrbitMovementFrame;

                vessel.Autopilot.SAS.LockRotation(rotation);
                _lastRefreshTime = UT;

            }
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
            } catch (Exception ex)
            {
                _logger.LogError($"Unable to fetch parent star for vessel {vessel.Name}. How is this possible?! Exception: {ex.Message}");
            }

            return body;
        }
    }    
}

public enum AttitudeReference
{
    Horizon,
    OrbitPrograde,
    OrbitRetrograde,
    SurfacePrograde,
    SurfaceRetrograde,
    Target,
    Up,
    SunPrograde,
    SunRetrograde
}

// KILL ROT

//case Target.KILLROT:
//    Core.Attitude.attitudeKILLROT = true;
//    attitude = Quaternion.LookRotation(Part.vessel.GetTransform().up, -Part.vessel.GetTransform().forward);
//    reference = AttitudeReference.INERTIAL;
//    break;


// SURFACE_PROGRADE & RETROGRADE

//case Target.SURFACE_PROGRADE:
//    attitude = Quaternion.AngleAxis(-(float)srfVelRol, Vector3.forward) *
//               Quaternion.AngleAxis(-(float)srfVelPit, Vector3.right) *
//               Quaternion.AngleAxis((float)srfVelYaw, Vector3.up);
//    reference = AttitudeReference.SURFACE_VELOCITY;
//    break;
//case Target.SURFACE_RETROGRADE:
//    attitude = Quaternion.AngleAxis((float)srfVelRol + 180, Vector3.forward) *
//               Quaternion.AngleAxis(-(float)srfVelPit + 180, Vector3.right) *
//               Quaternion.AngleAxis((float)srfVelYaw, Vector3.up);
//    reference = AttitudeReference.SURFACE_VELOCITY;
//    break;


// MECHJEB Surface

//RequestedAttitude = attitudeGetReferenceRotation(attitudeReference) * attitudeTarget;

//    QuaternionD attitude = QuaternionD.AngleAxis((float)heading, Vector3.up) * QuaternionD.AngleAxis(-(float)pitch, Vector3.right) *
//QuaternionD.AngleAxis(-(float)roll, Vector3.forward);
//    AttitudeReference reference = fixCOT ? AttitudeReference.SURFACE_NORTH_COT : AttitudeReference.SURFACE_NORTH;
//    attitudeTo(attitude, reference, controller, AxisCtrlPitch,
//        AxisCtrlYaw, AxisCtrlRoll);

