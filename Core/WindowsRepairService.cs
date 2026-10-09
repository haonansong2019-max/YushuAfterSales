using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.Core
{
    public enum SystemRepairAction
    {
        SfcScannow,
        DismRestoreHealth,
        EnableDotNet35
    }

    public sealed class RepairPlanItem
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Operation { get; set; }
        public string Permission { get; set; }
        public bool RequiresElevation { get; set; }
        public string Reason { get; set; }
        public string OfficialUrl { get; set; }
        public string SourcePolicy { get; set; }
        public bool RepairSupported { get; set; }
        public string RepairAction { get; set; }
    }

    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
        public bool Started { get; set; }
        public string RestorePointStatus { get; set; }
        public string Action { get; set; }
        public string LogPath { get; set; }
        public string CommandSummary { get; set; }
    }

    public static class WindowsRepairService
    {
        public static ReportDocument Scan(string applicationVersion)
        {
            var report = ReportBuilder.Create(applicationVersion);
            report.Scan.StartedUtc = DateTime.UtcNow.ToString("o");
            report.Inventory = ReportBuilder.CollectInventory();
            report.Findings = BuildFindings(report.Inventory);
            report.Scan.CompletedUtc = DateTime.UtcNow.ToString("o");
            report.Conclusion = new ReportConclusion
            {
                Status = "scanned",
                FindingCount = report.Findings.Count,
                BlockingFindingCount = report.Findings.Count(x => x.BlocksRepair),
                RepairableCount = report.Findings.Count(x => !x.BlocksRepair),
                Summary = report.Findings.Count == 0 ? "未发现需要处理的运行库证据异常。" : "发现 " + report.Findings.Count + " 个需要复核的项目；扫描未修改系统。"
            };
            return report;
        }

        public static List<FindingRecord> BuildFindings(IEnumerable<InventoryEntry> inventory)
        {
            var findings = new List<FindingRecord>();
            if (inventory == null) return findings;

            foreach (InventoryEntry item in inventory.Where(x => x != null &&
                (x.Status == "missing" || x.Status == "missing-or-unknown" || x.Status == "unknown" || x.Status == "outdated")))
            {
                bool outdated = String.Equals(item.Status, "outdated", StringComparison.OrdinalIgnoreCase);
                findings.Add(new FindingRecord
                {
                    FindingId = "finding-" + item.EvidenceId,
                    Code = outdated ? "RUNTIME-DOTNET-OUTDATED" :
                        item.Category == "directx" ? "RUNTIME-DIRECTX-UNKNOWN" : "RUNTIME-" + (item.Category ?? "runtime").ToUpperInvariant() + "-UNKNOWN",
                    Severity = "warning",
                    Title = outdated ? item.DisplayName + " 低于目标版本" : item.DisplayName + " 需要进一步确认",
                    Message = outdated
                        ? "已检测到该组件，但当前版本低于目标版本 " + (item.ExpectedVersion ?? "") + "。版本检测本身不能确认故障根因，请结合目标程序错误和关联证据复核。"
                        : "检测不到完整安装证据。请先查看来源和兼容性，再决定是否执行修复。",
                    EvidenceIds = String.IsNullOrEmpty(item.EvidenceId) ? new List<string>() : new List<string> { item.EvidenceId },
                    RootCauseCandidates = outdated
                        ? new List<string> { "组件版本低于目标版本", "目标程序另有兼容性或依赖问题" }
                        : new List<string> { "未安装", "注册表信息缺失", "系统版本不兼容" },
                    SuggestedActions = outdated
                        ? new List<string> { "查看微软官方目标版本来源", "结合目标程序错误码复核", "来源校验通过后再升级" }
                        : new List<string> { "打开官方来源", "重新扫描", "在预览后执行受控安装" },
                    BlocksRepair = false
                });
            }
            return findings;
        }

        public static List<RepairPlanItem> BuildPlan(ReportDocument report)
        {
            var plan = new List<RepairPlanItem>();
            if (report == null || report.Inventory == null) return plan;
            foreach (InventoryEntry item in report.Inventory.Where(x => x.Status == "missing" || x.Status == "missing-or-unknown" || x.Status == "unknown" || x.Status == "outdated"))
            {
                RuntimePackage package = RuntimeCatalog.Find(item.ComponentId);
                if (package == null && item.ComponentId == "dotnet-framework-4-full")
                    package = RuntimeCatalog.Find("dotnet-48");
                string sourceUrl = String.IsNullOrEmpty(item.SourceUrl) ? package == null ? String.Empty : package.OfficialUrl : item.SourceUrl;
                bool repairSupported = item.RepairSupported && !String.IsNullOrEmpty(sourceUrl);
                plan.Add(new RepairPlanItem
                {
                    Id = item.ComponentId,
                    DisplayName = item.DisplayName,
                    Operation = item.Status == "outdated"
                        ? "打开官方来源；升级包接入受信哈希和签名清单后方可自动安装"
                        : repairSupported ? "打开官方来源并人工复核安装包；当前不会自动覆盖文件" : "仅生成诊断预览；没有受信自动修复路线",
                    Permission = "用户浏览器操作",
                    RequiresElevation = false,
                    Reason = item.Status,
                    OfficialUrl = sourceUrl,
                    SourcePolicy = String.IsNullOrEmpty(item.RepairSourcePolicy) ? "official-source-review-only" : item.RepairSourcePolicy,
                    RepairSupported = repairSupported,
                    RepairAction = String.IsNullOrEmpty(item.RepairAction) ? "manual-review" : item.RepairAction
                });
            }
            return plan;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static Task<ProcessResult> RunSystemRepairAsync(SystemRepairAction action, bool userConfirmed)
        {
            return Task.Run(() => RunSystemRepair(action, userConfirmed));
        }

        public static ProcessResult RunSystemRepair(SystemRepairAction action, bool userConfirmed)
        {
            if (!userConfirmed) throw new InvalidOperationException("系统修复必须由调用方在预览后取得用户明确确认。" );
            if (!Enum.IsDefined(typeof(SystemRepairAction), action)) throw new ArgumentOutOfRangeException("action");
            if (action == SystemRepairAction.EnableDotNet35 && !IsSystemActionSupported(action, Environment.OSVersion.Version))
                return new ProcessResult
                {
                    Started = false,
                    ExitCode = 50,
                    Action = action.ToString(),
                    RestorePointStatus = "not-attempted",
                    StandardOutput = String.Empty,
                    StandardError = "Windows 7 不支持 Enable-WindowsOptionalFeature；未执行修复。",
                    CommandSummary = "not-supported-on-windows-7"
                };

            string logPath = CreateLogPath(action);
            string script = BuildPowerShellScript(action, logPath);
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var result = new ProcessResult
            {
                Action = action.ToString(),
                LogPath = SensitiveDataRedactor.RedactPath(logPath),
                CommandSummary = "powershell.exe -NoProfile -NonInteractive -EncodedCommand <redacted> (runas)",
                RestorePointStatus = "unknown",
                StandardOutput = String.Empty,
                StandardError = String.Empty
            };

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                        UseShellExecute = true,
                        Verb = "runas",
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                    };
                    process.Start();
                    result.Started = true;
                    process.WaitForExit();
                    result.ExitCode = process.ExitCode;
                }

                string text = ReadBoundedLog(logPath, 1024 * 1024);
                result.StandardOutput = SensitiveDataRedactor.Redact(text);
                result.StandardError = ExtractError(text);
                result.RestorePointStatus = ExtractMarker(text, "YSRESTORE") ?? "unknown";
                // Rewrite the persisted evidence after redaction; never retain raw command output.
                File.WriteAllText(logPath, result.StandardOutput, new UTF8Encoding(false));
                return result;
            }
            catch (Exception ex)
            {
                result.Started = false;
                var win32 = ex as System.ComponentModel.Win32Exception;
                result.ExitCode = win32 != null && win32.NativeErrorCode == 1223 ? 1223 : -1;
                result.StandardError = SensitiveDataRedactor.Redact(ex.Message);
                result.RestorePointStatus = "unknown";
                WriteLocalFailureLog(logPath, result.StandardError);
                return result;
            }
        }

        public static ProcessResult RunWingetInstall(RuntimePackage package)
        {
            throw new InvalidOperationException("自动安装已关闭：尚未配置与产品绑定的可信 SHA-256/签名清单。请使用应用展示的官方来源手动选择安装包。\n" +
                (package == null ? String.Empty : package.DisplayName));
        }

        private static string BuildPowerShellScript(SystemRepairAction action, string logPath)
        {
            string body;
            switch (action)
            {
                case SystemRepairAction.SfcScannow:
                    body = "& ($env:windir + '\\System32\\sfc.exe') /scannow 2>&1 | ForEach-Object { $events.Add([string]$_) }; $actionExit=$LASTEXITCODE;";
                    break;
                case SystemRepairAction.DismRestoreHealth:
                    body = "& ($env:windir + '\\System32\\dism.exe') /Online /Cleanup-Image /RestoreHealth 2>&1 | ForEach-Object { $events.Add([string]$_) }; $actionExit=$LASTEXITCODE;";
                    break;
                case SystemRepairAction.EnableDotNet35:
                    body = "Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All -NoRestart -ErrorAction Stop 2>&1 | ForEach-Object { $events.Add([string]$_) }; $actionExit=0;";
                    break;
                default:
                    throw new ArgumentOutOfRangeException("action");
            }

            string safePath = EscapePowerShellSingleQuoted(logPath);
            return "$ErrorActionPreference='Stop';$events=New-Object System.Collections.Generic.List[string];$actionExit=7401;$restoreStatus='failed';$actionStatus='not-run';" +
                "try { Checkpoint-Computer -Description 'YushuAfterSales repair checkpoint' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; $restoreStatus='created'; } " +
                "catch { $events.Add('YSERROR=' + [string]$_.Exception.Message); } " +
                "if ($restoreStatus -eq 'created') { try { " + body +
                "$actionStatus=if($actionExit -eq 0){'completed'}else{'failed'}; } catch { $actionStatus='failed'; $actionExit=7402; $events.Add('YSACTIONERROR=' + [string]$_.Exception.Message); } } " +
                "$events.Add('YSRESTORE=' + $restoreStatus);$events.Add('YSACTION=" + action.ToString() + "');$events.Add('YSACTIONSTATUS=' + $actionStatus);$events.Add('YSEXIT=' + [string]$actionExit);" +
                "try { $text=$events -join [Environment]::NewLine; $text=$text -replace '(?i)[A-Z]:\\Users\\[^\\]+' ,'%USERPROFILE%'; " +
                "$text=$text -replace '(?im)(machineguid|serial(?:number)?|password|token|api[-_]?key)\\s*[:=]\\s*[^\\r\\n]+','$1=<REDACTED>'; " +
                "[IO.File]::WriteAllText('" + safePath + "',$text,[Text.Encoding]::UTF8) } catch {}; exit $actionExit";
        }

        public static bool IsSystemActionSupported(SystemRepairAction action, Version operatingSystemVersion)
        {
            if (!Enum.IsDefined(typeof(SystemRepairAction), action)) return false;
            if (operatingSystemVersion == null) return false;
            if (action == SystemRepairAction.EnableDotNet35)
                return operatingSystemVersion.Major > 6 || (operatingSystemVersion.Major == 6 && operatingSystemVersion.Minor >= 2);
            return true;
        }

        private static string CreateLogPath(SystemRepairAction action)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "Logs");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, action.ToString() + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".log");
        }

        private static string ReadBoundedLog(string path, int maximumCharacters)
        {
            if (!File.Exists(path)) return String.Empty;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                char[] buffer = new char[maximumCharacters];
                int count = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, count);
            }
        }

        private static string ExtractMarker(string text, string marker)
        {
            if (String.IsNullOrEmpty(text)) return null;
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith(marker + "=", StringComparison.OrdinalIgnoreCase)) return line.Substring(marker.Length + 1).Trim();
            return null;
        }

        private static string ExtractError(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return String.Empty;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.StartsWith("YSERROR=", StringComparison.OrdinalIgnoreCase)).ToArray();
            return SensitiveDataRedactor.Redact(String.Join(Environment.NewLine, lines));
        }

        private static void WriteLocalFailureLog(string path, string message)
        {
            try { File.WriteAllText(path, SensitiveDataRedactor.Redact(message ?? String.Empty), new UTF8Encoding(false)); } catch { }
        }

        private static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? String.Empty).Replace("'", "''");
        }

        private static bool IsWindows7OrEarlier()
        {
            Version version = Environment.OSVersion.Version;
            return version.Major < 6 || (version.Major == 6 && version.Minor <= 1);
        }
    }
}
