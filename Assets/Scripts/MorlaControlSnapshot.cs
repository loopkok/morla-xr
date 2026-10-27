using Newtonsoft.Json.Linq;

namespace MORLA.XR
{
    /// <summary>Strict projection of the existing /api/v1/state envelope; acceptance is never completion.</summary>
    public sealed class MorlaControlSnapshot
    {
        public string profile, operationId, operation, phase, message;
        public string enabled = "未提供", recording = "未提供", owner = "未提供";
        public bool following;
        public bool? result;
        public static bool TryParse(string json, out MorlaControlSnapshot snapshot)
        {
            try { return ParseCore(json, out snapshot); }
            catch (System.Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is System.FormatException || ex is System.InvalidCastException || ex is System.ArgumentException)
            { snapshot = null; return false; }
        }
        private static bool ParseCore(string json, out MorlaControlSnapshot snapshot)
        {
            snapshot = null;
            var envelope = JObject.Parse(json);
            var data = envelope["data"] as JObject;
            var control = data?["control"] as JObject;
            if (envelope.Value<bool?>("ok") != true || control == null || control["stale"]?.Type != JTokenType.Boolean ||
                control["teleop_armed"]?.Type != JTokenType.Boolean || control["profile_id"]?.Type != JTokenType.String ||
                control.Value<bool?>("stale") != false ||
                string.IsNullOrEmpty(control.Value<string>("profile_id")) || control.Value<bool?>("teleop_armed") == null ||
                (control.Value<int?>("node_count") ?? 1) != 1) return false;
            var value = new MorlaControlSnapshot
            {
                profile = control.Value<string>("profile_id"), operationId = control.Value<string>("operation_id"),
                operation = control.Value<string>("operation") ?? "", phase = control.Value<string>("phase") ?? "",
                message = control.Value<string>("message") ?? "", following = control.Value<bool>("teleop_armed"),
                result = control.Value<bool?>("result"), owner = control.Value<string>("owner") ?? "未提供"
            };
            var collect = data["data_collect"] as JObject;
            if (collect != null && collect.Value<bool?>("stale") == false)
                value.recording = collect.Value<string>("state") ?? "未提供";
            var drivers = control["driver_status"] as JObject;
            if (drivers != null && drivers.Count > 0)
            {
                bool known = true, allEnabled = true;
                foreach (var item in drivers.Properties())
                {
                    var row = item.Value as JObject;
                    bool? enabled = row?.Value<bool?>("motors_enabled") ?? row?.Value<bool?>("enabled");
                    if (enabled == null || row.Value<bool?>("feedback_fresh") != true) { known = false; break; }
                    allEnabled &= enabled.Value;
                }
                if (known) value.enabled = allEnabled ? "已使能" : "未全部使能";
            }
            snapshot = value; return true;
        }
        public bool Confirms(string id, string command, string expectedProfile)
        {
            if (string.IsNullOrEmpty(id) || id != operationId || profile != expectedProfile || command != operation ||
                phase != "completed" || result != true) return false;
            return command == "pause" ? !following : command == "start" && following;
        }
    }
}
