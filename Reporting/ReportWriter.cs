using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;

namespace YushuAfterSales.Reporting
{
    /// <summary>Writes a local, shareable report package. No network or repair operation is performed here.</summary>
    public static class ReportWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static ReportPackageResult WritePackage(
            ReportDocument report,
            string reportsDirectory,
            IEnumerable<DiagnosticLogEntry> logs,
            bool createZip)
        {
            if (report == null) throw new ArgumentNullException("report");
            if (String.IsNullOrWhiteSpace(reportsDirectory)) throw new ArgumentException("A report directory is required.", "reportsDirectory");
            if (String.IsNullOrWhiteSpace(report.ReportId)) report.ReportId = Guid.NewGuid().ToString("N");
            ReportDocument safeReport = SanitizeReport(report);
            safeReport.ReportId = NormalizeReportId(safeReport.ReportId);

            string packageDirectory = PackageDirectory(reportsDirectory, safeReport.ReportId);
            if (Directory.Exists(packageDirectory) && IsReparsePoint(packageDirectory))
                throw new IOException("Report history package path cannot be a reparse point.");
            Directory.CreateDirectory(packageDirectory);
            string logDirectory = Path.Combine(packageDirectory, "logs");
            if (Directory.Exists(logDirectory) && IsReparsePoint(logDirectory))
                throw new IOException("Report logs path cannot be a reparse point.");
            Directory.CreateDirectory(logDirectory);
            foreach (string oldLog in Directory.GetFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly))
                File.Delete(oldLog);

