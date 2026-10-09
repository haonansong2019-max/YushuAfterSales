using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using YushuAfterSales.Core;

namespace YushuAfterSales.Reporting
{
    /// <summary>Minimal dependency-free smoke test for CI or a developer build.</summary>
    public static class ReportEngineSelfTest
    {
        public static void Run()
        {
            Require(ReportBuilder.ClassifyDirectXLegacyStatus("4.09.00.0904") == "unknown", "DirectX platform version is not June 2010 DLL evidence");
            Require(ReportBuilder.ClassifyVcRuntimeStatus(false, null) == "unknown", "absent VC runtime key is not confirmed missing");
            Require(ReportBuilder.ClassifyVcRuntimeStatus(true, "0") == "unknown", "VC Installed=0 is not confirmed missing");
            Require(ReportBuilder.ClassifyVcRuntimeStatus(true, null) == "unknown", "VC missing Installed value is unknown");
            Require(ReportBuilder.ClassifyVcRuntimeStatus(true, "1") == "installed", "VC Installed=1 is positive evidence");
            Require(ReportBuilder.ClassifyDotNetFramework4Status(false, true, null) == "missing", ".NET v4 key absence is missing");
            Require(ReportBuilder.ClassifyDotNetFramework4Status(true, false, null) == "unknown", ".NET v4 registry read failure is unknown");
            Require(ReportBuilder.ClassifyDotNetFramework4Status(true, true, "378389") == "outdated", ".NET 4.5 is outdated versus 4.8 target");
            Require(ReportBuilder.ClassifyDotNetFramework4Status(true, true, "528040") == "installed", ".NET 4.8 minimum release is installed");
            Require(ReportBuilder.ClassifyDotNetFramework4Status(true, true, null) == "unknown", ".NET missing release value is unknown");
            Require(ReportBuilder.ClassifyDotNet35Status(false, true, null) == "missing", ".NET 3.5 key absence is missing");
            Require(ReportBuilder.ClassifyDotNet35Status(true, true, "0") == "missing", ".NET 3.5 explicit Install=0 is missing");
            Require(ReportBuilder.ClassifyDotNet35Status(true, true, "1") == "installed", ".NET 3.5 Install=1 is installed");
            Require(ReportBuilder.ClassifyDotNet35Status(true, true, null) == "unknown", ".NET 3.5 missing Install value is unknown");
            int scanningProgress = 0, completedProgress = 0, reportedTotal = 0;
            List<InventoryEntry> inventory = ComponentScanService.CollectInventory(progress =>
            {
                reportedTotal = progress.Total;
                if (progress.Stage == "scanning") { scanningProgress++; Require(progress.Entry == null, "pre-probe progress contains no fabricated result"); }
                if (progress.Stage == "complete") { completedProgress++; Require(progress.Entry != null && progress.Current == completedProgress, "completed progress carries each real probe result in order"); }
            }, CancellationToken.None);
            Require(scanningProgress == inventory.Count && completedProgress == inventory.Count && reportedTotal == inventory.Count, "real per-component scan progress covers inventory");
            Require(!inventory.Any(x => x.ComponentId == "directx-legacy"), "DirectX platform registry is not treated as a legacy package inventory result");
            Require(inventory.Select(x => x.EvidenceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == inventory.Count, "each component has a unique evidence ID");
            Require(inventory.Select(x => x.ComponentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == inventory.Count, "catalog and detected runtime do not create duplicate rows");
            Require(inventory.Any(x => x.Category == "directx-legacy-file" && x.DisplayName == "D3DX9_43.dll"), "DirectX legacy DLL evidence is collected separately from platform version");
            Require(inventory.Any(x => x.Category == "dll" && x.DisplayName == "VCRUNTIME140.dll"), "common DLL evidence is present for the DLL repair page");
            List<InventoryEntry> dllEvidence = ReportBuilder.CollectDllEvidence(new[] { "VCRUNTIME140.dll" }, null, "x64");
            Require(dllEvidence.Count == 1 && dllEvidence[0].Category == "dll" && dllEvidence[0].EvidenceId.Contains("vcruntime140"), "DLL evidence has stable category and evidence ID");
            bool dllPathRejected = false;
            try { ReportBuilder.CollectDllEvidence(new[] { "..\\secret.dll" }, null, "x64"); }
            catch (ArgumentException) { dllPathRejected = true; }
            Require(dllPathRejected, "DLL probe rejects path traversal and only accepts basenames");
            Require(ComponentScanService.GetDllRepairPackage("api-ms-win-crt-runtime-l1-1-0.dll", "x64") == "system:sfc", "known UCRT API contract maps to system repair");
            Require(ComponentScanService.GetDllRepairPackage("api-ms-win-crt-invented-l1-1-0.dll", "x64") == "", "unrecognized API-set name is never declared present or repairable by prefix");
            foreach (string family in new[] { "2005", "2008", "2010", "2012", "2013", "2015plus" })
            foreach (string architecture in new[] { "x86", "x64" })
                Require(inventory.Count(x => x.ComponentId == "vc-" + family + "-" + architecture) == 1, "complete VC family/architecture inventory: " + family + "/" + architecture);
            Require(inventory.Where(x => x.ComponentId.StartsWith("vc-", StringComparison.Ordinal)).All(x => new[] { "installed", "outdated", "missing", "unknown", "unsupported" }.Contains(x.Status)), "VC inventory uses explicit evidence states");
            Require(inventory.Where(x => x.ComponentId == "dotnet-48" || x.ComponentId == "dotnet-35").All(x => x.Status == "installed" || x.Status == "outdated" || x.Status == "missing" || x.Status == "unknown"), ".NET inventory uses explicit evidence states");
            Require(inventory.Single(x => x.ComponentId == "xna-40").Status == "unsupported" && !inventory.Single(x => x.ComponentId == "msxml-4").RepairSupported, "end-of-support components are explicit and not auto-installed");
            VerifyCancellation();
            VerifyDriverGuards();

            var outdatedFramework = new InventoryEntry
            {
                ComponentId = "dotnet-48",
                Category = "dotnet",
                DisplayName = ".NET Framework 4.x Full",
                ExpectedVersion = "4.8",
                Status = "outdated",
                EvidenceId = "evidence-dotnet-48", SourceUrl = RuntimeCatalog.Find("dotnet-48").OfficialUrl,
                RepairSupported = true, RepairAction = "package:dotnet-48", RepairSourcePolicy = "official-microsoft-only"
            };
            List<FindingRecord> outdatedFindings = WindowsRepairService.BuildFindings(new[] { outdatedFramework });
            Require(outdatedFindings.Count == 1 && outdatedFindings[0].Code == "RUNTIME-DOTNET-OUTDATED", ".NET 4.x outdated state creates a specific finding");
            Require(outdatedFindings[0].EvidenceIds.Contains(outdatedFramework.EvidenceId), ".NET outdated finding links registry evidence");
            Require(outdatedFindings[0].Message.Contains("4.8"), ".NET outdated finding names target version");
            List<ComponentRepairPlanItem> outdatedPlan = ComponentRepairService.BuildPlanForEnvironment(new[] { outdatedFramework }, new Version(6, 1, 7601), true);
            Require(outdatedPlan.Count == 1 && outdatedPlan[0].PackageId == "dotnet-48" && outdatedPlan[0].Supported, ".NET 4.x outdated state enters a real compatible repair plan");
            Require(outdatedPlan[0].ExpectedSha256.Length == 64 && outdatedPlan[0].DownloadUrl.IndexOf("download.microsoft.com", StringComparison.OrdinalIgnoreCase) >= 0 && outdatedPlan[0].EvidenceIds.Contains(outdatedFramework.EvidenceId), ".NET real repair plan includes official fixed package, SHA256 and component evidence");

            string root = Path.Combine(Path.GetTempPath(), "ysrepair-report-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                ReportDocument report = ReportBuilder.Create("self-test");
                report.Inventory.Add(outdatedFramework);
                report.Findings.Add(new FindingRecord
                {
                    FindingId = "self-finding",
                    Code = "SELF_TEST",
                    Severity = "info",
                    Message = "C:\\Users\\Test\\sample.txt"
                });
                report.Actions.Add(new ActionRecord
                {
                    ActionId = "self-action",
                    Type = "scan",
                    Result = "ok",
                    CommandSummary = "token=must-be-redacted", SourcePolicy = "official-microsoft-only", AutomaticExecutionAllowed = false
                });
                ReportPackageResult result = ReportWriter.WritePackage(report, root, new[]
                {
                    new DiagnosticLogEntry
                    {
                        Name = "self-test",
                        Source = "self-test",
                        CapturedUtc = DateTime.UtcNow.ToString("o"),
                        Content = "MachineGuid=must-be-redacted\r\nC:\\Users\\Test\\sample.dll"
                    }
                }, true);

                Require(File.Exists(Path.Combine(result.DirectoryPath, "report.json")), "report.json");
                Require(File.Exists(Path.Combine(result.DirectoryPath, "inventory.json")), "inventory.json");
                Require(File.Exists(Path.Combine(result.DirectoryPath, "actions.jsonl")), "actions.jsonl");
                Require(File.Exists(Path.Combine(result.DirectoryPath, "findings.json")), "findings.json");
                Require(File.Exists(Path.Combine(result.DirectoryPath, "environment.json")), "environment.json");
                Require(File.Exists(Path.Combine(result.DirectoryPath, "files.json")), "files.json");
                Require(File.Exists(Path.Combine(result.DirectoryPath, "README.html")), "README.html");
                Require(File.Exists(result.ZipPath), "report ZIP");
                string json = File.ReadAllText(Path.Combine(result.DirectoryPath, "report.json"));
                Require(json.IndexOf("token=must-be-redacted", StringComparison.OrdinalIgnoreCase) < 0, "report secret redaction");
                ReportDocument roundTrip = ReportJson.DeserializeReport(json);
                Require(roundTrip.ReportId == report.ReportId, "report round-trip ID");
                Require(roundTrip.Inventory.Count == report.Inventory.Count, "report round-trip inventory");
                Require(roundTrip.Findings.Count == report.Findings.Count, "report round-trip findings");
                Require(roundTrip.Actions.Count == report.Actions.Count, "report round-trip actions");
                Require(roundTrip.Actions[0].ActionId == "self-action", "report round-trip nested action fields");
                Require(roundTrip.Inventory[0].RepairSupported && roundTrip.Inventory[0].RepairAction == "package:dotnet-48" && roundTrip.Inventory[0].RepairSourcePolicy == "official-microsoft-only", "report redaction preserves component repair policy and action");
                Require(roundTrip.Actions[0].SourcePolicy == "official-microsoft-only" && roundTrip.Actions[0].AutomaticExecutionAllowed == false, "report redaction preserves action source and execution policy");
                string log = File.ReadAllText(Path.Combine(result.DirectoryPath, "logs", "000_self-test.log"));
                Require(log.IndexOf("MachineGuid=must-be-redacted", StringComparison.OrdinalIgnoreCase) < 0, "log secret redaction");

                VerifyHistoryLogs();
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort temp cleanup */ }
            }
        }

        private static void VerifyCancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                int completed = 0;
                bool cancelled = false;
                try
                {
                    ComponentScanService.Scan("cancel-self-test", progress =>
                    {
                        if (progress.Stage == "complete") { completed++; cancellation.Cancel(); }
                    }, cancellation.Token);
                }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && completed == 1, "cancelling after a real probe does not continue or return a finished report");
            }
        }

        private static void VerifyDriverGuards()
        {
            string valid = "b91a5425-4c45-4a0b-95d7-cde340ac876e:1";
            Require(DriverUpdateService.IsValidUpdateId(valid), "driver update identity accepts GUID and revision");
            Require(!DriverUpdateService.IsValidUpdateId("https://example.invalid/driver.exe"), "driver update identity rejects arbitrary URL");
            Require(!DriverUpdateService.IsValidUpdateId("b91a5425-4c45-4a0b-95d7-cde340ac876e:1;Install"), "driver update identity rejects command suffix");
            Require(!DriverUpdateService.IsValidUpdateId(valid + "\n"), "driver update identity rejects a trailing newline");
            bool installConfirmationRejected = false;
            try { DriverUpdateService.InstallAsync(new[] { valid }, false, null, CancellationToken.None); }
            catch (InvalidOperationException) { installConfirmationRejected = true; }
            Require(installConfirmationRejected, "driver installation requires explicit confirmation before authorization or UAC");
            bool updateServiceConfirmationRejected = false;
            try { DriverUpdateService.EnableUpdateServiceAsync(false, CancellationToken.None); }
            catch (InvalidOperationException) { updateServiceConfirmationRejected = true; }
            Require(updateServiceConfirmationRejected, "Windows Update service enable requires explicit confirmation");
            MethodInfo scanBuilder = typeof(DriverUpdateService).GetMethod("BuildScanScript", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo installBuilder = typeof(DriverUpdateService).GetMethod("BuildInstallScript", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo enableBuilder = typeof(DriverUpdateService).GetMethod("BuildEnableServiceScript", BindingFlags.NonPublic | BindingFlags.Static);
            string task = Path.Combine(Path.GetTempPath(), "ysrepair-driver-parser-" + Guid.NewGuid().ToString("N"));
            string result = Path.Combine(task, "result.json"), progress = Path.Combine(task, "progress.log"), cancel = Path.Combine(task, "cancel");
            VerifyPowerShellParser((string)scanBuilder.Invoke(null, null), "driver scan");
            VerifyPowerShellParser((string)installBuilder.Invoke(null, new object[] { new[] { valid }, result, progress, cancel }), "driver install");
            VerifyPowerShellParser((string)enableBuilder.Invoke(null, new object[] { result, progress, cancel }), "Windows Update service enable");
        }

        private static void VerifyPowerShellParser(string generatedScript, string name)
        {
            Require(!String.IsNullOrWhiteSpace(generatedScript), name + " generates a nonempty broker script");
            string parser = "$text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(generatedScript)) +
                "'));$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors);" +
                "if($errors.Count -gt 0){$errors|ForEach-Object{Write-Output $_.Message};exit 1};exit 0";
            // Only parse the generated source. None of the driver, download, install, service, or restore-point commands run.
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(parser)),
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new InvalidOperationException(name + " parser timed out"); }
                Require(process.ExitCode == 0, name + " PowerShell source parses: " + SensitiveDataRedactor.Redact(output.Result + error.Result));
            }
        }

        private static void VerifyHistoryLogs()
        {
            ReportDocument report = ReportBuilder.Create("history-self-test");
            string root = Path.Combine(Path.GetTempPath(), "ysrepair-history-selftest-" + Guid.NewGuid().ToString("N"));
            string reportDirectory = ReportWriter.PackageDirectory(root, report.ReportId);
            try
            {
                ReportHistoryStore.SaveTo(root, report, new[]
                {
                    new DiagnosticLogEntry { Name = "old.log", Source = "test", CapturedUtc = "2026-10-09T00:00:00Z", Content = "stale-log" },
                    new DiagnosticLogEntry { Name = "new", Source = "history-test", CapturedUtc = "2026-10-09T00:00:01Z", EvidenceId = "evidence-history", Content = "current-log" }
                });
                Require(ReportHistoryStore.LoadFrom(root, report.ReportId) != null, "history report loaded by ID");
                Require(ReportHistoryStore.LoadLogsFrom(root, report.ReportId).Count == 2, "history logs saved and loaded");

                ReportHistoryStore.SaveTo(root, report, ReportHistoryStore.LoadLogsFrom(root, report.ReportId));
                Require(ReportHistoryStore.LoadLogsFrom(root, report.ReportId).Count == 2, "saving report metadata preserves stored logs");

                ReportHistoryStore.SaveTo(root, report, new[]
                {
                    new DiagnosticLogEntry { Name = "current.log", Source = "history-test", CapturedUtc = "2026-10-09T00:00:02Z", Content = "replacement-log" }
                });
                List<DiagnosticLogEntry> loadedLogs = ReportHistoryStore.LoadLogsFrom(root, report.ReportId);
                Require(loadedLogs.Count == 1, "re-saving a report removes stale logs");
                Require(loadedLogs[0].Name == "current.log" && loadedLogs[0].Content == "replacement-log", "history log content and name restored");

                bool traversalRejected = false;
                try { ReportHistoryStore.LoadLogsFrom(root, "..\\outside"); }
                catch (ArgumentException) { traversalRejected = true; }
                Require(traversalRejected, "history rejects unsafe report IDs");
            }
            finally
            {
                try { if (Directory.Exists(reportDirectory)) Directory.Delete(reportDirectory, true); } catch { /* best effort test cleanup */ }
            }
        }

        private static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Report engine self-test failed: " + name);
        }
    }
}
