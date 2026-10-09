using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;

namespace YushuAfterSales.Reporting
{
    /// <summary>Read-only evidence collection. This class never starts repair processes or writes registry/system state.</summary>
    public static class ReportBuilder
    {
        public static ReportDocument Create(string applicationVersion)
        {
            var report = new ReportDocument
            {
                ReportId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ApplicationVersion = applicationVersion ?? String.Empty,
                Authorization = new AuthorizationSummary
                {
                    AppId = "ysrepair",
                    State = "unknown",
                    CanRepair = false,
                    CanInstall = false,
                    CanUpgrade = false,
                    OfflineGraceDays = 7
                },
                Scan = new ScanRequest { Mode = "read-only" },
                Environment = CollectEnvironment(),
                Conclusion = new ReportConclusion { Status = "not-evaluated" }
            };
            return report;
        }

        public static EnvironmentSnapshot CollectEnvironment()
        {
            var snapshot = new EnvironmentSnapshot
            {
                ProductName = ReadWindowsValue("ProductName") ?? Environment.OSVersion.VersionString,
                DisplayVersion = ReadWindowsValue("DisplayVersion") ?? ReadWindowsValue("ReleaseId") ?? String.Empty,
                Build = BuildString(),
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                Runtime = Environment.Version.ToString(),
                UiCulture = CultureInfo.InstalledUICulture == null ? String.Empty : CultureInfo.InstalledUICulture.Name,
                IsAdministrator = IsAdministrator(),
                DotNetV4Release = ReadDotNetRelease(),
                DotNet35State = ReadDotNet35State()
            };
            snapshot.Capabilities["registry"] = true;
            snapshot.Capabilities["windowsOptionalFeatures"] = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "servicing"));
            snapshot.Capabilities["sfc"] = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sfc.exe"));
            snapshot.Capabilities["dism"] = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"));
            return snapshot;
        }

        public static List<InventoryEntry> CollectInventory()
        {
            var result = new List<InventoryEntry>();
            CollectVcEntries(result);
            CollectVcUninstallEntries(result);
            CollectDotNetEntries(result);
            CollectDirectXEntry(result);
            CollectCatalogEntries(result);
            return result;
        }

        private static void CollectCatalogEntries(List<InventoryEntry> result)
        {
            // Report source-only catalog entries as unknown, never as a detected absence.
            foreach (YushuAfterSales.Core.RuntimePackage package in YushuAfterSales.Core.RuntimeCatalog.Packages)
            {
                if (package.Id == "dotnet-48") continue; // represented by the .NET 4.x registry probe
                result.Add(new InventoryEntry
                {
                    ComponentId = package.Id,
                    Category = package.Category == "DirectX" ? "directx" : package.Category == ".NET" ? "dotnet" : "catalog-only",
                    DisplayName = package.DisplayName,
                    Architecture = package.Architecture,
                    ExpectedVersion = package.Notes,
                    SourceUrl = package.OfficialUrl,
                    Compatibility = package.SupportsWindows7 ? "Windows 7/10/11" : "Windows 10/11 only",
                    Status = "catalog-only",
                    SignatureStatus = "not-downloaded",
                    EvidenceId = "catalog-" + package.Id,
                    Details = "官方来源目录；当前未安装或下载，不能由此推断组件缺失。"
                });
            }
        }

        /// <summary>Collects version and hash evidence for an explicitly selected executable without starting it.</summary>
        public static InventoryEntry CollectExecutableEvidence(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A target path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Target executable was not found.", fullPath);

            var info = FileVersionInfo.GetVersionInfo(fullPath);
            return new InventoryEntry
            {
                ComponentId = "target-executable",
                Category = "diagnostic-target",
                DisplayName = Path.GetFileName(fullPath),
                Architecture = "unknown",
                DetectedVersion = info.FileVersion ?? String.Empty,
                SourceUrl = String.Empty,
                PackageFile = SensitiveDataRedactor.RedactPath(fullPath),
                Sha256 = ComputeSha256(fullPath),
                SignatureStatus = "not-checked",
                Compatibility = "unknown",
                Status = "observed",
                EvidenceId = "evidence-target-executable",
                Details = SensitiveDataRedactor.Redact(info.FileDescription ?? String.Empty)
            };
        }

        public static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        private static void CollectVcEntries(List<InventoryEntry> result)
        {
            var observations = new Dictionary<string, VcRuntimeObservation>(StringComparer.OrdinalIgnoreCase);
            string[] architectures = Environment.Is64BitOperatingSystem ? new[] { "x86", "x64" } : new[] { "x86" };
            foreach (string arch in architectures) observations[arch] = new VcRuntimeObservation();

            foreach (RegistryView view in GetRegistryViews())
            {
                RegistryKey baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using (RegistryKey key = baseKey.OpenSubKey("SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes"))
                    {
                        if (key == null) continue;
                        foreach (string arch in architectures)
                        {
                            using (RegistryKey runtime = key.OpenSubKey(arch))
                            {
                                if (runtime == null) continue;
                                VcRuntimeObservation observation = observations[arch];
                                observation.KeyFound = true;
                                string installed = Convert.ToString(runtime.GetValue("Installed"), CultureInfo.InvariantCulture);
                                string version = Convert.ToString(runtime.GetValue("Version"), CultureInfo.InvariantCulture);
                                if (ClassifyVcRuntimeStatus(true, installed) == "installed")
                                {
                                    observation.Installed = true;
                                    if (!String.IsNullOrEmpty(version)) observation.Version = version;
                                }
                                else if (!String.IsNullOrEmpty(version) && String.IsNullOrEmpty(observation.Version))
                                {
                                    observation.Version = version;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    foreach (VcRuntimeObservation observation in observations.Values)
                        if (String.IsNullOrEmpty(observation.ProbeError))
                            observation.ProbeError = SensitiveDataRedactor.Redact(ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    if (baseKey != null) baseKey.Dispose();
                }
            }

            foreach (string arch in architectures)
            {
                VcRuntimeObservation observation = observations[arch];
                result.Add(new InventoryEntry
                {
                    ComponentId = "vc-runtime-2015-2022-" + arch,
                    Category = "vc-runtime",
                    DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (" + arch + ")",
                    Architecture = arch,
                    DetectedVersion = observation.Version ?? String.Empty,
                    ExpectedVersion = "latest-supported",
                    Compatibility = "Windows 7 SP1+",
                    Status = observation.Installed ? "installed" : "unknown",
                    SignatureStatus = observation.KeyFound ? "registry-only" : "registry-no-evidence",
                    EvidenceId = "evidence-registry-vc-" + arch,
                    Details = observation.Installed ? String.Empty : observation.ProbeError ??
                        (observation.KeyFound ? "运行库注册表项存在，但 Installed 不等于 1；不能据此确认缺失。" : "未找到运行库注册表项；不能据此确认缺失。")
                });
            }
        }

        private static void CollectDotNetEntries(List<InventoryEntry> result)
        {
            RegistryValueProbe releaseProbe = ReadDotNetReleaseProbe();
            string release = releaseProbe.Value;
            string releaseStatus = ClassifyDotNetFramework4Status(releaseProbe.KeyFound, releaseProbe.ReadSucceeded, release);
            result.Add(new InventoryEntry
            {
                ComponentId = "dotnet-framework-4-full",
                Category = "dotnet",
                DisplayName = ".NET Framework 4.x Full",
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                DetectedVersion = release ?? String.Empty,
                ExpectedVersion = "4.8",
                Compatibility = "Windows 7 SP1+",
                Status = releaseStatus,
                SignatureStatus = "registry-only",
                EvidenceId = "evidence-registry-dotnet4",
                Details = releaseStatus == "outdated" ? "检测到 .NET Framework 4.x，但 Release 值低于 4.8。" :
                    releaseStatus == "unknown" ? "无法从注册表证据确定 .NET Framework 4.x 状态。" : String.Empty
            });
            RegistryValueProbe v35Probe = ReadDotNet35Probe();
            string v35 = ClassifyDotNet35Status(v35Probe.KeyFound, v35Probe.ReadSucceeded, v35Probe.Value);
            result.Add(new InventoryEntry
            {
                ComponentId = "dotnet-framework-3-5",
                Category = "dotnet",
                DisplayName = ".NET Framework 3.5 (2.0/3.0/3.5)",
                Architecture = "system",
                DetectedVersion = v35,
                ExpectedVersion = "3.5",
                Compatibility = "Windows optional feature",
                Status = v35,
                SignatureStatus = "registry-only",
                EvidenceId = "evidence-registry-dotnet35",
                Details = v35 == "unknown" ? "无法从注册表证据确定 .NET Framework 3.5 状态。" : String.Empty
            });

        }

        private static void CollectVcUninstallEntries(List<InventoryEntry> result)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InventoryEntry existing in result)
                if (!String.IsNullOrEmpty(existing.DisplayName)) seen.Add(existing.DisplayName + "|" + existing.DetectedVersion);

            foreach (RegistryView view in GetRegistryViews())
            {
                RegistryKey baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using (RegistryKey uninstall = baseKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (string subName in uninstall.GetSubKeyNames())
                        {
                            using (RegistryKey item = uninstall.OpenSubKey(subName))
                            {
                                string displayName = item == null ? null : Convert.ToString(item.GetValue("DisplayName"), CultureInfo.InvariantCulture);
                                if (String.IsNullOrEmpty(displayName) || displayName.IndexOf("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                string displayVersion = Convert.ToString(item.GetValue("DisplayVersion"), CultureInfo.InvariantCulture) ?? String.Empty;
                                string key = displayName + "|" + displayVersion;
                                if (!seen.Add(key)) continue;
                                string architecture = displayName.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0 ? "x64"
                                    : displayName.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0 ? "x86" : "unknown";
                                result.Add(new InventoryEntry
                                {
                                    ComponentId = "vc-uninstall-" + unchecked((uint)key.GetHashCode()).ToString(CultureInfo.InvariantCulture),
                                    Category = "vc-runtime",
                                    DisplayName = displayName,
                                    Architecture = architecture,
                                    DetectedVersion = displayVersion,
                                    ExpectedVersion = "catalog-dependent",
                                    Compatibility = "Windows 7 SP1+",
                                    Status = "installed",
                                    SignatureStatus = "registry-only",
                                    EvidenceId = "evidence-uninstall-vc-" + view
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Add(new InventoryEntry
                    {
                        ComponentId = "vc-uninstall-probe-" + view,
                        Category = "vc-runtime",
                        DisplayName = "VC++ uninstall registry probe (" + view + ")",
                        Architecture = view == RegistryView.Registry64 ? "x64" : "x86",
                        Compatibility = "probe-failed",
                        Status = "unknown",
                        SignatureStatus = "not-applicable",
                        EvidenceId = "evidence-uninstall-error-" + view,
                        Details = SensitiveDataRedactor.Redact(ex.GetType().Name + ": " + ex.Message)
                    });
                }
                finally
                {
                    if (baseKey != null) baseKey.Dispose();
                }
            }
        }

        private static void CollectDirectXEntry(List<InventoryEntry> result)
        {
            string version = ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE\\Microsoft\\DirectX", "Version")
                ?? ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE\\Microsoft\\DirectX", "Version");
            result.Add(new InventoryEntry
            {
                ComponentId = "directx-legacy",
                Category = "directx",
                DisplayName = "DirectX legacy runtime",
                Architecture = "system",
                DetectedVersion = version ?? String.Empty,
                ExpectedVersion = "June 2010 legacy package",
                Compatibility = "Windows 7 SP1+",
                Status = ClassifyDirectXLegacyStatus(version),
                SignatureStatus = "registry-only",
                EvidenceId = "evidence-registry-directx",
                Details = "DirectX 注册表版本只描述系统 DirectX 平台版本，不能证明 June 2010 可选 D3DX/XInput 等旧版 DLL 是否存在。"
            });
        }

        private static string BuildString()
        {
            string build = ReadWindowsValue("CurrentBuildNumber") ?? String.Empty;
            string ubr = ReadWindowsValue("UBR");
            return String.IsNullOrEmpty(ubr) ? build : build + "." + ubr;
        }

        private static string ReadWindowsValue(string name)
        {
            return ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", name)
                ?? ReadRegistryValue(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", name);
        }

        private static string ReadRegistryValue(RegistryHive hive, RegistryView view, string subKey, string valueName)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(subKey))
                {
                    object value = key == null ? null : key.GetValue(valueName);
                    return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }
            catch { return null; }
        }

        private static string ReadDotNetRelease()
        {
            return ReadDotNetReleaseProbe().Value;
        }

        private static string ReadDotNet35State()
        {
            RegistryValueProbe probe = ReadDotNet35Probe();
            return ClassifyDotNet35Status(probe.KeyFound, probe.ReadSucceeded, probe.Value);
        }

        private static RegistryValueProbe ReadDotNetReleaseProbe()
        {
            return ReadRegistryValueProbe(RegistryHive.LocalMachine,
                "SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full", "Release");
        }

        private static RegistryValueProbe ReadDotNet35Probe()
        {
            return ReadRegistryValueProbe(RegistryHive.LocalMachine,
                "SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v3.5", "Install");
        }

        private static RegistryValueProbe ReadRegistryValueProbe(RegistryHive hive, string subKey, string valueName)
        {
            var result = new RegistryValueProbe();
            foreach (RegistryView view in GetRegistryViews())
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                    using (RegistryKey key = root.OpenSubKey(subKey))
                    {
                        result.ReadSucceeded = true;
                        if (key == null) continue;
                        result.KeyFound = true;
                        object value = key.GetValue(valueName);
                        if (value != null)
                        {
                            result.Value = Convert.ToString(value, CultureInfo.InvariantCulture);
                            result.ValueFound = true;
                            return result;
                        }
                    }
                }
                catch
                {
                    result.ReadFailed = true;
                }
            }
            // Missing is definitive only when every applicable registry view was readable.
            result.ReadSucceeded = !result.ReadFailed;
            return result;
        }

        private static RegistryView[] GetRegistryViews()
        {
            return Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 };
        }

        /// <summary>DirectX platform version evidence cannot establish presence of June 2010 optional DLLs.</summary>
        public static string ClassifyDirectXLegacyStatus(string registryVersion)
        {
            return "unknown";
        }

        /// <summary>Only an explicit Installed=1 marker is positive evidence for a v14 runtime.</summary>
        public static string ClassifyVcRuntimeStatus(bool runtimeKeyFound, string installedValue)
        {
            return runtimeKeyFound && String.Equals(installedValue, "1", StringComparison.Ordinal) ? "installed" : "unknown";
        }

        public static string ClassifyDotNetFramework4Status(bool keyFound, bool readSucceeded, string releaseValue)
        {
            if (!readSucceeded) return "unknown";
            if (!keyFound) return "missing";
            int release;
            if (!Int32.TryParse(releaseValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out release)) return "unknown";
            if (release < 378389) return "unknown";
            return release < 528040 ? "outdated" : "installed";
        }

        public static string ClassifyDotNet35Status(bool keyFound, bool readSucceeded, string installValue)
        {
            if (!readSucceeded) return "unknown";
            if (!keyFound) return "missing";
            if (String.Equals(installValue, "1", StringComparison.Ordinal)) return "installed";
            if (String.Equals(installValue, "0", StringComparison.Ordinal)) return "missing";
            return "unknown";
        }

        private sealed class VcRuntimeObservation
        {
            public bool KeyFound;
            public bool Installed;
            public string Version;
            public string ProbeError;
        }

        private sealed class RegistryValueProbe
        {
            public bool KeyFound;
            public bool ValueFound;
            public bool ReadSucceeded;
            public bool ReadFailed;
            public string Value;
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }
    }
}
