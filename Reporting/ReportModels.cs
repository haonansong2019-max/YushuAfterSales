using System;
using System.Collections.Generic;

namespace YushuAfterSales.Reporting
{
    /// <summary>Root document for a diagnostic report. All timestamps are UTC ISO-8601 strings.</summary>
    public sealed class ReportDocument
    {
        public string SchemaVersion { get; set; }
        public string ReportId { get; set; }
        public string CreatedUtc { get; set; }
        public string ApplicationVersion { get; set; }
        public AuthorizationSummary Authorization { get; set; }
        public ScanRequest Scan { get; set; }
        public EnvironmentSnapshot Environment { get; set; }
        public List<InventoryEntry> Inventory { get; set; }
        public List<FindingRecord> Findings { get; set; }
        public List<ActionRecord> Actions { get; set; }
        public ReportConclusion Conclusion { get; set; }

        public ReportDocument()
        {
            SchemaVersion = "1.0";
            Inventory = new List<InventoryEntry>();
            Findings = new List<FindingRecord>();
            Actions = new List<ActionRecord>();
        }
    }

    public sealed class AuthorizationSummary
    {
        public string AppId { get; set; }
        public string State { get; set; }
        public bool CanRepair { get; set; }
        public bool CanInstall { get; set; }
        public bool CanUpgrade { get; set; }
        public int OfflineGraceDays { get; set; }
    }

    public sealed class ScanRequest
    {
        public string StartedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string Mode { get; set; }
        public string ErrorCode { get; set; }
        public string TargetExecutable { get; set; }
        public string TargetExecutableSha256 { get; set; }
        public string Notes { get; set; }
    }

    public sealed class EnvironmentSnapshot
    {
        public string ProductName { get; set; }
        public string DisplayVersion { get; set; }
        public string Build { get; set; }
        public string Architecture { get; set; }
        public string Runtime { get; set; }
        public string UiCulture { get; set; }
        public bool IsAdministrator { get; set; }
        public string DotNetV4Release { get; set; }
        public string DotNet35State { get; set; }
        public Dictionary<string, bool> Capabilities { get; set; }

        public EnvironmentSnapshot()
        {
            Capabilities = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class InventoryEntry
    {
        public string ComponentId { get; set; }
        public string Category { get; set; }
        public string DisplayName { get; set; }
        public string Architecture { get; set; }
        public string DetectedVersion { get; set; }
        public string ExpectedVersion { get; set; }
        public string SourceUrl { get; set; }
        public string PackageFile { get; set; }
        public string Sha256 { get; set; }
        public string SignatureStatus { get; set; }
        public string Compatibility { get; set; }
        public string Status { get; set; }
        public string EvidenceId { get; set; }
        public string Details { get; set; }
        /// <summary>Whether this entry has a product-controlled repair route. A false value is fail-closed.</summary>
        public bool RepairSupported { get; set; }
        /// <summary>Allowed source policy for a repair route (for example official-microsoft-only).</summary>
        public string RepairSourcePolicy { get; set; }
        /// <summary>Stable action name used by the repair preview and structured report.</summary>
        public string RepairAction { get; set; }
    }

    public sealed class FindingRecord
    {
        public string FindingId { get; set; }
        public string Code { get; set; }
        public string Severity { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public List<string> EvidenceIds { get; set; }
        public List<string> RootCauseCandidates { get; set; }
        public List<string> SuggestedActions { get; set; }
        public bool BlocksRepair { get; set; }

        public FindingRecord()
        {
            EvidenceIds = new List<string>();
            RootCauseCandidates = new List<string>();
            SuggestedActions = new List<string>();
        }
    }

    public sealed class ActionRecord
    {
        public string ActionId { get; set; }
        public string Type { get; set; }
        public string ComponentId { get; set; }
        public string EvidenceId { get; set; }
        public string StartedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string Permission { get; set; }
        public int? ExitCode { get; set; }
        public string Reboot { get; set; }
        public string Result { get; set; }
        public string CommandSummary { get; set; }
        public List<string> EvidenceIds { get; set; }
        public string SourcePolicy { get; set; }
        public bool? AutomaticExecutionAllowed { get; set; }

        public ActionRecord()
        {
            EvidenceIds = new List<string>();
        }
    }

    public sealed class ReportConclusion
    {
        public string Status { get; set; }
        public int FindingCount { get; set; }
        public int BlockingFindingCount { get; set; }
        public int RepairableCount { get; set; }
        public string Summary { get; set; }
    }

    /// <summary>Raw local evidence captured from a log source. Content is always redacted before writing.</summary>
    public sealed class DiagnosticLogEntry
    {
        public string EvidenceId { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public string CapturedUtc { get; set; }
        public string Content { get; set; }
    }

    public sealed class ReportPackageResult
    {
        public string DirectoryPath { get; internal set; }
        public string ZipPath { get; internal set; }
        public string ReportId { get; internal set; }
        public int FileCount { get; internal set; }
    }
}
