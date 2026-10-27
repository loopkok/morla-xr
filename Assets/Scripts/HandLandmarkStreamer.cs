using UnityEngine;
using Oculus.Interaction.Input;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System;
using System.Diagnostics;

namespace MORLA.XR
{

public class HandLandmarkStreamer : MonoBehaviour
{
    public enum HandSide { Left, Right }

    [Header("Configuration")]
    [Tooltip("Which hand data to track and stream")]
    [SerializeField] private HandSide _handSide;
    
    [Header("Logging")]
    [SerializeField] private bool _logToHUD = true;
    
    [Tooltip("The Log Source name to push text to (e.g., 'Right' to see everything on the right hand panel)")]
    [SerializeField] private string _hudLogSource = "Right"; // Default to Right as requested
    
    [SerializeField] private float _frequency = 0.01f; // 100Hz

    private IHand _hand;
    private float _timer;

    // Public Accessors
    public IHand Hand => _hand; 
    public HandSide Side => _handSide;
    
    // Networking
    private bool _isInitialized = false;

    // Optimization: Cache StringBuilders
    private StringBuilder _sbPacket = new StringBuilder(2048);
    private StringBuilder _sbLog = new StringBuilder(2048);

    // Debug timing metadata (per-hand stream)
    private uint _frameId = 0;
    private static readonly double _ticksToNs = 1_000_000_000.0 / Stopwatch.Frequency;
    
    // Indices for the 21 standard landmarks (Sending)
    private readonly int[] _streamedJoints = {
        1, 2, 3, 4, 5,       // Wrist, Thumb
        7, 8, 9, 10,         // Index
        12, 13, 14, 15,      // Middle
        17, 18, 19, 20,      // Ring
        22, 23, 24, 25       // Pinky
    };

    // Indices for the HUD Display (Wrist + Tips only)
    private readonly int[] _displayJoints = { 
        5,  // Thumb Tip
        10, // Index Tip
        15, // Middle Tip
        20, // Ring Tip
        25  // Pinky Tip
    };

    private void Start()
    {
        _hand = GetComponent<IHand>();
        if (_hand == null)
        {
            LogHUD("Error: No IHand component found!");
            enabled = false;
            return;
        }
        _hand.WhenHandUpdated += OnHandUpdated;
    }

    private void OnDestroy()
    {
        if (_hand != null) _hand.WhenHandUpdated -= OnHandUpdated;
        Disconnect();
    }

    private void OnHandUpdated()
    {
        var app = AppManager.Instance;
        if (app != null && app.TrackingEnabled && _hand != null && !AppManager.IsTouchPoseValid(_handSide))
            app.Status.sources[_handSide == HandSide.Left ? "left" : "right"].Observe("手部", _hand.IsTrackedDataValid && _hand.GetRootPose(out _));
        // Sampling above remains active without a network session.
        if (AppManager.Instance == null || !AppManager.Instance.isStreaming)
        {
            if (_isInitialized) Disconnect();
            return;
        }

        // 2. Check Hand Mode (Logic unchanged: still checks if this hand should be streaming)
        int mode = AppManager.Instance.SelectedHandMode;
        if (mode == 1 && _handSide == HandSide.Right) return;
        if (mode == 2 && _handSide == HandSide.Left) return;

        // Controller pose wins on that side so mixed SHAC does not double-publish hand+controller.
        if (AppManager.IsTouchPoseValid(_handSide))
        {
            return;
        }

        // 3. Init Network
        if (!_isInitialized) InitializeNetwork();

        // 4. Rate Limiting
        _timer += Time.deltaTime;
        if (_timer >= _frequency)
        {
            _timer = 0f;
            ProcessHandData();
        }
    }

