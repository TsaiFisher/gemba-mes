namespace SmartFactory.MES.Modules.RPT.Models
{
    /// <summary>現場遙測儀表板（RPT/FieldTelemetry）。</summary>
    public class FieldTelemetryReportVm
    {
        /// <summary>期間（小時）：24／168／720。</summary>
        public int Hours { get; set; } = 24;
        public string? SelectedKind { get; set; }

        public int ErrorCount { get; set; }
        public int ApiCalls { get; set; }
        /// <summary>超過 3 秒的呼叫數。</summary>
        public int ApiSlow { get; set; }
        public int ApiFailures { get; set; }
        /// <summary>回 409 的次數（伺服器重試後仍衝突）。</summary>
        public int ApiConflicts { get; set; }
        public int OfflineCount { get; set; }
        public int OfflineMinutes { get; set; }
        public int ActiveDevices { get; set; }
        public int DevicesWithPending { get; set; }

        public List<FieldTelemetryErrorRow> TopErrors { get; set; } = new();
        public List<FieldTelemetryEndpointRow> Endpoints { get; set; } = new();
        public List<FieldTelemetryDeviceRow> Devices { get; set; } = new();
        public List<ClientTelemetryEvent> Recent { get; set; } = new();
    }

    public class FieldTelemetryErrorRow
    {
        public string Kind { get; set; } = "";
        public string? Message { get; set; }
        public string? Page { get; set; }
        public int Count { get; set; }
        public int Devices { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    public class FieldTelemetryEndpointRow
    {
        public string? Endpoint { get; set; }
        public int Calls { get; set; }
        public long TotalMs { get; set; }
        public int MaxMs { get; set; }
        public int Slow3To10 { get; set; }
        public int Over10s { get; set; }
        public int Failures { get; set; }
        public int Conflicts { get; set; }
        public long AvgMs => Calls > 0 ? TotalMs / Calls : 0;
    }

    public class FieldTelemetryDeviceRow
    {
        public string DeviceId { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public string? UserName { get; set; }
        public string? AppVersion { get; set; }
        public string? NetType { get; set; }
        public int? RttMs { get; set; }
        public int Errors { get; set; }
        public int QueuePending { get; set; }
        public int? QueueOldestMinutes { get; set; }
    }
}
