using System;
using System.IO;
using System.Linq;
using YushuAfterSales.Core;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.SelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                return Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELFTEST_ERROR_TYPE=" + ex.GetType().FullName);
                Console.WriteLine("SELFTEST_ERROR_MESSAGE=" + ex.Message);
                Console.WriteLine("SELFTEST_ERROR_STACK=" + ex.StackTrace);
                return 1;
            }
        }

        private static int Run()
        {
            ReportEngineSelfTest.Run();
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-output");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);

            ReportDocument report = WindowsRepairService.Scan("selftest");
            Assert(report != null, "report created");
            Assert(report.SchemaVersion == "1.0", "schema version");
            Assert(report.Environment != null && report.Environment.Capabilities.ContainsKey("sfc"), "environment capabilities");
            Assert(report.Inventory.Count >= 3, "inventory collected");
            Assert(RuntimeCatalog.Packages.Count >= 10, "catalog populated");
            Assert(WindowsRepairService.BuildPlan(report) != null, "repair plan built");

            report.Findings.Add(new FindingRecord { FindingId = "selftest-finding", Code = "SELFTEST", Severity = "info", Title = "自测", Message = "machineguid=password=should-redact" });
            ReportPackageResult package = ReportWriter.WritePackage(report, root, new[]
            {
                new DiagnosticLogEntry { Name = "selftest.log", Source = "selftest", CapturedUtc = DateTime.UtcNow.ToString("o"), Content = "machineguid=secret\r\nC:\\Users\\Administrator\\secret.txt" }
            }, true);
            Assert(File.Exists(Path.Combine(package.DirectoryPath, "report.json")), "report.json");
            Assert(File.Exists(Path.Combine(package.DirectoryPath, "inventory.json")), "inventory.json");
            Assert(File.Exists(Path.Combine(package.DirectoryPath, "actions.jsonl")), "actions.jsonl");
            Assert(File.Exists(Path.Combine(package.DirectoryPath, "findings.json")), "findings.json");
            Assert(File.Exists(Path.Combine(package.DirectoryPath, "README.html")), "README.html");
            Assert(File.Exists(package.ZipPath), "zip");
            string log = File.ReadAllText(Directory.GetFiles(Path.Combine(package.DirectoryPath, "logs"), "*.log").Single());
            Assert(!log.Contains("machineguid=secret"), "redaction secret");
            Assert(!log.Contains("C:\\Users\\Administrator"), "redaction user path");
            Assert(AuthorizationService.Load().AppId == "ysrepair", "authorization app id");
            string digest = new String('a', 64);
            AuthorizationRequest request = AuthorizationService.CreateRequest(DateTime.UtcNow, digest, "selftest-nonce");
            Assert(request.AppId == "ysrepair" && request.MachineBinding == digest && request.Nonce == "selftest-nonce", "authorization request contract");
            bool invalidBindingRejected = false;
            try { AuthorizationService.CreateRequest(DateTime.UtcNow, "bad", "selftest-nonce"); }
            catch (ArgumentException) { invalidBindingRejected = true; }
            Assert(invalidBindingRejected, "authorization rejects malformed binding");

            bool confirmationRequired = false;
            try { WindowsRepairService.RunSystemRepair(SystemRepairAction.SfcScannow, false); }
            catch (InvalidOperationException) { confirmationRequired = true; }
            Assert(confirmationRequired, "system repair rejects missing confirmation before elevation");
            Assert(!WindowsRepairService.IsSystemActionSupported(SystemRepairAction.EnableDotNet35, new Version(6, 1)), "Windows 7 .NET 3.5 action unsupported");
            Assert(WindowsRepairService.IsSystemActionSupported(SystemRepairAction.EnableDotNet35, new Version(10, 0)), "Windows 10 .NET 3.5 action supported");
            Assert(!WindowsRepairService.IsSystemActionSupported((SystemRepairAction)999, new Version(10, 0)), "invalid system repair action rejected");

            bool wingetDisabled = false;
            try { WindowsRepairService.RunWingetInstall(RuntimeCatalog.Find("vc-2015plus-x64")); }
            catch (InvalidOperationException) { wingetDisabled = true; }
            Assert(wingetDisabled, "WinGet install fails closed without trusted package manifest");

            string untrustedPackagePath = Path.Combine(root, "not-an-installer.txt");
            File.WriteAllText(untrustedPackagePath, "not an executable installer");
            PackageSecurityService.PackageValidationResult untrustedPackage = PackageSecurityService.ValidatePackage(
                untrustedPackagePath, new String('0', 64), new[] { "CN=Microsoft Corporation" });
            Assert(!untrustedPackage.ExtensionAllowed && !untrustedPackage.IsAllowed, "non-installer extension rejected");

            string configuredManifestUrl = UpdateService.ManifestUrl;
            string configuredManifestEnvironment = Environment.GetEnvironmentVariable("YSREPAIR_UPDATE_MANIFEST_URL");
            try
            {
                UpdateService.ManifestUrl = null;
                Environment.SetEnvironmentVariable("YSREPAIR_UPDATE_MANIFEST_URL", null);
                bool unconfiguredUpdateRejected = false;
                try { UpdateService.ResolveManifestUrl(); }
                catch (InvalidOperationException) { unconfiguredUpdateRejected = true; }
                Assert(unconfiguredUpdateRejected, "unconfigured updater fails closed");
            }
            finally
            {
                UpdateService.ManifestUrl = configuredManifestUrl;
                Environment.SetEnvironmentVariable("YSREPAIR_UPDATE_MANIFEST_URL", configuredManifestEnvironment);
            }

            string generatedRepairScript = GetRepairScriptForTest(SystemRepairAction.DismRestoreHealth);
            int checkpointIndex = generatedRepairScript.IndexOf("Checkpoint-Computer", StringComparison.Ordinal);
            int actionIndex = generatedRepairScript.IndexOf("dism.exe", StringComparison.Ordinal);
            Assert(checkpointIndex >= 0 && actionIndex > checkpointIndex, "repair script checkpoints before fixed action");
            Assert(generatedRepairScript.Contains("if ($restoreStatus -eq 'created')") && generatedRepairScript.Contains("YSACTIONSTATUS="), "repair script gates action on checkpoint and records action status separately");

            UpdateManifest manifest = UpdateService.ParseManifest("{\"productId\":\"ysrepair\",\"version\":\"1.2.3.0\",\"downloadUrl\":\"https://updates.example.com/ysrepair.zip\",\"sha256\":\"" + digest + "\"}");
            Assert(manifest.Version == "1.2.3.0", "update manifest parsed");
            Assert(UpdateService.IsNewerVersion("1.2.3.0", new Version(1, 2, 2, 0)), "update version compare");
            bool insecureUrlRejected = false;
            try { UpdateService.ParseManifest("{\"productId\":\"ysrepair\",\"version\":\"1.2.3.0\",\"downloadUrl\":\"http://updates.example.com/ysrepair.zip\",\"sha256\":\"" + digest + "\"}"); }
            catch (InvalidDataException) { insecureUrlRejected = true; }
            Assert(insecureUrlRejected, "update rejects non-https download");
            bool wrongProductRejected = false;
            try { UpdateService.ParseManifest("{\"productId\":\"other\",\"version\":\"1.2.3.0\",\"downloadUrl\":\"https://updates.example.com/ysrepair.zip\",\"sha256\":\"" + digest + "\"}"); }
            catch (InvalidDataException) { wrongProductRejected = true; }
            Assert(wrongProductRejected, "update rejects wrong product id");

            Console.WriteLine("YUSHU_AFTER_SALES_SELFTEST=PASS");
            Console.WriteLine("REPORT_ID=" + package.ReportId);
            Console.WriteLine("FILES=" + package.FileCount);
            return 0;
        }

        private static void Assert(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("ASSERT_FAILED: " + label);
        }

        private static string GetRepairScriptForTest(SystemRepairAction action)
        {
            var method = typeof(WindowsRepairService).GetMethod("BuildPowerShellScript",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("BuildPowerShellScript is unavailable for safety verification.");
            return (string)method.Invoke(null, new object[] { action, Path.Combine(Path.GetTempPath(), "ysrepair-selftest.log") });
        }
    }
}
