using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YushuAfterSales.Core;
using YushuAfterSales.Reporting;

namespace YushuAfterSales
{
    public partial class MainWindow : Window
    {
        private readonly IDictionary<string, string> _pageTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "overview", "全面扫描" },
            { "runtime", "运行库修复" },
            { "directx", "DirectX 修复" },
            { "system", "系统修复" },
            { "components", "游戏组件" },
            { "reports", "诊断报告" },
            { "settings", "设置" },
            { "license", "授权状态" },
            { "updates", "在线升级" }
        };
        private ReportDocument _currentReport;
        private string _currentPageKey = "overview";
        private AuthorizationState _authorization;
        private readonly List<ReportDocument> _reportHistory = new List<ReportDocument>();
        private readonly List<DiagnosticLogEntry> _diagnosticLogs = new List<DiagnosticLogEntry>();
        private bool _isEnglish;
        private string _theme = "dark";
        private bool _systemActionRunning;

        public MainWindow()
        {
            InitializeComponent();
            _authorization = AuthorizationService.Load();
            LoadPreferences();
            _reportHistory.AddRange(ReportHistoryStore.LoadRecent(100));
            ShowPage("overview");
        }

        private void BrandButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("overview");
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var key = button == null ? "overview" : button.Tag as string;
            ShowPage(key);
        }

        private void ShowPage(string key)
        {
            if (string.IsNullOrEmpty(key) || !_pageTitles.ContainsKey(key))
            {
                key = "overview";
            }

            var title = Localize(_pageTitles[key], EnglishPageTitle(key));
            _currentPageKey = key;
            ToolbarTitle.Text = title;
            ToolbarStatus.Text = key == "overview" ? Localize("准备就绪", "Ready") : GetPageStatus(key);
            OverviewPage.Visibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;
            ModulePage.Visibility = key == "overview" ? Visibility.Collapsed : Visibility.Visible;
            ExportReportButton.Visibility = key == "overview" && _currentReport != null ? Visibility.Visible : Visibility.Collapsed;
            ExportModuleReportButton.Visibility = (key == "reports" && _reportHistory.Count > 0) || (key != "overview" && key != "reports" && _currentReport != null) ? Visibility.Visible : Visibility.Collapsed;
            OpenReportFolderButton.Visibility = key == "reports" ? Visibility.Visible : Visibility.Collapsed;
            OpenSourceListButton.Visibility = key == "runtime" || key == "directx" || key == "components" ? Visibility.Visible : Visibility.Collapsed;
            ModulePrimaryButton.Visibility = key == "runtime" || key == "directx" || key == "system" || key == "license" || key == "updates" || key == "reports" ? Visibility.Visible : Visibility.Collapsed;
            ModulePrimaryButton.Content = key == "runtime" || key == "directx" ? Localize("打开选中项官方来源", "Open selected official source") : key == "system" ? Localize("执行所选系统修复", "Run selected system repair") : key == "license" ? Localize("在线刷新授权", "Refresh license online") : Localize("检查并下载更新", "Check and download update");
            if (key == "reports") ModulePrimaryButton.Content = Localize("导出所选报告", "Export selected report");
            ModulePrimaryButton.IsEnabled = key == "runtime" || key == "directx" || key == "license" || key == "updates" || (key == "system" && _authorization != null && _authorization.CanRepair);
            if (key == "reports") ModulePrimaryButton.IsEnabled = _reportHistory.Count > 0;
            if (key == "license" && (_authorization == null || !_authorization.CanRepair)) ModulePrimaryButton.Content = Localize("刷新授权状态", "Refresh license status");
            if (key == "system" && _systemActionRunning) ModulePrimaryButton.IsEnabled = false;
            ModulePrimaryButton.ToolTip = key == "runtime" || key == "directx" ? Localize("自动安装暂不可用；只打开选中项目的官方来源，不会替你安装。", "Automatic installation is unavailable. This only opens the selected vendor source.") : null;
            foreach (ComboBoxItem item in SystemActionCombo.Items)
            {
                if (item.Content != null && item.Content.ToString().Contains(".NET"))
                    item.Content = Localize("启用 .NET Framework 3.5", "Enable .NET Framework 3.5");
            }
            SystemActionCombo.Visibility = key == "system" ? Visibility.Visible : Visibility.Collapsed;

            if (key != "overview")
            {
                PlaceholderTitle.Text = title;
                PlaceholderDescription.Text = GetDescription(key);
                PlaceholderStatus.Text = GetStatusText(key);
                ModuleEvidence.Text = GetEvidenceText(key);
                ModuleGrid.ItemsSource = GetModuleItems(key);
            }
            AuthorizationActivationPanel.Visibility = key == "license" ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetPageStatus(string key)
        {
            if (key == "license") return _authorization == null ? Localize("未读取", "Not loaded") : _authorization.State;
            if (key == "reports") return _reportHistory.Count == 0 ? Localize("暂无报告", "No reports") : Localize("已保存 " + _reportHistory.Count + " 份", _reportHistory.Count + " saved");
            return Localize("待用户确认", "Awaiting user action");
        }

        private string GetStatusText(string key)
        {
            if (_currentReport == null && key != "reports" && key != "license") return Localize("尚未完成扫描；当前页面不会自动修改系统。", "No scan has completed. This page will not change your system automatically.");
            if (key == "license") return Localize("AppID：ysrepair；状态：", "AppID: ysrepair; status: ") + (_authorization == null ? "unknown" : _authorization.State) + (_isEnglish ? "." : "。");
            if (key == "reports") return Localize("已保存 " + _reportHistory.Count + " 份本地诊断报告；选择一行后可导出该报告的证据包。", _reportHistory.Count + " local diagnostic reports saved. Select one to export its evidence package.");
            return Localize("扫描报告已载入；任何修复动作都需要单独确认。", "Scan report loaded. Every repair action requires separate confirmation.");
        }

        private string GetEvidenceText(string key)
        {
            if (_currentReport == null) return Localize("扫描后这里会显示可追溯的证据 ID 和状态。", "Traceable evidence IDs and status will appear here after a scan.");
            if (key == "runtime" || key == "directx")
            {
                int missing = _currentReport.Inventory.Count(x => x.Status == "missing" || x.Status == "missing-or-unknown" || x.Status == "unknown");
                return Localize("证据：", "Evidence: ") + _currentReport.ReportId + Localize("；待复核项目：", "; items to review: ") + missing + Localize("；来源和哈希会在安装前再次校验。", "; sources and hashes are checked again before installation.");
            }
            return Localize("证据索引：", "Evidence index: ") + _currentReport.ReportId + Localize("；导出后可按 evidenceId 关联 actions.jsonl 和 logs。", "; export links evidenceId to actions.jsonl and logs.");
        }

        private string GetDescription(string key)
        {
            switch (key)
            {
                case "runtime": return Localize("查看本机运行库证据，选择项目后可打开厂商官方来源；自动安装在配置受信安装策略前关闭。", "Review runtime evidence and open vendor sources. Automatic installation stays disabled until a trusted install policy is configured.");
                case "directx": return Localize("查看 DirectX legacy 组件证据并打开 Microsoft 官方来源；不替换 Windows DirectX 系统版本。", "Review legacy DirectX evidence and open Microsoft sources. Windows system DirectX is not replaced.");
                case "system": return Localize("通过 UAC 调用 SFC、DISM 或 Windows .NET 3.5 可选功能；执行前尝试创建还原点。", "Run SFC, DISM, or the Windows .NET 3.5 feature through UAC. A restore point is attempted first.");
                case "components": return Localize("OpenAL、MSXML、Java 与游戏平台只显示官方来源和兼容性信息。", "OpenAL, MSXML, Java, and game platforms show official sources and compatibility information only.");
                case "reports": return Localize("浏览本机保存的扫描历史；导出时生成脱敏 JSON、动作记录、日志索引和 SHA-256 清单。", "Browse local scan history. Exports include redacted JSON, action records, a log index, and SHA-256 manifest.");
                case "license": return Localize("AppID：ysrepair；服务未配置或回执签名无效时，修复保持禁用。", "AppID: ysrepair. Repairs remain disabled when the service is unconfigured or its reply signature is invalid.");
                case "updates": return Localize("从产品专用 HTTPS 清单检查版本，下载后校验 SHA-256；清单未配置时会明确失败。", "Check versions from the product HTTPS manifest and verify SHA-256 after download. An unconfigured manifest fails closed.");
                case "settings": return Localize("主题、语言和侧栏状态保存在当前 Windows 用户的本地设置中。", "Theme, language, and sidebar state are saved in this Windows user's local settings.");
                default: return Localize("此模块将在完成检测服务和报告契约后启用。", "This module will be enabled when its detection service and report contract are ready.");
            }
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                _diagnosticLogs.Clear();
                _currentReport = WindowsRepairService.Scan(GetApplicationVersion());
                _currentReport.Authorization = ToAuthorizationSummary(_authorization);
                _reportHistory.Insert(0, _currentReport);
                ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                FindingsGrid.ItemsSource = _currentReport.Inventory.Select(ToInventoryRow).ToList();
                OverviewStatus.Text = _currentReport.Conclusion.Summary;
                OverviewEvidence.Text = Localize("报告 ID：", "Report ID: ") + _currentReport.ReportId + Localize("；清单 ", "; inventory ") + _currentReport.Inventory.Count + Localize(" 项；发现 ", "; findings ") + _currentReport.Findings.Count + (_isEnglish ? "." : " 项。");
                ToolbarStatus.Text = Localize("扫描完成", "Scan complete");
                ShowPage("overview");
            }
            catch (Exception ex)
            {
                OverviewStatus.Text = Localize("扫描失败：", "Scan failed: ") + SensitiveDataRedactor.Redact(ex.Message);
                ToolbarStatus.Text = Localize("扫描失败", "Scan failed");
            }
            finally
            {
                Cursor = null;
            }
        }

        private void ErrorDiagnosisButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ErrorDiagnosisWindow();
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            try
            {
                if (_currentReport == null)
                {
                    _diagnosticLogs.Clear();
                    _currentReport = WindowsRepairService.Scan(GetApplicationVersion());
                    _currentReport.Authorization = ToAuthorizationSummary(_authorization);
                    _reportHistory.Insert(0, _currentReport);
                }
                _currentReport.Scan.ErrorCode = dialog.ErrorCode;
                if (!String.IsNullOrWhiteSpace(dialog.TargetPath))
                {
                    InventoryEntry target = ReportBuilder.CollectExecutableEvidence(dialog.TargetPath);
                    _currentReport.Inventory.Add(target);
                    _currentReport.Scan.TargetExecutable = target.PackageFile;
                    _currentReport.Scan.TargetExecutableSha256 = target.Sha256;
                }
                _currentReport.Findings.Add(new FindingRecord
                {
                    FindingId = "finding-error-code-" + Guid.NewGuid().ToString("N"),
                    Code = "USER-ERROR-CODE",
                    Severity = "info",
                    Title = "用户提供错误码",
                    Message = "错误码已记录为诊断线索，不会自动启动目标程序。",
                    EvidenceIds = String.IsNullOrWhiteSpace(dialog.TargetPath)
                        ? new List<string> { "evidence-user-error-code" }
                        : new List<string> { "evidence-user-error-code", "evidence-target-executable" },
                    RootCauseCandidates = new List<string> { "运行库缺失或损坏", "目标程序架构与运行库架构不匹配", "系统组件状态异常" },
                    SuggestedActions = new List<string> { "复核运行库清单", "检查目标文件版本和哈希", "按预览执行受控修复" },
                    BlocksRepair = false
                });
                _diagnosticLogs.Add(new DiagnosticLogEntry
                {
                    Name = "error-code-diagnosis.log",
                    Source = "YushuAfterSales.ErrorDiagnosis",
                    CapturedUtc = DateTime.UtcNow.ToString("o"),
                    EvidenceId = "evidence-user-error-code",
                    Content = "User supplied error code: " + dialog.ErrorCode + Environment.NewLine +
                        (String.IsNullOrWhiteSpace(dialog.TargetPath) ? "No executable selected." : "Target executable evidence: evidence-target-executable.")
                });
                ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                FindingsGrid.ItemsSource = _currentReport.Inventory.Select(ToInventoryRow).ToList();
                OverviewStatus.Text = Localize("错误码已加入报告：", "Error code added to report: ") + dialog.ErrorCode;
                OverviewEvidence.Text = Localize("错误码证据：evidence-user-error-code；目标程序不会被自动启动。", "Error code evidence: evidence-user-error-code. The target application was not started.");
                ToolbarStatus.Text = Localize("诊断线索已记录", "Diagnostic clues recorded");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localize("诊断线索未记录：", "Diagnostic clues were not recorded: ") + SensitiveDataRedactor.Redact(ex.Message), Localize("错误码诊断", "Error diagnosis"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPageKey == "overview")
            {
                if (_currentReport == null) { ToolbarStatus.Text = Localize("请先开始只读扫描", "Start a read-only scan first"); return; }
                ScanButton_Click(sender, e);
                return;
            }
            if (_currentPageKey == "license") { await RefreshAuthorizationOnlineAsync(); return; }
            if (_currentPageKey == "reports")
            {
                _reportHistory.Clear();
                _reportHistory.AddRange(ReportHistoryStore.LoadRecent(100));
                ShowPage("reports");
                ToolbarStatus.Text = Localize("已重新载入本地报告历史", "Local report history reloaded");
                return;
            }
            ShowPage(_currentPageKey);
            ToolbarStatus.Text = Localize("已刷新本地数据", "Local data refreshed");
        }

        private void CloseModuleButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPage("overview");
        }

        private void AppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            ThemeMenuTitle.Text = _isEnglish ? "Appearance" : "主题";
            ThemeMenu.Visibility = ThemeMenu.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            bool collapsed = SidebarColumn.Width.Value > 0;
            SidebarColumn.Width = collapsed ? new GridLength(0) : new GridLength(216);
            Sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            SidebarExpandButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            SidebarCollapseButton.Content = collapsed ? "▶" : "◀";
            SidebarCollapseButton.ToolTip = collapsed ? (_isEnglish ? "Expand navigation" : "展开导航栏") : (_isEnglish ? "Collapse navigation" : "收起导航栏");
            System.Windows.Automation.AutomationProperties.SetName(SidebarCollapseButton, SidebarCollapseButton.ToolTip.ToString());
            SavePreferences();
        }

        private void ToggleLanguageButton_Click(object sender, RoutedEventArgs e)
        {
            _isEnglish = !_isEnglish;
            LanguageButton.Content = _isEnglish ? "中" : "EN";
            LanguageButton.ToolTip = _isEnglish ? "切换为简体中文" : "Switch to English";
            ApplyLanguage();
            SavePreferences();
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            string theme = button == null ? "dark" : button.Tag as string;
            ApplyTheme(theme);
            ThemeMenu.Visibility = Visibility.Collapsed;
            SavePreferences();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) { ThemeMenu.Visibility = Visibility.Collapsed; }
        }

        private async void ModulePrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPageKey == "license") { await RefreshAuthorizationOnlineAsync(); return; }
            if (_currentPageKey == "reports") { ExportReportButton_Click(sender, e); return; }
            if (_currentPageKey == "updates")
            {
                await CheckForUpdatesAsync();
                return;
            }
            if (_currentPageKey == "runtime" || _currentPageKey == "directx") { OpenOfficialButton_Click(sender, e); return; }
            if (_currentReport == null)
            {
                MessageBox.Show(Localize("请先执行只读扫描。", "Run a read-only scan first."), "Yushu Support", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_authorization == null || !_authorization.CanRepair)
            {
                MessageBox.Show(Localize("当前授权状态为“", "Current license status is '") + (_authorization == null ? "unknown" : _authorization.State) + Localize("”。扫描和报告可用，修复与安装需要有效授权。", "'. Scanning and reports remain available; repairs and installs require a valid license."), Localize("授权限制", "License required"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_currentPageKey == "system")
            {
                await RunSystemRepairAsync();
            }
        }

        private async Task RefreshAuthorizationOnlineAsync()
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            ModulePrimaryButton.IsEnabled = false;
            try
            {
                _authorization = await AuthorizationService.RefreshOnlineAsync();
                ShowPage("license");
                PlaceholderStatus.Text = Localize("在线刷新结果：", "Online refresh result: ") + _authorization.State + (_authorization.CanRepair ? Localize("。授权有效。", ". License is valid.") : Localize("。修复、安装和升级保持禁用。", ". Repair, install, and update remain disabled."));
                ToolbarStatus.Text = _authorization.State;
                if (_currentReport != null)
                {
                    _currentReport.Authorization = ToAuthorizationSummary(_authorization);
                    ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                }
            }
            catch (Exception ex)
            {
                _authorization = AuthorizationService.Load();
                ShowPage("license");
                PlaceholderStatus.Text = Localize("在线授权刷新失败：", "Online license refresh failed: ") + SensitiveDataRedactor.Redact(ex.Message) + Localize("；不会延长离线宽限。", "; offline grace is not extended.");
                ToolbarStatus.Text = Localize("授权刷新失败", "License refresh failed");
            }
            finally { ShowPage(_currentPageKey); Cursor = null; }
        }

        private async void ActivateAuthorizationButton_Click(object sender, RoutedEventArgs e)
        {
            string code = AuthorizationCodeBox.Password;
            if (String.IsNullOrWhiteSpace(code))
            {
                AuthorizationCodeBox.Clear();
                PlaceholderStatus.Text = Localize("请输入授权卡密。卡密仅在本次请求中使用，不会写入报告。", "Enter a license code. It is used only for this request and is not written to reports.");
                return;
            }
            AuthorizationActivateButton.IsEnabled = false;
            ModulePrimaryButton.IsEnabled = false;
            Cursor = System.Windows.Input.Cursors.Wait;
            try
            {
                _authorization = await AuthorizationService.ActivateAsync(code);
                ShowPage("license");
                PlaceholderStatus.Text = Localize("在线激活结果：", "Online activation result: ") + _authorization.State + (_authorization.CanRepair ? Localize("。授权有效。", ". License is valid.") : Localize("。修复、安装和升级保持禁用。", ". Repair, install, and update remain disabled."));
                ToolbarStatus.Text = _authorization.State;
                if (_currentReport != null)
                {
                    _currentReport.Authorization = ToAuthorizationSummary(_authorization);
                    ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                }
            }
            catch (Exception ex)
            {
                _authorization = AuthorizationService.Load();
                PlaceholderStatus.Text = Localize("在线激活失败：", "Online activation failed: ") + SensitiveDataRedactor.Redact(ex.Message) + (_isEnglish ? "." : "。");
                ToolbarStatus.Text = Localize("激活失败", "Activation failed");
            }
            finally { AuthorizationCodeBox.Clear(); AuthorizationActivateButton.IsEnabled = true; ShowPage(_currentPageKey); Cursor = null; }
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_authorization == null || !_authorization.CanRepair)
            {
                PlaceholderStatus.Text = Localize("授权未生效，在线升级已禁用。请先完成 ysrepair 在线授权。", "A valid license is required. Online updates are disabled until ysrepair activation succeeds.");
                ToolbarStatus.Text = Localize("授权限制", "License required");
                return;
            }
            if (!UpdateService.IsConfigured)
            {
                PlaceholderStatus.Text = Localize("尚未配置 ysrepair 专用 HTTPS 更新清单地址；未连接其他产品服务器。", "The ysrepair HTTPS update manifest is not configured. No other product server was contacted.");
                ToolbarStatus.Text = Localize("更新未配置", "Update not configured");
                return;
            }
            Cursor = System.Windows.Input.Cursors.Wait;
            ModulePrimaryButton.IsEnabled = false;
            try
            {
                UpdateManifest manifest = await Task.Run(() => UpdateService.FetchManifest());
                Version current;
                Version.TryParse(GetApplicationVersion(), out current);
                if (!UpdateService.IsNewerVersion(manifest.Version, current))
                {
                    PlaceholderStatus.Text = Localize("当前已是最新版本 ", "Already up to date: ") + GetApplicationVersion() + (_isEnglish ? "." : "。");
                    ToolbarStatus.Text = Localize("已是最新版本", "Up to date");
                    return;
                }
                if (MessageBox.Show(Localize("发现版本 ", "Version ") + manifest.Version + Localize("。下载后会根据清单校验 SHA-256。是否下载？", ". Download and verify its SHA-256 against the manifest?"), Localize("在线升级", "Online update"), MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "Updates");
                UpdateDownloadResult download = await Task.Run(() => UpdateService.DownloadVerified(manifest, cache, System.Threading.CancellationToken.None));
                PlaceholderStatus.Text = Localize("更新 ZIP 已下载并通过 SHA-256 校验：", "Update ZIP downloaded and SHA-256 verified: ") + Path.GetFileName(download.FilePath) + Localize("；当前程序尚未自动替换。", "; the application has not been replaced automatically.");
                ToolbarStatus.Text = Localize("更新包已校验", "Package verified");
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + download.FilePath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                PlaceholderStatus.Text = Localize("更新检查或下载失败：", "Update check or download failed: ") + SensitiveDataRedactor.Redact(ex.Message);
                ToolbarStatus.Text = Localize("更新失败", "Update failed");
            }
            finally { ModulePrimaryButton.IsEnabled = true; Cursor = null; }
        }

        private async Task RunSystemRepairAsync()
        {
            if (_systemActionRunning) return;
            if (_currentReport == null)
            {
                MessageBox.Show(Localize("请先执行只读扫描。", "Run a read-only scan first."), "钰叔售后", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SystemRepairAction action = SystemActionCombo.SelectedIndex == 1 ? SystemRepairAction.DismRestoreHealth : SystemActionCombo.SelectedIndex == 2 ? SystemRepairAction.EnableDotNet35 : SystemRepairAction.SfcScannow;
            string description = action == SystemRepairAction.SfcScannow ? "sfc.exe /scannow" : action == SystemRepairAction.DismRestoreHealth ? "DISM /Online /Cleanup-Image /RestoreHealth" : Localize("启用 Windows 可选功能 NetFx3（.NET Framework 3.5）", "Enable the Windows NetFx3 (.NET Framework 3.5) optional feature");
            if (!WindowsRepairService.IsSystemActionSupported(action, Environment.OSVersion.Version))
            {
                PlaceholderStatus.Text = Localize("此系统版本不支持该操作；尚未执行。", "This action is not supported on this Windows version. Nothing was changed.");
                ToolbarStatus.Text = Localize("系统不支持", "Not supported");
                return;
            }
            if (MessageBox.Show(Localize("操作预览：", "Action preview: ") + description + Localize("\n\n将显示 UAC，并在执行前尝试创建系统还原点。继续？", "\n\nUAC will be shown and a system restore point will be attempted first. Continue?"), Localize("系统修复预览", "System repair preview"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Cursor = System.Windows.Input.Cursors.Wait;
            _systemActionRunning = true;
            ModulePrimaryButton.IsEnabled = false;
            try
            {
                DateTime startedUtc = DateTime.UtcNow;
                ProcessResult result = await WindowsRepairService.RunSystemRepairAsync(action, true);
                string actionId = "action-" + Guid.NewGuid().ToString("N");
                string logEvidenceId = "evidence-system-log-" + actionId.Substring("action-".Length);
                _currentReport.Actions.Add(new ActionRecord
                {
                    ActionId = actionId, Type = action.ToString(), ComponentId = "windows-image",
                    StartedUtc = startedUtc.ToString("o"), CompletedUtc = DateTime.UtcNow.ToString("o"), Permission = "uac",
                    ExitCode = result.ExitCode, Reboot = "unknown", Result = result.ExitCode == 0 ? "success" : "failed",
                    CommandSummary = result.CommandSummary + "; restorePoint=" + result.RestorePointStatus,
                    EvidenceIds = new List<string> { logEvidenceId }
                });
                _diagnosticLogs.Add(new DiagnosticLogEntry
                {
                    Name = action.ToString() + ".log", Source = "YushuAfterSales.SystemRepair",
                    CapturedUtc = DateTime.UtcNow.ToString("o"), EvidenceId = logEvidenceId,
                    Content = "action=" + action + Environment.NewLine + "exitCode=" + result.ExitCode + Environment.NewLine +
                        "restorePoint=" + result.RestorePointStatus + Environment.NewLine + "logPath=" + result.LogPath + Environment.NewLine +
                        (result.StandardOutput ?? String.Empty) + Environment.NewLine + (result.StandardError ?? String.Empty)
                });
                ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                ToolbarStatus.Text = result.ExitCode == 0 ? Localize("系统动作完成", "System action completed") : Localize("系统动作返回错误", "System action returned an error");
                PlaceholderStatus.Text = description + Localize("；退出码：", "; exit code: ") + result.ExitCode + Localize("；还原点：", "; restore point: ") + result.RestorePointStatus + Localize("；已记录脱敏日志证据。", "; redacted log evidence was recorded.");
            }
            catch (Exception ex)
            {
                ToolbarStatus.Text = Localize("系统修复失败", "System repair failed");
                MessageBox.Show(Localize("系统修复未完成：", "System repair did not complete: ") + SensitiveDataRedactor.Redact(ex.Message), Localize("系统修复", "System repair"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _systemActionRunning = false; ShowPage(_currentPageKey); Cursor = null; }
        }

        private void ExportReportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string root = ReportHistoryStore.Root();
                ReportDocument report = _currentReport;
                List<DiagnosticLogEntry> logs = new List<DiagnosticLogEntry>(_diagnosticLogs);
                InventoryRow selected = ModuleGrid.SelectedItem as InventoryRow;
                if (_currentPageKey == "reports")
                {
                    if (selected == null)
                    {
                        MessageBox.Show(Localize("请先选择一份本地诊断报告。", "Select a local diagnostic report first."), Localize("诊断报告", "Diagnostic reports"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    report = _reportHistory.FirstOrDefault(x => String.Equals(x.ReportId, selected.ComponentId, StringComparison.OrdinalIgnoreCase));
                    logs = ReportHistoryStore.LoadLogs(selected.ComponentId);
                }
                if (report == null)
                {
                    MessageBox.Show(Localize("请先选择或生成一份诊断报告。", "Select or generate a diagnostic report first."), Localize("诊断报告", "Diagnostic reports"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (_currentPageKey != "reports" && !logs.Any(x => x.EvidenceId == "evidence-scan-" + report.ReportId))
                    logs.Add(new DiagnosticLogEntry { Name = "scan.log", Source = "YushuAfterSales.Scanner", CapturedUtc = report.CreatedUtc, EvidenceId = "evidence-scan-" + report.ReportId, Content = "扫描完成；系统未被扫描过程修改。报告 ID=" + report.ReportId });
                ReportPackageResult result = ReportWriter.WritePackage(report, root, logs, true);
                MessageBox.Show(Localize("已生成诊断包：\n", "Diagnostic package created:\n") + result.ZipPath, Localize("诊断报告", "Diagnostic reports"), MessageBoxButton.OK, MessageBoxImage.Information);
                OpenPath(result.ZipPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Localize("报告生成失败：", "Could not create report: ") + SensitiveDataRedactor.Redact(ex.Message), Localize("诊断报告", "Diagnostic reports"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenOfficialButton_Click(object sender, RoutedEventArgs e)
        {
            InventoryRow selected = ModuleGrid.SelectedItem as InventoryRow;
            if (selected == null) selected = FindingsGrid.SelectedItem as InventoryRow;
            if (selected == null || String.IsNullOrWhiteSpace(selected.SourceUrl))
            {
                ToolbarStatus.Text = Localize("请先选择有官方来源的组件行", "Select a component with an official source first");
                return;
            }
            Uri uri;
            if (!Uri.TryCreate(selected.SourceUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                ToolbarStatus.Text = Localize("已阻止非 HTTPS 官方来源", "Blocked non-HTTPS source");
                return;
            }
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(Localize("无法打开官方来源：", "Could not open official source: ") + SensitiveDataRedactor.Redact(ex.Message), Localize("官方来源", "Official source"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenReportFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string root = ReportHistoryStore.Root();
            InventoryRow selected = ModuleGrid.SelectedItem as InventoryRow;
            if (_currentPageKey == "reports" && selected != null)
            {
                string reportDirectory = Path.Combine(root, selected.ComponentId);
                if (Directory.Exists(reportDirectory)) { OpenPath(reportDirectory); return; }
            }
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + root + "\"") { UseShellExecute = true });
        }

        private List<InventoryRow> GetModuleItems(string key)
        {
            if (key == "reports")
                return _reportHistory.Select(x => new InventoryEntry { ComponentId = x.ReportId, DisplayName = Localize("诊断报告 ", "Diagnostic report ") + x.ReportId, DetectedVersion = x.CreatedUtc, ExpectedVersion = Localize("发现 ", "") + (x.Findings == null ? 0 : x.Findings.Count) + Localize(" 项", " findings"), Status = "local-history", EvidenceId = "report-" + x.ReportId }).Take(100).Select(ToInventoryRow).ToList();
            if (_currentReport == null) return new List<InventoryRow>();
            IEnumerable<InventoryEntry> items = _currentReport.Inventory;
            if (key == "runtime") items = items.Where(x => x.Category == "vc-runtime" || x.Category == "dotnet");
            else if (key == "directx") items = items.Where(x => x.Category == "directx");
            else if (key == "components") items = RuntimeCatalog.Packages.Where(x => x.Category == "游戏组件" || x.Category == "游戏平台").Select(x => new InventoryEntry { ComponentId = x.Id, DisplayName = x.DisplayName, ExpectedVersion = x.Notes, SourceUrl = x.OfficialUrl, Compatibility = x.SupportsWindows7 ? "Windows 7/10/11" : "Windows 10/11 only", Status = "catalog-only", EvidenceId = "catalog-" + x.Id });
            return items.Select(ToInventoryRow).ToList();
        }

        private InventoryRow ToInventoryRow(InventoryEntry item)
        {
            string status = item.Status == "installed" ? Localize("已安装", "Installed") : item.Status == "missing" || item.Status == "missing-or-unknown" || item.Status == "unknown" ? Localize("需复核", "Needs review") : item.Status == "catalog-only" ? Localize("仅官方目录", "Official catalog only") : item.Status == "local-history" ? Localize("本地历史", "Local history") : item.Status;
            return new InventoryRow { ComponentId = item.ComponentId, SourceUrl = item.SourceUrl, DisplayName = item.DisplayName, DetectedVersion = item.DetectedVersion, StatusLabel = status, Compatibility = item.Compatibility, SourceLabel = String.IsNullOrEmpty(item.SourceUrl) ? Localize("本机证据", "Local evidence") : Localize("官方来源", "Official source"), EvidenceId = item.EvidenceId };
        }

        private sealed class InventoryRow
        {
            public string ComponentId { get; set; }
            public string SourceUrl { get; set; }
            public string DisplayName { get; set; }
            public string DetectedVersion { get; set; }
            public string StatusLabel { get; set; }
            public string Compatibility { get; set; }
            public string SourceLabel { get; set; }
            public string EvidenceId { get; set; }
        }

        private void ApplyTheme(string theme)
        {
            string savedTheme = theme;
            if (theme == "system")
            {
                try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false)) theme = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 0)) == 1 ? "light" : "dark"; }
                catch { theme = "dark"; }
            }
            _theme = savedTheme == "brown" || savedTheme == "light" || savedTheme == "system" ? savedTheme : "dark";
            var values = theme == "brown" ? new[] { "#241B17", "#1C1512", "#2B211C", "#F6EEE5", "#C5B3A2", "#998273", "#49372A", "#352820", "#3D2C20", "#D28B35", "#B87529", "#FFFFFF" } : theme == "light" ? new[] { "#FFFFFF", "#F7F7F7", "#FFFFFF", "#171717", "#5F5F5F", "#777777", "#E4E4E4", "#F0F0F0", "#E8E8E8", "#087F5B", "#066A4C", "#FFFFFF" } : new[] { "#141414", "#141414", "#181818", "#DCDCDC", "#A6A6A6", "#767676", "#2D2D2D", "#212121", "#262626", "#10A37F", "#0D8B6D", "#FFFFFF" };
            string[] keys = { "BackgroundBrush", "SidebarBrush", "SurfaceBrush", "TextBrush", "TextMutedBrush", "TextFaintBrush", "LineBrush", "SurfaceHoverBrush", "SurfaceSelectedBrush", "AccentBrush", "AccentHoverBrush", "ButtonTextBrush" };
            for (int i = 0; i < keys.Length; i++)
                Application.Current.Resources[keys[i]] = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(values[i]));
        }

        private void LoadPreferences()
        {
            UserSettings settings = UserSettings.Load();
            _theme = settings.Theme;
            _isEnglish = settings.Language == "en-US";
            LanguageButton.Content = _isEnglish ? "中" : "EN";
            ApplyTheme(settings.Theme);
            if (settings.SidebarCollapsed)
            {
                SidebarColumn.Width = new GridLength(0);
                Sidebar.Visibility = Visibility.Collapsed;
                SidebarCollapseButton.Content = "▶";
                SidebarExpandButton.Visibility = Visibility.Visible;
            }
            ApplyLanguage();
        }

        private void SavePreferences()
        {
                try { UserSettings.Save(_theme, SidebarColumn.Width.Value == 0, _isEnglish); }
            catch (Exception ex) { ToolbarStatus.Text = "设置保存失败：" + SensitiveDataRedactor.Redact(ex.Message); }
        }

        private void ApplyLanguage()
        {
            BrandButtonText.Text = _isEnglish ? "Yushu Support" : "钰叔售后";
            BrandButton.ToolTip = _isEnglish ? "Go to overview" : "回到概览";
            RefreshButton.ToolTip = _isEnglish ? "Refresh current status" : "刷新当前状态";
            System.Windows.Automation.AutomationProperties.SetName(RefreshButton, RefreshButton.ToolTip.ToString());
            CloseModuleButton.ToolTip = _isEnglish ? "Close current module" : "关闭当前模块";
            System.Windows.Automation.AutomationProperties.SetName(CloseModuleButton, CloseModuleButton.ToolTip.ToString());
            TeamAppsLabel.Text = _isEnglish ? "Team apps" : "团队应用";
            LicensedAppsLabel.Text = _isEnglish ? "Licensed apps" : "授权应用";
            ThemeMenuTitle.Text = _isEnglish ? "Appearance" : "主题";
            OverviewTitle.Text = _isEnglish ? "Full scan" : "全面扫描";
            OverviewDescription.Text = _isEnglish ? "Check installed runtimes, system components, and common dependencies." : "检查本机已安装的运行库、系统组件与常见依赖";
            ScanButton.Content = _isEnglish ? "Start scan  →" : "开始扫描  →";
            ErrorDiagnosisButton.Content = _isEnglish ? "Error diagnosis" : "错误码诊断";
            OverviewSafetyNote.Text = _isEnglish ? "Scanning is read-only by default. Review every piece of evidence before any install or system action." : "扫描默认只读。完成后可复核每一项证据；任何安装或系统级操作均需单独确认。";
            OpenOfficialButton.Content = _isEnglish ? "Open official source" : "打开官方来源";
            ExportReportButton.Content = _isEnglish ? "Export diagnostic ZIP" : "导出诊断 ZIP";
            OpenSourceListButton.Content = _isEnglish ? "Open selected official source" : "打开选中项官方来源";
            ExportModuleReportButton.Content = _isEnglish ? "Export diagnostic ZIP" : "导出诊断 ZIP";
            OpenReportFolderButton.Content = _isEnglish ? "Open report folder" : "打开报告目录";
            OverviewHeaderEvidence.Text = _isEnglish ? "Component / evidence" : "组件 / 证据";
            OverviewHeaderVersion.Text = _isEnglish ? "Version" : "版本";
            OverviewHeaderStatus.Text = _isEnglish ? "Status" : "状态";
            OverviewHeaderCompatibility.Text = _isEnglish ? "Compatibility" : "兼容性";
            OverviewHeaderSource.Text = _isEnglish ? "Source" : "来源";
            ((ComboBoxItem)SystemActionCombo.Items[2]).Content = _isEnglish ? "Enable .NET Framework 3.5" : "启用 .NET Framework 3.5";
            AuthorizationActivateButton.Content = _isEnglish ? "Activate license" : "激活授权";
            AuthorizationCodeBox.ToolTip = _isEnglish ? "Enter license code" : "输入授权卡密";
            AuthorizationCodeBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, _isEnglish ? "License code" : "授权卡密");
            SystemActionCombo.ToolTip = _isEnglish ? "Choose a Windows repair action" : "选择 Windows 修复操作";
            if (ModuleGrid.Columns.Count >= 5)
            {
                ModuleGrid.Columns[0].Header = _isEnglish ? "Component" : "组件";
                ModuleGrid.Columns[1].Header = _isEnglish ? "Current version" : "当前版本";
                ModuleGrid.Columns[2].Header = _isEnglish ? "Target version" : "目标版本";
                ModuleGrid.Columns[3].Header = _isEnglish ? "Status" : "状态";
                ModuleGrid.Columns[4].Header = _isEnglish ? "Evidence ID" : "证据 ID";
            }
            foreach (Button button in FindVisualChildren<Button>(ThemeMenu))
            {
                string tag = button.Tag as string;
                if (tag == "brown") button.Content = "CSYUSHU Brown";
                else if (tag == "light") button.Content = "Windows Light";
                else if (tag == "dark") button.Content = "Windows Dark";
                else if (tag == "system") button.Content = _isEnglish ? "Follow system" : "跟随系统";
            }
            SidebarCollapseButton.ToolTip = _isEnglish ? "Collapse navigation" : "收起导航栏";
            System.Windows.Automation.AutomationProperties.SetName(SidebarCollapseButton, SidebarCollapseButton.ToolTip.ToString());
            SidebarExpandButton.ToolTip = _isEnglish ? "Expand navigation" : "展开导航栏";
            System.Windows.Automation.AutomationProperties.SetName(SidebarExpandButton, SidebarExpandButton.ToolTip.ToString());
            LanguageButton.Content = _isEnglish ? "中" : "EN";
            foreach (Button button in FindVisualChildren<Button>(Sidebar))
            {
                string key = button.Tag as string;
                var stack = button.Content as StackPanel;
                var label = stack == null ? null : stack.Children.OfType<TextBlock>().LastOrDefault();
                if (label == null) continue;
                if (key == "overview") label.Text = _isEnglish ? "Full scan" : "全面扫描";
                else if (key == "runtime") label.Text = _isEnglish ? "Runtime repair" : "运行库修复";
                else if (key == "directx") label.Text = _isEnglish ? "DirectX repair" : "DirectX 修复";
                else if (key == "system") label.Text = _isEnglish ? "System repair" : "系统修复";
                else if (key == "components") label.Text = _isEnglish ? "Game components" : "游戏组件";
                else if (key == "reports") label.Text = _isEnglish ? "Diagnostic reports" : "诊断报告";
                else if (key == "settings") label.Text = _isEnglish ? "Settings" : "设置";
                else if (key == "license") label.Text = _isEnglish ? "License" : "授权状态";
                else if (key == "updates") label.Text = _isEnglish ? "Updates" : "在线升级";
            }
            if (_currentPageKey != null && _pageTitles.ContainsKey(_currentPageKey))
                ToolbarTitle.Text = Localize(_pageTitles[_currentPageKey], EnglishPageTitle(_currentPageKey));
            if (_currentPageKey != "overview")
            {
                PlaceholderTitle.Text = Localize(_pageTitles[_currentPageKey], EnglishPageTitle(_currentPageKey));
                PlaceholderDescription.Text = GetDescription(_currentPageKey);
                PlaceholderStatus.Text = GetStatusText(_currentPageKey);
                ModuleEvidence.Text = GetEvidenceText(_currentPageKey);
            }
        }

        private static string EnglishPageTitle(string key)
        {
            switch (key)
            {
                case "overview": return "Full scan";
                case "runtime": return "Runtime repair";
                case "directx": return "DirectX repair";
                case "system": return "System repair";
                case "components": return "Game components";
                case "reports": return "Diagnostic reports";
                case "settings": return "Settings";
                case "license": return "License";
                case "updates": return "Updates";
                default: return key;
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                T typed = child as T;
                if (typed != null) yield return typed;
                foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
            }
        }

        private static string GetApplicationVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.1.0" : version.ToString();
        }

        private static AuthorizationSummary ToAuthorizationSummary(AuthorizationState state)
        {
            return new AuthorizationSummary
            {
                AppId = state == null ? "ysrepair" : state.AppId,
                State = state == null ? "unknown" : state.State,
                CanRepair = state != null && state.CanRepair,
                CanInstall = state != null && state.CanRepair,
                CanUpgrade = state != null && state.CanRepair,
                OfflineGraceDays = 7
            };
        }

        private static void OpenPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string fullPath = Path.GetFullPath(path);
            string arguments = Directory.Exists(fullPath) ? "\"" + fullPath + "\"" : "/select,\"" + fullPath + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }

        private string Localize(string chinese, string english)
        {
            return _isEnglish ? english : chinese;
        }
    }
}
