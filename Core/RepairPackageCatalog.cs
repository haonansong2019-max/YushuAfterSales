using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace YushuAfterSales.Core
{
    /// <summary>Immutable, build-pinned Microsoft packages. These are proprietary vendor binaries, not project source.</summary>
    public sealed class RepairPackage
    {
        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public string Architecture { get; private set; }
        public string Version { get; private set; }
        public string DownloadUrl { get; private set; }
        public string Sha256 { get; private set; }
        public string ArchiveSha256 { get; private set; }
        public Version MinimumWindowsVersion { get; private set; }
        public long MaximumBytes { get; private set; }
        public IReadOnlyList<string> AllowedPublishers { get; private set; }
        internal string Kind { get; private set; }
        internal string InstallArguments { get; private set; }
        internal string RepairArguments { get; private set; }

        internal RepairPackage(string id, string name, string architecture, string version, string url, string hash,
            string publisher, Version minimumWindows, string kind, string install, string repair, string archiveSha256 = null)
        {
            Id = id; DisplayName = name; Architecture = architecture; Version = version;
            DownloadUrl = url; Sha256 = hash.ToUpperInvariant(); MinimumWindowsVersion = minimumWindows;
            ArchiveSha256 = archiveSha256;
            Kind = kind; InstallArguments = install; RepairArguments = repair;
            MaximumBytes = 256L * 1024 * 1024;
            AllowedPublishers = Array.AsReadOnly(new[] { publisher });
        }
    }

    public static class RepairPackageCatalog
    {
        public const string MicrosoftPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        public const string MicrosoftMoprPublisher = "CN=Microsoft Corporation, OU=MOPR, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        public const string DotNetPublisher = "CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        public const string DirectXSetupSha256 = "8F47D7121EF6532AD9AD9901E44E237F5C30448B752028C58A9D19521414E40D";
        public const string DirectXDsetupSha256 = "2A61679EEEDABF7D0D0AC14E5447486575622D6B7CFA56F136C1576FF96DA21F";
        public const string DirectXDsetup32Sha256 = "FB0E534F9B0926E518F1C2980640DFD29F14217CDFA37CF3A0C13349127ED9A8";
        private static readonly ReadOnlyCollection<RepairPackage> Catalog = BuildCatalog().AsReadOnly();
        public static IReadOnlyList<RepairPackage> Packages { get { return Catalog; } }

        public static RepairPackage Find(string id)
        {
            return Catalog.FirstOrDefault(x => String.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static RepairPackage FindForEnvironment(string id, Version windows)
        {
            // The last v16-compatible branch is separate from the modern v14 package; never send latest to Win7.
            if (windows != null && windows.Major == 6 && windows.Minor == 1 &&
                (id == "vc-2015plus-x86" || id == "vc-2015plus-x64"))
                id = id.Replace("vc-2015plus-", "vc-2015plus-win7-");
            return Find(id);
        }

        public static bool IsOfficialDownloadUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment)) return false;
            return String.Equals(uri.Host, "download.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "builds.dotnet.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                (String.Equals(uri.Host, "www.openal.org", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/downloads/oalinst.zip");
        }

        public static Version CurrentWindowsVersion()
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32))
                using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (key != null)
                    {
                        object major = key.GetValue("CurrentMajorVersionNumber");
                        object minor = key.GetValue("CurrentMinorVersionNumber");
                        int build;
                        Int32.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")), out build);
                        if (major != null) return new Version(Convert.ToInt32(major), minor == null ? 0 : Convert.ToInt32(minor), build);
                        Version old;
                        if (Version.TryParse(Convert.ToString(key.GetValue("CurrentVersion")), out old))
                            return new Version(old.Major, old.Minor, build);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is System.IO.IOException) { }
            return Environment.OSVersion.Version;
        }

        private static List<RepairPackage> BuildCatalog()
        {
            // URLs and hashes were checked against public upstream metadata and downloaded official files on 2026-10-09.
            // Every file and all three DirectX executables/support DLLs had a valid Microsoft Authenticode signature.
            var p = new List<RepairPackage>();
            AddVc(p, "2005", "x86", "8.0.61001", "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.EXE", "8648C5FC29C44B9112FE52F9A33F80E7FC42D10F3B5B42B2121542A13E44ADFD", MicrosoftPublisher);
            AddVc(p, "2005", "x64", "8.0.61000", "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.EXE", "4487570BD86E2E1AAC29DB2A1D0A91EB63361FCAAC570808EB327CD4E0E2240D", MicrosoftPublisher);
            AddVc(p, "2008", "x86", "9.0.30729.6161", "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe", "8742BCBF24EF328A72D2A27B693CC7071E38D3BB4B9B44DEC42AA3D2C8D61D92", MicrosoftPublisher);
            AddVc(p, "2008", "x64", "9.0.30729.6161", "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe", "C5E273A4A16AB4D5471E91C7477719A2F45DDADB76C7F98A38FA5074A6838654", MicrosoftPublisher);
            AddVc(p, "2010", "x86", "10.0.40219", "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe", "99DCE3C841CC6028560830F7866C9CE2928C98CF3256892EF8E6CF755147B0D8", MicrosoftPublisher);
            AddVc(p, "2010", "x64", "10.0.40219", "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe", "F3B7A76D84D23F91957AA18456A14B4E90609E4CE8194C5653384ED38DADA6F3", MicrosoftPublisher);
            AddVc(p, "2012", "x86", "11.0.61030", "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe", "B924AD8062EAF4E70437C8BE50FA612162795FF0839479546CE907FFA8D6E386", MicrosoftMoprPublisher);
            AddVc(p, "2012", "x64", "11.0.61030", "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe", "681BE3E5BA9FD3DA02C09D7E565ADFA078640ED66A0D58583EFAD2C1E3CC4064", MicrosoftMoprPublisher);
            AddVc(p, "2013", "x86", "12.0.40664", "https://download.microsoft.com/download/0/5/6/056dcda9-d667-4e27-8001-8a0c6971d6b1/vcredist_x86.exe", "89F4E593EA5541D1C53F983923124F9FD061A1C0C967339109E375C661573C17", MicrosoftMoprPublisher);
            AddVc(p, "2013", "x64", "12.0.40664", "https://download.microsoft.com/download/0/5/6/056dcda9-d667-4e27-8001-8a0c6971d6b1/vcredist_x64.exe", "20E2645B7CD5873B1FA3462B99A665AC8D6E14AAE83DED9D875FEA35FFDD7D7E", MicrosoftMoprPublisher);
            AddV14(p, "x86", false, "https://download.visualstudio.microsoft.com/download/pr/57eef8ae-a341-46c3-b0bc-c041027b54cd/F0BAB33A302B3CDB2E11113760D016F54FD3D2632C65BA7834FAC4F0ABD7F1A3/VC_redist.x86.exe", "F0BAB33A302B3CDB2E11113760D016F54FD3D2632C65BA7834FAC4F0ABD7F1A3");
            AddV14(p, "x64", false, "https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe", "843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C");
            AddV14(p, "x86", true, "https://download.visualstudio.microsoft.com/download/pr/b929b7fe-5c89-4553-9abe-6324631dcc3a/4C6C420CF4CBF2C9C9ED476E96580AE92A97B2822C21329A2E49E8439AC5AD30/VC_redist.x86.exe", "4C6C420CF4CBF2C9C9ED476E96580AE92A97B2822C21329A2E49E8439AC5AD30");
            AddV14(p, "x64", true, "https://download.visualstudio.microsoft.com/download/pr/b929b7fe-5c89-4553-9abe-6324631dcc3a/296F96CD102250636BCD23AB6E6CF70935337B1BBB3507FE8521D8D9CFAA932F/VC_redist.x64.exe", "296F96CD102250636BCD23AB6E6CF70935337B1BBB3507FE8521D8D9CFAA932F");
            p.Add(new RepairPackage("directx-jun2010", "DirectX June 2010", "x86/x64", "9.29.1974.1", "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe", "053F76DCBB28802E23341B6A787E3B0791C0FA5C8D4D011B1044172DBF89C73B", MicrosoftPublisher, new Version(6, 1, 7601), "directx", "/silent", "/silent"));
            p.Add(new RepairPackage("dotnet-48", ".NET Framework 4.8", "x86/x64", "4.8", "https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe", "0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40", MicrosoftPublisher, new Version(6, 1, 7601), "framework", "/q /norestart /log \"{log}\"", "/repair /q /norestart /log \"{log}\""));
            AddDotNet(p, "desktop", "8", "x86", "8.0.31", "https://download.microsoft.com/download/7e7e34b1-b352-43d1-a6de-d5d3460bee5e/712549d5-4eef-4548-8b0d-56216fca03fe/windowsdesktop-runtime-8.0.31-win-x86.exe", "FA5D97A9EBD375212910CA92F3D4A1AA5D40007B4C035D74D5746FD25C9BA371");
            AddDotNet(p, "desktop", "8", "x64", "8.0.31", "https://download.microsoft.com/download/7e7e34b1-b352-43d1-a6de-d5d3460bee5e/855a3161-903f-4e3a-a9d7-79d5579c82d2/windowsdesktop-runtime-8.0.31-win-x64.exe", "C375DFD80A967405CFEFF634912C1FCCC56261CE3D9209EA473F60A21184D1CC");
            AddDotNet(p, "desktop", "9", "x86", "9.0.20", "https://download.microsoft.com/download/6c1a1f56-f732-46c6-adb6-245377f2d599/f344467e-7d05-4188-b2d8-d517583c1a4b/windowsdesktop-runtime-9.0.20-win-x86.exe", "66BA540E013243FF1B3EC51E0E01B09E8FDC15AAA2934C655B7EC00C0E9287DA");
            AddDotNet(p, "desktop", "9", "x64", "9.0.20", "https://download.microsoft.com/download/6c1a1f56-f732-46c6-adb6-245377f2d599/b7fd647e-7ea6-4ab9-b67d-67e9dd63897c/windowsdesktop-runtime-9.0.20-win-x64.exe", "56CA2926E797C3D124E13766F84D4F3B75A0D86B0D877E384CDA52FDD027E431");
            AddDotNet(p, "runtime", "8", "x86", "8.0.31", "https://download.microsoft.com/download/0487b495-7b7e-41cc-a798-c70779af69f7/7755e0c8-8484-4c50-b299-5279db224eaa/dotnet-runtime-8.0.31-win-x86.exe", "2C1691932F72D166DC4D5A9CF6ACCF94D94566FEECD3CFED205ED85BAEACF44C");
            AddDotNet(p, "runtime", "8", "x64", "8.0.31", "https://download.microsoft.com/download/0487b495-7b7e-41cc-a798-c70779af69f7/78fdbd4d-f97c-4573-968b-891787a3c5d6/dotnet-runtime-8.0.31-win-x64.exe", "249B0D10D4BFA8EC8DED5E6E220E871E71CCA89290F1A2FDB93CA59ABB8FF97B");
            AddDotNet(p, "runtime", "9", "x86", "9.0.20", "https://download.microsoft.com/download/d9b865de-33cd-4613-99d9-b75f307b177e/23b136de-97eb-4923-9c97-c19784eb54ec/dotnet-runtime-9.0.20-win-x86.exe", "413A1EABC23A3CF772D51500CAEB97DED1A0352891C8DE793E7996E7A2EE8A95");
            AddDotNet(p, "runtime", "9", "x64", "9.0.20", "https://download.microsoft.com/download/d9b865de-33cd-4613-99d9-b75f307b177e/e3e486f5-6d51-4725-8f70-df36f208679e/dotnet-runtime-9.0.20-win-x64.exe", "6ECB0EB560DDB25C9318D28CEFBA06C752F395BB9BE03EF78F6F4DBFF58F3249");
            p.Add(new RepairPackage("openal", "OpenAL", "x86/x64", "2.0.7.0", "https://www.openal.org/downloads/oalinst.zip", "B8F39714D41E009F75EFB183C37100F2CBABB71784BBD243BE881AC5B42D86FD", "CN=Creative Labs Inc, OU=CLI, OU=Digital ID Class 3 - Microsoft Software Validation v2, O=Creative Labs Inc, L=Milpitas, S=California, C=US", new Version(6, 1, 7601), "openal", "/silent", "/silent", "D165BCB7628FD950D14847585468CC11943B2A1DA92A59A839D397C68F9D4B06"));
            p.Add(new RepairPackage("vstor-2010", "Visual Studio Tools for Office Runtime 2010", "x86/x64", "10.0.60917", "https://download.microsoft.com/download/5/d/2/5d24f8f8-efbb-4b63-aa33-3785e3104713/vstor_redist.exe", "CFE1A40BBE4A50022DB2164ABDB0154984E2CECB761A23CDC81CB5754F6E0A18", MicrosoftPublisher, new Version(10, 0), "vstor", "/q /norestart", "/repair /q /norestart"));
            return p;
        }

        private static void AddVc(List<RepairPackage> p, string year, string arch, string version, string url, string hash, string publisher)
        {
            bool legacy = year == "2005" || year == "2008";
            p.Add(new RepairPackage("vc-" + year + "-" + arch, "Visual C++ " + year + " (" + arch + ")", arch, version,
                url, hash, publisher, new Version(6, 1, 7601), "vc", legacy ? "/q" : year == "2010" ? "/quiet /norestart /log \"{log}\"" : "/install /quiet /norestart /log \"{log}\"",
                legacy ? String.Empty : "/repair /quiet /norestart /log \"{log}\""));
        }
        private static void AddV14(List<RepairPackage> p, string arch, bool win7, string url, string hash)
        {
            p.Add(new RepairPackage("vc-2015plus-" + (win7 ? "win7-" : "") + arch,
                "Visual C++ v14 " + (win7 ? "Win7 兼容分支" : "共享运行库") + " (" + arch + ")", arch,
                win7 ? "14.29.30139" : "14.51.36247", url, hash, MicrosoftPublisher,
                win7 ? new Version(6, 1, 7601) : new Version(10, 0), "vc", "/install /quiet /norestart /log \"{log}\"", "/repair /quiet /norestart /log \"{log}\""));
        }
        private static void AddDotNet(List<RepairPackage> p, string kind, string major, string arch, string version, string url, string hash)
        {
            p.Add(new RepairPackage("dotnet-" + kind + "-" + major + "-" + arch,
                ".NET " + major + (kind == "desktop" ? " Desktop Runtime" : " Runtime") + " (" + arch + ")", arch,
                version, url, hash, DotNetPublisher, new Version(10, 0), "dotnet", "/install /quiet /norestart /log \"{log}\"", "/repair /quiet /norestart /log \"{log}\""));
        }
    }
}
