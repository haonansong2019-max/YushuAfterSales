# 钰叔售后

Windows 7 SP1 / Windows 10 / Windows 11 的运行库只读检查、官方组件目录、系统修复入口和诊断报告工具。

## 构建

需要 Windows、.NET SDK 和 .NET Framework 4.8 Developer/Targeting Pack（CI 使用参考程序集 NuGet 包）。

```powershell
dotnet restore .\YushuAfterSales.csproj
dotnet build .\YushuAfterSales.csproj -c Release --no-restore
dotnet build .\SelfTest\SelfTest.csproj -c Release
.\SelfTest\bin\Release\net48\SelfTest.exe
```

组件安装包不随程序打包。软件只提供厂商官方来源和受控执行入口；下载后校验 SHA-256 与 Authenticode 发布者。系统修复前需要 UAC 确认并尝试创建还原点。扫描与导出诊断包不修改系统，也不会自动启动用户选择的游戏程序。

## 报告

本地报告写入 `%LocalAppData%\CSYUSHU\YushuAfterSales\Reports`。分享 ZIP 由用户主动生成，包含 JSON 结构化报告、发现项、动作日志、环境快照和 SHA-256 清单。报告生成前会对敏感字段和用户路径做脱敏。

## 在线服务

在线升级与 `ysrepair` 授权通过 `appsettings.json` 指定独立 HTTPS manifest/API 地址。服务端契约见 `docs/online-services.md`。仓库不含 VPS 或 GitHub 凭据，在线功能必须由运维方配置独立的产品路由后才能启用。

## 许可证

源码采用 MIT。微软、游戏平台及其他厂商的运行库安装包不属于本仓库，不属于开源源码。
