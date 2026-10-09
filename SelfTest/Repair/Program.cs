using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using YushuAfterSales.Core;
using YushuAfterSales.Reporting;

internal static class Program
{
    private static int _assertions;
    private static int Main(string[] args)
    {
        try
        {
            var win10 = new Version(10, 0, 19045);
            var win7 = new Version(6, 1, 7601);
            var items = new[]
            {
                Entry("dx1", "package:directx-jun2010", "missing", "a"),
                Entry("dx2", "package:directx-jun2010", "architecture-mismatch", "b")
            };
            var plan = ComponentRepairService.BuildPlanForEnvironment(items, win10, true);
            Assert(plan.Count == 1 && plan[0].EvidenceIds.SequenceEqual(new[] { "a", "b" }) && plan[0].RepairExisting, "DLLs share one official DirectX action with all evidence");
            Assert(ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("vc", "package:vc-2015plus-x86", "missing", "vc") }, win7, false)[0].PackageId == "vc-2015plus-win7-x86", "Win7 uses pinned v16-compatible VC runtime");
            Assert(!ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("vc", "package:vc-2013-x64", "missing", "vc") }, win10, false)[0].Supported, "x64 package blocked on x86 Windows");
            Assert(!ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("net", "package:dotnet-desktop-8-x86", "missing", "net") }, win7, false)[0].Supported, "modern .NET blocked on Win7 regardless of permissive upstream metadata");
            Assert(!ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("unknown", "package:random-dll-download", "missing", "unknown") }, win10, true)[0].Supported, "unknown package cannot acquire install route");
            Assert(ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("vc", "package:vc-2013-x86", "installed", "vc") }, win10, true).Count == 0, "complete component is never automatically reinstalled");
            Assert(ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("ucrt", "system:sfc", "missing", "ucrt"), Entry("msxml", "system:sfc", "corrupt", "msxml") }, win10, true).Count == 1, "Windows servicing actions also deduplicate");
            Assert(!ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("net35", "system:dotnet35", "missing", "net35") }, win7, false)[0].Supported, "unsupported Win7 NetFx3 provider is blocked");
            Assert(ComponentRepairService.BuildPlanForEnvironment(new[] { Entry("dotnet-35", "system:sfc", "missing", "net35") }, win7, false)[0].Supported, "Win7 integrated .NET uses its explicit SFC route");
            Assert(ComponentRepairService.ClassifyExitCode(3010) == "reboot-required" && ComponentRepairService.ClassifyExitCode(1641) == "reboot-required", "successful reboot exits stay distinct");
            Assert(ComponentRepairService.ClassifyExitCode(1602) == "cancelled" && ComponentRepairService.ClassifyExitCode(1223) == "cancelled", "installer and UAC cancellation exits stay distinct");
            Assert(ComponentRepairService.ClassifyExitCode(1638) != "installer-completed" && ComponentRepairService.ClassifyExitCode(1603) == "failed", "existing newer version or error is not claimed repaired");
            foreach (RepairPackage package in RepairPackageCatalog.Packages)
            {
                Assert(RepairPackageCatalog.IsOfficialDownloadUri(new Uri(package.DownloadUrl)), "pinned official host " + package.Id);
                Assert(package.Sha256.Length == 64 && package.AllowedPublishers.Count > 0, "pinned hash and publisher " + package.Id);
            }
            foreach (string bad in new[] { "http://download.microsoft.com/a.exe", "https://download.microsoft.com.evil.example/a.exe", "https://user:password@download.microsoft.com/a.exe", "https://download.microsoft.com:8443/a.exe", "https://github.com/random/repo/releases/a.exe" })
                Assert(!RepairPackageCatalog.IsOfficialDownloadUri(new Uri(bad)), "untrusted URL rejected");
            bool rejected = false;
            try { ComponentRepairService.ExecuteAsync(items, false, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "missing confirmation rejected before install");
            Assert(!AuthorizationService.Load().CanRepair, "default production build has no unsigned local grant");
            rejected = false;
            try { ComponentRepairService.ExecuteAsync(items, true, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Assert(rejected, "production service rejects invalid authorization even when UI confirmed");
            string driverId = "12345678-1234-1234-1234-123456789abc:1";
            rejected = false;
            try { DriverUpdateService.InstallAsync(new[] { driverId }, true, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "production driver installer rejects missing authorization before UAC");
            rejected = false;
            try { DriverUpdateService.EnableUpdateServiceAsync(true, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "production service-change entry rejects missing authorization");
            rejected = false;
            try { DriverUpdateService.InstallAsync(new[] { driverId }, false, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "driver install requires explicit confirmation");
            rejected = false;
            try { DriverUpdateService.EnableUpdateServiceAsync(false, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Windows Update service change requires explicit confirmation");
            foreach (string badId in new[] { "https://example.com/a.exe", driverId + "\n", driverId + ";powershell.exe", "invalid" })
            {
                Assert(!DriverUpdateService.IsValidUpdateId(badId), "invalid driver identity rejected");
                rejected = false;
                try { DriverUpdateService.InstallAsync(new[] { badId }, true, null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "arbitrary driver URL or command rejected before authorization");
            }
            var cancelled = new CancellationToken(true);
            rejected = false;
            try { ComponentRepairService.DownloadVerifiedPackage(RepairPackageCatalog.Find("directx-jun2010"), "not-created", null, cancelled); }
            catch (OperationCanceledException) { rejected = true; }
            Assert(rejected && !Directory.Exists("not-created"), "cancelled download does not create cache");
            MethodInfo scriptMethod = typeof(ComponentRepairService).GetMethod("BuildElevatedPackageScript", BindingFlags.NonPublic | BindingFlags.Static);
            string script = (string)scriptMethod.Invoke(null, new object[] { RepairPackageCatalog.Find("directx-jun2010"), @"C:\cache\package.exe", false, true, @"C:\logs\action.log" });
            Assert(script.Contains("DXSETUP.exe") && script.Contains("'/silent'") && script.Contains(RepairPackageCatalog.DirectXSetupSha256), "DirectX action invokes verified DXSETUP, not just outer extraction");
            Assert(script.Contains(RepairPackageCatalog.DirectXDsetupSha256) && script.Contains(RepairPackageCatalog.DirectXDsetup32Sha256), "DirectX helper dependencies have pinned integrity");
            Assert(script.IndexOf("Checkpoint-Computer", StringComparison.Ordinal) < script.IndexOf("$exitCode=Run", StringComparison.Ordinal), "system restore checkpoint precedes actual installer");
            Assert(script.Contains("SetAccessRuleProtection($true,$false)") && script.Contains("Verify $installer"), "elevated copy is staged under protected ACL and reverified");
            string scripts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "generated-scripts");
            Directory.CreateDirectory(scripts);
            foreach (RepairPackage package in RepairPackageCatalog.Packages)
                File.WriteAllText(Path.Combine(scripts, package.Id + ".ps1"), (string)scriptMethod.Invoke(null, new object[] { package, @"C:\cache\package.exe", true, true, @"C:\logs\action.log" }));
            if (args.Length == 2 && args[0] == "--official-cache") VerifyOfficialFixtures(args[1]);
            if (args.Length == 2 && args[0] == "--download-only")
            {
                RepairPackage package = RepairPackageCatalog.Find(args[1]);
                Assert(package != null, "download-only accepts a catalog ID");
                string cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "download-validation-" + Guid.NewGuid().ToString("N"));
                string downloaded = ComponentRepairService.DownloadVerifiedPackage(package, cache, null, CancellationToken.None);
                Assert(PackageSecurityService.ValidatePackage(downloaded, package.Sha256, package.AllowedPublishers).IsAllowed, "real network download and archive extraction satisfy native production verifier");
                Console.WriteLine("OFFICIAL_DOWNLOAD_VERIFIED=" + package.Id + " path=" + downloaded);
            }
            Console.WriteLine("REPAIR_SELFTEST=PASS assertions=" + _assertions + " (no installers executed)");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("REPAIR_SELFTEST=FAIL " + ex); return 1; }
    }

    private static void VerifyOfficialFixtures(string fixtureRoot)
    {
        string cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "official-validation-cache");
        Directory.CreateDirectory(cache);
        foreach (RepairPackage package in RepairPackageCatalog.Packages)
        {
            string fixtureId = package.Id.Replace("dotnet-desktop-", "dotnet-desktopruntime-");
            string fixture = package.Id == "openal" ? Path.Combine(fixtureRoot, "openal-extracted", "oalinst.exe") : Path.Combine(fixtureRoot, fixtureId + ".exe");
            string versioned = Path.Combine(fixtureRoot, fixtureId + "-" + package.Version + ".exe");
            if (File.Exists(versioned)) fixture = versioned;
            Assert(File.Exists(fixture), "official downloaded fixture exists " + package.Id);
            string destination = Path.Combine(cache, package.Id + "-" + package.Sha256.Substring(0, 16) + ".exe");
            File.Copy(fixture, destination, true);
            string actual = ComponentRepairService.DownloadVerifiedPackage(package, cache, null, CancellationToken.None);
            Assert(actual == destination, "real downloader reused only reverified cache " + package.Id);
            var validation = PackageSecurityService.ValidatePackage(actual, package.Sha256, package.AllowedPublishers);
            Assert(validation.IsAllowed && validation.SignatureVerified && validation.ExpectedHashMatched && validation.PublisherAllowed, "native WinVerifyTrust + pinned subject + hash " + package.Id);
            var wrongHash = PackageSecurityService.ValidatePackage(actual, new String('0', 64), package.AllowedPublishers);
            Assert(!wrongHash.IsAllowed && !wrongHash.ExpectedHashMatched, "signed official file rejected with wrong hash " + package.Id);
            var wrongPublisher = PackageSecurityService.ValidatePackage(actual, package.Sha256, new[] { "CN=Untrusted Publisher" });
            Assert(!wrongPublisher.IsAllowed && !wrongPublisher.PublisherAllowed, "signed official file rejected with wrong publisher " + package.Id);
            if (package.Id == "directx-jun2010")
            {
                bool denied = false;
                try { PackageSecurityService.StartVerifiedPackage(validation, package.Sha256, package.AllowedPublishers, true); }
                catch (UnauthorizedAccessException) { denied = true; }
                Assert(denied, "legacy verified local installer entry also rejects production missing authorization");
            }
        }
        string dx = Path.Combine(fixtureRoot, "directx-extracted", "DXSETUP.exe");
        Assert(PackageSecurityService.ValidatePackage(dx, RepairPackageCatalog.DirectXSetupSha256, new[] { RepairPackageCatalog.MicrosoftMoprPublisher }).IsAllowed, "actual extracted DXSETUP passes production verifier");
    }

    private static InventoryEntry Entry(string id, string route, string status, string evidence)
    { return new InventoryEntry { ComponentId = id, DisplayName = id, Architecture = "x86", Status = status, RepairAction = route, EvidenceId = evidence }; }
    private static void Assert(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); _assertions++; }
}
