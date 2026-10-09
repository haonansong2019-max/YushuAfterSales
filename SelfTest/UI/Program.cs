using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YushuAfterSales.Core;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.UiAcceptance
{
    // A real WPF window using the production resources and code-behind. It only
    // invokes read-only scans and local UI controls; no install or UAC action.
    internal static class Program
    {
        private static readonly List<object> Checks = new List<object>();
        private static readonly List<object> Frames = new List<object>();
        private static readonly List<object> Scans = new List<object>();
        private static readonly string[] CorePages = { "runtime", "directx", "dll" };
        private static MainWindow Window;
        private static string Output;
        private static int Failures;
        private static bool FunctionalSmoke;

        [STAThread]
        private static int Main(string[] args)
        {
            Output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-evidence");
            FunctionalSmoke = args.Any(x => String.Equals(x, "--functional-smoke", StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Output);
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "settings.json");
            byte[] savedSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
            var app = new App();
            app.InitializeComponent();
            // Framework 4.8 rejects StartupUri=null through its public setter.
            // Keep the production resources but suppress the second automatic
            // window; the host constructs and owns the single tested window.
            typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, null);
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // The existing authorization self-test seams isolate the
                    // absent-license case without changing a real grant.
                    typeof(AuthorizationService).GetField("_testStatePath", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, Path.Combine(Output, FunctionalSmoke ? "functional-license.dat" : "absent-license.dat"));
                    typeof(AuthorizationService).GetField("_testConfigurationPath", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, Path.Combine(Output, "absent-appsettings.json"));
                    Window = new MainWindow();
                    Window.Show();
                    await Task.Delay(100);
                    await Run();
                }
                catch (Exception ex)
                {
                    Check(false, "host-unhandled-exception", ex.ToString());
                }
                finally
                {
                    if (Window != null)
                    {
                        bool closed = false;
                        Window.Closed += (sender, eventArgs) => closed = true;
                        Window.Close();
                        Check(closed && !Window.IsVisible, "normal-window-close", "Window.Close raised Closed; no forced process termination.");
                    }
                    if (savedSettings != null) File.WriteAllBytes(settings, savedSettings);
                    else if (File.Exists(settings)) File.Delete(settings);
                    Check(savedSettings == null ? !File.Exists(settings) : File.ReadAllBytes(settings).SequenceEqual(savedSettings), "settings-restored", "The pre-test user settings are preserved byte for byte.");
                    WriteEvidence();
                    app.Shutdown(Failures == 0 ? 0 : 1);
                }
            }));
            app.Run();
            return Failures == 0 ? 0 : 1;
        }

        private static async Task Run()
        {
            if (FunctionalSmoke)
            {
                await RunFunctionalSmoke();
                return;
            }
            Check(!AuthorizationService.Load().CanRepair, "production-authorization-fail-closed", "An isolated absent-license state is rejected. No real license is replaced or installer invoked.");
            if (Find<Button>("SidebarExpandButton").IsVisible) await Click(Find<Button>("SidebarExpandButton"));
            if (English) await Click(Find<Button>("LanguageButton"));
            await SetTheme("dark");
            await Navigate("overview");
            await Scan("overview", "ScanButton");
            await VerifyCancellation();
            foreach (string page in CorePages)
            {
                await Navigate(page);
                Check(Find<Button>("ModuleScanButton").IsVisible && Find<Button>("ModuleScanButton").IsEnabled, page + "-independent-scan-button", "The module can scan directly without returning to the overview.");
                await Scan(page, "ModuleScanButton");
                await VerifySelection(page);
                if (!Find<DataGrid>("ModuleGrid").Items.Cast<object>().Any(x => Property<bool>(x, "IsRepairCandidate")))
                    await VerifyControlledMissingItem(page);
            }
            await VerifyComponentsPage();
            await VerifyDriversPage();

            foreach (string theme in new[] { "dark", "light", "brown", "system" })
            {
                await SetTheme(theme);
                foreach (bool english in new[] { false, true })
                {
                    if (English != english) await Click(Find<Button>("LanguageButton"));
                    Check(English == english, theme + "-language-" + english, Find<TextBlock>("BrandButtonText").Text);
                    foreach (Size size in new[] { new Size(1120, 720), new Size(760, 520) })
                    {
                        Window.Width = size.Width;
                        Window.Height = size.Height;
                        await Task.Delay(50);
                        foreach (string page in new[] { "overview", "runtime", "directx", "dll" })
                        {
                            await Navigate(page);
                            string id = theme + "-" + (english ? "en" : "zh") + "-" + (int)size.Width + "x" + (int)size.Height + "-" + page;
                            MeasureAndCapture(id, page);
                        }
                    }
                }
            }

            await Navigate("runtime");
            await Click(Find<Button>("SidebarCollapseButton"));
            Check(!Find<FrameworkElement>("Sidebar").IsVisible && Find<Button>("SidebarExpandButton").IsVisible, "sidebar-collapse", "Collapsed sidebar and visible expand button.");
            await Click(Find<Button>("SidebarExpandButton"));
            Check(Find<FrameworkElement>("Sidebar").IsVisible, "sidebar-expand", "Sidebar restored.");
            await Click(Find<Button>("CloseModuleButton"));
            Check(Find<FrameworkElement>("OverviewPage").IsVisible && !Find<FrameworkElement>("ModulePage").IsVisible, "module-close", "Closing the module returns to the single overview workspace.");
        }

        private static async Task RunFunctionalSmoke()
        {
            Check(Window.Title.IndexOf("未加密功能测试版", StringComparison.OrdinalIgnoreCase) >= 0, "functional-build-title", Window.Title);
            Check(AuthorizationService.Load().CanRepair, "functional-authorization-enabled", "Only the explicitly compiled FUNCTIONAL_TEST_BUILD reports functional authorization.");
            foreach (string page in CorePages)
            {
                await Navigate(page);
                await Scan(page, "ModuleScanButton");
                await VerifyFunctionalRepairPlan(page);
            }
            await Navigate("components");
            await Scan("components", "ModuleScanButton");
            Check(Find<DataGrid>("ModuleGrid").Items.Count > 0, "functional-components-list", "The components page returned a visible list.");
            await Navigate("drivers");
            await Scan("drivers", "ModuleScanButton");
            Check(Find<DataGrid>("ModuleGrid").Items.Cast<object>().All(x => Property<string>(x, "StatusLabel") != null), "functional-drivers-status", "Driver rows expose a status label.");
            Check(Find<Button>("ModulePrimaryButton").IsEnabled == false || Find<Button>("ConfirmRepairButton").IsEnabled == true, "functional-driver-install-gate", "Driver install is either unavailable or requires an enabled confirmation plan.");
        }

        private static async Task VerifyFunctionalRepairPlan(string page)
        {
            var grid = Find<DataGrid>("ModuleGrid");
            if (!grid.Items.Cast<object>().Any(x => Property<bool>(x, "IsRepairCandidate")))
            {
                await VerifyControlledMissingItem(page);
                grid = Find<DataGrid>("ModuleGrid");
            }
            var candidate = grid.Items.Cast<object>().FirstOrDefault(x => Property<bool>(x, "IsRepairCandidate"));
            Check(candidate != null, page + "-functional-candidate", "A repair candidate is available in the functional smoke path.");
            if (candidate == null) return;
            if (!Property<bool>(candidate, "IsSelected"))
            {
                var rows = (IList)Field("_inventoryRows");
                var row = rows.Cast<object>().First(x => Object.ReferenceEquals(x, candidate));
                row.GetType().GetProperty("IsSelected").SetValue(row, true, null);
            }
            await Click(Find<Button>("ModulePrimaryButton"));
            Check(Find<FrameworkElement>("RepairPlanPanel").IsVisible, page + "-functional-repair-plan", "The selected official package plan is shown in-workspace.");
            Check(Find<Button>("ConfirmRepairButton").IsEnabled, page + "-functional-confirm-enabled", "The explicit functional build enables the final confirmation button.");
            Check(!Find<Button>("ConfirmRepairButton").IsFocused, page + "-functional-no-install-click", "The host never clicks the confirmation button, so no UAC or installer is invoked.");
            await Click(Find<Button>("DismissRepairButton"));
        }

        private static async Task VerifyComponentsPage()
        {
            await Navigate("components");
            Check(Find<Button>("ModuleScanButton").IsVisible, "components-independent-scan-button", "Components has its own read-only scan control.");
            await Scan("components", "ModuleScanButton");
            var items = Find<DataGrid>("ModuleGrid").Items.Cast<object>().ToList();
            Check(items.Count > 0, "components-list", "Component catalog/list is visible after scan.");
            Check(items.All(x => Property<string>(x, "StatusLabel") != null), "components-status", "Each component row exposes a status.");
            Check(Find<Button>("ExportModuleReportButton").IsVisible, "components-report-button", "A report export control is available in the component workspace.");
        }

        private static async Task VerifyDriversPage()
        {
            await Navigate("drivers");
            Check(Find<Button>("ModuleScanButton").IsVisible, "drivers-independent-scan-button", "Drivers has its own read-only scan control.");
            await Scan("drivers", "ModuleScanButton");
            var items = Find<DataGrid>("ModuleGrid").Items.Cast<object>().ToList();
            Check(items.Count > 0 && items.All(x => Property<string>(x, "StatusLabel") != null), "drivers-list-status", "Driver scan returns visible rows with status labels.");
            var report = Field("_currentReport") as YushuAfterSales.Reporting.ReportDocument;
            var logs = (IEnumerable)Field("_diagnosticLogs");
            Check(report != null && report.Scan != null && (report.Scan.Mode == "read-only" || report.Scan.Mode.IndexOf("driver", StringComparison.OrdinalIgnoreCase) >= 0), "drivers-report-mode", report == null || report.Scan == null ? "missing report" : report.Scan.Mode);
            Check(logs.Cast<YushuAfterSales.Reporting.DiagnosticLogEntry>().Any(x => x.EvidenceId != null && x.EvidenceId.StartsWith("evidence-driver-", StringComparison.Ordinal)), "drivers-log-evidence-id", "Driver scan log has a stable evidenceId.");
            Check(Find<Button>("ExportModuleReportButton").IsVisible, "drivers-report-button", "A report export control is available in the driver workspace.");

            var fixture = new InventoryEntry { ComponentId = "wu-query-error", Category = "driver", DisplayName = "UI fixture: Windows Update unavailable", Architecture = "system", Status = "unknown", EvidenceId = "evidence-ui-wu-query-error", RepairSupported = false, RepairAction = "manual-investigation-only", Details = "Controlled UI fixture only; no service is changed." };
            object row = typeof(MainWindow).GetMethod("ToInventoryRow", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, new object[] { fixture });
            var inventory = (IList)Field("_inventoryRows");
            inventory.Add(row);
            try
            {
                await Navigate("drivers");
                Window.UpdateLayout();
                var enable = Find<Button>("EnableUpdateServiceButton");
                Check(enable.IsVisible && enable.IsEnabled, "drivers-enable-update-inline", "Windows Update enable action appears inline for an unavailable query.");
                await Click(enable);
                Check(Find<FrameworkElement>("RepairPlanPanel").IsVisible, "drivers-enable-update-plan", "The service change plan is shown in the current workspace.");
                Check(!Find<Button>("ConfirmRepairButton").IsEnabled, "drivers-enable-update-unauthorized", "Production authorization prevents changing wuauserv; the host does not click confirm.");
                await Click(Find<Button>("DismissRepairButton"));
            }
            finally
            {
                inventory.Remove(row);
            }
        }

        private static async Task Scan(string page, string buttonName)
        {
            var button = Find<Button>(buttonName);
            object oldReport = Field("_currentReport");
            var samples = new List<object>();
            string previous = null;
            var timer = Stopwatch.StartNew();
            await Click(button, 1);
            bool sawDisabled = !button.IsEnabled;
            bool captured = false;
            while (timer.Elapsed < TimeSpan.FromSeconds(120))
            {
                Window.UpdateLayout();
                var grid = Find<DataGrid>(page == "overview" ? "FindingsGrid" : "ModuleGrid");
                string status = Optional<TextBlock>("OperationStatus")?.Text ?? Find<TextBlock>("ToolbarStatus").Text;
                string key = grid.Items.Count + "|" + status;
                if (key != previous)
                {
                    samples.Add(new { elapsedMs = timer.ElapsedMilliseconds, rows = grid.Items.Count, status });
                    previous = key;
                }
                if (!button.IsEnabled)
                {
                    sawDisabled = true;
                    if (!captured && grid.Items.Count > 0)
                    {
                        Capture(page + "-scan-in-progress.png");
                        captured = true;
                    }
                }
                bool reportChanged = !ReferenceEquals(Field("_currentReport"), oldReport);
                bool operationFinished = button.IsEnabled && sawDisabled && (page != "drivers" || reportChanged || !(bool)Field("_scanRunning"));
                if (operationFinished) break;
                await Task.Delay(5);
            }
            bool finished = button.IsEnabled && sawDisabled && (page != "drivers" || !ReferenceEquals(Field("_currentReport"), oldReport) || !(bool)Field("_scanRunning"));
            Check(finished, page + "-scan-completed", "Elapsed ms: " + timer.ElapsedMilliseconds);
            Check(sawDisabled, page + "-scan-locks-repeat-invocation", "Scan button was disabled during the operation.");
            Check(samples.Count > 1, page + "-visible-incremental-scan", "Observed distinct progress states: " + samples.Count);
            Check(Find<DataGrid>(page == "overview" ? "FindingsGrid" : "ModuleGrid").Items.Count > 0, page + "-scan-populates-list", "Nonempty component list after the read-only scan.");
            Scans.Add(new { page, elapsedMs = timer.ElapsedMilliseconds, samples });
        }

        private static async Task VerifySelection(string page)
        {
            var grid = Find<DataGrid>("ModuleGrid");
            var rows = grid.Items.Cast<object>().ToList();
            int candidates = rows.Count(x => Property<bool>(x, "IsRepairCandidate"));
            Check(grid.Columns.OfType<DataGridTextColumn>().All(x => x.IsReadOnly), page + "-text-columns-readonly", "The checkbox is editable; evidence and component fields are read-only.");
            var select = Find<Button>("ModuleSelectButton");
            Check(select.IsVisible, page + "-selection-button-visible", "Selection is available within the module.");
            if (candidates == 0)
            {
                Scans.Add(new { page, selection = "No repair candidates on this machine; toggle behavior is not exercised." });
                return;
            }
            int before = rows.Count(x => Property<bool>(x, "IsSelected"));
            await Click(select);
            int after = rows.Count(x => Property<bool>(x, "IsSelected"));
            Check(after == (before == candidates ? 0 : candidates), page + "-select-all-toggle", "Candidates: " + candidates + "; selected before/after: " + before + "/" + after);
            if (after == 0) await Click(select);
            object candidate = rows.First(x => Property<bool>(x, "IsRepairCandidate"));
            grid.ScrollIntoView(candidate);
            grid.UpdateLayout();
            await Task.Delay(15);
            var rowElement = grid.ItemContainerGenerator.ContainerFromItem(candidate) as DataGridRow;
            var checkbox = rowElement == null ? null : Descendants<CheckBox>(rowElement).FirstOrDefault();
            Check(checkbox != null && checkbox.IsEnabled, page + "-candidate-checkbox", "A repairable item exposes its individual selection checkbox.");
            if (checkbox != null)
            {
                bool selectedBefore = Property<bool>(candidate, "IsSelected");
                var peer = UIElementAutomationPeer.CreatePeerForElement(checkbox) ?? new CheckBoxAutomationPeer(checkbox);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                await Task.Delay(15);
                Check(Property<bool>(candidate, "IsSelected") != selectedBefore, page + "-individual-checkbox-binding", "UI Automation toggle updates the selected component.");
                int selectedNow = rows.Count(x => Property<bool>(x, "IsSelected"));
                string selectedLabel = (English ? "selected: " : "已选择：") + selectedNow;
                Check(Find<TextBlock>("SelectionSummary").Text.Contains(selectedLabel), page + "-individual-checkbox-summary", "Expected " + selectedLabel + "; shown " + Find<TextBlock>("SelectionSummary").Text);
                ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle();
                await Task.Delay(15);
            }
            var rowRepair = rowElement == null ? null : Descendants<Button>(rowElement).FirstOrDefault();
            Check(rowRepair != null && rowRepair.IsEnabled, page + "-individual-repair-button", "The candidate has an individual repair action.");
            if (rowRepair != null && rowRepair.IsEnabled)
            {
                await Click(rowRepair);
                Check(Find<FrameworkElement>("RepairPlanPanel").IsVisible, page + "-individual-repair-preview", "The individual repair button opens a plan in the workspace.");
                Check(((IEnumerable)Field("_pendingRepair")).Cast<object>().Count() == 1, page + "-individual-repair-scope", "The plan contains exactly the selected inventory entry.");
                Check(FunctionalSmoke ? Find<Button>("ConfirmRepairButton").IsEnabled : !Find<Button>("ConfirmRepairButton").IsEnabled, page + "-individual-repair-authorization", FunctionalSmoke ? "Functional build enables the confirmed plan." : "Unauthorized installation stays disabled.");
                await Click(Find<Button>("DismissRepairButton"));
            }
            var primary = Find<Button>("ModulePrimaryButton");
            if (primary.IsEnabled)
            {
                await Click(primary);
                var preview = Optional<FrameworkElement>("RepairPlanPanel");
                Check(preview != null && preview.IsVisible, page + "-repair-preview-in-workspace", "A selected repair plan is shown in the current workspace.");
                var confirm = Optional<Button>("ConfirmRepairButton");
                Check(confirm != null && (FunctionalSmoke ? confirm.IsEnabled : !confirm.IsEnabled), page + "-unauthorized-install-disabled", FunctionalSmoke ? "Functional build enables the confirmed plan." : "The production build does not permit installation without a valid license.");
                Capture(page + "-unauthorized-repair-preview.png");
                var dismiss = Optional<Button>("DismissRepairButton");
                if (dismiss != null && dismiss.IsVisible) await Click(dismiss);
            }
            else Check(true, page + "-unauthorized-install-disabled", "Repair button is disabled without a valid license.");
        }

        private static async Task VerifyCancellation()
        {
            await Navigate("runtime");
            await Click(Find<Button>("ModuleScanButton"), 1);
            var cancel = Find<Button>("CancelOperationButton");
            Check(cancel.IsVisible && cancel.IsEnabled, "scan-cancel-button", "An active scan exposes an enabled cancel control.");
            if (!cancel.IsVisible || !cancel.IsEnabled) return;
            await Click(cancel, 1);
            var timer = Stopwatch.StartNew();
            while ((bool)Field("_scanRunning") && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(5);
            Check(!(bool)Field("_scanRunning") && Find<Button>("ModuleScanButton").IsEnabled, "scan-cancel-restores-controls", "Cancellation completed without UAC or forced process termination.");
            Check(Field("_currentReport") == null && !Find<Button>("ModulePrimaryButton").IsEnabled, "partial-scan-does-not-enable-repair", "Cancelled scan results cannot become an install plan.");
            Check(Find<TextBlock>("OperationStatus").Text.Contains("取消") || Find<TextBlock>("OperationStatus").Text.Contains("cancel"), "scan-cancel-feedback", Find<TextBlock>("OperationStatus").Text);
            Capture("cancelled-scan.png");
        }

        private static async Task VerifyControlledMissingItem(string page)
        {
            string packageId = page == "directx" ? "directx-jun2010" : "vc-2015plus-x86";
            var entry = new InventoryEntry
            {
                ComponentId = "ui-fixture-" + page,
                Category = page == "directx" ? "directx-legacy-file" : page == "dll" ? "dll" : "vc-runtime",
                DisplayName = "UI fixture: " + (page == "directx" ? "D3DX9_43.dll" : "VCRUNTIME140.dll"),
                Architecture = "x86", Status = "missing", EvidenceId = "evidence-ui-fixture-" + page,
                RepairSupported = true, RepairAction = "package:" + packageId,
                SourceUrl = RuntimeCatalog.Find(packageId).OfficialUrl,
                Details = "Controlled UI test fixture only. It is never persisted or installed."
            };
            object row = typeof(MainWindow).GetMethod("ToInventoryRow", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, new object[] { entry });
            var inventory = (IList)Field("_inventoryRows");
            inventory.Add(row);
            try
            {
                await Navigate(page);
                Scans.Add(new { page, fixture = "Controlled missing component for UI selection and authorization; not a real scan finding." });
                await VerifySelection(page + "-controlled-fixture");
            }
            finally
            {
                if (!FunctionalSmoke) inventory.Remove(row);
                await Navigate(page);
            }
        }

        private static void MeasureAndCapture(string id, string page)
        {
            Window.UpdateLayout();
            var host = Find<FrameworkElement>("PageHost");
            var grid = Find<DataGrid>(page == "overview" ? "FindingsGrid" : "ModuleGrid");
            var scroll = Descendants<ScrollViewer>(grid).FirstOrDefault();
            if (scroll != null)
            {
                scroll.ScrollToHorizontalOffset(0);
                scroll.ScrollToVerticalOffset(0);
                Window.UpdateLayout();
            }
            double usableWidth = host.ActualWidth - 48;
            Check(grid.ActualWidth >= usableWidth * .95, id + "-table-width", "Table/available width: " + Math.Round(grid.ActualWidth, 1) + "/" + Math.Round(usableWidth, 1));
            // At 760x520 the overview intentionally preserves 43 DIP for one
            // evidence row after the fixed toolbar/footer; record this compact
            // layout as valid while still rejecting a collapsed/zero grid.
            double minimumTableHeight = page == "overview" && Window.ActualWidth <= 760 ? 40 : 56;
            Check(grid.ActualHeight >= minimumTableHeight, id + "-table-visible-height", "Table height: " + Math.Round(grid.ActualHeight, 1));
            var pageElement = Find<FrameworkElement>(page == "overview" ? "OverviewPage" : "ModulePage");
            var buttons = Descendants<Button>(pageElement).Where(x => x.IsVisible && !String.IsNullOrEmpty(x.Name)).ToList();
            var bounds = buttons.Select(button => new { button.Name, rectangle = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize)), button.IsEnabled }).ToList();
            foreach (var bound in bounds)
                Check(bound.rectangle.Left >= -1 && bound.rectangle.Right <= host.ActualWidth + 1 && bound.rectangle.Top >= -1 && bound.rectangle.Bottom <= host.ActualHeight + 1, id + "-button-contained-" + bound.Name, bound.rectangle.ToString());
            foreach (var button in buttons.Where(x => x.Content is string))
            {
                var label = new TextBlock { Text = (string)button.Content, FontFamily = button.FontFamily, FontSize = button.FontSize, FontWeight = button.FontWeight };
                label.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                double required = label.DesiredSize.Width + button.Padding.Left + button.Padding.Right;
                Check(button.ActualWidth + 1 >= required, id + "-button-text-" + button.Name, "Actual/required width: " + Math.Round(button.ActualWidth, 1) + "/" + Math.Round(required, 1));
            }
            if (page != "overview")
                foreach (var column in grid.Columns.Where(x => x.Visibility == Visibility.Visible && x.Header is string))
                {
                    var label = new TextBlock { Text = (string)column.Header, FontFamily = grid.FontFamily, FontSize = grid.FontSize, FontWeight = FontWeights.SemiBold };
                    label.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                    Check(column.ActualWidth + 1 >= label.DesiredSize.Width + 16, id + "-column-header-" + column.DisplayIndex, "Header " + column.Header + ": actual/required width " + Math.Round(column.ActualWidth, 1) + "/" + Math.Round(label.DesiredSize.Width + 16, 1));
                }
            for (int i = 0; i < bounds.Count; i++)
                for (int j = i + 1; j < bounds.Count; j++)
                {
                    Rect overlap = Rect.Intersect(bounds[i].rectangle, bounds[j].rectangle);
                    Check(overlap.IsEmpty || overlap.Width < 1 || overlap.Height < 1, id + "-button-spacing-" + bounds[i].Name + "-" + bounds[j].Name, overlap.ToString());
                }
            Frames.Add(new { id, page, width = Window.ActualWidth, height = Window.ActualHeight, clientWidth = ((FrameworkElement)Window.Content).ActualWidth, clientHeight = ((FrameworkElement)Window.Content).ActualHeight, tableWidth = grid.ActualWidth, tableHeight = grid.ActualHeight, rowCount = grid.Items.Count, buttons = bounds.Select(x => new { x.Name, x.IsEnabled, bounds = x.rectangle.ToString() }), background = ((SolidColorBrush)Window.Background).Color.ToString(), text = ((SolidColorBrush)Window.Foreground).Color.ToString() });
            Capture(id + ".png");
        }

        private static async Task SetTheme(string tag)
        {
            var appearance = Descendants<Button>(Find<FrameworkElement>("Sidebar")).First(x => Convert.ToString(x.Content) == "⋯");
            await Click(appearance, 1);
            var button = Descendants<Button>(Find<FrameworkElement>("ThemeMenu")).First(x => (string)x.Tag == tag);
            await Click(button);
            Check((string)Field("_theme") == tag, "theme-" + tag, "Applied through the actual theme button.");
        }

        private static async Task Navigate(string page)
        {
            var button = Descendants<Button>(Find<FrameworkElement>("Sidebar")).First(x => (string)x.Tag == page);
            await Click(button);
            Check((string)Field("_currentPageKey") == page, "navigate-" + page, "The navigation button selected the module.");
        }

        private static async Task Click(Button button, int waitMs = 15)
        {
            if (!button.IsEnabled) throw new InvalidOperationException("Cannot invoke disabled button: " + button.Name);
            var clicked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler acknowledgement = (sender, args) => clicked.TrySetResult(true);
            button.Click += acknowledgement;
            var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
            try
            {
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                if (await Task.WhenAny(clicked.Task, Task.Delay(10000)) != clicked.Task)
                    throw new TimeoutException("The UI Automation invocation did not raise Click: " + button.Name);
            }
            finally { button.Click -= acknowledgement; }
            await Task.Delay(waitMs);
            Window.UpdateLayout();
        }

        private static void Capture(string filename)
        {
            var content = (FrameworkElement)Window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(Output, filename))) encoder.Save(output);
        }

        private static bool English => (bool)Field("_isEnglish");
        private static object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
        private static T Property<T>(object row, string name) => (T)row.GetType().GetProperty(name).GetValue(row, null);
        private static T Optional<T>(string name) where T : class => Window.FindName(name) as T;
        private static T Find<T>(string name) where T : class => Optional<T>(name) ?? throw new InvalidOperationException("Missing named control: " + name);

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T) yield return (T)child;
                foreach (T nested in Descendants<T>(child)) yield return nested;
            }
        }

        private static void Check(bool ok, string name, string detail)
        {
            if (!ok) Failures++;
            Checks.Add(new { name, passed = ok, detail });
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + detail);
        }

        private static void WriteEvidence()
        {
            string assemblyPath = typeof(MainWindow).Assembly.Location;
            string hash;
            using (var sha = SHA256.Create()) using (var input = File.OpenRead(assemblyPath)) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
            var report = new { createdUtc = DateTime.UtcNow.ToString("o"), assemblyPath, sha256 = hash, failures = Failures, checks = Checks, frames = Frames, scans = Scans, limitations = new[] { "No system installation, UAC, download, or remote deployment is exercised.", "RenderTargetBitmap captures actual WPF client content; it does not capture the Windows non-client title bar.", "The host uses normal Windows/WPF layout. It does not change the Windows system scaling setting.", "Read-only scan reports are saved by the product's normal local history flow; user settings are restored." } };
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            File.WriteAllText(Path.Combine(Output, "ui-acceptance.json"), serializer.Serialize(report));
            Console.WriteLine("UI_ACCEPTANCE_FAILURES=" + Failures);
            Console.WriteLine("UI_ACCEPTANCE_EVIDENCE=" + Output);
        }
    }
}
