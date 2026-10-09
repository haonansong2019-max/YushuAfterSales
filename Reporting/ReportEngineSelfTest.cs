using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            List<InventoryEntry> inventory = ReportBuilder.CollectInventory();
            Require(inventory.Single(x => x.ComponentId == "directx-legacy").Status == "unknown", "DirectX registry is not reported as June 2010 runtime presence");
            Require(inventory.Any(x => x.Category == "directx-legacy-file" && x.DisplayName == "D3DX9_43.dll"), "DirectX legacy DLL evidence is collected separately from platform version");
            Require(inventory.Any(x => x.Category == "dll" && x.DisplayName == "VCRUNTIME140.dll"), "common DLL evidence is present for the DLL repair page");
            List<InventoryEntry> dllEvidence = ReportBuilder.CollectDllEvidence(new[] { "VCRUNTIME140.dll" }, null, "x64");
            Require(dllEvidence.Count == 1 && dllEvidence[0].Category == "dll" && dllEvidence[0].EvidenceId.Contains("vcruntime140"), "DLL evidence has stable category and evidence ID");
            bool dllPathRejected = false;
            try { ReportBuilder.CollectDllEvidence(new[] { "..\\secret.dll" }, null, "x64"); }
            catch (ArgumentException) { dllPathRejected = true; }
            Require(dllPathRejected, "DLL probe rejects path traversal and only accepts basenames");
            Require(inventory.Where(x => x.ComponentId.StartsWith("vc-runtime-2015-2022-", StringComparison.Ordinal)).All(x => x.Status == "installed" || x.Status == "unknown"), "VC v14 absence remains unknown without positive evidence");
            Require(inventory.Where(x => x.ComponentId == "dotnet-framework-4-full" || x.ComponentId == "dotnet-framework-3-5").All(x => x.Status == "installed" || x.Status == "outdated" || x.Status == "missing" || x.Status == "unknown"), ".NET inventory uses explicit evidence states");

            var outdatedFramework = new InventoryEntry
            {
                ComponentId = "dotnet-framework-4-full",
                Category = "dotnet",
                DisplayName = ".NET Framework 4.x Full",
                ExpectedVersion = "4.8",
                Status = "outdated",
                EvidenceId = "evidence-registry-dotnet4"
            };
            List<FindingRecord> outdatedFindings = WindowsRepairService.BuildFindings(new[] { outdatedFramework });
            Require(outdatedFindings.Count == 1 && outdatedFindings[0].Code == "RUNTIME-DOTNET-OUTDATED", ".NET 4.x outdated state creates a specific finding");
            Require(outdatedFindings[0].EvidenceIds.Contains(outdatedFramework.EvidenceId), ".NET outdated finding links registry evidence");
            Require(outdatedFindings[0].Message.Contains("4.8"), ".NET outdated finding names target version");
            var outdatedPlanReport = ReportBuilder.Create("self-test");
            outdatedPlanReport.Inventory.Add(outdatedFramework);
            List<RepairPlanItem> outdatedPlan = WindowsRepairService.BuildPlan(outdatedPlanReport);
            Require(outdatedPlan.Count == 1 && outdatedPlan[0].Id == outdatedFramework.ComponentId, ".NET 4.x outdated state enters repair plan");
            Require(outdatedPlan[0].Reason == "outdated" && outdatedPlan[0].OfficialUrl.IndexOf("dotnet-framework", StringComparison.OrdinalIgnoreCase) >= 0, ".NET outdated plan preserves status and official source");

            string root = Path.Combine(Path.GetTempPath(), "ysrepair-report-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                ReportDocument report = ReportBuilder.Create("self-test");
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
                    CommandSummary = "token=must-be-redacted"
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
                string log = File.ReadAllText(Path.Combine(result.DirectoryPath, "logs", "000_self-test.log"));
                Require(log.IndexOf("MachineGuid=must-be-redacted", StringComparison.OrdinalIgnoreCase) < 0, "log secret redaction");

                VerifyHistoryLogs();
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* best effort temp cleanup */ }
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
