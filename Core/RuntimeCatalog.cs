using System;
using System.Collections.Generic;

namespace YushuAfterSales.Core
{
    public sealed class RuntimePackage
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public string Architecture { get; set; }
        public string WingetId { get; set; }
        public string OfficialUrl { get; set; }
        public bool SupportsWindows7 { get; set; }
        public string Notes { get; set; }
        public bool IsCatalogOnly { get; set; }
        public bool EndOfSupport { get; set; }
    }

    public static class RuntimeCatalog
    {
        public const string VcSource = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist";
        public const string DirectXSource = "https://www.microsoft.com/en-us/download/details.aspx?id=8109";
        public static IReadOnlyList<RuntimePackage> Packages { get; } = BuildPackages();

        private static List<RuntimePackage> BuildPackages()
        {
            var packages = new List<RuntimePackage>();
            foreach (string year in new[] { "2005", "2008", "2010", "2012", "2013", "2015plus" })
            foreach (string arch in new[] { "x86", "x64" })
                packages.Add(new RuntimePackage
                {
                    Id = "vc-" + year + "-" + arch,
                    DisplayName = "Visual C++ " + (year == "2015plus" ? "2015–2022 (v14)" : year) + " Redistributable (" + arch + ")",
                    Category = "VC++", Architecture = arch,
                    WingetId = year == "2015plus" ? "Microsoft.VCRedist.2015+." + arch : "Microsoft.VCRedist." + year,
                    OfficialUrl = VcSource, SupportsWindows7 = true,
                    Notes = year == "2015plus" ? "v14 系列共享兼容性；最新包需要 Windows 10/11，Win7 必须选兼容旧版。" : "历史游戏依赖；只在程序需要时使用微软原始可再发行包。"
                });
            packages.Add(new RuntimePackage { Id = "vstor-2010", DisplayName = "Visual Studio 2010 Tools for Office Runtime", Category = "VC++", Architecture = "system", OfficialUrl = "https://download.microsoft.com/download/5/d/2/5d24f8f8-efbb-4b63-aa33-3785e3104713/vstor_redist.exe", SupportsWindows7 = false, Notes = "固定受信 10.0.60917 包用于 Windows 10/11；Win7 不自动安装最新包。" });
            packages.Add(new RuntimePackage { Id = "ucrt", DisplayName = "Universal C Runtime (UCRT)", Category = "VC++", Architecture = "system", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment", SupportsWindows7 = true, Notes = "Windows 10/11 系统组件；Win7 为 KB2999226，系统文件通过 SFC/DISM 修复。" });
            packages.Add(new RuntimePackage { Id = "directx-jun2010", DisplayName = "DirectX End-User Runtime June 2010", Category = "DirectX", Architecture = "system", OfficialUrl = DirectXSource, SupportsWindows7 = true, Notes = "完整的旧版 D3DX、D3DCompiler、XInput、XACT 和 XAudio 组件，不替换系统 DirectX 平台。" });
            packages.Add(new RuntimePackage { Id = "dotnet-35", DisplayName = ".NET Framework 3.5（含 2.0/3.0）", Category = ".NET", Architecture = "system", OfficialUrl = "https://learn.microsoft.com/en-us/dotnet/framework/install/dotnet-35-windows", SupportsWindows7 = true, Notes = "优先启用 Windows 可选功能。" });
            packages.Add(new RuntimePackage { Id = "dotnet-48", DisplayName = ".NET Framework 4.8", Category = ".NET", Architecture = "system", OfficialUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48", SupportsWindows7 = true, Notes = "4.x 原位兼容更新，不并列安装 4.0/4.5/4.6/4.7；Win7 需要 SP1。" });
            foreach (string major in new[] { "8", "9" })
            foreach (string kind in new[] { "runtime", "desktop" })
            foreach (string arch in new[] { "x86", "x64" })
                packages.Add(new RuntimePackage
                {
                    Id = "dotnet-" + kind + "-" + major + "-" + arch,
                    DisplayName = ".NET " + (kind == "desktop" ? "Desktop " : "") + "Runtime " + major + " (" + arch + ")",
                    Category = ".NET", Architecture = arch,
                    WingetId = "Microsoft.DotNet." + (kind == "desktop" ? "DesktopRuntime." : "Runtime.") + major,
                    OfficialUrl = "https://dotnet.microsoft.com/download/dotnet/" + major + ".0",
                    SupportsWindows7 = false, Notes = "检测该主版本的共享框架；应用私有运行时不等于系统安装。"
                });
            packages.Add(new RuntimePackage { Id = "openal", DisplayName = "OpenAL", Category = "游戏组件", Architecture = "system", OfficialUrl = "https://www.openal.org/downloads/", SupportsWindows7 = true, Notes = "检测 OpenAL32.dll；通过官方 ZIP 及内部 Creative 安装器双 SHA-256、签名校验安装。" });
            packages.Add(new RuntimePackage { Id = "msxml", DisplayName = "MSXML 6.0", Category = "游戏组件", Architecture = "system", OfficialUrl = "https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ms763742(v=vs.85)", SupportsWindows7 = true, Notes = "Windows 系统组件，通过 SFC/DISM 修复。" });
            packages.Add(new RuntimePackage { Id = "java", DisplayName = "Java SE", Category = "游戏组件", Architecture = "system", OfficialUrl = "https://www.oracle.com/java/technologies/downloads/", SupportsWindows7 = false, IsCatalogOnly = true, Notes = "检测系统安装；版本/许可证取决于游戏，禁止自动替换应用自带 Java。" });
            packages.Add(new RuntimePackage { Id = "msxml-4", DisplayName = "MSXML 4.0（已停止支持）", Category = "游戏组件", Architecture = "system", OfficialUrl = "https://learn.microsoft.com/en-us/lifecycle/products/msxml-40", SupportsWindows7 = true, IsCatalogOnly = true, EndOfSupport = true, Notes = "已停止支持，只记录兼容性证据，不提供一键安装。" });
            packages.Add(new RuntimePackage { Id = "xna-40", DisplayName = "XNA Framework 4.0（已停止支持）", Category = "游戏组件", Architecture = "system", OfficialUrl = "https://learn.microsoft.com/en-us/lifecycle/products/microsoft-xna-framework-40", SupportsWindows7 = true, IsCatalogOnly = true, EndOfSupport = true, Notes = "历史游戏依赖，已停止支持；不从非官方站点下载安装。" });
            packages.Add(new RuntimePackage { Id = "steam", DisplayName = "Steam", Category = "游戏平台", Architecture = "system", OfficialUrl = "https://store.steampowered.com/about/", SupportsWindows7 = false, IsCatalogOnly = true, Notes = "游戏平台官方目录。" });
            packages.Add(new RuntimePackage { Id = "ea", DisplayName = "EA app", Category = "游戏平台", Architecture = "system", OfficialUrl = "https://www.ea.com/ea-app", SupportsWindows7 = false, IsCatalogOnly = true, Notes = "游戏平台官方目录。" });
            packages.Add(new RuntimePackage { Id = "ubisoft-connect", DisplayName = "Ubisoft Connect", Category = "游戏平台", Architecture = "system", OfficialUrl = "https://ubisoftconnect.com/", SupportsWindows7 = false, IsCatalogOnly = true, Notes = "游戏平台官方目录。" });
            return packages;
        }

        public static RuntimePackage Find(string id)
        {
            foreach (RuntimePackage package in Packages)
                if (String.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)) return package;
            return null;
        }
    }
}
