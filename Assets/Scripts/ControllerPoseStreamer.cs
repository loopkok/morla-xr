using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace MORLA.XR
{

/// <summary>
/// Streams Touch controller 6DoF when that side has a valid controller pose.
/// Controller wins over a concurrent hand on the same side (needed for SHAC mixed).
/// Put the controller down to fall back to Hand Tracking on that side.
/// </summary>
public class ControllerPoseStreamer : MonoBehaviour
{
    public static string HudLine { get; private set; } = "Mix L=none R=none";
    /// <summary>Compact controller button/thumbstick state for the HUD.</summary>
    public static string ButtonHudLine { get; private set; } = "L:none R:none";

    // Send-rate cap only — the render frame rate (72 Hz default, 90 Hz via the menu
    // toggle) is the real limiter. 240 Hz never binds at supported refresh rates.
    [SerializeField] private float frequencySeconds = 1f / 240f;
    [SerializeField] private bool logToHUD = true;
    [SerializeField] private string hudLogSource = "Left";

    private HandLandmarkStreamer[] _handStreamers;
    private UdpClient _udpClient;
    private TcpClient _tcpClient;
    private NetworkStream _tcpStream;
    private IPEndPoint _remoteEndPoint;
    private bool _isInitialized;
    private int _currentProtocol = -1;
    private float _timer;
    private string _lastMix = "";

    private readonly StringBuilder _sbPacket = new StringBuilder(256);
    private readonly StringBuilder _sbLog = new StringBuilder(256);
    private readonly StringBuilder _sbHud = new StringBuilder(64);

    private void Start()
    {
        _handStreamers = FindObjectsByType<HandLandmarkStreamer>(FindObjectsSortMode.None);
    }

    private void Update()
    {
        // Button HUD runs in menu and streaming so the operator can see presses.
        BuildButtonHud();

        if (AppManager.Instance == null || !AppManager.Instance.isStreaming)
        {
            if (_isInitialized)
            {
                Disconnect();
            }
            HudLine = "Mix (menu — start stream)";
            // In the menu, still pin the controller pose + button state to the
            // world-space panels so the operator can see presses while clicking.
            PinMenuController("Left", OVRInput.Controller.LTouch, OVRInput.Hand.HandLeft, HandLandmarkStreamer.HandSide.Left);
            PinMenuController("Right", OVRInput.Controller.RTouch, OVRInput.Hand.HandRight, HandLandmarkStreamer.HandSide.Right);
            return;
        }

        if (!_isInitialized)
        {
            InitializeNetwork();
        }

        _timer += Time.deltaTime;
        if (_timer < frequencySeconds)
        {
            return;
        }
        // Subtract (not reset to 0): keeps the send cadence self-correcting so
        // frame-time jitter never accumulates into skipped sends.
        _timer -= frequencySeconds;

        int mode = AppManager.Instance.SelectedHandMode;
        bool sendLeft = mode != 2;
        bool sendRight = mode != 1;

        Vector3 leftPos = Vector3.zero;
        Vector3 rightPos = Vector3.zero;
        Quaternion leftRot = Quaternion.identity;
        Quaternion rightRot = Quaternion.identity;
        bool leftCtrl = sendLeft && TrySendSide("Left", OVRInput.Controller.LTouch, out leftPos, out leftRot);
        bool rightCtrl = sendRight && TrySendSide("Right", OVRInput.Controller.RTouch, out rightPos, out rightRot);
        bool leftHand = sendLeft && !leftCtrl && IsHandTracked(HandLandmarkStreamer.HandSide.Left);
        bool rightHand = sendRight && !rightCtrl && IsHandTracked(HandLandmarkStreamer.HandSide.Right);

        string mix = $"L={(leftCtrl ? "ctrl" : leftHand ? "hand" : "none")} R={(rightCtrl ? "ctrl" : rightHand ? "hand" : "none")}";
        _sbLog.Clear();
        _sbLog.Append("Mix ").AppendLine(mix);
        if (leftCtrl)
        {
            _sbLog.AppendLine("=== [Left] Controller ===");
            _sbLog.Append("Pos: ").AppendLine($"({leftPos.x:F3}, {leftPos.y:F3}, {leftPos.z:F3})");
            _sbLog.Append("Rot: ").AppendLine($"({leftRot.x:F3}, {leftRot.y:F3}, {leftRot.z:F3}, {leftRot.w:F3})");
        }
        if (rightCtrl)
        {
            _sbLog.AppendLine("=== [Right] Controller ===");
            _sbLog.Append("Pos: ").AppendLine($"({rightPos.x:F3}, {rightPos.y:F3}, {rightPos.z:F3})");
            _sbLog.Append("Rot: ").Append($"({rightRot.x:F3}, {rightRot.y:F3}, {rightRot.z:F3}, {rightRot.w:F3})");
        }
        HudLine = _sbLog.ToString();

        PinController("Left", leftCtrl, leftPos, leftRot);
        PinController("Right", rightCtrl, rightPos, rightRot);

        if (mix != _lastMix)
        {
            _lastMix = mix;
            LogHUD("HTS mix " + mix);
        }
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    private bool TrySendSide(string label, OVRInput.Controller controller, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = Vector3.zero;
        worldRot = Quaternion.identity;
        OVRInput.Hand hand = controller == OVRInput.Controller.LTouch
            ? OVRInput.Hand.HandLeft
            : OVRInput.Hand.HandRight;
        HandLandmarkStreamer.HandSide side = controller == OVRInput.Controller.LTouch
            ? HandLandmarkStreamer.HandSide.Left
            : HandLandmarkStreamer.HandSide.Right;

        bool mixed = AppManager.Instance != null && AppManager.Instance.MixedInputEnabled;
        bool controllerActive = IsControllerActive(controller, hand, side, mixed);

        if (!controllerActive)
        {
            return false;
        }

        Vector3 localPos = OVRInput.GetLocalControllerPosition(controller);
        Quaternion localRot = OVRInput.GetLocalControllerRotation(controller);
        Camera cam = Camera.main;
        if (cam != null)
        {
            OVRPose world = new OVRPose { position = localPos, orientation = localRot }.ToWorldSpacePose(cam);
            localPos = world.position;
            localRot = world.orientation;
        }

        // When body tracking (IOBT) is active, express the controller in the body(hips) frame.
        if (AppManager.Instance != null && AppManager.Instance.TrackIobt)
        {
            Pose bodyPose = BodyPoseStreamer.ToBodyRelative(new Pose(localPos, localRot));
            localPos = bodyPose.position;
            localRot = bodyPose.rotation;
        }

        _sbPacket.Clear();
        _sbPacket.Append(label).Append(" controller:, ");
        AppendVector3(_sbPacket, localPos);
        _sbPacket.Append(", ");
        AppendQuaternion(_sbPacket, localRot);
        worldPos = localPos;
        worldRot = localRot;
        SendData(_sbPacket.ToString());

        // Also stream controller button/thumbstick state on a separate line so
        // downstream can drive grippers / actions from the Touch controller.
        // Format: "{label} buttons:, trigger, grip, stickX, stickY, mask"
        // mask bits: 0=primary(X/A) 1=secondary(Y/B) 2=thumbstick press
        //           3=menu 4=trigger click 5=grip click
        SendData(BuildButtonsLine(label, controller));
        return true;
    }

    private static string BuildButtonsLine(string label, OVRInput.Controller controller)
    {
        float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
        float grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller);
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller);
        int mask = 0;
        if (OVRInput.Get(OVRInput.Button.One, controller)) mask |= 1;
        if (OVRInput.Get(OVRInput.Button.Two, controller)) mask |= 2;
        if (OVRInput.Get(OVRInput.Button.PrimaryThumbstick, controller)) mask |= 4;
        if (OVRInput.Get(OVRInput.Button.Start, controller)) mask |= 8;
        if (OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, controller)) mask |= 16;
        if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller)) mask |= 32;
        return $"{label} buttons:, {trigger:F3}, {grip:F3}, {stick.x:F3}, {stick.y:F3}, {mask}";
    }

    private void PinController(string side, bool active, Vector3 pos, Quaternion rot)
    {
        if (LogManager.Instance == null)
        {
            return;
        }
        if (!active)
        {
            LogManager.Instance.Pin(side, "controller", null);
            return;
        }
        OVRInput.Controller ctrl = side == "Left" ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        _sbLog.Clear();
        _sbLog.AppendLine($"=== [{side}] Controller ===");
        _sbLog.Append("Pos: ").AppendLine($"({pos.x:F3}, {pos.y:F3}, {pos.z:F3})");
        _sbLog.Append("Rot: ").AppendLine($"({rot.x:F3}, {rot.y:F3}, {rot.z:F3}, {rot.w:F3})");
        AppendButtonSummary(_sbLog, ctrl);
        LogManager.Instance.Pin(side, "controller", _sbLog.ToString());
    }

    private static void AppendButtonSummary(StringBuilder sb, OVRInput.Controller ctrl)
    {
        float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ctrl);
        float grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, ctrl);
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, ctrl);
        sb.Append("Btn: T").Append(trigger.ToString("F2"))
          .Append(" G").Append(grip.ToString("F2"))
          .Append(" S(").Append(stick.x.ToString("F2")).Append(',').Append(stick.y.ToString("F2")).Append(')');
        int mask = 0;
        if (OVRInput.Get(OVRInput.Button.One, ctrl)) mask |= 1;
        if (OVRInput.Get(OVRInput.Button.Two, ctrl)) mask |= 2;
        if (OVRInput.Get(OVRInput.Button.PrimaryThumbstick, ctrl)) mask |= 4;
        if (OVRInput.Get(OVRInput.Button.Start, ctrl)) mask |= 8;
        if (OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, ctrl)) mask |= 16;
        if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, ctrl)) mask |= 32;
        if (mask != 0)
        {
            sb.Append(" [");
            if ((mask & 1) != 0) sb.Append('A');
            if ((mask & 2) != 0) sb.Append('B');
            if ((mask & 4) != 0) sb.Append('s');
            if ((mask & 8) != 0) sb.Append('M');
            if ((mask & 16) != 0) sb.Append('t');
            if ((mask & 32) != 0) sb.Append('g');
            sb.Append(']');
        }
    }

    private void PinMenuController(string side, OVRInput.Controller ctrl, OVRInput.Hand hand, HandLandmarkStreamer.HandSide handSide)
    {
        if (!IsControllerActive(ctrl, hand, handSide, false))
        {
            PinController(side, false, Vector3.zero, Quaternion.identity);
            return;
        }
        Vector3 pos = OVRInput.GetLocalControllerPosition(ctrl);
        Quaternion rot = OVRInput.GetLocalControllerRotation(ctrl);
        Camera cam = Camera.main;
        if (cam != null)
        {
            OVRPose w = new OVRPose { position = pos, orientation = rot }.ToWorldSpacePose(cam);
            pos = w.position;
            rot = w.orientation;
        }
        PinController(side, true, pos, rot);
    }

    private bool IsControllerActive(
        OVRInput.Controller controller, OVRInput.Hand hand, HandLandmarkStreamer.HandSide side, bool mixed)
    {
        bool streaming = AppManager.Instance != null && AppManager.Instance.isStreaming;
        bool shacActive = streaming && mixed;
        if (shacActive)
        {
            // Mixed (SHAC): stream the controller only while it's actually held in hand.
            return OVRInput.GetControllerIsInHandState(hand) == OVRInput.ControllerInHandState.ControllerInHand
                && OVRInput.GetControllerPositionValid(controller);
        }
        if (!streaming)
        {
            // Menu: SHAC is off and hand tracking stays ON while holding the controller,
            // so the "hand off" proxy below never fires. Just use controller pose validity
            // so the menu ray + button HUD work while the controller is on.
            return OVRInput.GetControllerPositionValid(controller);
        }
        // IOBT streaming: pressing a button wakes the controller and the runtime switches
        // hand tracking OFF, so "hand no longer tracked + pose valid" means it's active.
        return !IsHandTracked(side) && OVRInput.GetControllerPositionValid(controller);
    }

    private void BuildButtonHud()
    {
        bool mixed = AppManager.Instance != null && AppManager.Instance.MixedInputEnabled;
        _sbHud.Clear();
        AppendSideButtonHud(_sbHud, "L", OVRInput.Controller.LTouch,
            OVRInput.Hand.HandLeft, HandLandmarkStreamer.HandSide.Left, mixed);
        _sbHud.Append(' ');
        AppendSideButtonHud(_sbHud, "R", OVRInput.Controller.RTouch,
            OVRInput.Hand.HandRight, HandLandmarkStreamer.HandSide.Right, mixed);
        ButtonHudLine = _sbHud.ToString();
    }

    private void AppendSideButtonHud(
        StringBuilder sb, string tag, OVRInput.Controller controller,
        OVRInput.Hand hand, HandLandmarkStreamer.HandSide side, bool mixed)
    {
        sb.Append(tag).Append(':');
        if (!IsControllerActive(controller, hand, side, mixed))
        {
            sb.Append("none");
            return;
        }
        float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
        float grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller);
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller);
        sb.Append("T").Append(trigger.ToString("F2"));
        sb.Append(" G").Append(grip.ToString("F2"));
        sb.Append(" S(").Append(stick.x.ToString("F2")).Append(',').Append(stick.y.ToString("F2")).Append(')');
        int mask = 0;
        if (OVRInput.Get(OVRInput.Button.One, controller)) mask |= 1;
        if (OVRInput.Get(OVRInput.Button.Two, controller)) mask |= 2;
        if (OVRInput.Get(OVRInput.Button.PrimaryThumbstick, controller)) mask |= 4;
        if (OVRInput.Get(OVRInput.Button.Start, controller)) mask |= 8;
        if (OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, controller)) mask |= 16;
        if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller)) mask |= 32;
        if (mask != 0)
        {
            sb.Append(" [");
            if ((mask & 1) != 0) sb.Append('A');      // primary (X/A)
            if ((mask & 2) != 0) sb.Append('B');        // secondary (Y/B)
            if ((mask & 4) != 0) sb.Append('s');        // stick press
            if ((mask & 8) != 0) sb.Append('M');        // menu
            if ((mask & 16) != 0) sb.Append('t');       // trigger click
            if ((mask & 32) != 0) sb.Append('g');       // grip click
            sb.Append(']');
        }
    }

    private bool IsHandTracked(HandLandmarkStreamer.HandSide side)
    {
        if (_handStreamers == null)
        {
            return false;
        }
        for (int i = 0; i < _handStreamers.Length; i++)
        {
            HandLandmarkStreamer hs = _handStreamers[i];
            if (hs == null || hs.Side != side || hs.Hand == null)
            {
                continue;
            }
            if (hs.Hand.IsTrackedDataValid)
            {
                return true;
            }
        }
        return false;
    }

    private void InitializeNetwork()
    {
        if (AppManager.Instance == null)
        {
            return;
        }

        string ip = AppManager.Instance.ServerIP;
        int port = AppManager.Instance.ServerPort;
        _currentProtocol = AppManager.Instance.SelectedProtocol;

        try
        {
            if (_currentProtocol == 0)
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SendBufferSize = 0;
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), port);
                _udpClient.BeginReceive(new AsyncCallback(OnUdpReceive), null);
                LogHUD($"Ctrl UDP Ready: {ip}:{port}");
            }
            else
            {
                _tcpClient = new TcpClient(AddressFamily.InterNetwork);
                _tcpClient.NoDelay = true;
                _tcpClient.SendTimeout = 1000;
                _tcpClient.Connect(ip, port);
                _tcpStream = _tcpClient.GetStream();
                QuestPacketEnvelope.StartTcpSyncListener(_tcpStream);
                string type = _currentProtocol == 1 ? "Wired" : "WiFi";
                LogHUD($"Ctrl TCP({type}) Connected: {ip}:{port}");
            }
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            LogHUD($"Ctrl Conn Error: {ex.Message}");
            if (AppManager.Instance != null)
            {
                AppManager.Instance.StopStreaming();
            }
        }
    }

    private void OnUdpReceive(IAsyncResult res)
    {
        QuestPacketEnvelope.ContinueUdpSyncReceive(
            _udpClient, res, new AsyncCallback(OnUdpReceive));
    }

    private void SendData(string message)
    {
        if (AppManager.Instance != null && !AppManager.Instance.isStreaming)
        {
            return;
        }
        message = QuestPacketEnvelope.Wrap(message);

        try
        {
            if (_currentProtocol == 0 && _udpClient != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                _udpClient.Send(data, data.Length, _remoteEndPoint);
            }
            else if ((_currentProtocol == 1 || _currentProtocol == 2) && _tcpStream != null && _tcpStream.CanWrite)
            {
                byte[] data = Encoding.UTF8.GetBytes(message + "\n");
                QuestPacketEnvelope.WriteTcp(_tcpStream, data);
            }
        }
        catch (Exception)
        {
            Disconnect();
            if (AppManager.Instance != null)
            {
                AppManager.Instance.HandleDisconnection("Host Closed Connection (controller)");
            }
        }
    }

    private void Disconnect()
    {
        try
        {
            if (_udpClient != null) { _udpClient.Close(); _udpClient = null; }
            if (_tcpStream != null) { _tcpStream.Close(); _tcpStream = null; }
            if (_tcpClient != null) { _tcpClient.Close(); _tcpClient = null; }
        }
        catch { }
        _isInitialized = false;
    }

    private static void AppendVector3(StringBuilder sb, Vector3 vec)
    {
        sb.Append(vec.x.ToString("F4")).Append(", ")
          .Append(vec.y.ToString("F4")).Append(", ")
          .Append(vec.z.ToString("F4"));
    }

    private static void AppendQuaternion(StringBuilder sb, Quaternion q)
    {
        sb.Append(q.x.ToString("F3")).Append(", ")
          .Append(q.y.ToString("F3")).Append(", ")
          .Append(q.z.ToString("F3")).Append(", ")
          .Append(q.w.ToString("F3"));
    }

    private void LogHUD(string msg)
    {
        if (logToHUD && LogManager.Instance != null)
        {
            LogManager.Instance.Log(hudLogSource, msg);
        }
    }
}
}
