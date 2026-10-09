# 钰叔售后设计交接

更新时间：2026-10-09

## 已确认决策

用户已确认：

1. 加入钰叔软件在线升级模块。
2. 加入钰叔APPID加密方式，AppID：`ysrepair`。
3. 使用 UIA。
4. 首版支持 Windows 7、Windows 10、Windows 11，采用系统自适应运行库目录。
5. 只通过官方运行库、SFC/DISM、签名和哈希校验修复，不使用随机 DLL 来源。
6. 首版以运行库、DirectX、VC++、.NET、SFC/DISM 为核心，其他组件先做官方目录。
7. 客服采用本地报告、帮助文档和客服链接，不内置通用远程控制。
8. 项目源码采用 MIT。
9. 视觉采用 UIA/Codex 基线，并吸收附件截图的左侧功能分组。

## 待确认设计树

- VC++、DirectX、.NET、OpenAL、MSXML、Java 和游戏平台的具体首发清单。
- 检测、修复、重装、卸载、跳过和回滚的权限与确认规则。
- 在线模式、离线包、缓存保留和来源失败时的行为。
- 报告格式、敏感信息脱敏和导出字段。
- Windows 7 的受限组件与提示文案。
- 在线升级清单地址、签名/哈希策略和 APPID 服务契约。
- 构建、启动、按钮操作、实际修复和发布包验证证据。

## 第二轮已确认

1. 首发运行库为 VC++ 2005/2008/2010/2012/2013、2015–2022 x86/x64、VSTOR、UCRT、DirectX June 2010 legacy、.NET 3.5/4.8 和兼容的 .NET Desktop Runtime；其他组件先做官方目录。
2. 默认只读扫描，只修复缺失或异常项目；强制重装单独确认，不默认卸载。
3. 扫描无需管理员；修复时触发 UAC；SFC/DISM 和系统级操作前创建还原点。
4. 官方 HTTPS 源优先，下载后缓存，使用前重新校验哈希和签名，支持导入离线包。
5. 生成 JSON、HTML 和日志 ZIP，并脱敏路径、序列号和授权信息。
6. Windows 7 显示兼容旧版组件，不支持的最新包明确提示。
7. 使用 C# WPF .NET Framework 4.8 单程序兼容 Win7 SP1/10/11。
8. 在线升级先做可替换 HTTPS manifest 和本地模拟服务。
9. `ysrepair` 使用 DPAPI、注册表镜像、机器绑定和 7 天离线宽限；失效后可查看报告但禁止修复。

## 第三轮已确认

1. 左侧导航为全面扫描、运行库修复、DirectX 修复、系统修复、游戏组件、诊断报告；设置/授权/升级置于底部。
2. 用户点击开始后执行只读扫描，完成后生成待修复清单。
3. 修复流程为扫描结果、清单确认、执行修复、实时进度、报告。
4. 错误诊断支持错误码、EXE 路径、文件版本和依赖线索，不自动启动目标程序。
5. .NET 3.5 优先使用 Windows 可选功能，失败后使用官方安装源。
6. OpenAL、MSXML、Java 和游戏平台显示版本、官方来源、哈希和兼容性，按用户点击受控安装。
7. 首版交付便携 ZIP，包含 EXE、配置模板、第三方许可和使用说明。
8. 报告默认本地保存；用户明确导出或提交客服后才生成分享包，不自动上传。
9. 执行前显示目标组件、操作、权限、还原点状态和脱敏命令摘要。
10. 授权失效后保留扫描、历史报告和导出，禁用修复、安装和升级。

## Codex 可定位报告约定（待最终规格确认）

报告分享包拟包含：

- `report.json`：固定 `schemaVersion`、报告 ID、时间、应用版本、系统环境、授权状态摘要、扫描请求、运行库清单、发现项、动作和结论。
- `inventory.json`：组件 ID、架构、检测版本、期望版本、来源、签名、SHA-256、兼容性和状态。
- `actions.jsonl`：每个动作一行，记录动作 ID、类型、开始/结束时间、权限、退出码、重启码、结果和关联证据 ID。
- `findings.json`：错误码、严重级别、证据 ID、根因候选、建议动作和是否阻断修复。
- `environment.json`：Windows 版本/构建/架构、.NET、语言区域、管理员状态和关键系统能力；不含原始机器指纹。
- `logs/`：SFC、DISM、安装器和应用日志原文，统一 UTF-8/时间戳；敏感路径和授权字段脱敏。
- `files.json`：分享包内每个文件的相对路径、大小和 SHA-256。
- `README.html`：给客户看的摘要和给 Codex 定位用的证据索引。

报告设计目标是：客户把压缩包发来后，可以先读 `report.json`/`findings.json`，再沿 `evidenceId` 查动作、原始日志和文件哈希。

## 实施约束

- 不手工处理 DPI。
- 不把微软运行库二进制称为开源代码。
- 不在未完成设计树和用户确认前开始业务实现。

