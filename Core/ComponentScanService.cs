using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.Core
{
    public sealed class ComponentScanProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string DisplayName { get; set; }
        public InventoryEntry Entry { get; set; }
        public string Stage { get; set; }
    }

    /// <summary>Read-only per-component probes. Progress is raised around each real probe, with no simulated delay.</summary>
    public static class ComponentScanService
    {
        public static readonly IReadOnlyList<string> DirectXLegacyDllNames = BuildDirectXNames();
        private static readonly HashSet<string> UcrtContracts = new HashSet<string>(new[]
        {
            "conio", "convert", "environment", "filesystem", "heap", "locale", "math", "multibyte",
            "private", "process", "runtime", "stdio", "string", "time", "utility"
        }.Select(x => "api-ms-win-crt-" + x + "-l1-1-0.dll"), StringComparer.OrdinalIgnoreCase);
        private static readonly string[] CommonDllNames =
        {
            "D3DX9_43.dll", "D3DX10_43.dll", "D3DX11_43.dll", "D3DCompiler_43.dll", "XInput1_3.dll",
            "XAudio2_7.dll", "X3DAudio1_7.dll", "XAPOFX1_5.dll", "MSVCR80.dll", "MSVCP80.dll",
            "MSVCR90.dll", "MSVCP90.dll", "MSVCR100.dll", "MSVCP100.dll", "MSVCR110.dll", "MSVCP110.dll",
            "MSVCR120.dll", "MSVCP120.dll", "MSVCP140.dll", "VCRUNTIME140.dll", "VCRUNTIME140_1.dll",
            "api-ms-win-crt-runtime-l1-1-0.dll", "api-ms-win-crt-time-l1-1-0.dll", "ucrtbase.dll", "MSVCRTD.dll"
        };

        public static ReportDocument Scan(string applicationVersion, Action<ComponentScanProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportDocument report = ReportBuilder.Create(applicationVersion);
            report.Scan.StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report.Inventory = CollectInventory(progress, cancellationToken);
            report.Findings = WindowsRepairService.BuildFindings(report.Inventory);
            report.Scan.CompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            report.Conclusion = new ReportConclusion
            {
                Status = "scanned", FindingCount = report.Findings.Count,
                BlockingFindingCount = report.Findings.Count(x => x.BlocksRepair),
                RepairableCount = report.Inventory.Count(x => x.RepairSupported && IsRepairState(x.Status)),
                Summary = "逐项检测 " + report.Inventory.Count + " 个组件；完整 " + report.Inventory.Count(x => x.Status == "installed") +
                    "，缺失或异常 " + report.Inventory.Count(x => IsRepairState(x.Status)) + "。扫描未修改系统；未知与不支持项不会自动安装。"
            };
            return report;
        }

        public static List<InventoryEntry> CollectInventory(Action<ComponentScanProgress> progress, CancellationToken cancellationToken)
        {
            var context = new ScanContext();
            var probes = new List<ScanProbe>();
            foreach (RuntimePackage package in RuntimeCatalog.Packages.Where(x => x.Id != "directx-jun2010"))
            {
                RuntimePackage captured = package;
                probes.Add(new ScanProbe { Name = package.DisplayName, Read = () => ProbeRuntime(captured, context) });
            }
            string[] architectures = Environment.Is64BitOperatingSystem ? new[] { "x86", "x64" } : new[] { "x86" };
            foreach (string name in DirectXLegacyDllNames)
            foreach (string architecture in architectures)
            {
                string capturedName = name, capturedArchitecture = architecture;
                probes.Add(new ScanProbe
                {
                    Name = name + " (" + architecture + ")",
                    Read = () => ProbeDll(capturedName, "directx-legacy-" + Id(capturedName) + "-" + capturedArchitecture,
                        "directx-legacy-file", capturedArchitecture, null)
                });
            }
            foreach (string name in CommonDllNames)
            foreach (string architecture in architectures)
            {
                string capturedName = name, capturedArchitecture = architecture;
                probes.Add(new ScanProbe
                {
                    Name = name + " (" + architecture + ")",
                    Read = () => ProbeDll(capturedName, "dll-" + Id(capturedName) + "-" + capturedArchitecture,
                        "dll", capturedArchitecture, null)
                });
            }

            var entries = new List<InventoryEntry>();
            for (int i = 0; i < probes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanProbe probe = probes[i];
                if (progress != null) progress(new ComponentScanProgress { Current = i + 1, Total = probes.Count, DisplayName = probe.Name, Stage = "scanning" });
                InventoryEntry entry = probe.Read();
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
                if (progress != null) progress(new ComponentScanProgress { Current = i + 1, Total = probes.Count, DisplayName = probe.Name, Entry = entry, Stage = "complete" });
            }
            return entries;
        }

        public static List<InventoryEntry> CollectDllEvidence(IEnumerable<string> requestedNames, string targetDirectory, string targetArchitecture)
        {
            var result = new List<InventoryEntry>();
            if (requestedNames == null) return result;
            string target = String.IsNullOrWhiteSpace(targetDirectory) ? null : Path.GetFullPath(targetDirectory);
            if (target != null && !Directory.Exists(target)) throw new DirectoryNotFoundException("The selected target directory was not found.");
            string[] architectures = targetArchitecture == "x86" || targetArchitecture == "x64" ? new[] { targetArchitecture }
                : Environment.Is64BitOperatingSystem ? new[] { "x86", "x64" } : new[] { "x86" };
            foreach (string requestedName in requestedNames.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string name = requestedName.Trim();
                if (Path.GetFileName(name) != name || !Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,126}\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new ArgumentException("DLL names must be plain .dll basenames without paths.", "requestedNames");
                foreach (string architecture in architectures)
                    result.Add(ProbeDll(name, "dll-" + Id(name) + "-" + architecture, "dll", architecture, target));
            }
            return result;
        }

        private static InventoryEntry ProbeRuntime(RuntimePackage package, ScanContext context)
        {
            string category = package.Category == "VC++" ? "vc-runtime" : package.Category == ".NET" ? "dotnet"
                : package.Category == "游戏平台" ? "catalog-only" : "game-runtime";
            var entry = NewEntry(package.Id, package.DisplayName, category, package.Architecture, package.OfficialUrl);
            entry.ExpectedVersion = package.Notes;
            entry.Compatibility = package.SupportsWindows7 ? "Windows 7 SP1/10/11" : "Windows 10/11";
            entry.RepairAction = "package:" + package.Id;
            entry.RepairSourcePolicy = package.Category == "游戏组件" || package.Category == "游戏平台" ? "official-vendor-only" : "official-microsoft-only";
            bool architectureSupported = package.Architecture != "x64" || Environment.Is64BitOperatingSystem;
            if (!architectureSupported)
            {
                entry.Status = "unsupported"; entry.Details = "32 位 Windows 无法安装 x64 组件。"; return entry;
            }
            if (package.Category == "游戏平台")
            {
                entry.Status = "catalog-only"; entry.Details = package.Notes; entry.RepairAction = "manual-investigation-only"; return entry;
            }
            if (package.Id.StartsWith("vc-", StringComparison.Ordinal)) ProbeVc(entry, package, context);
            else if (package.Id == "dotnet-35") ProbeFramework(entry, true);
            else if (package.Id == "dotnet-48") ProbeFramework(entry, false);
            else if (package.Id.StartsWith("dotnet-runtime-", StringComparison.Ordinal) || package.Id.StartsWith("dotnet-desktop-", StringComparison.Ordinal)) ProbeModernDotNet(entry, package);
            else if (package.Id == "ucrt" || package.Id == "msxml" || package.Id == "msxml-4" || package.Id == "openal")
            {
                string name = package.Id == "ucrt" ? "ucrtbase.dll" : package.Id == "msxml" ? "msxml6.dll" : package.Id == "msxml-4" ? "msxml4.dll" : "OpenAL32.dll";
                string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                InventoryEntry file = ProbeDll(name, package.Id, category, arch, null);
                entry.Status = file.Status; entry.DetectedVersion = file.DetectedVersion; entry.PackageFile = file.PackageFile;
                entry.Sha256 = file.Sha256; entry.SignatureStatus = file.SignatureStatus;
                entry.Details = file.Details;
                if (package.Id == "ucrt" || package.Id == "msxml") entry.RepairAction = "system:sfc";
            }
            else if (package.Id == "java")
                ProbeUninstall(entry, context, x => x.Name.IndexOf("Java", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("JDK", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("OpenJDK", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (package.Id == "xna-40")
                ProbeUninstall(entry, context, x => x.Name.IndexOf("XNA Framework", StringComparison.OrdinalIgnoreCase) >= 0 && x.Version.StartsWith("4.", StringComparison.Ordinal));
            else if (package.Id == "vstor-2010")
                ProbeUninstall(entry, context, x => x.Name.IndexOf("Visual Studio 2010 Tools for Office Runtime", StringComparison.OrdinalIgnoreCase) >= 0);
            else { entry.Status = "unknown"; entry.Details = "没有可靠的系统级检测规则。"; }

            RepairPackage trusted = RepairPackageCatalog.FindForEnvironment(package.Id, context.WindowsVersion);
            if (trusted != null)
            {
                entry.SourceUrl = trusted.DownloadUrl;
                entry.ExpectedVersion = trusted.Version;
                if (entry.Status == "installed" && (package.Id.StartsWith("vc-", StringComparison.Ordinal) ||
                    package.Id.StartsWith("dotnet-runtime-", StringComparison.Ordinal) ||
                    package.Id.StartsWith("dotnet-desktop-", StringComparison.Ordinal) || package.Id == "vstor-2010") &&
                    ParseVersion(entry.DetectedVersion) > new Version(0, 0) && ParseVersion(entry.DetectedVersion) < ParseVersion(trusted.Version))
                {
                    entry.Status = "outdated";
                    entry.Details += " | 检测版本低于当前系统的固定受信版本 " + trusted.Version + "。";
                }
            }
            bool systemSupported = trusted != null ? context.WindowsVersion >= trusted.MinimumWindowsVersion
                : package.SupportsWindows7 || context.WindowsBuild >= 14393;
            if (package.EndOfSupport || (!systemSupported && entry.Status != "installed"))
            {
                string observed = entry.Status;
                entry.Status = "unsupported";
                entry.Details = "观察状态：" + observed + "。" + (package.EndOfSupport ? "组件已停止支持，不提供一键安装。" : "当前 Windows 不满足最新安装包要求。") + " " + entry.Details;
            }
            entry.RepairSupported = IsRepairState(entry.Status) && !package.IsCatalogOnly && !package.EndOfSupport;
            if (entry.Status == "unsupported" || package.IsCatalogOnly) entry.RepairAction = "manual-investigation-only";
            return entry;
        }

        private static void ProbeVc(InventoryEntry entry, RuntimePackage package, ScanContext context)
        {
            string family = package.Id.Split('-')[1];
            context.LoadUninstall();
            List<UninstallItem> matches = context.Uninstall.Where(x => x.Name.IndexOf("Visual C++", StringComparison.OrdinalIgnoreCase) >= 0 &&
                x.Name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 && x.Architecture == package.Architecture &&
                (family == "2015plus" ? Regex.IsMatch(x.Name, @"2015|2017|2019|2022") || x.Version.StartsWith("14.", StringComparison.Ordinal)
                : x.Name.IndexOf(family, StringComparison.Ordinal) >= 0)).ToList();
            bool readFailed = context.UninstallReadFailed;
            bool installed = matches.Count > 0;
            string version = matches.Select(x => x.Version).OrderByDescending(x => ParseVersion(x)).FirstOrDefault();
            var evidence = new List<string>();
            foreach (UninstallItem item in matches) evidence.Add(item.Location + "; " + item.Name + "; " + item.Version);
            if (family == "2015plus")
            foreach (RegistryView view in Views())
            {
                RegistryProbe marker = ReadRegistry(view, @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + package.Architecture, "Installed", "Version");
                readFailed |= marker.Failed;
                if (marker.Values.ContainsKey("Installed") && marker.Values["Installed"] == "1")
                {
                    installed = true;
                    string observed;
                    if (marker.Values.TryGetValue("Version", out observed) && ParseVersion(observed) > ParseVersion(version)) version = observed;
                    evidence.Add(marker.Location + "; Installed=1; Version=" + (observed ?? ""));
                }
            }
            entry.Status = installed ? "installed" : readFailed ? "unknown" : "missing";
            entry.DetectedVersion = version ?? "";
            entry.SignatureStatus = "registry-only";
            entry.Details = installed ? String.Join(" | ", evidence) : readFailed
                ? "至少一个适用的注册表视图或条目读取失败；不能判定缺失。"
                : "两个适用注册表视图均已检查，未找到该代际/架构的系统可再发行运行库。应用本地组件不在此系统清单内。";
        }

        private static void ProbeUninstall(InventoryEntry entry, ScanContext context, Func<UninstallItem, bool> predicate)
        {
            context.LoadUninstall();
            UninstallItem item = context.Uninstall.FirstOrDefault(predicate);
            entry.Status = item != null ? "installed" : context.UninstallReadFailed ? "unknown" : "missing";
            entry.DetectedVersion = item == null ? "" : item.Version;
            entry.SignatureStatus = "registry-only";
            entry.Details = item != null ? item.Location + "; " + item.Name : context.UninstallReadFailed
                ? "安装信息读取失败，不能判定缺失。" : "未检测到系统安装登记；应用私有/便携组件不在此清单内。";
        }

        private static void ProbeFramework(InventoryEntry entry, bool v35)
        {
            var observations = Views().Select(view => ReadRegistry(view,
                v35 ? @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5" : @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", v35 ? "Install" : "Release")).ToList();
            string name = v35 ? "Install" : "Release";
            RegistryProbe positive = observations.FirstOrDefault(x => x.Values.ContainsKey(name) &&
                (v35 ? x.Values[name] == "1" : ParseNumber(x.Values[name]) >= 528040));
            RegistryProbe found = positive ?? observations.FirstOrDefault(x => x.Values.ContainsKey(name));
            string value = found == null ? null : found.Values[name];
            bool failed = observations.Any(x => x.Failed);
            entry.Status = positive != null ? "installed" : v35
                ? ReportBuilder.ClassifyDotNet35Status(observations.Any(x => x.KeyFound), !failed, value)
                : ReportBuilder.ClassifyDotNetFramework4Status(observations.Any(x => x.KeyFound), !failed, value);
            entry.DetectedVersion = v35 ? (entry.Status == "installed" ? "3.5" : "") : value ?? "";
            entry.ExpectedVersion = v35 ? "3.5" : "4.8 (Release >= 528040)";
            entry.SignatureStatus = "registry-only";
            entry.RepairAction = v35 ? WindowsBuild() < 9200 ? "system:sfc" : "system:dotnet35" : "package:dotnet-48";
            entry.Details = String.Join(" | ", observations.Select(x => x.Location + "; " + name + "=" + (x.Values.ContainsKey(name) ? x.Values[name] : "not-found") + (x.Failed ? "; read-failed" : "")));
        }

        private static void ProbeModernDotNet(InventoryEntry entry, RuntimePackage package)
        {
            string[] parts = package.Id.Split('-');
            string major = parts[2];
            string framework = parts[1] == "desktop" ? "Microsoft.WindowsDesktop.App" : "Microsoft.NETCore.App";
            string architecture = package.Architecture;
            bool failed = false;
            bool physicalRuntimeFound = false;
            var versions = new List<string>();
            var evidence = new List<string>();
            foreach (RegistryView view in Views())
            {
                RegistryProbe registry = ReadRegistry(view, @"SOFTWARE\dotnet\Setup\InstalledVersions\" + architecture + @"\sharedfx\" + framework);
                failed |= registry.Failed;
                foreach (KeyValuePair<string, string> item in registry.Values)
                    if (item.Key.StartsWith(major + ".", StringComparison.Ordinal) && item.Value == "1")
                    { versions.Add(item.Key); evidence.Add(registry.Location + "; " + item.Key + "=1"); }
            }
            string programFiles = architecture == "x86" && Environment.Is64BitOperatingSystem
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                : Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string folder = Path.Combine(programFiles, "dotnet", "shared", framework);
            try
            {
                bool folderPresent = true;
                string parent = programFiles;
                foreach (string part in new[] { "dotnet", "shared", framework })
                {
                    string[] matches = Directory.GetDirectories(parent, part, SearchOption.TopDirectoryOnly);
                    if (matches.Length == 0) { folderPresent = false; break; }
                    parent = matches[0];
                }
                if (folderPresent)
                foreach (string versionDirectory in Directory.GetDirectories(folder, major + ".*", SearchOption.TopDirectoryOnly))
                {
                    string marker = Path.Combine(versionDirectory, parts[1] == "desktop" ? "wpfgfx_cor3.dll" : "coreclr.dll");
                    FileObservation observation = InspectFile(marker, architecture, false);
                    if (observation.Status == "installed")
                    { string version = Path.GetFileName(versionDirectory); versions.Add(version); physicalRuntimeFound = true; evidence.Add(SensitiveDataRedactor.RedactPath(marker) + "; PE=" + architecture); }
                    else if (observation.Status != "missing") failed = true;
                }
            }
            catch (IOException) { failed = true; }
            catch (UnauthorizedAccessException) { failed = true; }
            catch (SecurityException) { failed = true; }
            entry.Status = physicalRuntimeFound ? "installed" : failed || versions.Count > 0 ? "unknown" : "missing";
            entry.DetectedVersion = versions.OrderByDescending(ParseVersion).FirstOrDefault() ?? "";
            entry.ExpectedVersion = major + ".x " + framework;
            entry.SignatureStatus = "registry-and-folder-evidence";
            entry.Details = evidence.Count > 0 ? String.Join(" | ", evidence) + (physicalRuntimeFound ? "" : " | 仅有安装登记，没有在标准目录验证到对应架构的实际运行时文件，不能标为完整。") : failed
                ? "至少一项注册表或运行时目录读取失败，不能判定缺失。" : "已检查两个适用注册表视图和标准共享框架目录，未找到该主版本/架构。";
        }

        private static InventoryEntry ProbeDll(string name, string componentId, string category, string architecture, string target)
        {
            string packageId = GetDllRepairPackage(name, architecture);
            RuntimePackage package = RuntimeCatalog.Find(packageId);
            string source = package == null ? "" : package.OfficialUrl;
            if (packageId == "system:sfc") source = "https://support.microsoft.com/windows/using-system-file-checker-in-windows-365e0031-36b1-6031-f804-8fd86e0ef4ca";
            var entry = NewEntry(componentId, name, category, architecture, source);
            entry.ExpectedVersion = category == "directx-legacy-file" ? "DirectX June 2010 可再发行组件" : "以目标程序所需的组件代际/架构为准";
            entry.Compatibility = "target-application-dependent";
            entry.RepairAction = packageId == "system:sfc" ? packageId : String.IsNullOrEmpty(packageId) ? "manual-investigation-only" : "package:" + packageId;
            entry.RepairSourcePolicy = String.IsNullOrEmpty(packageId) ? "no-approved-source" : "official-microsoft-only";
            if (architecture == "x64" && !Environment.Is64BitOperatingSystem) { entry.Status = "unsupported"; entry.Details = "32 位 Windows 不支持 x64 DLL。"; return entry; }
            if (String.Equals(name, "MSVCRTD.dll", StringComparison.OrdinalIgnoreCase))
            { entry.Status = "unsupported"; entry.Details = "这是调试运行库，不能通过 VC++ 可再发行包分发修复；请使用游戏/软件厂商发布的正式版本。"; return entry; }

            string systemFolder = SystemFolder(architecture);
            var paths = new List<string>();
            if (target != null) paths.Add(Path.Combine(target, name));
            paths.Add(Path.Combine(systemFolder, name));
            // VC80/VC90 are normally side-by-side assemblies, not System32 files.
            if (packageId.StartsWith("vc-2005-", StringComparison.Ordinal) || packageId.StartsWith("vc-2008-", StringComparison.Ordinal))
            {
                try
                {
                    string winsxs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
                    string pattern = (architecture == "x64" ? "amd64" : "x86") + "_microsoft.vc" + (packageId.StartsWith("vc-2005-", StringComparison.Ordinal) ? "80" : "90") + ".crt_*";
                    paths.AddRange(Directory.GetDirectories(winsxs, pattern, SearchOption.TopDirectoryOnly).Select(directory => Path.Combine(directory, name)));
                }
                catch (IOException) { entry.Details = "WinSxS 目录不可读。"; }
                catch (UnauthorizedAccessException) { entry.Details = "WinSxS 目录不可读。"; }
                catch (SecurityException) { entry.Details = "WinSxS 目录不可读。"; }
            }
            var observations = paths.Select(path => InspectFile(path, architecture, true)).ToList();
            FileObservation observed = observations.FirstOrDefault(x => x.Status != "missing") ?? observations[0];
            entry.Status = observed.Status == "missing" && !String.IsNullOrEmpty(entry.Details) ? "unknown" : observed.Status;
            entry.PackageFile = observed.Path == null ? "" : SensitiveDataRedactor.RedactPath(observed.Path);
            entry.Sha256 = observed.Sha256 ?? "";
            entry.DetectedVersion = observed.Version ?? "";
            entry.SignatureStatus = entry.Status == "missing" ? "not-present" : "not-checked";
            entry.Details = (entry.Details ?? "") + String.Join(" | ", observations.Select(x => SensitiveDataRedactor.RedactPath(x.Path) + "; " + x.Status + "; " + x.Detail));
            if (entry.Status == "missing" && UcrtContracts.Contains(name) && WindowsBuild() >= 10240)
            {
                FileObservation ucrt = InspectFile(Path.Combine(systemFolder, "ucrtbase.dll"), architecture, true);
                if (ucrt.Status == "installed")
                { entry.Status = "installed"; entry.DetectedVersion = ucrt.Version; entry.PackageFile = SensitiveDataRedactor.RedactPath(ucrt.Path); entry.Sha256 = ucrt.Sha256; entry.Details += " | Windows 10/11 API-set 合约由 UCRT 提供；无同名磁盘文件不能证明此合约缺失。"; }
                else if (ucrt.Status == "unknown") entry.Status = "unknown";
            }
            entry.RepairSupported = IsRepairState(entry.Status) && !String.IsNullOrEmpty(packageId);
            if (target != null && observed.Path != null &&
                String.Equals(Path.GetDirectoryName(observed.Path), target, StringComparison.OrdinalIgnoreCase) && IsRepairState(entry.Status))
            {
                entry.RepairSupported = false;
                entry.RepairAction = "manual-investigation-only";
                entry.Details += " | 异常文件来自所选应用目录；系统可再发行包不会覆盖应用私有文件，应使用软件厂商的原始安装器或平台文件校验。";
            }
            return entry;
        }

        /// <summary>Maps only known redistributable members; never routes an arbitrary DLL name to a download.</summary>
        public static string GetDllRepairPackage(string name, string architecture)
        {
            if (String.IsNullOrEmpty(name) || (architecture != "x86" && architecture != "x64")) return "";
            if (DirectXLegacyDllNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return "directx-jun2010";
            string lower = name.ToLowerInvariant();
            if (lower == "ucrtbase.dll" || UcrtContracts.Contains(lower)) return "system:sfc";
            foreach (KeyValuePair<string, string> family in new Dictionary<string, string> { { "80", "2005" }, { "90", "2008" }, { "100", "2010" }, { "110", "2012" }, { "120", "2013" } })
                if (lower == "msvcr" + family.Key + ".dll" || lower == "msvcp" + family.Key + ".dll") return "vc-" + family.Value + "-" + architecture;
            if (lower == "msvcp140.dll" || lower == "msvcp140_1.dll" || lower == "msvcp140_2.dll" ||
                lower == "msvcp140_atomic_wait.dll" || lower == "msvcp140_codecvt_ids.dll" ||
                lower == "vcruntime140.dll" || lower == "vcruntime140_1.dll") return "vc-2015plus-" + architecture;
            return "";
        }

        /// <summary>Checks the PE structure and machine type, not authenticity or an application-specific ABI.</summary>
        public static string InspectDllFile(string path, string expectedArchitecture)
        {
            return InspectFile(path, expectedArchitecture, false).Status;
        }

        private static FileObservation InspectFile(string path, string architecture, bool includeHash)
        {
            var observation = new FileObservation { Path = path, Status = "unknown" };
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
                    { observation.Status = "corrupt"; observation.Detail = "缺少有效 MZ 头。"; }
                    else
                    {
                        stream.Position = 0x3c;
                        int offset = reader.ReadInt32();
                        if (offset < 64 || offset > stream.Length - 24)
                        { observation.Status = "corrupt"; observation.Detail = "PE 头偏移超出文件边界。"; }
                        else
                        {
                            stream.Position = offset;
                            if (reader.ReadUInt32() != 0x00004550)
                            { observation.Status = "corrupt"; observation.Detail = "缺少有效 PE 签名。"; }
                            else
                            {
                                ushort machine = reader.ReadUInt16();
                                string observedArchitecture = machine == 0x014c ? "x86" : machine == 0x8664 ? "x64" : machine == 0xaa64 ? "arm64" : "unknown";
                                observation.Status = observedArchitecture == architecture ? "installed" : "architecture-mismatch";
                                observation.Detail = "PE machine=" + observedArchitecture + "; expected=" + architecture + "；结构检查不代表签名或完整文件内容已通过验证。";
                            }
                        }
                    }
                }
                if (includeHash) observation.Sha256 = ReportBuilder.ComputeSha256(path);
                try { observation.Version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
                catch (System.ComponentModel.Win32Exception) { observation.Version = ""; }
            }
            catch (FileNotFoundException) { observation.Status = "missing"; observation.Detail = "该已知候选路径没有文件。"; }
            catch (DirectoryNotFoundException) { observation.Status = "unknown"; observation.Detail = "候选目录不存在，证据不足。"; }
            catch (EndOfStreamException) { observation.Status = "corrupt"; observation.Detail = "PE 头截断。"; }
            catch (IOException ex) { observation.Status = "unknown"; observation.Detail = ex.GetType().Name; }
            catch (UnauthorizedAccessException) { observation.Status = "unknown"; observation.Detail = "访问被拒绝。"; }
            catch (SecurityException) { observation.Status = "unknown"; observation.Detail = "访问被系统策略拒绝。"; }
            return observation;
        }

        private static IReadOnlyList<string> BuildDirectXNames()
        {
            var names = new List<string>();
            AddRange(names, "D3DX9_", 24, 43); AddRange(names, "D3DX10_", 33, 43);
            AddRange(names, "D3DX11_", 42, 43); AddRange(names, "D3DCompiler_", 33, 43); AddRange(names, "D3DCSX_", 42, 43);
            AddRange(names, "XInput1_", 1, 3); AddRange(names, "XAudio2_", 0, 7);
            AddRange(names, "XAPOFX1_", 0, 5); AddRange(names, "X3DAudio1_", 0, 7);
            AddRange(names, "xactengine2_", 0, 10); AddRange(names, "xactengine3_", 0, 7);
            names.Add("XInput9_1_0.dll");
            return names.AsReadOnly();
        }

        private static void AddRange(List<string> names, string prefix, int start, int end)
        { for (int i = start; i <= end; i++) names.Add(prefix + i.ToString(CultureInfo.InvariantCulture) + ".dll"); }
        private static string Id(string value) { return value.ToLowerInvariant().Replace('.', '-'); }
        private static bool IsRepairState(string status) { return status == "missing" || status == "outdated" || status == "corrupt" || status == "architecture-mismatch"; }
        private static Version ParseVersion(string value) { Version version; return Version.TryParse((value ?? "").TrimStart('v'), out version) ? version : new Version(0, 0); }
        private static int ParseNumber(string value) { int number; return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0; }
        private static string SystemFolder(string architecture)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windows, Environment.Is64BitOperatingSystem && architecture == "x86" ? "SysWOW64"
                : Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32");
        }
        private static RegistryView[] Views() { return Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 }; }
        private static int WindowsBuild()
        {
            RegistryProbe value = ReadRegistry(Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");
            return value.Values.ContainsKey("CurrentBuildNumber") ? ParseNumber(value.Values["CurrentBuildNumber"]) : Environment.OSVersion.Version.Build;
        }
        private static InventoryEntry NewEntry(string id, string name, string category, string architecture, string source)
        {
            return new InventoryEntry
            {
                ComponentId = id, DisplayName = name, Category = category, Architecture = architecture,
                SourceUrl = source, Status = "unknown", EvidenceId = "evidence-" + id,
                DetectedVersion = "", Sha256 = "", PackageFile = "", SignatureStatus = "not-checked"
            };
        }
        private static RegistryProbe ReadRegistry(RegistryView view, string path, params string[] valueNames)
        {
            var result = new RegistryProbe { Location = "HKLM(" + view + ")\\" + path };
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = root.OpenSubKey(path))
                {
                    result.KeyFound = key != null;
                    if (key != null)
                    foreach (string name in valueNames.Length == 0 ? key.GetValueNames() : valueNames)
                    {
                        object value = key.GetValue(name);
                        if (value != null) result.Values[name] = Convert.ToString(value, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (IOException) { result.Failed = true; }
            catch (UnauthorizedAccessException) { result.Failed = true; }
            catch (SecurityException) { result.Failed = true; }
            return result;
        }

        private sealed class ScanProbe { public string Name; public Func<InventoryEntry> Read; }
        private sealed class FileObservation { public string Path; public string Status; public string Detail; public string Sha256; public string Version; }
        private sealed class RegistryProbe
        {
            public string Location; public bool KeyFound; public bool Failed;
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        private sealed class UninstallItem { public string Name; public string Version; public string Architecture; public string Location; }
        private sealed class ScanContext
        {
            public readonly int WindowsBuild = ComponentScanService.WindowsBuild();
            public readonly Version WindowsVersion = RepairPackageCatalog.CurrentWindowsVersion();
            public readonly List<UninstallItem> Uninstall = new List<UninstallItem>();
            public bool UninstallReadFailed;
            private bool uninstallLoaded;
            public void LoadUninstall()
            {
                if (uninstallLoaded) return;
                uninstallLoaded = true;
                foreach (RegistryView view in Views())
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (key == null) continue;
                            foreach (string subKey in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey item = key.OpenSubKey(subKey))
                                    {
                                        if (item == null) continue;
                                        string name = Convert.ToString(item.GetValue("DisplayName"), CultureInfo.InvariantCulture);
                                        if (String.IsNullOrWhiteSpace(name)) continue;
                                        string architecture = name.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0 ? "x64"
                                            : name.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0 ? "x86" : view == RegistryView.Registry64 ? "x64" : "x86";
                                        Uninstall.Add(new UninstallItem
                                        {
                                            Name = name, Architecture = architecture,
                                            Version = Convert.ToString(item.GetValue("DisplayVersion"), CultureInfo.InvariantCulture) ?? "",
                                            Location = "HKLM(" + view + @")\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + subKey
                                        });
                                    }
                                }
                                catch (IOException) { UninstallReadFailed = true; }
                                catch (UnauthorizedAccessException) { UninstallReadFailed = true; }
                                catch (SecurityException) { UninstallReadFailed = true; }
                            }
                        }
                    }
                    catch (IOException) { UninstallReadFailed = true; }
                    catch (UnauthorizedAccessException) { UninstallReadFailed = true; }
                    catch (SecurityException) { UninstallReadFailed = true; }
                }
            }
        }
    }
}