    private void ProcessHandData()
    {
        if (!_hand.IsTrackedDataValid) return;

        _sbPacket.Clear();
        _sbLog.Clear();

        bool addDebugHeaderMeta = AppManager.Instance != null && AppManager.Instance.ShowDebugInfo;

        uint frameId = 0;
        ulong sendTimestampNs = 0;

        if (addDebugHeaderMeta)
        {
            _frameId++;
            frameId = _frameId;
            // Single timestamp reused for both headers in this packet for deterministic pairing
            sendTimestampNs = GetMonotonicTimestampNs();
        }

        // --- 1. PROCESS WRIST ---
        if (_hand.GetRootPose(out Pose rootPose))
        {
            // When body tracking (IOBT) is active, express the wrist in the body(hips)
            // frame so hand data is decoupled from body translation/rotation and head motion.
            if (AppManager.Instance != null && AppManager.Instance.TrackIobt)
            {
                rootPose = BodyPoseStreamer.ToBodyRelative(rootPose);
            }

            // Prepare Network Packet
            if (addDebugHeaderMeta)
            {
                AppendHeaderWithMeta(_sbPacket, "wrist", frameId, sendTimestampNs);
                _sbPacket.Append(", ");
            }
            else
            {
                _sbPacket.Append(_handSide).Append(" wrist:, ");
            }

            AppendVector3(_sbPacket, rootPose.position);
            _sbPacket.Append(", ");
            AppendQuaternion(_sbPacket, rootPose.rotation);

            // Prepare HUD Log
            if (_logToHUD && AppManager.Instance.ShowDiagnostics)
            {
                _sbLog.AppendLine($"=== [{_handSide}] Wrist ===");
                _sbLog.AppendLine($"Pos: {rootPose.position.ToString("F3")}");
                // _sbLog.AppendLine($"Rot: {rootPose.rotation.eulerAngles.ToString("F0")}");
            }
        }

        // --- 2. PROCESS LANDMARKS ---
        if (_hand.GetJointPosesFromWrist(out ReadOnlyHandJointPoses joints))
        {
            // Network Packet
            _sbPacket.Append("\n");
            if (addDebugHeaderMeta)
            {
                AppendHeaderWithMeta(_sbPacket, "landmarks", frameId, sendTimestampNs);
            }
            else
            {
                _sbPacket.Append(_handSide).Append(" landmarks:");
            }
            
            foreach (int index in _streamedJoints)
            {
                if (index < joints.Count)
                {
                    _sbPacket.Append(", ");
                    AppendVector3(_sbPacket, joints[index].position);
                }
                else
                {
                    _sbPacket.Append(", 0, 0, 0");
                }
            }

            // HUD Log
            if (_logToHUD && AppManager.Instance.ShowDiagnostics)
            {
                _sbLog.AppendLine($"=== [{_handSide}] Landmarks ===");
                for (int i = 0; i < _displayJoints.Length; i++)
                {
                    int jointIndex = _displayJoints[i];
                    if (jointIndex < joints.Count)
                    {
                        string name = GetRenumberedJointName(i);
                        Vector3 pos = joints[jointIndex].position;
                        _sbLog.AppendLine($"{name}: {pos.ToString("F3")}");
                    }
                }
                
                LogHUD(_sbLog.ToString());
            }
        }

        // --- 3. SEND NETWORK DATA ---
        SendData(_sbPacket.ToString());
    }

    // --- NETWORK HELPERS ---
    private TelemetryChannel _channel;
    private void InitializeNetwork()
    {
        if (_channel != null) return;
        _channel = AppManager.Instance.CreateChannel(_handSide == HandSide.Left ? "left" : "right");
        _isInitialized = true;
    }
    private void SendData(string message)
    {
        if (AppManager.Instance == null || !AppManager.Instance.isStreaming || string.IsNullOrWhiteSpace(message)) return;
        if (!_isInitialized) InitializeNetwork();
        _channel?.Submit(message);
    }
    public void CloseTransport() => Disconnect();
    private void Disconnect()
    {
        _channel?.Dispose(); _channel = null; _isInitialized = false;
    }


    // --- UTILITY HELPERS ---
    private static ulong GetMonotonicTimestampNs()
    {
        return (ulong)(Stopwatch.GetTimestamp() * _ticksToNs);
    }

    private void AppendHeaderWithMeta(StringBuilder sb, string section, uint frameId, ulong sendTimestampNs)
    {
        // Matches normal message capitalization style ("Left wrist", "Right landmarks")
        // Example:
        // Left wrist | f = 123 | t = 123456789012345:
        sb.Append(_handSide)
          .Append(" ")
          .Append(section)
          .Append(" | f = ")
          .Append(frameId)
          .Append(" | t = ")
          .Append(sendTimestampNs)
          .Append(":");
    }

    private void AppendVector3(StringBuilder sb, Vector3 vec)
    {
        // F7 = 7 位小数（0.1µm @1m，约等于 float 原生精度），彻底消除 0.1mm 量化阶梯。
        // 历史坑：F4（0.1mm）让慢速遥操的位姿目标成 0.1mm 台阶 → 下游 EMA 滤不净、
        // 慢速平移"抖抖的"（实测遥操日志 vr 零速占比 36%、cmd 高频抖动 ~10mm/s）。
        // 协议行结构不变（仍是逗号分隔 float），quest3_hand_mocap 解析无需改动。
        sb.Append(vec.x.ToString("F7")).Append(", ")
          .Append(vec.y.ToString("F7")).Append(", ")
          .Append(vec.z.ToString("F7"));
    }

    private void AppendQuaternion(StringBuilder sb, Quaternion q)
    {
        // F6 → 1e-6，消除 F3（0.001）的旋转量化（旋转/慢转同样会阶梯）。
        sb.Append(q.x.ToString("F6")).Append(", ")
          .Append(q.y.ToString("F6")).Append(", ")
          .Append(q.z.ToString("F6")).Append(", ")
          .Append(q.w.ToString("F6"));
    }

    private string GetRenumberedJointName(int index)
    {
        switch (index)
        {
            case 0: return "Thumb";
            case 1: return "Index";
            case 2: return "Mid";
            case 3: return "Ring";
            case 4: return "Pinky";
            default: return "J";
        }
    }

    private void LogHUD(string msg)
    {
        if (_logToHUD && AppManager.Instance != null && AppManager.Instance.ShowDiagnostics && LogManager.Instance != null)
        {
            LogManager.Instance.Log(_hudLogSource, msg);
        }
    }
}
}
