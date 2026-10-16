using UnityEngine;
using TMPro; 
using UnityEngine.UI;
using System;
using System.Net.Sockets;

namespace MORLA.XR
{

public class AppManager : MonoBehaviour
{
    public static AppManager Instance { get; private set; } 

    [Header("UI References")]
    public TMP_Dropdown protocolDropdown; 
    public TMP_InputField ipInputField;
    public TMP_InputField portInputField;
    public TMP_Dropdown handDropdown;     
    public GameObject menuPanel;          
    
    [Header("Network Status References")]
    public TextMeshProUGUI networkStatusText; 
    public Button btnStart;                

    [Header("Build Info")]
    public TextMeshProUGUI versionText;   

    [Header("Visual Settings")]
    public Toggle visualizationToggle; 
    public bool ShowLandmarks => visualizationToggle != null && visualizationToggle.isOn;

    [Header("Debug and Experimental")]
    public Toggle debugInfoToggle;
    public bool ShowDebugInfo => debugInfoToggle != null && debugInfoToggle.isOn;

    public Toggle videoStreamToggle;
    public bool ShowVideoStream => videoStreamToggle != null && videoStreamToggle.isOn;

    public Toggle headPoseToggle;
    public bool TrackHeadPose => headPoseToggle != null && headPoseToggle.isOn;

    public Toggle iobtToggle;
    public Toggle mixedInputToggle;
    public Toggle refresh90Toggle;
    public bool MixedInputEnabled => mixedInputToggle == null || mixedInputToggle.isOn;
    public bool WantIobt => iobtToggle == null || iobtToggle.isOn;
    /// <summary>Meta Body API cannot run while SHAC mixed input is on.</summary>
    public bool TrackIobt => WantIobt && !MixedInputEnabled;

    [Header("Interaction Settings")]
    public GameObject[] rayInteractors;

    [Header("Hand Visuals")]
    public GameObject syntheticHandLeft;
    public GameObject syntheticHandRight;

    [Header("Logging Settings")]
    public string targetLogSource = "Left"; 

    [Header("Status")]
    public bool isStreaming = false;
    private string _connectionErrorMessage = ""; 
    private Color _statusColor = Color.green; // Persistent color cache

    public string ServerIP { get; private set; }
    public int ServerPort { get; private set; }
    public int SelectedProtocol { get; private set; } 
    public int SelectedHandMode { get; private set; } 

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;
    }

    private void Start()
    {
        try
        {
            // Automatically pulls from Project Settings > Player > Version
            string version = Application.version;

            if (versionText != null)
            {
                versionText.text = $"v{version}";
            }

            if (protocolDropdown != null)
            {
                protocolDropdown.onValueChanged.AddListener(OnProtocolChanged);
                // OnProtocolChanged(protocolDropdown.value);
            }

            ipInputField.onValueChanged.AddListener(delegate { ClearError(); });
            portInputField.onValueChanged.AddListener(delegate { ClearError(); });
            // Load saved config (if any)
            LoadConfig();
            EnsureTrackingToggleRow();
            ApplyPassthrough();
            // SHAC is only enabled after Start Stream; enabling it on the menu disables
            // Interaction SDK ray interactors (OVRInputDeviceActiveState checks for pure
            // Touch or pure Hands, and SHAC returns Touch|Hands, so both rays go inactive).
            DisableShac();
            ApplyVideoCanvasVisibility();
            EnsureBodyPoseStreamer();
            EnsureControllerPoseStreamer();
            EnsureControllerMenuPointer();
            // OVRBody auto-enables in Awake and starts body tracking immediately.
            // Disable it here so body tracking only runs when the user actually wants IOBT.
            OVRBody body = FindFirstObjectByType<OVRBody>();
            if (body != null && !TrackIobt)
            {
                body.enabled = false;
            }
            StatusHud.Ensure();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[AppManager] Start() threw: {ex}");
        }

        LogStartupDiagnostics();
    }

