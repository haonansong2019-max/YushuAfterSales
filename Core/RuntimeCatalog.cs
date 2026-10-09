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
    }

    public static class RuntimeCatalog
    {
        public static IReadOnlyList<RuntimePackage> Packages { get; } = new List<RuntimePackage>
        {
            new RuntimePackage { Id = "vc-2005-x86", DisplayName = "Visual C++ 2005 Redistributable (x86)", Category = "VC++", Architecture = "x86", WingetId = "Microsoft.VCRedist.2005", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "历史组件；仅在目标程序明确需要时安装" },
            new RuntimePackage { Id = "vc-2008-x86", DisplayName = "Visual C++ 2008 Redistributable (x86)", Category = "VC++", Architecture = "x86", WingetId = "Microsoft.VCRedist.2008", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "历史组件；仅在目标程序明确需要时安装" },
            new RuntimePackage { Id = "vc-2010-x86", DisplayName = "Visual C++ 2010 Redistributable (x86)", Category = "VC++", Architecture = "x86", WingetId = "Microsoft.VCRedist.2010", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "历史组件；仅在目标程序明确需要时安装" },
            new RuntimePackage { Id = "vc-2012-x86", DisplayName = "Visual C++ 2012 Redistributable (x86)", Category = "VC++", Architecture = "x86", WingetId = "Microsoft.VCRedist.2012", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "历史组件；仅在目标程序明确需要时安装" },
            new RuntimePackage { Id = "vc-2013-x86", DisplayName = "Visual C++ 2013 Redistributable (x86)", Category = "VC++", Architecture = "x86", WingetId = "Microsoft.VCRedist.2013", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "历史组件；仅在目标程序明确需要时安装" },
            new RuntimePackage { Id = "vc-2015plus-x86", DisplayName = "Visual C++ 2015–2022 Redistributable (x86)", Category = "VC++", Architecture = "x86", WingetId = "Microsoft.VCRedist.2015+.x86", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "v14 系列共享二进制兼容性" },
            new RuntimePackage { Id = "vc-2015plus-x64", DisplayName = "Visual C++ 2015–2022 Redistributable (x64)", Category = "VC++", Architecture = "x64", WingetId = "Microsoft.VCRedist.2015+.x64", OfficialUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist", SupportsWindows7 = true, Notes = "v14 系列共享二进制兼容性" },
            new RuntimePackage { Id = "vstor-2010", DisplayName = "Visual Studio 2010 Tools for Office Runtime", Category = "VC++", Architecture = "x86/x64", WingetId = "", OfficialUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=48217", SupportsWindows7 = true, Notes = "官方来源目录；安装前必须验证下载包签名和哈希" },
            new RuntimePackage { Id = "directx-jun2010", DisplayName = "DirectX End-User Runtime June 2010", Category = "DirectX", Architecture = "x86/x64", WingetId = "", OfficialUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=8109", SupportsWindows7 = true, Notes = "补充旧版 D3DX/XInput 等组件，不替换系统 DirectX 版本" },
            new RuntimePackage { Id = "dotnet-35", DisplayName = ".NET Framework 3.5（2.0/3.0/3.5）", Category = ".NET", Architecture = "system", WingetId = "", OfficialUrl = "https://learn.microsoft.com/en-us/dotnet/framework/install/dotnet-35-windows", SupportsWindows7 = true, Notes = "首选 Windows 可选功能" },
            new RuntimePackage { Id = "dotnet-48", DisplayName = ".NET Framework 4.8", Category = ".NET", Architecture = "x86/x64", WingetId = "Microsoft.DotNet.Framework.DeveloperPack_4", OfficialUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48", SupportsWindows7 = true, Notes = "Win7 需要 SP1" },
            new RuntimePackage { Id = "dotnet-desktop-8", DisplayName = ".NET Desktop Runtime 8", Category = ".NET", Architecture = "x86/x64", WingetId = "Microsoft.DotNet.DesktopRuntime.8", OfficialUrl = "https://dotnet.microsoft.com/download/dotnet/8.0", SupportsWindows7 = false, Notes = "Windows 10/11；Win7 显示不兼容" },
            new RuntimePackage { Id = "openal", DisplayName = "OpenAL", Category = "游戏组件", Architecture = "x86/x64", WingetId = "", OfficialUrl = "https://www.openal.org/downloads/", SupportsWindows7 = true, Notes = "官方来源目录，安装前检查发布者和签名" },
            new RuntimePackage { Id = "msxml", DisplayName = "MSXML", Category = "游戏组件", Architecture = "x86/x64", WingetId = "", OfficialUrl = "https://learn.microsoft.com/en-us/previous-versions/windows/desktop/msxml/msxml", SupportsWindows7 = true, Notes = "仅按目标软件要求选择版本" },
            new RuntimePackage { Id = "java", DisplayName = "Java SE", Category = "游戏组件", Architecture = "x86/x64", WingetId = "Oracle.JavaRuntimeEnvironment", OfficialUrl = "https://www.oracle.com/java/technologies/downloads/", SupportsWindows7 = false, Notes = "官方来源目录；许可证和版本需用户确认" },
            new RuntimePackage { Id = "steam", DisplayName = "Steam", Category = "游戏平台", Architecture = "x86/x64", WingetId = "Valve.Steam", OfficialUrl = "https://store.steampowered.com/about/", SupportsWindows7 = false, Notes = "仅提供官方来源" },
            new RuntimePackage { Id = "ea", DisplayName = "EA app", Category = "游戏平台", Architecture = "x86/x64", WingetId = "ElectronicArts.EADesktop", OfficialUrl = "https://www.ea.com/ea-app", SupportsWindows7 = false, Notes = "仅提供官方来源" },
            new RuntimePackage { Id = "ubisoft-connect", DisplayName = "Ubisoft Connect", Category = "游戏平台", Architecture = "x86/x64", WingetId = "Ubisoft.Connect", OfficialUrl = "https://ubisoftconnect.com/", SupportsWindows7 = false, Notes = "仅提供官方来源" }
        };

        public static RuntimePackage Find(string id)
        {
            foreach (RuntimePackage package in Packages)
                if (String.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)) return package;
            return null;
        }
    }
}
