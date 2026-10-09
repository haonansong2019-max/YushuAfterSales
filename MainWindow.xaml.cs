using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
            { "dll", "DLL 修复" },
            { "drivers", "驱动安装" },
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
        private bool _scanRunning;
        private bool _repairRunning;
        private CancellationTokenSource _operationCancellation;
        private readonly ObservableCollection<InventoryRow> _inventoryRows = new ObservableCollection<InventoryRow>();
        private List<InventoryEntry> _pendingRepair;
        private string _lastRepairSummary;
        private bool _driverScanComplete;
        private bool _pendingEnableUpdateService;
        private bool IsBusy { get { return _scanRunning || _repairRunning || _systemActionRunning; } }

        public MainWindow()
        {
            InitializeComponent();
            SizeChanged += MainWindow_SizeChanged;
#if FUNCTIONAL_TEST_BUILD
            Title = "钰叔售后 · 未加密功能测试版";
            OperationStatus.Text = "未加密功能测试版：用于本地验收；安装前仍校验官方包并触发 UAC。";
#endif
            Closing += (sender, e) =>
            {
                if (_repairRunning || _systemActionRunning)
                {
                    e.Cancel = true;
                    OperationStatus.Text = Localize("正在执行修复，请等待当前安装结束后关闭。", "Wait for the current repair to finish before closing.");
                }
                else if (_operationCancellation != null) _operationCancellation.Cancel();
            };
            _authorization = AuthorizationService.Load();
            LoadPreferences();
            _reportHistory.AddRange(ReportHistoryStore.LoadRecent(100));
            ShowPage("overview");
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (OverviewPage == null) return;
            bool compact = ActualWidth < 800 || ActualHeight < 560;
            OverviewPage.Margin = compact ? new Thickness(16) : new Thickness(24);
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
            if (_currentPageKey != key) DismissRepairButton_Click(null, null);
            _currentPageKey = key;
            ToolbarTitle.Text = title;
            ToolbarStatus.Text = key == "overview" ? Localize("准备就绪", "Ready") : GetPageStatus(key);
            OverviewPage.Visibility = key == "overview" ? Visibility.Visible : Visibility.Collapsed;
            ModulePage.Visibility = key == "overview" ? Visibility.Collapsed : Visibility.Visible;
            ExportReportButton.Visibility = key == "overview" && _currentReport != null ? Visibility.Visible : Visibility.Collapsed;
            ExportModuleReportButton.Visibility = (key == "reports" && _reportHistory.Count > 0) || (key != "overview" && key != "reports" && _currentReport != null) ? Visibility.Visible : Visibility.Collapsed;
            OpenReportFolderButton.Visibility = key == "reports" ? Visibility.Visible : Visibility.Collapsed;
            OpenSourceListButton.Visibility = Visibility.Collapsed;
            ModuleSelectButton.Visibility = IsCorePage(key) ? Visibility.Visible : Visibility.Collapsed;
            ModulePrimaryButton.Visibility = IsCorePage(key) || key == "system" || key == "license" || key == "updates" || key == "reports" ? Visibility.Visible : Visibility.Collapsed;
            ModulePrimaryButton.Content = IsCorePage(key) ? Localize("立即修复所选", "Repair selected") : key == "system" ? Localize("执行所选系统修复", "Run selected system repair") : key == "license" ? Localize("在线刷新授权", "Refresh license online") : Localize("检查并下载更新", "Check and download update");
            if (key == "reports") ModulePrimaryButton.Content = Localize("导出所选报告", "Export selected report");
            ModulePrimaryButton.IsEnabled = key == "runtime" || key == "directx" || key == "dll" || key == "license" || key == "updates" || (key == "system" && _authorization != null && _authorization.CanRepair);
            if (key == "reports") ModulePrimaryButton.IsEnabled = _reportHistory.Count > 0;
            if (key == "license" && (_authorization == null || !_authorization.CanRepair)) ModulePrimaryButton.Content = Localize("刷新授权状态", "Refresh license status");
            if (IsBusy) ModulePrimaryButton.IsEnabled = false;
            ModuleScanButton.Visibility = IsCorePage(key) || key == "system" ? Visibility.Visible : Visibility.Collapsed;
            ModuleScanButton.Content = _currentReport == null ? Localize("开始扫描", "Start scan") : Localize("重新扫描", "Scan again");
            RowRepairColumn.Visibility = IsCorePage(key) ? Visibility.Visible : Visibility.Collapsed;
            EnableUpdateServiceButton.Visibility = key == "drivers" && _inventoryRows.Any(x => x.Entry.Category == "driver" && x.Entry.ComponentId.Contains("wu-query-error")) ? Visibility.Visible : Visibility.Collapsed;
            ModulePrimaryButton.ToolTip = IsCorePage(key) ? Localize("确认后下载并校验官方安装包，执行修复并重新扫描。", "Confirm, download and verify official installers, repair, then scan again.") : null;
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
                UpdateSelectionSummary();
            }
            AuthorizationActivationPanel.Visibility = key == "license" ? Visibility.Visible : Visibility.Collapsed;
            UpdateBusyControls();
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
            if (key == "runtime" || key == "directx" || key == "dll")
            {
                return Localize("报告 ID：", "Report ID: ") + _currentReport.ReportId;
            }
            return Localize("证据索引：", "Evidence index: ") + _currentReport.ReportId + Localize("；导出后可按 evidenceId 关联 actions.jsonl 和 logs。", "; export links evidenceId to actions.jsonl and logs.");
        }

        private string GetDescription(string key)
        {
            switch (key)
            {
                case "runtime": return Localize("逐项检测 VC++、.NET 和 UCRT；缺失或异常项可在此下载官方包并修复。", "Scan VC++, .NET and UCRT individually; download official packages here to repair missing or abnormal items.");
                case "directx": return Localize("逐项检测 DirectX 游戏组件；使用 Microsoft June 2010 安装包补齐缺失组件。", "Scan DirectX game components individually; repair with Microsoft's June 2010 installer.");
                case "dll": return Localize("按明确的 DLL 名称检查目标程序目录和系统组件证据；只把缺失项映射到受信官方组件，禁止从随机 DLL 网站下载或覆盖文件。", "Check explicit DLL evidence in the target and system component locations; map confirmed issues to trusted vendor packages only. Random DLL downloads and file replacement are prohibited.");
                case "drivers": return Localize("检测设备驱动问题，并查询 Windows Update 提供的匹配驱动；找到适配包后可直接下载安装。查询需要联网。", "Check device driver issues and matching drivers from Windows Update; download and install available matches here. An internet connection is required.");
                case "system": return Localize("通过 UAC 调用 SFC、DISM 或 Windows .NET 3.5 可选功能；执行前尝试创建还原点。", "Run SFC, DISM, or the Windows .NET 3.5 feature through UAC. A restore point is attempted first.");
                case "components": return Localize("逐项检测游戏组件；有受信安装包的缺失项提供下载安装，系统组件通过 Windows 修复。", "Scan game components individually; trusted packages can be downloaded and installed, while system components use Windows repair.");
                case "reports": return Localize("浏览本机保存的扫描历史；导出时生成脱敏 JSON、动作记录、日志索引和 SHA-256 清单。", "Browse local scan history. Exports include redacted JSON, action records, a log index, and SHA-256 manifest.");
                case "license": return Localize("AppID：ysrepair；服务未配置或回执签名无效时，修复保持禁用。", "AppID: ysrepair. Repairs remain disabled when the service is unconfigured or its reply signature is invalid.");
                case "updates": return Localize("从产品专用 HTTPS 清单检查版本，下载后校验 SHA-256；清单未配置时会明确失败。", "Check versions from the product HTTPS manifest and verify SHA-256 after download. An unconfigured manifest fails closed.");
                case "settings": return Localize("主题、语言和侧栏状态保存在当前 Windows 用户的本地设置中。", "Theme, language, and sidebar state are saved in this Windows user's local settings.");
                default: return Localize("此模块将在完成检测服务和报告契约后启用。", "This module will be enabled when its detection service and report contract are ready.");
            }
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            if (_currentPageKey == "drivers") { await RunDriverScanAsync(); return; }
            await RunScanAsync(null);
        }

        private async Task RunScanAsync(ReportDocument repairedReport)
        {
            _scanRunning = true;
            _operationCancellation = new CancellationTokenSource();
            _currentReport = null;
            _inventoryRows.Clear();
            FindingsGrid.ItemsSource = _inventoryRows;
            DismissRepairButton_Click(null, null);
            ShowPage(_currentPageKey);
            OperationProgress.Visibility = Visibility.Visible;
            OperationProgress.IsIndeterminate = false;
            OperationProgress.Value = 0;
            OverviewStatus.Text = Localize("正在逐项扫描…", "Scanning components…");
            if (repairedReport == null) _diagnosticLogs.Clear();
            try
            {
                Action<ComponentScanProgress> progress = p => Dispatcher.Invoke(new Action(() =>
                {
                    if (!_scanRunning) return;
                    OperationProgress.Value = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
                    OperationStatus.Text = Localize("扫描 ", "Scanning ") + p.Current + "/" + p.Total + " · " + p.DisplayName;
                    ToolbarStatus.Text = Localize("扫描中", "Scanning");
                    if (p.Entry != null && p.Stage == "complete")
                    {
                        InventoryRow row = ToInventoryRow(p.Entry);
                        _inventoryRows.Add(row);
                        if (_currentPageKey != "overview") ModuleGrid.ItemsSource = GetModuleItems(_currentPageKey);
                        PlaceholderStatus.Text = OperationStatus.Text;
                        OverviewStatus.Text = OperationStatus.Text;
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
                CancellationToken token = _operationCancellation.Token;
                _currentReport = await Task.Run(() => ComponentScanService.Scan(GetApplicationVersion(), progress, token), token);
                _currentReport.Authorization = ToAuthorizationSummary(_authorization);
                if (repairedReport != null) _currentReport.Actions.AddRange(repairedReport.Actions);
                _reportHistory.Insert(0, _currentReport);
                ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                _inventoryRows.Clear();
                foreach (InventoryEntry entry in _currentReport.Inventory) _inventoryRows.Add(ToInventoryRow(entry));
                OverviewStatus.Text = _currentReport.Conclusion.Summary;
                OverviewEvidence.Text = Localize("报告 ID：", "Report ID: ") + _currentReport.ReportId + Localize("；清单 ", "; inventory ") + _currentReport.Inventory.Count + Localize(" 项；发现 ", "; findings ") + _currentReport.Findings.Count + (_isEnglish ? "." : " 项。");
                ToolbarStatus.Text = Localize("扫描完成", "Scan complete");
                OperationStatus.Text = Localize("扫描完成，共 ", "Scan complete: ") + _currentReport.Inventory.Count + Localize(" 项。", " items.");
                if (repairedReport != null) OperationStatus.Text += " " + _lastRepairSummary + Localize(" 修复动作和重扫结果已保存到同一报告。", " Repair actions and verification were saved together.");
            }
            catch (OperationCanceledException)
            {
                OperationStatus.Text = Localize("扫描已取消；当前是部分结果，请重新扫描后再修复。", "Scan cancelled; results are partial. Scan again before repairing.");
                OverviewStatus.Text = OperationStatus.Text;
                _currentReport = repairedReport;
                if (repairedReport != null) ReportHistoryStore.Save(repairedReport, _diagnosticLogs);
            }
            catch (Exception ex)
            {
                OverviewStatus.Text = Localize("扫描失败：", "Scan failed: ") + SensitiveDataRedactor.Redact(ex.Message);
                ToolbarStatus.Text = Localize("扫描失败", "Scan failed");
                OperationStatus.Text = OverviewStatus.Text;
                _currentReport = repairedReport;
                if (repairedReport != null) ReportHistoryStore.Save(repairedReport, _diagnosticLogs);
            }
            finally
            {
                _scanRunning = false;
                _operationCancellation.Dispose();
                _operationCancellation = null;
                OperationProgress.Visibility = Visibility.Collapsed;
                ShowPage(_currentPageKey);
            }
        }

        private static bool IsCorePage(string key) { return key == "runtime" || key == "directx" || key == "dll" || key == "components" || key == "drivers"; }

        private void UpdateBusyControls()
        {
            ScanButton.IsEnabled = ModuleScanButton.IsEnabled = !IsBusy;
            EnableUpdateServiceButton.IsEnabled = !IsBusy;
            ModuleSelectButton.IsEnabled = ModuleGrid.IsEnabled = !IsBusy;
            RefreshButton.IsEnabled = ErrorDiagnosisButton.IsEnabled = !IsBusy;
            AuthorizationActivateButton.IsEnabled = !IsBusy;
            ExportReportButton.IsEnabled = ExportModuleReportButton.IsEnabled = !IsBusy;
            CancelOperationButton.Visibility = _scanRunning || _repairRunning ? Visibility.Visible : Visibility.Collapsed;
            CancelOperationButton.IsEnabled = _operationCancellation != null && !_operationCancellation.IsCancellationRequested;
            foreach (InventoryRow row in _inventoryRows) row.CanClickRepair = !IsBusy && row.IsRepairCandidate && _currentReport != null && (row.Entry.Category != "driver" || _driverScanComplete);
        }

        private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_operationCancellation == null) return;
            _operationCancellation.Cancel();
            CancelOperationButton.IsEnabled = false;
            OperationStatus.Text = _repairRunning ? Localize("已请求停止；等待当前安装退出后结束，不强杀安装器。", "Stop requested; waiting for the active installer to exit.") : Localize("正在取消扫描…", "Cancelling scan…");
        }

        private async Task RunDriverScanAsync()
        {
            if (IsBusy) return;
            _scanRunning = true;
            _driverScanComplete = false;
            _operationCancellation = new CancellationTokenSource();
            DismissRepairButton_Click(null, null);
            UpdateBusyControls();
            ModulePrimaryButton.IsEnabled = false;
            OperationProgress.Visibility = Visibility.Visible;
            OperationProgress.IsIndeterminate = true;
            OperationStatus.Text = Localize("正在检测设备并查询 Windows Update 驱动…", "Checking devices and querying Windows Update drivers…");
            try
            {
                var result = await DriverUpdateService.ScanAsync(DriverProgress, _operationCancellation.Token);
                if (_currentReport == null)
                {
                    _currentReport = ReportBuilder.Create(GetApplicationVersion());
                    _currentReport.Scan.StartedUtc = _currentReport.CreatedUtc;
                    _reportHistory.Insert(0, _currentReport);
                    _inventoryRows.Clear();
                    _diagnosticLogs.Clear();
                }
                _currentReport.Scan.Mode = "driver-read-only";
                _currentReport.Inventory.RemoveAll(x => x.Category == "driver");
                _currentReport.Inventory.AddRange(result.Inventory);
                foreach (var row in _inventoryRows.Where(x => x.Entry.Category == "driver").ToList()) _inventoryRows.Remove(row);
                foreach (var entry in result.Inventory) _inventoryRows.Add(ToInventoryRow(entry));
                _diagnosticLogs.AddRange(result.Logs);
                _currentReport.Authorization = ToAuthorizationSummary(AuthorizationService.Load());
                _currentReport.Scan.CompletedUtc = DateTime.UtcNow.ToString("o");
                _currentReport.Findings = WindowsRepairService.BuildFindings(_currentReport.Inventory);
                _currentReport.Conclusion = new ReportConclusion { Status = "scanned", FindingCount = _currentReport.Findings.Count, RepairableCount = _currentReport.Inventory.Count(x => x.RepairSupported), Summary = result.Summary };
                ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                FindingsGrid.ItemsSource = _inventoryRows;
                _driverScanComplete = true;
                OperationStatus.Text = result.Summary;
                OverviewStatus.Text = result.Summary;
            }
            catch (OperationCanceledException) { OperationStatus.Text = Localize("驱动查询已取消；未修改设备。", "Driver query cancelled; devices were not changed."); }
            catch (Exception ex) { OperationStatus.Text = Localize("驱动查询未完成：", "Driver query incomplete: ") + SensitiveDataRedactor.Redact(ex.Message); }
            finally
            {
                _scanRunning = false;
                _operationCancellation.Dispose();
                _operationCancellation = null;
                OperationProgress.Visibility = Visibility.Collapsed;
                ShowPage(_currentPageKey);
            }
        }

        private void DriverProgress(DriverUpdateProgress progress)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                OperationProgress.IsIndeterminate = progress.Stage == "searching" || progress.Stage == "scanning";
                OperationProgress.Value = progress.Percent;
                OperationStatus.Text = LocalizeRepairStage(progress.Stage) + " · " + progress.DisplayName +
                    (progress.Total > 0 ? " · " + progress.Current + "/" + progress.Total : "");
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void EnableUpdateServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || _currentReport == null) return;
            DismissRepairButton_Click(null, null);
            _pendingEnableUpdateService = true;
            _authorization = AuthorizationService.Load();
            AllowWithoutRestoreCheck.Visibility = Visibility.Collapsed;
            RepairPlanText.Text = Localize("确认将 Windows Update（wuauserv）启动方式设为“手动”并启动该服务，然后重新查询匹配驱动。需要管理员权限；变更前后的服务状态会写入报告。此步骤不会安装驱动。", "Set Windows Update (wuauserv) to Manual and start it, then query matching drivers again. Administrator access is required. Before and after service states are recorded; this step does not install drivers.") + Environment.NewLine + Localize("授权状态：", "License: ") + _authorization.State;
            ConfirmRepairButton.IsEnabled = _authorization.CanRepair;
            RepairPlanPanel.Visibility = Visibility.Visible;
        }

        private async Task RunEnableUpdateServiceAsync()
        {
            if (IsBusy || !_pendingEnableUpdateService || _currentReport == null) return;
            DismissRepairButton_Click(null, null);
            _repairRunning = true;
            _operationCancellation = new CancellationTokenSource();
            UpdateBusyControls();
            ModulePrimaryButton.IsEnabled = false;
            bool recheck = false;
            try
            {
                var result = await DriverUpdateService.EnableUpdateServiceAsync(true, _operationCancellation.Token);
                _currentReport.Actions.AddRange(result.Actions);
                _diagnosticLogs.AddRange(result.Logs);
                ReportHistoryStore.Save(_currentReport, _diagnosticLogs);
                recheck = result.Actions.Any(x => x.Result == "completed");
                OperationStatus.Text = recheck ? Localize("Windows Update 已启用，正在重新检测。", "Windows Update enabled; checking again.") : Localize("未能启用 Windows Update；退出码和原因已记录。", "Windows Update could not be enabled; the exit code and reason were recorded.");
            }
            catch (Exception ex) { OperationStatus.Text = SensitiveDataRedactor.Redact(ex.Message); }
            finally
            {
                _repairRunning = false;
                _operationCancellation.Dispose();
                _operationCancellation = null;
                ShowPage(_currentPageKey);
            }
            if (recheck) await RunDriverScanAsync();
        }

        private async Task RunDriverInstallAsync()
        {
            if (IsBusy || !_driverScanComplete || _pendingRepair == null) return;
            var ids = _pendingRepair.Select(x => x.RepairAction.Substring("driver:".Length)).ToList();
            var report = _currentReport;
            DismissRepairButton_Click(null, null);
            _repairRunning = true;
            _operationCancellation = new CancellationTokenSource();
            UpdateBusyControls();
            ModulePrimaryButton.IsEnabled = false;
            OperationProgress.Visibility = Visibility.Visible;
            bool completed = false;
            try
            {
                var result = await DriverUpdateService.InstallAsync(ids, true, DriverProgress, _operationCancellation.Token);
                report.Actions.AddRange(result.Actions);
                _diagnosticLogs.AddRange(result.Logs);
                ReportHistoryStore.Save(report, _diagnosticLogs);
                _lastRepairSummary = result.RebootRequired ? Localize("驱动动作完成；需重启后复核。", "Driver action finished; restart required for verification.") : Localize("驱动动作已记录。", "Driver actions were recorded.");
                OperationStatus.Text = _lastRepairSummary;
                completed = true;
            }
            catch (Exception ex) { OperationStatus.Text = Localize("驱动安装未完成：", "Driver installation incomplete: ") + SensitiveDataRedactor.Redact(ex.Message); }
            finally
            {
                _repairRunning = false;
                _operationCancellation.Dispose();
                _operationCancellation = null;
                OperationProgress.Visibility = Visibility.Collapsed;
                ShowPage(_currentPageKey);
            }
            if (completed)
            {
                await RunDriverScanAsync();
                OperationStatus.Text += " " + _lastRepairSummary;
            }
        }

        private async void ErrorDiagnosisButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy) return;
            var dialog = new ErrorDiagnosisWindow();
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            try
            {
                if (_currentReport == null)
                {
                    await RunScanAsync(null);
                    if (_currentReport == null) return;
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
                _inventoryRows.Clear();
                foreach (InventoryEntry entry in _currentReport.Inventory) _inventoryRows.Add(ToInventoryRow(entry));
                FindingsGrid.ItemsSource = _inventoryRows;
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
            if (IsBusy) return;
            if (_currentPageKey == "drivers") { await RunDriverScanAsync(); return; }
            if (IsCorePage(_currentPageKey)) { await RunScanAsync(null); return; }
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
            if (IsBusy) return;
            if (_currentPageKey == "license") { await RefreshAuthorizationOnlineAsync(); return; }
            if (_currentPageKey == "reports") { ExportReportButton_Click(sender, e); return; }
            if (_currentPageKey == "updates")
            {
                await CheckForUpdatesAsync();
                return;
            }
            if (IsCorePage(_currentPageKey))
            {
                ShowRepairPreview();
                return;
            }
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

        private void ModuleSelectButton_Click(object sender, RoutedEventArgs e)
        {
            bool allSelected = GetVisibleModuleRows().Where(x => x.IsRepairCandidate).All(x => x.IsSelected);
            foreach (InventoryRow row in GetVisibleModuleRows())
                if (row.IsRepairCandidate) row.IsSelected = !allSelected;
            UpdateSelectionSummary();
        }

        private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            UpdateSelectionSummary();
        }

        private IEnumerable<InventoryRow> GetVisibleModuleRows()
        {
            return ModuleGrid.ItemsSource as IEnumerable<InventoryRow> ?? Enumerable.Empty<InventoryRow>();
        }

        private void UpdateSelectionSummary()
        {
            if (SelectionSummary == null) return;
            List<InventoryRow> rows = GetVisibleModuleRows().ToList();
            int candidates = rows.Count(x => x.IsRepairCandidate);
            int selected = rows.Count(x => x.IsSelected);
            ModuleSelectButton.Content = candidates > 0 && selected == candidates
                ? Localize("取消全选", "Clear selection")
                : Localize("全选待修复", "Select repair items");
            SelectionSummary.Text = IsCorePage(_currentPageKey)
                ? Localize("清单：" + rows.Count + "；待修复：" + candidates + "；已选择：" + selected, "Items: " + rows.Count + "; repairable: " + candidates + "; selected: " + selected)
                : String.Empty;
            if (IsCorePage(_currentPageKey)) ModulePrimaryButton.IsEnabled = !IsBusy && _currentReport != null && selected > 0;
        }

        private void ShowRepairPreview()
        {
            PrepareRepair(GetVisibleModuleRows().Where(x => x.IsSelected && x.IsRepairCandidate).Select(x => x.Entry));
        }

        private void RowRepairButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy || _currentReport == null) return;
            var row = (sender as Button)?.DataContext as InventoryRow;
            if (row != null && row.IsRepairCandidate) PrepareRepair(new[] { row.Entry });
        }

        private void PrepareRepair(IEnumerable<InventoryEntry> entries)
        {
            if (IsBusy || _currentReport == null) return;
            _pendingRepair = entries.Where(x => x != null).ToList();
            AllowWithoutRestoreCheck.IsChecked = false;
            AllowWithoutRestoreCheck.Visibility = Visibility.Visible;
            if (_pendingRepair.Count == 0)
            {
                OperationStatus.Text = Localize("请先选择缺失或异常项。", "Select missing or abnormal items first.");
                return;
            }
            _authorization = AuthorizationService.Load();
            if (_pendingRepair.All(x => x.Category == "driver"))
            {
                if (!_driverScanComplete) return;
                AllowWithoutRestoreCheck.Visibility = Visibility.Collapsed;
                RepairPlanText.Text = Localize("将通过 Windows Update 下载并安装这些设备匹配驱动：", "Download and install these matching drivers via Windows Update:") + Environment.NewLine +
                    String.Join(Environment.NewLine, _pendingRepair.Select(x => "• " + x.DisplayName)) + Environment.NewLine +
                    Localize("需要 UAC；执行前尝试创建还原点。驱动包由 Windows Update 校验，安装后重新查询；可能需要重启。", "UAC is required; a restore point is attempted. Windows Update verifies the packages. Recheck after installation; a restart may be required.") + Environment.NewLine +
                    Localize("授权状态：", "License: ") + _authorization.State;
                ConfirmRepairButton.IsEnabled = _authorization.CanRepair;
                RepairPlanPanel.Visibility = Visibility.Visible;
                return;
            }
            var plan = ComponentRepairService.BuildPlan(_pendingRepair);
            RepairPlanText.Text = Localize("将下载并安装以下官方组件（同一安装包只执行一次）：", "Download and install these official components (deduplicated):") + Environment.NewLine +
                String.Join(Environment.NewLine, plan.Select(p => "• " + p.DisplayName + " [" + p.Architecture + "]" + (p.Supported ? "" : " — " + p.Reason))) + Environment.NewLine +
                Localize("SHA-256 与发布者签名校验 → UAC → 尝试创建还原点 → 安装 → 重扫验证。安装可能需要重启。", "SHA-256 and publisher verification → UAC → restore point attempt → installation → verification scan. A restart may be required.") + Environment.NewLine +
                Localize("授权状态：", "License: ") + _authorization.State;
            ConfirmRepairButton.IsEnabled = plan.Count > 0 && plan.All(x => x.Supported) && _authorization.CanRepair;
            RepairPlanPanel.Visibility = Visibility.Visible;
            if (!_authorization.CanRepair) OperationStatus.Text = Localize("正式版修复需要有效授权；当前授权未配置或无效。", "Production repairs require a valid license; authorization is missing or invalid.");
        }

        private void DismissRepairButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingRepair = null;
            _pendingEnableUpdateService = false;
            if (RepairPlanPanel != null) RepairPlanPanel.Visibility = Visibility.Collapsed;
        }

        private async void ConfirmRepairButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingEnableUpdateService) { await RunEnableUpdateServiceAsync(); return; }
            if (IsBusy || _currentReport == null || _pendingRepair == null || _pendingRepair.Count == 0) return;
            if (_pendingRepair.All(x => x.Category == "driver")) { await RunDriverInstallAsync(); return; }
            var selected = _pendingRepair.ToList();
            var report = _currentReport;
            bool allowWithoutRestore = AllowWithoutRestoreCheck.IsChecked == true;
            DismissRepairButton_Click(null, null);
            _repairRunning = true;
            _operationCancellation = new CancellationTokenSource();
            UpdateBusyControls();
            ModulePrimaryButton.IsEnabled = false;
            OperationProgress.Visibility = Visibility.Visible;
            bool started = false;
            try
            {
                Action<RepairProgress> progress = p => Dispatcher.Invoke(new Action(() =>
                {
                    OperationProgress.IsIndeterminate = p.Stage == "installing" || p.Stage == "extracting";
                    OperationProgress.Value = p.Percent;
                    string stage = LocalizeRepairStage(p.Stage);
                    OperationStatus.Text = p.ItemIndex + "/" + p.ItemCount + " · " + stage + " · " + p.Component +
                        (p.BytesReceived > 0 ? " · " + (p.BytesReceived / 1048576.0).ToString("F1") + " MB" + (p.TotalBytes > 0 ? " / " + (p.TotalBytes / 1048576.0).ToString("F1") + " MB" : "") : "");
                    ToolbarStatus.Text = stage;
                }), System.Windows.Threading.DispatcherPriority.Background);
                var results = await ComponentRepairService.ExecuteAsync(selected, true, progress, _operationCancellation.Token, allowWithoutRestore);
                foreach (var result in results)
                {
                    started |= result.Started;
                    if (result.Action != null) report.Actions.Add(result.Action);
                    if (result.Log != null) _diagnosticLogs.Add(result.Log);
                }
                ReportHistoryStore.Save(report, _diagnosticLogs);
                OperationStatus.Text = Localize("安装执行完成：", "Installation finished: ") + results.Count(x => x.Success) + "/" + results.Count +
                    (results.Any(x => x.RebootRequired) ? Localize("；需要重启后复核。", "; restart required for verification.") : "") +
                    " " + String.Join("; ", results.Where(x => !x.Success).Select(x => x.Message));
                _lastRepairSummary = OperationStatus.Text;
            }
            catch (Exception ex)
            {
                OperationStatus.Text = Localize("修复未完成：", "Repair incomplete: ") + SensitiveDataRedactor.Redact(ex.Message);
            }
            finally
            {
                _repairRunning = false;
                _operationCancellation.Dispose();
                _operationCancellation = null;
                OperationProgress.Visibility = Visibility.Collapsed;
                ShowPage(_currentPageKey);
            }
            if (started) await RunScanAsync(report);
        }

        private string LocalizeRepairStage(string stage)
        {
            switch (stage)
            {
                case "downloading": return Localize("下载", "Downloading");
                case "verifying": return Localize("校验", "Verifying");
                case "extracting": return Localize("解压", "Extracting");
                case "installing": return Localize("安装修复", "Installing");
                case "completed": return Localize("执行完成", "Finished");
                case "cancelled": return Localize("已取消", "Cancelled");
                case "failed": return Localize("失败", "Failed");
                case "blocked": return Localize("已阻止", "Blocked");
                case "scanning": return Localize("扫描", "Scanning");
                case "searching": return Localize("查询驱动", "Searching drivers");
                default: return Localize("准备", "Preparing");
            }
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
            return _inventoryRows.Where(x => EntryMatchesPage(x.Entry, key)).ToList();
        }

        private InventoryRow ToInventoryRow(InventoryEntry item)
        {
            bool candidate = item.RepairSupported && (item.Status == "missing" || item.Status == "corrupt" || item.Status == "invalid" || item.Status == "outdated" || item.Status == "architecture-mismatch");
            string label = item.Status == "installed" && (item.Category == "dll" || item.Category == "directx-legacy-file") ? Localize("结构正常", "Valid structure") : StatusLabel(item.Status);
            var row = new InventoryRow { Entry = item, ComponentId = item.ComponentId, SourceUrl = item.SourceUrl, DisplayName = item.DisplayName, Architecture = item.Architecture, DetectedVersion = item.DetectedVersion, ExpectedVersion = item.ExpectedVersion, StatusLabel = label, Compatibility = item.Compatibility, SourceLabel = String.IsNullOrEmpty(item.SourceUrl) ? Localize("本机证据", "Local evidence") : Localize("官方包", "Official package"), EvidenceId = item.EvidenceId, IsRepairCandidate = candidate, IsSelected = candidate, CanClickRepair = candidate && !IsBusy && _currentReport != null, RepairLabel = candidate ? Localize("立即修复", "Repair") : Localize("—", "—") };
            row.PropertyChanged += (sender, e) => { if (e.PropertyName == "IsSelected") UpdateSelectionSummary(); };
            return row;
        }

        private static bool EntryMatchesPage(InventoryEntry item, string key)
        {
            if (item == null) return false;
            if (key == "runtime") return item.Category == "vc-runtime" || item.Category == "dotnet" || item.Category == "ucrt" || item.Category == "vstor";
            if (key == "directx") return item.Category == "directx" || item.Category == "directx-legacy-file";
            if (key == "dll") return item.Category == "dll";
            if (key == "drivers") return item.Category == "driver";
            if (key == "components") return item.Category == "game-runtime" || item.Category == "game-platform";
            return true;
        }

        private string StatusLabel(string status)
        {
            switch (status)
            {
                case "installed": case "complete": return Localize("完整", "Complete");
                case "present": case "observed": return Localize("文件存在", "File present");
                case "missing": return Localize("缺失", "Missing");
                case "outdated": return Localize("需升级", "Outdated");
                case "corrupt": case "invalid": case "architecture-mismatch": return Localize("异常", "Abnormal");
                case "unsupported": case "not-applicable": return Localize("系统不适用", "Not applicable");
                case "unknown": case "missing-or-unknown": return Localize("需复核", "Needs review");
                case "catalog-only": return Localize("尚未检测", "Not scanned");
                case "local-history": return Localize("本地历史", "Local history");
                default: return status;
            }
        }

        private sealed class InventoryRow : INotifyPropertyChanged
        {
            public InventoryEntry Entry { get; set; }
            public string ComponentId { get; set; }
            public string SourceUrl { get; set; }
            public string DisplayName { get; set; }
            public string Architecture { get; set; }
            public string DetectedVersion { get; set; }
            public string ExpectedVersion { get; set; }
            public string StatusLabel { get; set; }
            public string Compatibility { get; set; }
            public string SourceLabel { get; set; }
            public string EvidenceId { get; set; }
            public bool IsRepairCandidate { get; set; }
            public string RepairLabel { get; set; }
            private bool _selected;
            public bool IsSelected { get { return _selected; } set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsSelected")); } }
            private bool _canClickRepair;
            public bool CanClickRepair { get { return _canClickRepair; } set { _canClickRepair = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CanClickRepair")); } }
            public event PropertyChangedEventHandler PropertyChanged;
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
            ModuleScanButton.Content = _isEnglish ? "Start scan" : "开始扫描";
            CancelOperationButton.Content = _isEnglish ? "Cancel" : "取消";
            EnableUpdateServiceButton.Content = _isEnglish ? "Enable Windows Update and recheck" : "启用 Windows Update 并重查";
            ConfirmRepairButton.Content = _isEnglish ? "Download and repair" : "确认下载并修复";
            DismissRepairButton.Content = _isEnglish ? "Back to list" : "返回清单";
            AllowWithoutRestoreCheck.Content = _isEnglish ? "Continue if restore point fails (no restore point rollback)" : "还原点创建失败时仍继续（无法用还原点回滚）";
            ErrorDiagnosisButton.Content = _isEnglish ? "Error diagnosis" : "错误码诊断";
            OverviewSafetyNote.Text = _isEnglish ? "Scanning is read-only by default. Review every piece of evidence before any install or system action." : "扫描默认只读。完成后可复核每一项证据；任何安装或系统级操作均需单独确认。";
            OpenOfficialButton.Content = _isEnglish ? "Open official source" : "打开官方来源";
            ExportReportButton.Content = _isEnglish ? "Export diagnostic ZIP" : "导出诊断 ZIP";
            OpenSourceListButton.Content = _isEnglish ? "Open selected official source" : "打开选中项官方来源";
            ExportModuleReportButton.Content = _isEnglish ? "Export diagnostic ZIP" : "导出诊断 ZIP";
            OpenReportFolderButton.Content = _isEnglish ? "Open report folder" : "打开报告目录";
            ModuleSelectButton.Content = _isEnglish ? "Select repair items" : "全选待修复";
            SelectionSummary.Text = _isEnglish ? "Select missing items to download and install official packages." : "选择缺失项，确认后下载安装官方包。";
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
            if (ModuleGrid.Columns.Count >= 8)
            {
                ModuleGrid.Columns[0].Header = _isEnglish ? "Sel." : "选择";
                ModuleGrid.Columns[1].Header = _isEnglish ? "Component" : "组件";
                ModuleGrid.Columns[2].Header = _isEnglish ? "Action" : "操作";
                ModuleGrid.Columns[3].Header = _isEnglish ? "Arch" : "架构";
                ModuleGrid.Columns[4].Header = _isEnglish ? "Current" : "当前版本";
                ModuleGrid.Columns[5].Header = _isEnglish ? "Target" : "目标版本";
                ModuleGrid.Columns[6].Header = _isEnglish ? "Status" : "状态";
                ModuleGrid.Columns[7].Header = _isEnglish ? "Evidence ID" : "证据 ID";
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
                else if (key == "dll") label.Text = _isEnglish ? "DLL repair" : "DLL 修复";
                else if (key == "drivers") label.Text = _isEnglish ? "Driver installation" : "驱动安装";
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
            if (!IsBusy)
            {
                if (_currentReport != null)
                {
                    int missing = _currentReport.Inventory.Count(x => x.Status == "missing" || x.Status == "outdated" || x.Status == "corrupt" || x.Status == "architecture-mismatch");
                    OverviewStatus.Text = Localize("已检测 " + _currentReport.Inventory.Count + " 项；缺失或异常 " + missing + " 项。", "Scanned " + _currentReport.Inventory.Count + " items; " + missing + " missing or abnormal.");
                    OverviewEvidence.Text = Localize("报告 ID：", "Report ID: ") + _currentReport.ReportId;
                    if (OperationStatus.Text.StartsWith("扫描完成") || OperationStatus.Text.StartsWith("Scan complete")) OperationStatus.Text = OverviewStatus.Text;
                }
                else OverviewStatus.Text = Localize("尚未完成扫描", "No completed scan");
                if (OperationStatus.Text == "准备就绪" || OperationStatus.Text == "Ready") OperationStatus.Text = Localize("准备就绪", "Ready");
                if (OperationStatus.Text.StartsWith("扫描已取消") || OperationStatus.Text.StartsWith("Scan cancelled")) OperationStatus.Text = Localize("扫描已取消；当前是部分结果，请重新扫描后再修复。", "Scan cancelled; results are partial. Scan again before repairing.");
            }
            foreach (InventoryRow row in _inventoryRows)
            {
                row.StatusLabel = ToInventoryRow(row.Entry).StatusLabel;
                row.RepairLabel = row.IsRepairCandidate ? Localize("立即修复", "Repair") : "—";
            }
            FindingsGrid.Items.Refresh();
            ModuleGrid.Items.Refresh();
            ShowPage(_currentPageKey);
        }

        private static string EnglishPageTitle(string key)
        {
            switch (key)
            {
                case "overview": return "Full scan";
                case "runtime": return "Runtime repair";
                case "directx": return "DirectX repair";
                case "dll": return "DLL repair";
                case "drivers": return "Driver installation";
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