    /// <summary>Logs hand-tracking / SHAC / body state at startup so logcat can reveal why hands don't work.</summary>
    private void LogStartupDiagnostics()
    {
        try
        {
            OVRHand[] hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            bool shac = OVRManager.instance != null && OVRManager.instance.SimultaneousHandsAndControllersEnabled;
            bool passthrough = OVRManager.instance != null && OVRManager.instance.isInsightPassthroughEnabled;
            Debug.Log($"[AppManager] DIAG hands={hands.Length} SHAC={shac} Mixed={MixedInputEnabled} TrackIobt={TrackIobt} passthrough={passthrough}");
            foreach (OVRHand h in hands)
            {
                Debug.Log($"[AppManager] DIAG OVRHand '{h.name}': enabled={h.enabled} IsTracked={h.IsTracked} IsPointerPoseValid={h.IsPointerPoseValid}");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[AppManager] DIAG error: {ex}");
        }
    }

    private static void EnsureBodyPoseStreamer()
    {
        if (FindFirstObjectByType<BodyPoseStreamer>() != null)
        {
            return;
        }
        var go = new GameObject("BodyPoseStreamer");
        go.AddComponent<BodyPoseStreamer>();
    }

    private static void EnsureControllerPoseStreamer()
    {
        if (FindFirstObjectByType<ControllerPoseStreamer>() != null)
        {
            return;
        }
        var go = new GameObject("ControllerPoseStreamer");
        go.AddComponent<ControllerPoseStreamer>();
    }

    private static void EnsureControllerMenuPointer()
    {
        // Self-contained controller -> uGUI bridge so the Touch controllers can
        // operate the menu (Building Blocks has no Controller Ray Interactor rig
        // in this SDK version; the menu only had Hand Ray). Auto-creates itself,
        // so no scene edit is needed.
        if (FindFirstObjectByType<ControllerMenuPointer>() != null)
        {
            return;
        }
        var go = new GameObject("ControllerMenuPointer");
        go.AddComponent<ControllerMenuPointer>();
    }

    private bool _applyingMutex;

    private void EnsureTrackingToggleRow()
    {
        if (headPoseToggle == null)
        {
            return;
        }

        Transform debugRow = headPoseToggle.transform.parent;
        Transform rowParent = debugRow.parent;
        Transform existingRow = rowParent.Find("Tracking_Modes");
        GameObject rowGo;
        if (existingRow != null)
        {
            rowGo = existingRow.gameObject;
        }
        else
        {
            rowGo = new GameObject("Tracking_Modes", typeof(RectTransform));
            rowGo.layer = debugRow.gameObject.layer;
            RectTransform rt = rowGo.GetComponent<RectTransform>();
            rt.SetParent(rowParent, false);
            rt.SetSiblingIndex(debugRow.GetSiblingIndex() + 1);
            rt.sizeDelta = new Vector2(0f, 60f);
            LayoutElement le = rowGo.AddComponent<LayoutElement>();
            le.minHeight = 60f;
            le.preferredHeight = 60f;
            HorizontalLayoutGroup src = debugRow.GetComponent<HorizontalLayoutGroup>();
            HorizontalLayoutGroup h = rowGo.AddComponent<HorizontalLayoutGroup>();
            if (src != null)
            {
                h.padding = src.padding;
                h.childAlignment = src.childAlignment;
                h.spacing = src.spacing;
                h.childForceExpandWidth = src.childForceExpandWidth;
                h.childForceExpandHeight = src.childForceExpandHeight;
                h.childControlWidth = src.childControlWidth;
                h.childControlHeight = src.childControlHeight;
                h.childScaleWidth = src.childScaleWidth;
                h.childScaleHeight = src.childScaleHeight;
            }
            else
            {
                h.spacing = 20f;
                h.childAlignment = TextAnchor.MiddleLeft;
                h.childForceExpandWidth = true;
                h.childControlWidth = true;
                h.childControlHeight = true;
            }
        }

        if (mixedInputToggle == null)
        {
            mixedInputToggle = CloneMenuToggle(headPoseToggle, rowGo.transform, "Tgl_Mixed", "Mixed Input");
        }
        if (iobtToggle == null)
        {
            iobtToggle = CloneMenuToggle(headPoseToggle, rowGo.transform, "Tgl_IOBT", "IOBT Body");
        }
        if (refresh90Toggle == null)
        {
            refresh90Toggle = CloneMenuToggle(headPoseToggle, rowGo.transform, "Tgl_90Hz", "90 Hz");
        }

        _applyingMutex = true;
        mixedInputToggle.isOn = PlayerPrefs.GetInt("SavedMixed", 0) == 1;
        iobtToggle.isOn = PlayerPrefs.GetInt("SavedIobt", 1) == 1;
        if (mixedInputToggle.isOn && iobtToggle.isOn)
        {
            iobtToggle.isOn = false;
        }
        _applyingMutex = false;

        mixedInputToggle.onValueChanged.RemoveListener(OnMixedChanged);
        mixedInputToggle.onValueChanged.AddListener(OnMixedChanged);
        iobtToggle.onValueChanged.RemoveListener(OnIobtChanged);
        iobtToggle.onValueChanged.AddListener(OnIobtChanged);

        refresh90Toggle.onValueChanged.RemoveListener(OnRefresh90Changed);
        refresh90Toggle.isOn = PlayerPrefs.GetInt("Saved90Hz", 1) == 1;
        refresh90Toggle.onValueChanged.AddListener(OnRefresh90Changed);
        ApplyRefreshRate();
    }

    private static Toggle CloneMenuToggle(Toggle template, Transform parent, string name, string labelText)
    {
        GameObject clone = Instantiate(template.gameObject, parent);
        clone.name = name;
        Text label = clone.GetComponentInChildren<Text>(true);
        if (label != null)
        {
            label.text = labelText;
        }
        TMP_Text tmp = clone.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            tmp.text = labelText;
        }
        Toggle toggle = clone.GetComponent<Toggle>();
        toggle.onValueChanged.RemoveAllListeners();
        return toggle;
    }

