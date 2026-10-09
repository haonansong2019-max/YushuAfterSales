using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using YushuAfterSales.Core;

namespace YushuAfterSales.Reporting
{
    /// <summary>Read-only evidence collection. This class never starts repair processes or writes registry/system state.</summary>
    public static class ReportBuilder
    {
        public static ReportDocument Create(string applicationVersion)
        {
            return new ReportDocument
            {
                ReportId = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ApplicationVersion = applicationVersion ?? String.Empty,
                Authorization = new AuthorizationSummary { AppId = "ysrepair", State = "unknown", CanRepair = false, CanInstall = false, CanUpgrade = false, OfflineGraceDays = 7 },
                Scan = new ScanRequest { Mode = "read-only" }, Environment = CollectEnvironment(),
                Conclusion = new ReportConclusion { Status = "not-evaluated" }
            };
        }

        public static EnvironmentSnapshot CollectEnvironment()
        {
            RegistryValueProbe release = ReadRegistryValueProbe(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release");
            RegistryValueProbe v35 = ReadRegistryValueProbe(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5", "Install");
            var snapshot = new EnvironmentSnapshot
            {
                ProductName = ReadWindowsValue("ProductName") ?? Environment.OSVersion.VersionString,
                DisplayVersion = ReadWindowsValue("DisplayVersion") ?? ReadWindowsValue("ReleaseId") ?? String.Empty,
                Build = BuildString(), Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                Runtime = Environment.Version.ToString(), UiCulture = CultureInfo.InstalledUICulture.Name,
                IsAdministrator = IsAdministrator(), DotNetV4Release = release.Value,
                DotNet35State = ClassifyDotNet35Status(v35.KeyFound, v35.ReadSucceeded, v35.Value)
            };
            snapshot.Capabilities["registry"] = release.ReadSucceeded && v35.ReadSucceeded;
            snapshot.Capabilities["windowsOptionalFeatures"] = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "servicing"));
            snapshot.Capabilities["sfc"] = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sfc.exe"));
            snapshot.Capabilities["dism"] = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"));
            return snapshot;
        }

        public static List<InventoryEntry> CollectInventory()
        {
            return ComponentScanService.CollectInventory(null, CancellationToken.None);
        }

        public static List<InventoryEntry> CollectDllEvidence(IEnumerable<string> requestedNames, string targetDirectory, string targetArchitecture)
        {
            return ComponentScanService.CollectDllEvidence(requestedNames, targetDirectory, targetArchitecture);
        }

        /// <summary>Collects version and hash evidence for an explicitly selected executable without starting it.</summary>
        public static InventoryEntry CollectExecutableEvidence(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Target executable was not found.", fullPath);
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(fullPath);
            return new InventoryEntry
            {
                ComponentId = "target-executable", Category = "diagnostic-target", DisplayName = Path.GetFileName(fullPath),
                Architecture = "unknown", DetectedVersion = info.FileVersion ?? String.Empty, SourceUrl = String.Empty,
                PackageFile = SensitiveDataRedactor.RedactPath(fullPath), Sha256 = ComputeSha256(fullPath),
                SignatureStatus = "not-checked", Compatibility = "unknown", Status = "observed", EvidenceId = "evidence-target-executable",
                Details = SensitiveDataRedactor.Redact(info.FileDescription ?? String.Empty)
            };
        }

        public static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();
        }

        private static string BuildString()
        {
            string build = ReadWindowsValue("CurrentBuildNumber") ?? String.Empty;
            string ubr = ReadWindowsValue("UBR");
            return String.IsNullOrEmpty(ubr) ? build : build + "." + ubr;
        }
        private static string ReadWindowsValue(string name)
        {
            foreach (RegistryView view in GetRegistryViews())
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        object value = key == null ? null : key.GetValue(name);
                        if (value != null) return Convert.ToString(value, CultureInfo.InvariantCulture);
                    }
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return null;
        }

        private static RegistryValueProbe ReadRegistryValueProbe(string subKey, string valueName)
        {
            var result = new RegistryValueProbe();
            foreach (RegistryView view in GetRegistryViews())
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = root.OpenSubKey(subKey))
                    {
                        if (key == null) continue;
                        result.KeyFound = true;
                        object value = key.GetValue(valueName);
                        if (value != null)
                        {
                            string observation = Convert.ToString(value, CultureInfo.InvariantCulture);
                            int oldNumber, newNumber;
                            if (result.Value == null || (Int32.TryParse(result.Value, out oldNumber) && Int32.TryParse(observation, out newNumber) && newNumber > oldNumber)) result.Value = observation;
                        }
                    }
                }
                catch (System.Security.SecurityException) { result.ReadFailed = true; }
                catch (UnauthorizedAccessException) { result.ReadFailed = true; }
                catch (IOException) { result.ReadFailed = true; }
            }
            // One unreadable registry view cannot establish absence; positive evidence remains useful.
            result.ReadSucceeded = !result.ReadFailed || result.Value == "1" || ParseRelease(result.Value) >= 528040;
            return result;
        }
        private static int ParseRelease(string value) { int release; return Int32.TryParse(value, out release) ? release : 0; }
        private static RegistryView[] GetRegistryViews()
        {
            return Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 };
        }

        /// <summary>The platform version cannot establish presence of June 2010 optional DLLs.</summary>
        public static string ClassifyDirectXLegacyStatus(string registryVersion) { return "unknown"; }
        public static string ClassifyVcRuntimeStatus(bool runtimeKeyFound, string installedValue)
        {
            return runtimeKeyFound && String.Equals(installedValue, "1", StringComparison.Ordinal) ? "installed" : "unknown";
        }
        public static string ClassifyDotNetFramework4Status(bool keyFound, bool readSucceeded, string releaseValue)
        {
            if (!readSucceeded) return "unknown";
            if (!keyFound) return "missing";
            int release;
            if (!Int32.TryParse(releaseValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out release) || release < 378389) return "unknown";
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
        private sealed class RegistryValueProbe { public bool KeyFound; public bool ReadSucceeded; public bool ReadFailed; public string Value; }
        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return identity != null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
