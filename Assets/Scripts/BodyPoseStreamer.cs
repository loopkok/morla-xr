using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace MORLA.XR
{

/// <summary>
/// Streams Meta Movement SDK IOBT (Body Tracking Fidelity = High, Full Body).
/// Wrist / fingers stay on HandLandmarkStreamer (Hand Tracking).
/// Cannot run at the same time as Mixed Input (SHAC): Meta Body API disables itself.
/// Passthrough is on for see-through; IOBT may drop to Low — HUD reports actual fidelity.
/// </summary>
public class BodyPoseStreamer : MonoBehaviour
{
    public static string HudLine { get; private set; } = "IOBT: idle";

    // Send-rate cap only — the render frame rate is the real limiter (see 90 Hz toggle).
    [SerializeField] private float frequencySeconds = 1f / 240f;
    [SerializeField] private bool logToHUD = true;
    [SerializeField] private string hudLogSource = "Left";
    [SerializeField] private bool drawDebugSkeleton = true;

    private static readonly (OVRPlugin.BoneId bone, string name)[] StreamedJoints =
    {
        (OVRPlugin.BoneId.FullBody_Hips, "hips"),
        (OVRPlugin.BoneId.FullBody_SpineLower, "spine-lower"),
        (OVRPlugin.BoneId.FullBody_SpineMiddle, "spine-middle"),
        (OVRPlugin.BoneId.FullBody_SpineUpper, "spine-upper"),
        (OVRPlugin.BoneId.FullBody_Chest, "chest"),
        (OVRPlugin.BoneId.FullBody_Neck, "neck"),
        (OVRPlugin.BoneId.FullBody_Head, "head"),
        (OVRPlugin.BoneId.FullBody_LeftShoulder, "left-shoulder"),
        (OVRPlugin.BoneId.FullBody_LeftScapula, "left-scapula"),
        (OVRPlugin.BoneId.FullBody_LeftArmUpper, "left-arm-upper"),
        (OVRPlugin.BoneId.FullBody_LeftArmLower, "left-arm-lower"),
        (OVRPlugin.BoneId.FullBody_RightShoulder, "right-shoulder"),
        (OVRPlugin.BoneId.FullBody_RightScapula, "right-scapula"),
        (OVRPlugin.BoneId.FullBody_RightArmUpper, "right-arm-upper"),
        (OVRPlugin.BoneId.FullBody_RightArmLower, "right-arm-lower"),
        (OVRPlugin.BoneId.FullBody_LeftUpperLeg, "left-upper-leg"),
        (OVRPlugin.BoneId.FullBody_LeftLowerLeg, "left-lower-leg"),
        (OVRPlugin.BoneId.FullBody_LeftFootAnkleTwist, "left-foot-ankle-twist"),
        (OVRPlugin.BoneId.FullBody_LeftFootAnkle, "left-foot-ankle"),
        (OVRPlugin.BoneId.FullBody_LeftFootSubtalar, "left-foot-subtalar"),
        (OVRPlugin.BoneId.FullBody_LeftFootTransverse, "left-foot-transverse"),
        (OVRPlugin.BoneId.FullBody_LeftFootBall, "left-foot-ball"),
        (OVRPlugin.BoneId.FullBody_RightUpperLeg, "right-upper-leg"),
        (OVRPlugin.BoneId.FullBody_RightLowerLeg, "right-lower-leg"),
        (OVRPlugin.BoneId.FullBody_RightFootAnkleTwist, "right-foot-ankle-twist"),
        (OVRPlugin.BoneId.FullBody_RightFootAnkle, "right-foot-ankle"),
        (OVRPlugin.BoneId.FullBody_RightFootSubtalar, "right-foot-subtalar"),
        (OVRPlugin.BoneId.FullBody_RightFootTransverse, "right-foot-transverse"),
        (OVRPlugin.BoneId.FullBody_RightFootBall, "right-foot-ball"),
    };

    private OVRBody _ovrBody;
    private bool _permissionRequested;
    private bool _lastEnabledDiag;
    private bool _lastHasStateDiag;

    /// <summary>Latest hips pose in world space, used as the body reference frame
    /// for body-relative hand/head/controller output. This is the re-axed torso
    /// frame (+X right, +Y up, +Z forward), not the raw FullBody_Hips bone frame
    /// (whose local axes are +X down, +Y forward, +Z left).</summary>
    private static Pose? _lastBodyReferencePose;
    private static long _referenceUs;
    public static void ResetReference() { _lastBodyReferencePose = null; _referenceUs = 0; }
    public static bool ReferenceFresh => _lastBodyReferencePose.HasValue && QuestPacketEnvelope.MonotonicUs() - _referenceUs < 250000;

    /// <summary>Constant local rotation that re-axes the FullBody_Hips bone frame
    /// into a natural torso frame (+X right, +Y up, +Z forward). Derived from the
    /// bone's default upright axes (bone +X=down, +Y=forward, +Z=left) as
    /// R_fix = R_bone_default^-1 (the conjugate of the bone's default quaternion).
    /// Quaternion (x,y,z,w) = (-0.5, 0.5, 0.5, 0.5).</summary>
    private static readonly Quaternion _hipsBoneToTorsoFix = new Quaternion(-0.5f, 0.5f, 0.5f, 0.5f);
    private bool _isInitialized;
    private float _timer;
    private string _lastFidelityLogged = "";
    private GameObject[] _debugSpheres;
    private readonly StringBuilder _sbPacket = new StringBuilder(4096);
    private readonly StringBuilder _sbLog = new StringBuilder(512);

    /// <summary>Returns the latest hips pose (world space) to use as the body reference frame.</summary>
    public static bool TryGetBodyReferencePose(out Pose hipsPose)
    {
        if (ReferenceFresh)
        {
            hipsPose = _lastBodyReferencePose.Value;
            return true;
        }
        hipsPose = Pose.identity;
        return false;
    }

    /// <summary>Converts a world-space pose into the body(hips)-relative frame.
    /// Returns the pose unchanged when no body reference is available yet.</summary>
    public static Pose ToBodyRelative(Pose worldPose)
    {
        if (!ReferenceFresh)
        {
            return worldPose;
        }
        Pose hips = _lastBodyReferencePose.Value;
        Quaternion invRot = Quaternion.Inverse(hips.rotation);
        return new Pose(invRot * (worldPose.position - hips.position), invRot * worldPose.rotation);
    }

    private void Awake()
    {
        var settings = OVRRuntimeSettings.GetRuntimeSettings();
        settings.BodyTrackingFidelity = OVRPlugin.BodyTrackingFidelity2.High;
        settings.BodyTrackingJointSet = OVRPlugin.BodyJointSet.FullBody;
        Debug.Log($"[IOBT] Awake: jointSet={settings.BodyTrackingJointSet} fidelity={settings.BodyTrackingFidelity}");

        // NOTE: OVRBody is created lazily in EnsureBodyTrackingActive(), not here.
        // Adding an enabled OVRBody at startup starts body tracking immediately, and
        // body tracking is mutually exclusive with hand tracking — that would disable
        // the hand-driven menu. Body tracking must only start when streaming IOBT.
    }

    private void Update()
    {
        var app = AppManager.Instance;
        if (app == null || !app.TrackingEnabled || !app.TrackIobt)
        {
            Disconnect(); ResetReference();
            if (_ovrBody != null) _ovrBody.enabled = false;
            HideUnusedDebugSpheres(0);
            if (app != null) app.Status.sources["body"].Observe("身体", false);
            return;
        }
        if (!EnsureBodyTrackingActive())
        {
            ResetReference(); app.Status.sources["body"].Observe("身体", false, "等待身体权限"); return;
        }
        _timer += Time.deltaTime;
        if (_timer < frequencySeconds) return;
        _timer = 0f; // Preserve the existing source cadence in this UI migration.
        BuildAndSendPacket();
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    /// <summary>
    /// Creates the OVRBody component (if needed) and makes sure body tracking is actually
    /// running. Body tracking and hand tracking are mutually exclusive, so this must only
    /// be called while streaming IOBT — never at the menu, where hand tracking drives the UI.
    /// Returns true once body tracking is active.
    /// </summary>
    private bool EnsureBodyTrackingActive()
    {
        if (_ovrBody == null)
        {
            // Add OVRBody on an inactive GameObject so OnEnable (which starts body
            // tracking) does not fire until we activate it below.
            Debug.Log("[IOBT] creating OVRBody component");
            GameObject bodyGo = new GameObject("OVRBody");
            bodyGo.transform.SetParent(transform, false);
            bodyGo.SetActive(false);
            _ovrBody = bodyGo.AddComponent<OVRBody>();
            _ovrBody.ProvidedSkeletonType = OVRPlugin.BodyJointSet.FullBody;
            OVRBody.SetRequestedJointSet(OVRPlugin.BodyJointSet.FullBody);
        }

        if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.BodyTracking))
        {
            if (AppManager.Instance.Workspace != null && AppManager.Instance.Workspace.OnboardingVisible) return false;
            if (!_permissionRequested)
            {
                _permissionRequested = true;
                Debug.Log("[IOBT] requesting body tracking permission");
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.BodyTracking });
            }
            return false;
        }

        if (!_ovrBody.gameObject.activeSelf)
        {
            Debug.Log("[IOBT] activating OVRBody GameObject");
            _ovrBody.gameObject.SetActive(true);
        }
        if (!_ovrBody.enabled)
        {
            Debug.Log("[IOBT] enabling OVRBody component");
            _ovrBody.enabled = true; // triggers OnEnable -> StartBodyTracking
        }

        bool enabledNow = _ovrBody.enabled;
        bool hasStateNow = _ovrBody.BodyState.HasValue;
        if (enabledNow != _lastEnabledDiag || hasStateNow != _lastHasStateDiag)
        {
            _lastEnabledDiag = enabledNow;
            _lastHasStateDiag = hasStateNow;
            Debug.Log($"[IOBT] enabled={enabledNow} BodyState={hasStateNow} fidelity={_ovrBody.GetBodyTrackingFidelityStatus()}");
        }
        return enabledNow;
    }

    private void BuildAndSendPacket()
    {
        if (_ovrBody == null || !_ovrBody.enabled)
        {
            HudLine = "=== [IOBT] Body ===\nOVRBody disabled (SHAC or start failed)";
            PinIobt(HudLine);
            return;
        }
        if (!_ovrBody.BodyState.HasValue)
        {
            ResetReference();
            AppManager.Instance.Status.sources["body"].Observe("身体", false);
            HudLine = "=== [IOBT] Body ===\nno BodyState (permission / tracking not ready)";
            PinIobt(HudLine);
            return;
        }

        OVRPlugin.BodyState state = _ovrBody.BodyState.Value;
        int hipsIndex = (int)OVRPlugin.BoneId.FullBody_Hips;
        if (state.JointLocations == null || state.JointLocations.Length <= hipsIndex ||
            !state.JointLocations[hipsIndex].PositionValid || !state.JointLocations[hipsIndex].OrientationValid || state.Confidence <= 0)
        {
            ResetReference(); AppManager.Instance.Status.sources["body"].Observe("身体", false); return;
        }
        string fidelity = state.Fidelity.ToString();
        AppManager.Instance.Status.sources["body"].Observe("身体", true, fidelity);
        if (fidelity != _lastFidelityLogged)
        {
            _lastFidelityLogged = fidelity;
            LogHUD($"IOBT fidelity={fidelity} joints={state.JointSet} conf={state.Confidence:F2}");
            if (state.Fidelity != OVRPlugin.BodyTrackingFidelity2.High)
            {
                LogHUD("IOBT is NOT High. Passthrough or load may have dropped it to Low.");
            }
        }

        Camera cam = Camera.main;
        // Refresh the reference locally even when no transport or diagnostics are enabled.
        var hipsLocation = state.JointLocations[hipsIndex];
        var reference = new OVRPose { position = hipsLocation.Pose.Position.FromFlippedZVector3f(), orientation = hipsLocation.Pose.Orientation.FromFlippedZQuatf() };
        if (cam != null) reference = reference.ToWorldSpacePose(cam);
        _lastBodyReferencePose = new Pose(reference.position, reference.orientation * _hipsBoneToTorsoFix);
        _referenceUs = QuestPacketEnvelope.MonotonicUs();
        if (!AppManager.Instance.isStreaming && !AppManager.Instance.ShowDiagnostics && !AppManager.Instance.ShowLandmarks)
        { HideUnusedDebugSpheres(0); return; }
        _sbPacket.Clear();
        _sbPacket.Append("body iobt | fid=").Append(fidelity).Append(':');

        int drawn = 0;
        Vector3 hipsPos = Vector3.zero;
        Quaternion hipsRot = Quaternion.identity;
        bool hasHips = false;
        Vector3 chestPos = Vector3.zero;
        bool hasChest = false;
        Vector3 lShoulderPos = Vector3.zero;
        Vector3 rShoulderPos = Vector3.zero;
        bool hasLShoulder = false;
        bool hasRShoulder = false;

        for (int i = 0; i < StreamedJoints.Length; i++)
        {
            int boneIndex = (int)StreamedJoints[i].bone;
            if (state.JointLocations == null || boneIndex >= state.JointLocations.Length)
            {
                continue;
            }

            OVRPlugin.BodyJointLocation loc = state.JointLocations[boneIndex];
            if (!loc.PositionValid && !loc.OrientationValid)
            {
                continue;
            }

            Vector3 pos = loc.Pose.Position.FromFlippedZVector3f();
            Quaternion rot = loc.Pose.Orientation.FromFlippedZQuatf();
            if (cam != null)
            {
                OVRPose world = new OVRPose { position = pos, orientation = rot }.ToWorldSpacePose(cam);
                pos = world.position;
                rot = world.orientation;
            }

            // Hips is streamed in WORLD space (re-axed torso frame) so the downstream
            // can publish the body root in world. Every other joint is streamed
            // already expressed in the hips (torso) frame — matching hand/head/
            // controller — so the downstream no longer needs pose_in_parent_frame.
            // hips is StreamedJoints[0], so _lastBodyReferencePose is set to this
            // frame's hips before any other joint is converted via ToBodyRelative.
            _sbPacket.Append(' ').Append(StreamedJoints[i].name).Append(',');
            if (StreamedJoints[i].name == "hips")
            {
                Quaternion torsoRot = rot * _hipsBoneToTorsoFix;
                hipsPos = pos;
                hipsRot = torsoRot;
                hasHips = true;
                _lastBodyReferencePose = new Pose(hipsPos, hipsRot);
                _referenceUs = QuestPacketEnvelope.MonotonicUs();
                AppendVector3(_sbPacket, pos);
                _sbPacket.Append(',');
                AppendQuaternion(_sbPacket, torsoRot);
            }
            else
            {
                Pose rel = ToBodyRelative(new Pose(pos, rot));
                AppendVector3(_sbPacket, rel.position);
                _sbPacket.Append(',');
                AppendQuaternion(_sbPacket, rel.rotation);

                // HUD / debug spheres keep WORLD positions for diagnostics.
                if (StreamedJoints[i].name == "chest") { chestPos = pos; hasChest = true; }
                else if (StreamedJoints[i].name == "left-shoulder") { lShoulderPos = pos; hasLShoulder = true; }
                else if (StreamedJoints[i].name == "right-shoulder") { rShoulderPos = pos; hasRShoulder = true; }
            }

            if (drawDebugSkeleton && AppManager.Instance.ShowLandmarks)
            {
                UpdateDebugSphere(drawn, pos, StreamedJoints[i].name);
                drawn++;
            }
        }

        HideUnusedDebugSpheres(drawn);

        if (AppManager.Instance.ShowDiagnostics)
        {
            _sbLog.Clear();
            _sbLog.AppendLine("=== [IOBT] Body ===");
            _sbLog.Append("fid=").Append(fidelity);
            _sbLog.Append(" conf=").Append(state.Confidence.ToString("F2"));
            _sbLog.Append(" joints=").Append(drawn).Append('/').Append(StreamedJoints.Length).AppendLine();
            if (hasHips)
            {
                _sbLog.Append("Hips Pos: ").AppendLine($"({hipsPos.x:F3}, {hipsPos.y:F3}, {hipsPos.z:F3})");
                _sbLog.Append("Hips Rot: ").AppendLine($"({hipsRot.x:F3}, {hipsRot.y:F3}, {hipsRot.z:F3}, {hipsRot.w:F3})");
            }
            if (hasChest)
            {
                _sbLog.Append("Chest Pos: ").AppendLine($"({chestPos.x:F3}, {chestPos.y:F3}, {chestPos.z:F3})");
            }
            if (hasLShoulder)
            {
                _sbLog.Append("LShoulder: ").AppendLine($"({lShoulderPos.x:F3}, {lShoulderPos.y:F3}, {lShoulderPos.z:F3})");
            }
            if (hasRShoulder)
            {
                _sbLog.Append("RShoulder: ").Append($"({rShoulderPos.x:F3}, {rShoulderPos.y:F3}, {rShoulderPos.z:F3})");
            }
            HudLine = _sbLog.ToString();
            PinIobt(HudLine);

        }
        SendData(_sbPacket.ToString());
    }

    private void UpdateDebugSphere(int index, Vector3 pos, string name)
    {
        if (_debugSpheres == null)
        {
            _debugSpheres = new GameObject[StreamedJoints.Length];
        }
        if (_debugSpheres[index] == null)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.name = "IOBT_" + name;
            s.transform.localScale = Vector3.one * 0.04f;
            var col = s.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var r = s.GetComponent<Renderer>();
            if (r != null)
            {
                // Project uses URP, which has no "Standard" shader. Find a compatible
                // shader or skip the debug sphere material entirely.
                Shader debugShader = Shader.Find("Universal Render Pipeline/Lit")
                    ?? Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Standard");
                if (debugShader != null)
                {
                    r.material = new Material(debugShader);
                    bool leg = name.Contains("leg") || name.Contains("foot");
                    bool arm = name.Contains("shoulder") || name.Contains("scapula") || name.Contains("arm");
                    r.material.color = leg ? new Color(0.8f, 0.4f, 1f) : arm ? Color.yellow : new Color(1f, 0.53f, 0f);
                }
            }
            _debugSpheres[index] = s;
        }
        _debugSpheres[index].SetActive(true);
        _debugSpheres[index].transform.position = pos;
    }

    private void HideUnusedDebugSpheres(int used)
    {
        if (_debugSpheres == null) return;
        for (int i = used; i < _debugSpheres.Length; i++)
        {
            if (_debugSpheres[i] != null) _debugSpheres[i].SetActive(false);
        }
    }

    private TelemetryChannel _channel;
    private void InitializeNetwork()
    {
        if (_channel != null) return;
        _channel = AppManager.Instance.CreateChannel("body");
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


    private static void AppendVector3(StringBuilder sb, Vector3 vec)
    {
        sb.Append(vec.x.ToString("F4")).Append(',').Append(vec.y.ToString("F4")).Append(',').Append(vec.z.ToString("F4"));
    }

    private static void AppendQuaternion(StringBuilder sb, Quaternion q)
    {
        sb.Append(q.x.ToString("F3")).Append(',').Append(q.y.ToString("F3")).Append(',')
          .Append(q.z.ToString("F3")).Append(',').Append(q.w.ToString("F3"));
    }

    private void PinIobt(string block)
    {
        if (AppManager.Instance == null || !AppManager.Instance.ShowDiagnostics || LogManager.Instance == null)
        {
            return;
        }
        // Body is a single full-body stream; show it on the Left panel only.
        LogManager.Instance.Pin("Left", "iobt", block);
    }

    private void LogHUD(string msg)
    {
        if (logToHUD && AppManager.Instance != null && AppManager.Instance.ShowDiagnostics && LogManager.Instance != null)
        {
            LogManager.Instance.Log(hudLogSource, msg);
        }
    }
}
}