    private void OnMixedChanged(bool on)
    {
        if (_applyingMutex)
        {
            return;
        }
        if (on && iobtToggle != null && iobtToggle.isOn)
        {
            _applyingMutex = true;
            iobtToggle.isOn = false;
            _applyingMutex = false;
        }
        if (isStreaming)
        {
            ApplyTrackingMode();
        }
    }

    private void OnIobtChanged(bool on)
    {
        if (_applyingMutex)
        {
            return;
        }
        if (on && mixedInputToggle != null && mixedInputToggle.isOn)
        {
            _applyingMutex = true;
            mixedInputToggle.isOn = false;
            _applyingMutex = false;
        }
        if (isStreaming)
        {
            ApplyTrackingMode();
        }
    }

    private void OnRefresh90Changed(bool on)
    {
        ApplyRefreshRate();
        PlayerPrefs.SetInt("Saved90Hz", on ? 1 : 0);
        PlayerPrefs.Save();
    }

    private void ApplyRefreshRate()
    {
        // Request 90 Hz when the toggle is on, else 72 Hz. The runtime snaps to the
        // closest supported rate and falls back safely if 90 is unavailable (thermal
        // limit, unsupported config). A brief display flash on switch is normal.
        float want = refresh90Toggle != null && refresh90Toggle.isOn ? 90f : 72f;
        float chosen = want;
        float[] available = OVRPlugin.systemDisplayFrequenciesAvailable;
        if (available != null && available.Length > 0)
        {
            chosen = available[0];
            for (int i = 1; i < available.Length; i++)
            {
                if (Mathf.Abs(available[i] - want) < Mathf.Abs(chosen - want))
                {
                    chosen = available[i];
                }
            }
        }
        OVRPlugin.systemDisplayFrequency = chosen;
        Debug.Log($"[AppManager] Display refresh: want={want} applied={chosen}");
    }

    private static void ApplyPassthrough()
    {
        if (OVRManager.instance != null)
        {
            OVRManager.instance.isInsightPassthroughEnabled = true;
        }
        OVRPassthroughLayer layer = FindFirstObjectByType<OVRPassthroughLayer>();
        if (layer != null)
        {
            layer.enabled = true;
            layer.hidden = false;
        }
    }

    public void ApplyTrackingMode()
    {
        bool mixed = MixedInputEnabled;
        if (OVRManager.instance != null)
        {
            OVRManager.instance.SimultaneousHandsAndControllersEnabled = mixed;
        }
        OVRPlugin.SetSimultaneousHandsAndControllersEnabled(mixed);

        OVRBody body = FindFirstObjectByType<OVRBody>();
        if (body != null)
        {
            body.enabled = TrackIobt;
        }
    }

