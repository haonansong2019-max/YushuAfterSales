using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.Core
{
    public sealed class ComponentRepairPlanItem
    {
        public string PackageId { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Architecture { get; internal set; }
        public string DownloadUrl { get; internal set; }
        public string ExpectedSha256 { get; internal set; }
        public List<string> EvidenceIds { get; internal set; }
        public bool Supported { get; internal set; }
        public string Reason { get; internal set; }
        public bool RepairExisting { get; internal set; }
        internal RepairPackage Package { get; set; }
        internal SystemRepairAction? SystemAction { get; set; }
        internal List<InventoryEntry> BeforeEvidence { get; set; }
    }

    public sealed class RepairProgress
    {
        public string Stage { get; set; }
        public int Percent { get; set; }
        public string Component { get; set; }
        public int ItemIndex { get; set; }
        public int ItemCount { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
    }

    public sealed class ComponentRepairResult
    {
        public ComponentRepairPlanItem Plan { get; internal set; }
        public ActionRecord Action { get; internal set; }
        public DiagnosticLogEntry Log { get; internal set; }
        public bool Started { get; internal set; }
        /// <summary>Official installer reported a successful exit. The caller must rescan to establish repairs.</summary>
        public bool Success { get; internal set; }
        public bool RebootRequired { get; internal set; }
        public bool Cancelled { get; internal set; }
        public string RestorePointStatus { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>Only build-pinned packages or fixed Windows actions are executable. No DLL copying or shell arguments from UI.</summary>
    public static class ComponentRepairService
    {
        private static readonly SemaphoreSlim ExecutionGate = new SemaphoreSlim(1, 1);

        public static List<ComponentRepairPlanItem> BuildPlan(IEnumerable<InventoryEntry> selected)
        {
            return BuildPlanForEnvironment(selected, RepairPackageCatalog.CurrentWindowsVersion(), Environment.Is64BitOperatingSystem);
        }

        // An explicit environment is useful for deterministic compatibility tests; ExecuteAsync always uses the real system.
        public static List<ComponentRepairPlanItem> BuildPlanForEnvironment(IEnumerable<InventoryEntry> selected, Version windows, bool is64Bit)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            var result = new List<ComponentRepairPlanItem>();
            foreach (InventoryEntry entry in selected.Where(x => x != null))
            {
                // A normal repair plan never silently reinstalls an item reported complete.
                if (entry.Status == "installed") continue;
                string route = entry.RepairAction ?? String.Empty;
                string id = route.StartsWith("package:", StringComparison.Ordinal) ? route.Substring(8) : entry.ComponentId ?? String.Empty;
                SystemRepairAction? system = route == "system:sfc" ? (SystemRepairAction?)SystemRepairAction.SfcScannow
                    : route == "system:dotnet35" ? (SystemRepairAction?)SystemRepairAction.EnableDotNet35 : null;
                if (!system.HasValue && (id == "dotnet-framework-3-5" || id == "dotnet-35")) system = SystemRepairAction.EnableDotNet35;
                if (id == "dotnet-framework-4-full") id = "dotnet-48";
                if (id.StartsWith("vc-runtime-2015-2022-", StringComparison.Ordinal)) id = id.Replace("vc-runtime-2015-2022-", "vc-2015plus-");
                if (id == "directx-legacy" || id.StartsWith("directx-legacy-", StringComparison.Ordinal) || route == "install-directx-june-2010") id = "directx-jun2010";
                RepairPackage package = system.HasValue ? null : RepairPackageCatalog.FindForEnvironment(id, windows);
                // Windows 10's integrated .NET framework is serviced by Windows, not by replacing an older offline framework.
                if (id == "dotnet-48" && windows != null && windows.Major >= 10 && windows.Build >= 19041)
                { system = SystemRepairAction.SfcScannow; package = null; }
                string key = system.HasValue ? "system:" + system.Value : package == null ? "unsupported:" + id : package.Id;
                ComponentRepairPlanItem plan = result.FirstOrDefault(x => x.PackageId == key);
                if (plan == null)
                {
                    bool compatible = package != null && windows != null && windows >= package.MinimumWindowsVersion &&
                        (package.Architecture != "x64" || is64Bit);
                    bool supported = system.HasValue ? WindowsRepairService.IsSystemActionSupported(system.Value, windows) : compatible;
                    plan = new ComponentRepairPlanItem
                    {
                        PackageId = key, DisplayName = system.HasValue ? system.Value == SystemRepairAction.SfcScannow ? "Windows 系统组件修复 (SFC)" : ".NET Framework 3.5 可选功能" : package == null ? entry.DisplayName : package.DisplayName,
                        Architecture = system.HasValue ? "system" : package == null ? entry.Architecture : package.Architecture,
                        DownloadUrl = package == null ? String.Empty : package.DownloadUrl,
                        ExpectedSha256 = package == null ? String.Empty : package.Sha256,
                        EvidenceIds = new List<string>(), Supported = supported, Package = package, SystemAction = system,
                        BeforeEvidence = new List<InventoryEntry>(),
                        Reason = supported ? "仅使用官方固定安装包或 Windows 自带修复；执行后重新扫描确认结果。"
                            : package != null ? "此固定版本不支持当前系统或架构；不会下载或安装。" : "没有固定官方包、哈希和签名策略；此项目禁止自动修复。"
                    };
                    result.Add(plan);
                }
                if (!String.IsNullOrWhiteSpace(entry.EvidenceId) && !plan.EvidenceIds.Contains(entry.EvidenceId)) plan.EvidenceIds.Add(entry.EvidenceId);
                plan.BeforeEvidence.Add(entry);
                plan.RepairExisting |= entry.Status == "corrupt" || entry.Status == "damaged" || entry.Status == "wrong-architecture" || entry.Status == "architecture-mismatch";
            }
            return result;
        }

        public static async Task<List<ComponentRepairResult>> ExecuteAsync(IEnumerable<InventoryEntry> selected,
            bool userConfirmed, Action<RepairProgress> progress, CancellationToken cancellationToken, bool allowWithoutRestorePoint = false)
        {
            if (!userConfirmed) throw new InvalidOperationException("修复必须先显示计划并取得用户确认。");
            EnsureAuthorization();
            List<ComponentRepairPlanItem> plans = BuildPlan(selected);
            if (!await ExecutionGate.WaitAsync(0).ConfigureAwait(false)) throw new InvalidOperationException("已有修复任务正在执行。");
            try
            {
                return await Task.Run(() => Execute(plans, progress, cancellationToken, allowWithoutRestorePoint)).ConfigureAwait(false);
            }
            finally { ExecutionGate.Release(); }
        }

        private static List<ComponentRepairResult> Execute(List<ComponentRepairPlanItem> plans, Action<RepairProgress> progress, CancellationToken token, bool allowWithoutRestorePoint)
        {
            var results = new List<ComponentRepairResult>();
            bool sessionRestoreCreated = false;
            for (int i = 0; i < plans.Count; i++)
            {
                ComponentRepairPlanItem plan = plans[i];
                string actionId = "action-" + Guid.NewGuid().ToString("N");
                string logId = "evidence-repair-log-" + actionId.Substring(7);
                var action = new ActionRecord
                {
                    ActionId = actionId, Type = plan.SystemAction.HasValue ? plan.SystemAction.Value.ToString() : "official-package-install",
                    ComponentId = plan.PackageId, EvidenceId = logId, EvidenceIds = plan.EvidenceIds.Concat(new[] { logId }).ToList(),
                    StartedUtc = DateTime.UtcNow.ToString("o"), Permission = "uac", Reboot = "not-required",
                    SourcePolicy = "pinned-official-https-sha256-authenticode", AutomaticExecutionAllowed = false,
                    CommandSummary = plan.SystemAction.HasValue ? plan.SystemAction.Value.ToString() : "official-package:" + plan.PackageId
                };
                var item = new ComponentRepairResult
                {
                    Plan = plan, Action = action, RestorePointStatus = "not-attempted",
                    Log = new DiagnosticLogEntry { EvidenceId = logId, Name = actionId + ".log", Source = "YushuAfterSales.ComponentRepair", CapturedUtc = DateTime.UtcNow.ToString("o") }
                };
                var evidence = new StringBuilder();
                evidence.AppendLine("actionId=" + actionId).AppendLine("packageId=" + plan.PackageId);
                evidence.AppendLine("source=" + plan.DownloadUrl).AppendLine("expectedSha256=" + plan.ExpectedSha256);
                foreach (InventoryEntry before in plan.BeforeEvidence)
                    evidence.AppendLine("beforeEvidence=" + ReportJson.Serialize(before));
                try
                {
                    token.ThrowIfCancellationRequested();
                    EnsureAuthorization();
                    if (!plan.Supported) throw new NotSupportedException(plan.Reason);
                    Action<RepairProgress> report = p => { p.ItemIndex = i + 1; p.ItemCount = plans.Count; if (progress != null) progress(p); };
                    ProcessResult run;
                    if (plan.SystemAction.HasValue)
                    {
                        Notify(report, "installing", 0, plan.DisplayName, 0, -1);
                        EnsureAuthorization();
                        action.AutomaticExecutionAllowed = true;
                        run = WindowsRepairService.RunSystemRepair(plan.SystemAction.Value, true, sessionRestoreCreated, allowWithoutRestorePoint);
                    }
                    else
                    {
                        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "Packages");
                        string path = DownloadVerifiedPackage(plan.Package, cache, report, token);
                        PackageSecurityService.PackageValidationResult validation = PackageSecurityService.ValidatePackage(path, plan.Package.Sha256, plan.Package.AllowedPublishers);
                        if (!validation.IsAllowed) throw new InvalidDataException(validation.Failure);
                        evidence.AppendLine("sha256=" + validation.Sha256).AppendLine("signature=" + validation.SignatureStatusHex).AppendLine("publisher=" + validation.Publisher);
                        token.ThrowIfCancellationRequested();
                        EnsureAuthorization();
                        Notify(report, plan.Package.Kind == "directx" ? "extracting" : "installing", 0, plan.DisplayName, 0, -1);
                        action.AutomaticExecutionAllowed = true;
                        run = RunElevatedPackage(plan.Package, path, plan.RepairExisting || IsPackageRegistered(plan.Package), !sessionRestoreCreated, allowWithoutRestorePoint, report, token);
                    }
                    if (run.RestorePointStatus == "created" || run.RestorePointStatus == "session-created") sessionRestoreCreated = true;
                    item.Started = run.Started;
                    item.RestorePointStatus = run.RestorePointStatus;
                    action.ExitCode = run.ExitCode;
                    action.CommandSummary = run.CommandSummary + "; restorePoint=" + run.RestorePointStatus;
                    action.Result = ClassifyExitCode(run.ExitCode);
                    item.Success = action.Result == "installer-completed" || action.Result == "reboot-required";
                    item.RebootRequired = action.Result == "reboot-required";
                    item.Cancelled = action.Result == "cancelled";
                    action.Reboot = item.RebootRequired ? "required" : "not-required";
                    item.Message = item.Success ? item.RebootRequired ? "官方动作完成，需要重启并重新扫描。" : "官方动作完成；正在等待重新扫描验证。" : "官方动作未完成；退出码 " + run.ExitCode + "。";
                    evidence.AppendLine("exitCode=" + run.ExitCode).AppendLine("restorePoint=" + run.RestorePointStatus);
                    evidence.AppendLine(run.StandardOutput ?? String.Empty).AppendLine(run.StandardError ?? String.Empty);
                    Notify(report, item.Success ? "completed" : item.Cancelled ? "cancelled" : "failed", item.Success ? 100 : 0, plan.DisplayName, 0, -1);
                }
                catch (OperationCanceledException)
                { item.Cancelled = true; item.Message = "任务已取消；未开始下一项安装。"; action.Result = "cancelled"; action.ExitCode = 1223; }
                catch (Exception ex)
                {
                    item.Message = SensitiveDataRedactor.Redact(ex.Message);
                    action.Result = ex is NotSupportedException || ex is UnauthorizedAccessException ? "blocked" : "failed";
                    evidence.AppendLine("errorType=" + ex.GetType().Name).AppendLine("error=" + item.Message);
                    Notify(progress, action.Result, 0, plan.DisplayName, 0, -1);
                }
                action.CompletedUtc = DateTime.UtcNow.ToString("o");
                item.Log.CapturedUtc = action.CompletedUtc;
                item.Log.Content = SensitiveDataRedactor.Redact(evidence.AppendLine("result=" + action.Result).ToString());
                results.Add(item);
            }
            return results;
        }

        public static string ClassifyExitCode(int exitCode)
        {
            if (exitCode == 0) return "installer-completed";
            if (exitCode == 3010 || exitCode == 1641) return "reboot-required";
            if (exitCode == 1223 || exitCode == 1602) return "cancelled";
            if (exitCode == 1638 || exitCode == unchecked((int)0x80070666)) return "newer-or-existing-version";
            return "failed";
        }

        public static string DownloadVerifiedPackage(RepairPackage package, string cacheDirectory, Action<RepairProgress> progress, CancellationToken token)
        {
            EnsurePinned(package);
            token.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(cacheDirectory);
            Directory.CreateDirectory(root);
            EnsureNoReparse(root);
            string path = Path.Combine(root, package.Id + "-" + package.Sha256.Substring(0, 16) + ".exe");
            if (File.Exists(path))
            {
                EnsureNoReparse(path);
                Notify(progress, "verifying", 0, package.DisplayName, 0, -1);
                var cached = PackageSecurityService.ValidatePackage(path, package.Sha256, package.AllowedPublishers);
                if (cached.IsAllowed) return path;
                // A corrupt cache is never run. Redownload only this app-owned file.
                File.Delete(path);
            }
            string temp = path + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                Uri url = new Uri(package.DownloadUrl);
                for (int redirects = 0; ; redirects++)
                {
                    token.ThrowIfCancellationRequested();
                    if (redirects > 5 || !RepairPackageCatalog.IsOfficialDownloadUri(url)) throw new InvalidDataException("下载或重定向不是受信微软 HTTPS 主机。");
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.AllowAutoRedirect = false; request.Timeout = 30000; request.ReadWriteTimeout = 30000;
                    request.UserAgent = "YushuAfterSales/1.0";
                    using (token.Register(() => request.Abort()))
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            string location = response.Headers[HttpResponseHeader.Location];
                            if (String.IsNullOrEmpty(location)) throw new InvalidDataException("官方源返回了无目标的重定向。");
                            url = new Uri(url, location); continue;
                        }
                        if (status != 200) throw new InvalidDataException("官方源返回 HTTP " + status + "。");
                        long length = response.ContentLength;
                        if (length == 0 || length > package.MaximumBytes) throw new InvalidDataException("官方安装包大小超出限制。");
                        using (Stream source = response.GetResponseStream())
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[65536]; long total = 0; int count;
                            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                token.ThrowIfCancellationRequested(); total += count;
                                if (total > package.MaximumBytes) throw new InvalidDataException("下载数据超过安装包大小限制。");
                                output.Write(buffer, 0, count);
                                Notify(progress, "downloading", length > 0 ? (int)Math.Min(100, total * 100 / length) : 0, package.DisplayName, total, length);
                            }
                            if (total == 0 || (length > 0 && total != length)) throw new InvalidDataException("官方安装包下载不完整。");
                        }
                        break;
                    }
                }
                token.ThrowIfCancellationRequested();
                Notify(progress, "verifying", 0, package.DisplayName, 0, -1);
                // InspectPackage requires a safe .exe basename; the partial file is hashed first and renamed only after it matches.
                if (!String.Equals(HashFile(temp), package.ArchiveSha256 ?? package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("官方安装包 SHA-256 不匹配；禁止安装。");
                if (package.Kind == "openal")
                {
                    string inner = temp + ".inner";
                    try
                    {
                        using (var archive = ZipFile.OpenRead(temp))
                        {
                            var entry = archive.GetEntry("oalinst.exe");
                            if (entry == null || entry.Length == 0 || entry.Length > package.MaximumBytes) throw new InvalidDataException("官方 OpenAL 压缩包不含受信安装器。");
                            using (Stream input = entry.Open())
                            using (var output = new FileStream(inner, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                        }
                        if (!String.Equals(HashFile(inner), package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OpenAL 安装器 SHA-256 不匹配。");
                        File.Delete(temp); File.Move(inner, temp);
                    }
                    finally { if (File.Exists(inner)) File.Delete(inner); }
                }
                File.Move(temp, path);
                var verified = PackageSecurityService.ValidatePackage(path, package.Sha256, package.AllowedPublishers);
                if (!verified.IsAllowed) { File.Delete(path); throw new InvalidDataException(verified.Failure); }
                return path;
            }
            catch (WebException) { token.ThrowIfCancellationRequested(); throw; }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static ProcessResult RunElevatedPackage(RepairPackage package, string path, bool repair, bool restoreRequired, bool allowWithoutRestorePoint,
            Action<RepairProgress> progress, CancellationToken token)
        {
            EnsurePinned(package); EnsureAuthorization(); token.ThrowIfCancellationRequested();
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "Logs");
            Directory.CreateDirectory(logs); EnsureNoReparse(logs);
            string log = Path.Combine(logs, "package-" + Guid.NewGuid().ToString("N") + ".log");
            string script = BuildElevatedPackageScriptInSession(package, path, repair, restoreRequired, allowWithoutRestorePoint, log);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string native = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
            string powershell = Path.Combine(windows, native, @"WindowsPowerShell\v1.0\powershell.exe");
            var broker = PackageSecurityService.InspectPackage(powershell);
            if (!broker.SignatureVerified || String.IsNullOrEmpty(broker.Publisher) || broker.Publisher.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("Windows PowerShell 签名或微软发布者无效，禁止请求管理员权限。");
            var result = new ProcessResult { Action = package.Id, LogPath = SensitiveDataRedactor.RedactPath(log), CommandSummary = "official-package:" + package.Id + (repair ? " repair" : " install"), RestorePointStatus = "unknown", StandardOutput = String.Empty, StandardError = String.Empty };
            try
            {
                // FileShare.Read keeps the already verified source immutable while the elevated helper copies and revalidates it.
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = powershell, Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                    UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
                }))
                {
                    result.Started = true;
                    Notify(progress, "installing", 0, package.DisplayName, 0, -1);
                    // Never terminate Windows servicing or an MSI transaction; cancellation stops the next item.
                    process.WaitForExit(); result.ExitCode = process.ExitCode;
                }
                result.StandardOutput = SensitiveDataRedactor.Redact(ReadBounded(log));
                result.RestorePointStatus = Marker(result.StandardOutput, "YSRESTORE") ?? "unknown";
                result.StandardError = Marker(result.StandardOutput, "YSERROR") ?? String.Empty;
                File.WriteAllText(log, result.StandardOutput, new UTF8Encoding(false));
            }
            catch (System.ComponentModel.Win32Exception ex)
            { result.ExitCode = ex.NativeErrorCode == 1223 ? 1223 : -1; result.StandardError = SensitiveDataRedactor.Redact(ex.Message); }
            return result;
        }

        internal static string BuildElevatedPackageScript(RepairPackage package, string source, bool repair, bool restoreRequired, string log)
        { return BuildElevatedPackageScriptInSession(package, source, repair, restoreRequired, false, log); }

        private static string BuildElevatedPackageScriptInSession(RepairPackage package, string source, bool repair, bool restoreRequired, bool allowWithoutRestorePoint, string log)
        {
            EnsurePinned(package);
            string arguments = repair && !String.IsNullOrEmpty(package.RepairArguments) ? package.RepairArguments : package.InstallArguments;
            string publisher = package.AllowedPublishers[0];
            var script = new StringBuilder();
            script.Append("$ErrorActionPreference='Stop';$events=New-Object System.Collections.Generic.List[string];$exitCode=7401;$restore='not-attempted';$stage=$null;");
            script.Append("function Hash($p){$s=[IO.File]::OpenRead($p);$h=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($h.ComputeHash($s))).Replace('-','')}finally{$s.Dispose();$h.Dispose()}};");
            script.Append("function Verify($p,$h,$publisher){if((Hash $p)-ne $h){throw 'Pinned package hash mismatch'};$sig=Get-AuthenticodeSignature -LiteralPath $p;if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -ne $publisher){throw 'Pinned package signer invalid'}};");
            script.Append("function Run($p,$a,$cwd){$si=New-Object Diagnostics.ProcessStartInfo;$si.FileName=$p;$si.Arguments=$a;$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.WorkingDirectory=$cwd;$pr=[Diagnostics.Process]::Start($si);$pr.WaitForExit();$c=$pr.ExitCode;$pr.Dispose();return $c};");
            script.Append("function ReadLog($p){if(Test-Path -LiteralPath $p){$r=New-Object IO.StreamReader($p);try{$b=New-Object char[] 262144;$n=$r.ReadBlock($b,0,$b.Length);if($n-gt 0){$events.Add((-join $b[0..($n-1)]))}}finally{$r.Dispose()}}};");
            script.Append("try{");
            // Use a protected ProgramData staging directory, so the elevated installer cannot load unverified adjacent DLLs.
            script.Append("$stage=Join-Path $env:ProgramData ('ysrepair-stage-'+[Guid]::NewGuid().ToString('N'));$sec=New-Object Security.AccessControl.DirectorySecurity;$sec.SetAccessRuleProtection($true,$false);foreach($sid in @('S-1-5-18','S-1-5-32-544')){$identity=New-Object Security.Principal.SecurityIdentifier($sid);$rule=New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow');$sec.AddAccessRule($rule)};[IO.Directory]::CreateDirectory($stage,$sec)|Out-Null;");
            script.Append("$installer=Join-Path $stage 'installer.exe';[IO.File]::Copy('").Append(Ps(source)).Append("',$installer,$false);Verify $installer '").Append(package.Sha256).Append("' '").Append(Ps(publisher)).Append("';");
            if (restoreRequired)
            {
                script.Append("try{$before=(Get-ComputerRestorePoint -ErrorAction Stop|Measure-Object -Property SequenceNumber -Maximum).Maximum;Checkpoint-Computer -Description 'YushuAfterSales component repair' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop;$after=(Get-ComputerRestorePoint -ErrorAction Stop|Measure-Object -Property SequenceNumber -Maximum).Maximum;if($null-eq $after -or $after-le $before){throw 'Restore point not created; Windows frequency limit or protection setting'};$restore='created'}catch{$restore='failed';$events.Add('YSRESTOREERROR='+[string]$_.Exception.Message);");
                script.Append(allowWithoutRestorePoint ? "$restore='failed-user-approved';" : "throw;");
                script.Append("};");
            }
            else script.Append("$restore='session-created';");
            if (package.Kind == "directx")
            {
                script.Append("$dx=Join-Path $stage 'DirectX';[IO.Directory]::CreateDirectory($dx)|Out-Null;$c=Run $installer ('/Q /T:\"'+$dx+'\"') $stage;if($c-ne 0){throw ('DirectX extraction failed: '+$c)};");
                script.Append("Verify (Join-Path $dx 'DXSETUP.exe') '").Append(RepairPackageCatalog.DirectXSetupSha256).Append("' '").Append(RepairPackageCatalog.MicrosoftMoprPublisher).Append("';");
                script.Append("Verify (Join-Path $dx 'DSETUP.dll') '").Append(RepairPackageCatalog.DirectXDsetupSha256).Append("' '").Append(RepairPackageCatalog.MicrosoftMoprPublisher).Append("';");
                script.Append("Verify (Join-Path $dx 'dsetup32.dll') '").Append(RepairPackageCatalog.DirectXDsetup32Sha256).Append("' '").Append(RepairPackageCatalog.MicrosoftMoprPublisher).Append("';");
                script.Append("$exitCode=Run (Join-Path $dx 'DXSETUP.exe') '/silent' $dx;foreach($name in @('DirectX.log','DXError.log')){foreach($dir in @($env:windir,(Join-Path $env:windir 'Logs'))){ReadLog (Join-Path $dir $name)}};");
            }
            else
            {
                script.Append("$a='").Append(Ps(arguments)).Append("'.Replace('{log}',(Join-Path $stage 'installer.log'));$exitCode=Run $installer $a $stage;ReadLog (Join-Path $stage 'installer.log');");
            }
            script.Append("}catch{$events.Add('YSERROR='+[string]$_.Exception.Message);$exitCode=7402};$events.Add('YSRESTORE='+$restore);$events.Add('YSEXIT='+$exitCode);");
            script.Append("$text=$events -join [Environment]::NewLine;$text=$text -replace '(?i)[A-Z]:\\Users\\[^\\]+' ,'%USERPROFILE%';$text=$text -replace '(?im)(machineguid|serial(?:number)?|password|token|api[-_]?key)\\s*[:=]\\s*[^\\r\\n]+','$1=<REDACTED>';[IO.File]::WriteAllText('").Append(Ps(log)).Append("',$text,[Text.Encoding]::UTF8);");
            // Cleanup only the exact GUID staging directory created above, never paths supplied by callers.
            script.Append("if($stage -and ([IO.Path]::GetFileName($stage)-match '^ysrepair-stage-[a-f0-9]{32}$')){try{[IO.Directory]::Delete($stage,$true)}catch{}};exit $exitCode;");
            return script.ToString();
        }

        private static void EnsureAuthorization()
        { if (!AuthorizationService.Load().CanRepair) throw new UnauthorizedAccessException("ysrepair 授权无效，修复与安装被禁止；扫描和报告仍可用。"); }
        private static bool IsPackageRegistered(RepairPackage package)
        {
            if (package.Kind != "vc" && package.Kind != "dotnet" && package.Kind != "framework") return false;
            try
            {
                foreach (Microsoft.Win32.RegistryView view in Environment.Is64BitOperatingSystem
                    ? new[] { Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryView.Registry64 }
                    : new[] { Microsoft.Win32.RegistryView.Registry32 })
                using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view))
                using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                {
                    if (uninstall == null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    using (var key = uninstall.OpenSubKey(name, false))
                    {
                        string display = key == null ? String.Empty : Convert.ToString(key.GetValue("DisplayName"));
                        string version = key == null ? String.Empty : Convert.ToString(key.GetValue("DisplayVersion"));
                        Version parsed;
                        if (String.IsNullOrEmpty(display) || !Version.TryParse(version, out parsed)) continue;
                        bool arch = package.Architecture == "x86/x64" || display.IndexOf(package.Architecture, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!arch) continue;
                        if (package.Kind == "vc" && display.IndexOf("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            parsed.Major == new Version(package.Version).Major) return true;
                        if (package.Kind == "dotnet" && parsed.Major == new Version(package.Version).Major &&
                            display.IndexOf(package.Id.Contains("desktop") ? "Windows Desktop Runtime" : "Microsoft .NET Runtime", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        if (package.Kind == "framework" && display.IndexOf("Microsoft .NET Framework 4.8", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            return false;
        }
        private static void EnsurePinned(RepairPackage p)
        { if (p == null || !Object.ReferenceEquals(RepairPackageCatalog.Find(p.Id), p)) throw new InvalidOperationException("必须使用程序内置的固定官方包目录。"); }
        private static string Ps(string value) { return value.Replace("'", "''"); }
        private static string HashFile(string path)
        { using (var stream = File.OpenRead(path)) using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", String.Empty); }
        private static void EnsureNoReparse(string path)
        {
            string p = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(p))
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("缓存或日志目录不能通过重解析点访问。");
                p = Path.GetDirectoryName(p);
            }
        }
        private static string ReadBounded(string path)
        { if (!File.Exists(path)) return String.Empty; using (var reader = new StreamReader(path, Encoding.UTF8, true)) { var buffer = new char[1024 * 1024]; int n = reader.ReadBlock(buffer, 0, buffer.Length); return new string(buffer, 0, n); } }
        private static string Marker(string text, string name)
        { foreach (string line in (text ?? String.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (line.StartsWith(name + "=", StringComparison.Ordinal)) return line.Substring(name.Length + 1); return null; }
        private static void Notify(Action<RepairProgress> progress, string stage, int percent, string component, long received, long total)
        { if (progress != null) progress(new RepairProgress { Stage = stage, Percent = percent, Component = component, BytesReceived = received, TotalBytes = total }); }
    }
}