            WriteText(Path.Combine(packageDirectory, "report.json"), ReportJson.Serialize(safeReport));
            WriteText(Path.Combine(packageDirectory, "inventory.json"), ReportJson.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", safeReport.SchemaVersion }, { "reportId", safeReport.ReportId }, { "items", safeReport.Inventory ?? new List<InventoryEntry>() }
            }));
            WriteText(Path.Combine(packageDirectory, "environment.json"), ReportJson.Serialize(safeReport.Environment ?? new EnvironmentSnapshot()));
            WriteText(Path.Combine(packageDirectory, "findings.json"), ReportJson.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", safeReport.SchemaVersion }, { "reportId", safeReport.ReportId }, { "items", safeReport.Findings ?? new List<FindingRecord>() }
            }));

            string actionsPath = Path.Combine(packageDirectory, "actions.jsonl");
            var actionLines = new StringBuilder();
            if (safeReport.Actions != null)
            {
                foreach (ActionRecord action in safeReport.Actions)
                    actionLines.AppendLine(ReportJson.SerializeCompact(action));
            }
            WriteText(actionsPath, actionLines.ToString());

            var writtenLogs = new List<string>();
            if (logs != null)
            {
                int index = 0;
                foreach (DiagnosticLogEntry log in logs)
                {
                    if (log == null) continue;
                    string baseName = SafeFileName(String.IsNullOrWhiteSpace(log.Name) ? "application.log" : log.Name);
                    if (!baseName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) baseName += ".log";
                    string fileName = String.Format(CultureInfo.InvariantCulture, "{0:000}_{1}", index++, baseName);
                    string path = Path.Combine(logDirectory, fileName);
                    var content = new StringBuilder();
                    if (!String.IsNullOrWhiteSpace(log.EvidenceId)) content.AppendLine("# evidenceId: " + SensitiveDataRedactor.Redact(log.EvidenceId));
                    content.AppendLine("# source: " + SensitiveDataRedactor.Redact(log.Source ?? String.Empty));
                    content.AppendLine("# capturedUtc: " + SensitiveDataRedactor.Redact(log.CapturedUtc ?? String.Empty));
                    content.Append(SensitiveDataRedactor.Redact(log.Content ?? String.Empty));
                    WriteText(path, content.ToString());
                    writtenLogs.Add("logs/" + fileName);
                }
            }
            WriteText(Path.Combine(logDirectory, "index.json"), ReportJson.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", safeReport.SchemaVersion }, { "reportId", safeReport.ReportId }, { "files", writtenLogs }
            }));
            WriteText(Path.Combine(packageDirectory, "README.html"), BuildReadme(safeReport, writtenLogs));

            var fileEntries = new List<PackageFileEntry>();
            foreach (string file in Directory.GetFiles(packageDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(packageDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                if (String.Equals(relative, "files.json", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                fileEntries.Add(new PackageFileEntry { Path = relative, Size = info.Length, Sha256 = ReportBuilder.ComputeSha256(file) });
            }
            fileEntries = fileEntries.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
            WriteText(Path.Combine(packageDirectory, "files.json"), ReportJson.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", safeReport.SchemaVersion }, { "reportId", safeReport.ReportId }, { "files", fileEntries },
                { "note", "files.json intentionally excludes its own hash" }
            }));

            string zipPath = null;
            if (createZip)
            {
                zipPath = Path.Combine(Path.GetDirectoryName(packageDirectory), safeReport.ReportId + ".zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(packageDirectory, zipPath, CompressionLevel.Optimal, false);
            }
            return new ReportPackageResult
            {
                DirectoryPath = packageDirectory,
                ZipPath = zipPath,
                ReportId = safeReport.ReportId,
                FileCount = fileEntries.Count + 1
            };
        }

        private static void WriteText(string path, string content)
        {
            File.WriteAllText(path, content ?? String.Empty, Utf8NoBom);
        }

        internal static string PackageDirectory(string root, string reportId)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("A report directory is required.", "root");
            string safeId = NormalizeReportId(reportId);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string package = Path.GetFullPath(Path.Combine(fullRoot, safeId));
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            if (!package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Report ID resolves outside the report directory.", "reportId");
            return package;
        }

        private static string NormalizeReportId(string reportId)
        {
            Guid parsed;
            if (!Guid.TryParseExact(reportId, "N", out parsed))
                throw new ArgumentException("Report ID must be a 32-character hexadecimal identifier.", "reportId");
            return parsed.ToString("N");
        }

        private static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static ReportDocument SanitizeReport(ReportDocument source)
        {
            var target = new ReportDocument
            {
                SchemaVersion = SensitiveDataRedactor.Redact(source.SchemaVersion),
                ReportId = SensitiveDataRedactor.Redact(source.ReportId),
                CreatedUtc = SensitiveDataRedactor.Redact(source.CreatedUtc),
                ApplicationVersion = SensitiveDataRedactor.Redact(source.ApplicationVersion),
                Authorization = source.Authorization == null ? null : new AuthorizationSummary
                {
                    AppId = SensitiveDataRedactor.Redact(source.Authorization.AppId),
                    State = SensitiveDataRedactor.Redact(source.Authorization.State),
                    CanRepair = source.Authorization.CanRepair,
                    CanInstall = source.Authorization.CanInstall,
                    CanUpgrade = source.Authorization.CanUpgrade,
                    OfflineGraceDays = source.Authorization.OfflineGraceDays
                },
                Scan = source.Scan == null ? null : new ScanRequest
                {
                    StartedUtc = SensitiveDataRedactor.Redact(source.Scan.StartedUtc),
                    CompletedUtc = SensitiveDataRedactor.Redact(source.Scan.CompletedUtc),
                    Mode = SensitiveDataRedactor.Redact(source.Scan.Mode),
                    ErrorCode = SensitiveDataRedactor.Redact(source.Scan.ErrorCode),
                    TargetExecutable = SensitiveDataRedactor.RedactPath(source.Scan.TargetExecutable),
                    TargetExecutableSha256 = SensitiveDataRedactor.Redact(source.Scan.TargetExecutableSha256),
                    Notes = SensitiveDataRedactor.Redact(source.Scan.Notes)
                },
                Environment = source.Environment == null ? null : new EnvironmentSnapshot
                {
                    ProductName = SensitiveDataRedactor.Redact(source.Environment.ProductName),
                    DisplayVersion = SensitiveDataRedactor.Redact(source.Environment.DisplayVersion),
                    Build = SensitiveDataRedactor.Redact(source.Environment.Build),
                    Architecture = SensitiveDataRedactor.Redact(source.Environment.Architecture),
                    Runtime = SensitiveDataRedactor.Redact(source.Environment.Runtime),
                    UiCulture = SensitiveDataRedactor.Redact(source.Environment.UiCulture),
                    IsAdministrator = source.Environment.IsAdministrator,
                    DotNetV4Release = SensitiveDataRedactor.Redact(source.Environment.DotNetV4Release),
                    DotNet35State = SensitiveDataRedactor.Redact(source.Environment.DotNet35State)
                },
                Conclusion = source.Conclusion == null ? null : new ReportConclusion
                {
                    Status = SensitiveDataRedactor.Redact(source.Conclusion.Status),
                    FindingCount = source.Conclusion.FindingCount,
                    BlockingFindingCount = source.Conclusion.BlockingFindingCount,
                    RepairableCount = source.Conclusion.RepairableCount,
                    Summary = SensitiveDataRedactor.Redact(source.Conclusion.Summary)
                }
            };
            if (target.Environment != null && source.Environment != null)
                foreach (KeyValuePair<string, bool> item in source.Environment.Capabilities ?? new Dictionary<string, bool>())
                    target.Environment.Capabilities[SensitiveDataRedactor.Redact(item.Key)] = item.Value;
            if (source.Inventory != null)
                foreach (InventoryEntry item in source.Inventory)
                    target.Inventory.Add(new InventoryEntry
                    {
                        ComponentId = SensitiveDataRedactor.Redact(item.ComponentId), Category = SensitiveDataRedactor.Redact(item.Category),
                        DisplayName = SensitiveDataRedactor.Redact(item.DisplayName), Architecture = SensitiveDataRedactor.Redact(item.Architecture),
                        DetectedVersion = SensitiveDataRedactor.Redact(item.DetectedVersion), ExpectedVersion = SensitiveDataRedactor.Redact(item.ExpectedVersion),
                        SourceUrl = SensitiveDataRedactor.Redact(item.SourceUrl), PackageFile = SensitiveDataRedactor.RedactPath(item.PackageFile),
                        Sha256 = SensitiveDataRedactor.Redact(item.Sha256), SignatureStatus = SensitiveDataRedactor.Redact(item.SignatureStatus),
                        Compatibility = SensitiveDataRedactor.Redact(item.Compatibility), Status = SensitiveDataRedactor.Redact(item.Status),
                        EvidenceId = SensitiveDataRedactor.Redact(item.EvidenceId), Details = SensitiveDataRedactor.Redact(item.Details)
                    });
            if (source.Findings != null)
                foreach (FindingRecord item in source.Findings)
                    target.Findings.Add(new FindingRecord
                    {
                        FindingId = SensitiveDataRedactor.Redact(item.FindingId), Code = SensitiveDataRedactor.Redact(item.Code),
                        Severity = SensitiveDataRedactor.Redact(item.Severity), Title = SensitiveDataRedactor.Redact(item.Title),
                        Message = SensitiveDataRedactor.Redact(item.Message), BlocksRepair = item.BlocksRepair,
                        EvidenceIds = RedactList(item.EvidenceIds), RootCauseCandidates = RedactList(item.RootCauseCandidates),
                        SuggestedActions = RedactList(item.SuggestedActions)
                    });
            if (source.Actions != null)
                foreach (ActionRecord item in source.Actions)
                    target.Actions.Add(new ActionRecord
                    {
                        ActionId = SensitiveDataRedactor.Redact(item.ActionId), Type = SensitiveDataRedactor.Redact(item.Type),
                        ComponentId = SensitiveDataRedactor.Redact(item.ComponentId), EvidenceId = SensitiveDataRedactor.Redact(item.EvidenceId),
                        StartedUtc = SensitiveDataRedactor.Redact(item.StartedUtc), CompletedUtc = SensitiveDataRedactor.Redact(item.CompletedUtc),
                        Permission = SensitiveDataRedactor.Redact(item.Permission), ExitCode = item.ExitCode,
                        Reboot = SensitiveDataRedactor.Redact(item.Reboot), Result = SensitiveDataRedactor.Redact(item.Result),
                        CommandSummary = SensitiveDataRedactor.Redact(item.CommandSummary), EvidenceIds = RedactList(item.EvidenceIds)
                    });
            return target;
        }

        private static List<string> RedactList(IEnumerable<string> values)
        {
            var output = new List<string>();
            if (values == null) return output;
            foreach (string value in values) output.Add(SensitiveDataRedactor.Redact(value));
            return output;
        }

        private static string SafeFileName(string name)
        {
            var chars = name.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
                if (invalid.Contains(chars[i])) chars[i] = '_';
            string safe = new string(chars).Trim();
            return String.IsNullOrEmpty(safe) ? "application.log" : safe;
        }

        private static string BuildReadme(ReportDocument report, List<string> logs)
        {
            int findings = report.Findings == null ? 0 : report.Findings.Count;
            int actions = report.Actions == null ? 0 : report.Actions.Count;
            var builder = new StringBuilder();
            builder.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>钰叔售后诊断报告</title>");
            builder.AppendLine("<style>body{font-family:Segoe UI,Microsoft YaHei,sans-serif;color:#222;max-width:900px;margin:32px auto;line-height:1.5}table{border-collapse:collapse;width:100%}td,th{padding:7px 9px;border-bottom:1px solid #ddd;text-align:left}code{font-family:Consolas,monospace}small{color:#666}</style></head><body>");
            builder.AppendLine("<h1>钰叔售后诊断报告</h1>");
            builder.AppendLine("<p>报告 ID：<code>" + Html(report.ReportId) + "</code><br>生成时间（UTC）：" + Html(report.CreatedUtc) + "</p>");
            builder.AppendLine("<table><tr><th>项目</th><th>值</th></tr>");
            builder.AppendLine("<tr><td>应用版本</td><td>" + Html(report.ApplicationVersion) + "</td></tr>");
            builder.AppendLine("<tr><td>系统</td><td>" + Html(report.Environment == null ? String.Empty : report.Environment.ProductName) + " " + Html(report.Environment == null ? String.Empty : report.Environment.Build) + "</td></tr>");
            builder.AppendLine("<tr><td>扫描模式</td><td>" + Html(report.Scan == null ? String.Empty : report.Scan.Mode) + "</td></tr>");
            builder.AppendLine("<tr><td>发现项</td><td>" + findings.ToString(CultureInfo.InvariantCulture) + "</td></tr>");
            builder.AppendLine("<tr><td>动作记录</td><td>" + actions.ToString(CultureInfo.InvariantCulture) + "</td></tr></table>");
            builder.AppendLine("<h2>证据索引</h2><p>先查看 <code>report.json</code> 和 <code>findings.json</code>，再按 <code>evidenceId</code> 对照 <code>actions.jsonl</code> 与 logs 目录。</p>");
            if (logs.Count > 0)
            {
                builder.AppendLine("<ul>");
                foreach (string log in logs) builder.AppendLine("<li><code>" + Html(log) + "</code></li>");
                builder.AppendLine("</ul>");
            }
            builder.AppendLine("<p><small>路径、机器标识、授权凭据和令牌已按报告规则脱敏；files.json 的自校验项被排除。</small></p></body></html>");
            return builder.ToString();
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? String.Empty);
        }

        private sealed class PackageFileEntry
        {
            public string Path { get; set; }
            public long Size { get; set; }
            public string Sha256 { get; set; }
        }
    }
}