    private static void DisableShac()
    {
        if (OVRManager.instance != null)
        {
            OVRManager.instance.SimultaneousHandsAndControllersEnabled = false;
        }
        OVRPlugin.SetSimultaneousHandsAndControllersEnabled(false);
    }

    /// <summary>True only while the Touch controller on this side is actually held in hand.
    /// A controller resting on a table is still "position valid" but must NOT suppress hand tracking.</summary>
    public static bool IsTouchPoseValid(HandLandmarkStreamer.HandSide side)
    {
        OVRInput.Hand hand = side == HandLandmarkStreamer.HandSide.Left
            ? OVRInput.Hand.HandLeft
            : OVRInput.Hand.HandRight;
        return OVRInput.GetControllerIsInHandState(hand) == OVRInput.ControllerInHandState.ControllerInHand;
    }

    private void SaveConfig()
    {
        // Save the current UI values to disk
        PlayerPrefs.SetInt("SavedProtocol", protocolDropdown.value);
        PlayerPrefs.SetString("SavedIP", ipInputField.text);
        PlayerPrefs.SetString("SavedPort", portInputField.text);
        PlayerPrefs.SetInt("SavedHandMode", handDropdown.value);
        if (iobtToggle != null)
        {
            PlayerPrefs.SetInt("SavedIobt", iobtToggle.isOn ? 1 : 0);
        }
        if (mixedInputToggle != null)
        {
            PlayerPrefs.SetInt("SavedMixed", mixedInputToggle.isOn ? 1 : 0);
        }
        if (refresh90Toggle != null)
        {
            PlayerPrefs.SetInt("Saved90Hz", refresh90Toggle.isOn ? 1 : 0);
        }
        
        // Force write to disk immediately
        PlayerPrefs.Save(); 
        Debug.Log("[AppManager] Config Saved.");
    }

    private void LoadConfig()
    {
        // 1. Load Protocol (Default to 1: TCP wired if not found)
        if (PlayerPrefs.HasKey("SavedProtocol"))
        {
            int savedProto = PlayerPrefs.GetInt("SavedProtocol");
            // This triggers OnProtocolChanged, which sets default IPs
            protocolDropdown.value = savedProto; 
        }
        else
        {
            // Default TCP wired; setting value fires OnProtocolChanged(1), which also
            // fills in the matching IP/port defaults (127.0.0.1:8000).
            protocolDropdown.value = 1;
        }

        // 2. Load IP (Overwrite the default set by the dropdown)
        if (PlayerPrefs.HasKey("SavedIP"))
        {
            ipInputField.text = PlayerPrefs.GetString("SavedIP");
        }

        // 3. Load Port
        if (PlayerPrefs.HasKey("SavedPort"))
        {
            portInputField.text = PlayerPrefs.GetString("SavedPort");
        }

        // 4. Load Hand Mode
        if (PlayerPrefs.HasKey("SavedHandMode"))
        {
            handDropdown.value = PlayerPrefs.GetInt("SavedHandMode");
        }
    }