## 2026-10-09 实施补充

- 用户已确认开始实现；本条设计树原“待确认/尚未开始”状态由当前实现阶段覆盖。
- GitHub 候选调研见 `docs/github-reference-projects.md`；只复用许可兼容的清单/检测/UI 编排思路。
- 客户端授权 API 的真实响应字段、签名密钥与续期协议尚未从服务端得到确认；未配置独立 ysrepair HTTPS 地址时 fail-closed，在线流程不得据猜测宣称已验证。
- GitHub 仓库已创建并推送：`https://github.com/haonansong2019-max/YushuAfterSales`，默认分支 `main`，本地提交 `363a2705fd5c0b9acfac7c68e63f3c2dc4f1abfa`。GitHub Actions `Windows CI` 运行 `37882898596` 已成功，构建产物 `YushuAfterSales-portable` 已上传。
- VPS 凭据只存在于用户文档，不复制到源码、报告、命令输出或日志；仅能在目标主机与 ysrepair 独立路由明确后核验部署契约。稳定升级清单和发行物仍须用户明确“定稿并推送”。
- WinGet 上游清单不是本产品固定的下载哈希/发布者策略；在线稳定前，自动安装暂时关闭，保留只读检测和用户主动打开的官方来源。
- 授权回执须带 request_id 并以固定公钥验证 RSA-SHA256；公钥不能取自同一份可编辑配置。当前没有已部署服务公钥，因此在线授权继续 fail-closed。

## 2026-10-09 录屏需求实现候选

- 按录屏实现逐项可见扫描、缺失/异常状态、软件内确认计划、官方包下载、SHA-256/Authenticode 二次校验、UAC 安装和安装后重扫；DirectX 外包解压后实际运行 DXSETUP，不把打开网页当修复。
- 核心页面为运行库、DirectX、DLL；新增游戏组件和 Windows Update 驱动检测/安装页。驱动仅使用 Windows Update 匹配项，服务禁用时显示原因并提供受授权的启用/重查计划。
- 正式版 `bin/Release/net48/YushuAfterSales.exe` SHA-256：`6B0F35D0C4FAF12AEFFD93E897284A148150987191900A5C58B0A77B7F884F1E`。
- 便携候选 `artifacts/YushuAfterSales-portable-20261009-1955.zip` SHA-256：`ADB3084625F6C7037BCC01BABB252B409E0FA8925C967CA7B6BF5FA7B093771F`。
- 未加密功能测试候选通过 `FunctionalTestBuild=true` 单独编译，EXE SHA-256：`1EB8A96DFE008E66487B9BB1640DEFB4A8C269DB7054D92A75366F0D07802604`；仅用于本地安装流程验收，不作为正式发行物。
- UI 验收证据：`E:\codexxxx\work\ysrepair-video-20261009\validation\ui-release-final`，`UI_ACCEPTANCE_FAILURES=0`；功能烟测证据：`...\validation\ui-functional2`，失败数为 0。未执行真实安装、驱动安装、Windows Update 服务修改或 UAC。
- `SelfTest`、`AuthorizationSelfTest`、`RepairSelfTest --official-cache E:\codexxxx\work\ysrepair-package-verification` 均通过；RepairSelfTest 221 项，无安装器执行。

## 2026-10-09 核心三模块补充

- 运行库页：扫描后展示 VC++/.NET 检测结果和 VC++ 官方目录项，支持逐项勾选、架构、状态和 evidenceId；主操作先生成修复预览，不自动下载安装。
- DirectX 页：除平台注册表版本外，逐项检查 June 2010 常见 D3DX/XInput/XAudio/XAPOFX/X3DAudio 文件，并显示 x86/x64、状态、官方 Microsoft 来源和 evidenceId。
- DLL 页：新增独立导航和逐项清单，默认覆盖常见 XInput/D3DX/D3DCompiler/MSVC/UCRT DLL；只读、非递归、拒绝路径穿越，记录版本与 SHA-256；禁止从未知 DLL 网站下载或覆盖。
- 修复计划增加来源策略、RepairSupported 和 RepairAction 字段；当前默认 fail-closed，仅生成预览或打开官方来源，真实安装仍需受信哈希/签名清单和有效 ysrepair 授权。
- 本轮验证：主程序 Release 构建 0 errors；SelfTest `YUSHU_AFTER_SALES_SELFTEST=PASS`；AuthorizationSelfTest `AUTHORIZATION_SELFTEST=PASS`（19 项）；EXE 启动后清洁退出；本地便携包 `artifacts/YushuAfterSales-portable-20261009-180301.zip`，SHA-256 `FFDF7496A45478BDED160A0A572043B12B5F232FD5C59FD623851B4EC01360C5`；EXE SHA-256 `343623D5C77233C01E3CB731DFD35BD953127BCAD3CE46A860B69E004FC4D60D`。