    private void Update()
    {
        // If streaming, check if the user clicks the Left Menu Button
        // This is the flat button with three lines on the Left Quest Controller
        if (isStreaming && OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
        {
            StopStreaming(); 
        }

        if (!isStreaming)
        {
            ValidateNetwork();
        }
    }
    
    private void OnDestroy()
    {
        if (protocolDropdown != null)
        {
            protocolDropdown.onValueChanged.RemoveListener(OnProtocolChanged);
        }
    }

private void OnProtocolChanged(int index)
    {
        ClearError();
        if (index == 0) // UDP
        {
            if (ipInputField != null) ipInputField.text = "255.255.255.255";
            if (portInputField != null) portInputField.text = "9000";
            UpdateStatusUI("UDP Ready", Color.green, true);
        }
        else if (index == 1) // TCP (Wired / ADB)
        {
            if (ipInputField != null) ipInputField.text = "127.0.0.1";
            if (portInputField != null) portInputField.text = "8000"; 
            StartCoroutine(QuickTCPCheck());
        }
        else if (index == 2) // TCP (Wireless) - NEW
        {
            // Set a placeholder or the last known IP. 
            if (ipInputField != null) ipInputField.text = "192.168.1.1"; // Placeholder for the PC's Wi-Fi IP
            if (portInputField != null) portInputField.text = "8000"; 
            StartCoroutine(QuickTCPCheck());
        }
    }

    public void ClearError()
    {
        _connectionErrorMessage = "";
        _statusColor = Color.green;
    }

    private void ValidateNetwork()
    {
        if (Application.internetReachability == NetworkReachability.NotReachable)
        {
            UpdateStatusUI("Error: No Active Network Connection", Color.red, false);
            return;
        }

        if (!string.IsNullOrEmpty(_connectionErrorMessage))
        {
            // Uses the persistent color (Red or Yellow) set by the connection logic
            UpdateStatusUI(_connectionErrorMessage, _statusColor, true);
            return;
        }

        UpdateStatusUI("System Ready", Color.green, true);
    }

    private void UpdateStatusUI(string message, Color color, bool canStart)
    {
        _statusColor = color; // Cache the color for the Update loop
        if (networkStatusText != null)
        {
            networkStatusText.text = message;
            networkStatusText.color = color;
        }

        if (btnStart != null)
        {
            btnStart.interactable = canStart;
            var breather = btnStart.GetComponent<UIButtonBreather>();
            if (breather != null) breather.enabled = canStart;
        }
    }

    public async void OnStartStreaming()
    {
        ClearError();
        ServerIP = ipInputField.text;
        
        int parsedPort;
        if (!int.TryParse(portInputField.text, out parsedPort))
        {
            UpdateStatusUI("Error: Port number is invalid", Color.red, false);
            return;
        }
        ServerPort = parsedPort;

        SelectedProtocol = protocolDropdown.value;
        SelectedHandMode = handDropdown.value;

        // --- UPDATED TCP CHECK BLOCK ---
        if (SelectedProtocol == 1 || SelectedProtocol == 2) // TCP wireless and wired
        {
            try 
            {
                // Optional: visual feedback that we are trying
                UpdateStatusUI($"Connecting to {ServerIP}...", Color.yellow, false);

                using (TcpClient testClient = new TcpClient())
                {
                    IAsyncResult result = testClient.BeginConnect(ServerIP, ServerPort, null, null);
                    
                    // Wait for 1 second max
                    bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    
                    if (!success)
                    {
                        // Clean up manually if we timed out
                        try { testClient.Close(); } catch {}
                        throw new Exception("Connection Timed Out");
                    }

                    // CRITICAL: EndConnect throws the specific SocketException (Refused/Unreachable)
                    testClient.EndConnect(result);
                }
            }
            catch (Exception ex)
            {
                // 1. Get the real error message (e.g. "No connection could be made...", "Timed Out")
                string realError = ex.Message;
                
                // 2. Format a shorter message for the small UI text
                _connectionErrorMessage = $"TCP Connection Error: {realError}";
                
                // 3. LOG EVERYTHING to the HUD/Console
                // This will show up on your shared screen log
                string detailedLog = $"TCP Connection Target: {ServerIP}:{ServerPort}\nException: {realError}";
                SendLog(detailedLog);
                Debug.LogError(detailedLog); // Also print to ADB Logcat

                UpdateStatusUI(_connectionErrorMessage, Color.red, true);
                return; 
            }
        }
        // -------------------------------

        UpdateStatusUI("Streaming Active", Color.green, true);

        // Save the current config for next time
        SaveConfig();
        
        string protocolName = protocolDropdown.options[SelectedProtocol].text;
        string handName = handDropdown.options[SelectedHandMode].text;
        string statusMsg = $"Stream started! \nIP: {ServerIP} \nPort: {ServerPort} \nProtocol: {protocolName} \nHands: {handName}\nMixed: {MixedInputEnabled} IOBT: {TrackIobt}";
        SendLog(statusMsg);
        ApplyTrackingMode();

        if(menuPanel != null) menuPanel.SetActive(false);

        ToggleRays(false);
        UpdateHandVisuals(SelectedHandMode);
        isStreaming = true;

        // Optional host->Quest video plane (separate from telemetry transport)
        if (ShowVideoStream)
        {
            if (string.Equals(ServerIP, "255.255.255.255", StringComparison.Ordinal))
            {
                HandleDisconnection("Video requires a concrete host IP. Switch protocol to TCP/Wireless or enter host LAN IP.");
                return;
            }

            VideoStreamManager manager = FindFirstObjectByType<VideoStreamManager>();
            if (manager == null)
            {
                HandleDisconnection("Video manager missing from scene.");
                return;
            }

            bool ok;
            try
            {
                ok = await manager.StartVideoSession(
                    ServerIP,
                    8765,
                    "720p30",
                    false
                );
            }
            catch (Exception ex)
            {
                ok = false;
                Debug.LogError($"[Video] Start failed: {ex.Message}");
            }

            if (!ok)
            {
                HandleDisconnection("Video session failed to start.");
                return;
            }
        }
        else
        {
            ApplyVideoCanvasVisibility();
        }
    }

    // Called by the Streamer when the socket dies
    public void HandleDisconnection(string errorMsg)
    {
        // Prevent spamming if multiple hands fail at once
        if (!isStreaming) return;

        Debug.LogError($"[Network] TCP Disconnect Triggered: {errorMsg}");
        
        // 1. Reset Logic
        isStreaming = false;

        // 2. Re-enable UI
        if (menuPanel != null) 
        {
            menuPanel.SetActive(true);
            
            // Optional: Recenter menu in front of user so they see the error
            MenuRecenter recenter = FindFirstObjectByType<MenuRecenter>();
            if (recenter != null) recenter.Recenter();
        }
        ToggleRays(true);
        DisableShac();

        // 3. Show Error Status
        _connectionErrorMessage = $"TCP Disconnected: {errorMsg}"; // persistent
        UpdateStatusUI(_connectionErrorMessage, Color.yellow, true);
        SendLog($"Connection dropped: {errorMsg}");
        StopVideoSessionAsync("disconnection");
        ApplyVideoCanvasVisibility();
    }

    public void StopStreaming()
    {
        isStreaming = false;
        ClearError();
        StopVideoSessionAsync("user_stop");
        ApplyVideoCanvasVisibility();

        // Re-enable UI and Rays for interaction
        if (menuPanel != null) 
        {
            menuPanel.SetActive(true);
            MenuRecenter recenterScript = FindFirstObjectByType<MenuRecenter>();
            if (recenterScript != null) recenterScript.Recenter();
        }
        ToggleRays(true);
        DisableShac();

        SendLog("Streaming stopped by user.");
    }

    private async void StopVideoSessionAsync(string reason)
    {
        VideoStreamManager manager = FindFirstObjectByType<VideoStreamManager>();
        if (manager != null)
        {
            try
            {
                await manager.StopVideoSession(reason);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Video] Stop failed: {ex.Message}");
            }
        }
    }

    private void UpdateHandVisuals(int mode)
    {
        bool showLeft = (mode == 0 || mode == 1);
        bool showRight = (mode == 0 || mode == 2);

        if (syntheticHandLeft != null) syntheticHandLeft.SetActive(showLeft);
        if (syntheticHandRight != null) syntheticHandRight.SetActive(showRight);
    }

    private void ToggleRays(bool state)
    {
        if (rayInteractors == null) return;
        foreach (var ray in rayInteractors)
        {
            if (ray != null) ray.SetActive(state);
        }
    }

    private void SendLog(string message)
    {
        if (LogManager.Instance != null)
            LogManager.Instance.Log(targetLogSource, message);
        else
            Debug.Log($"[{targetLogSource}] {message}");
    }

    private void ApplyVideoCanvasVisibility()
    {
        VideoStreamManager manager = FindFirstObjectByType<VideoStreamManager>();
        if (manager == null)
        {
            return;
        }
        manager.SetVideoUiVisible(isStreaming && ShowVideoStream);
    }

    private System.Collections.IEnumerator QuickTCPCheck()
    {
        UpdateStatusUI("Checking TCP Connection...", Color.yellow, false);
        
        string targetIP = ipInputField.text;
        int targetPort;
        if (!int.TryParse(portInputField.text, out targetPort)) yield break;

        bool success = false;
        
        var task = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    var result = client.BeginConnect(targetIP, targetPort, null, null);
                    if (result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        client.EndConnect(result);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        });

        while (!task.IsCompleted) yield return null;
        success = task.Result;

        if (!success)
        {
            _connectionErrorMessage = "TCP Error: Connection refused. Is host server running?";
            // Persistent Yellow for the passive background check
            UpdateStatusUI(_connectionErrorMessage, Color.yellow, true);
        }
    }
}
}
