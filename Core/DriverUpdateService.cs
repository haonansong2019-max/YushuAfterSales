using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YushuAfterSales.Reporting;

namespace YushuAfterSales.Core
{
    public sealed class DriverUpdateProgress
    {
        public string Stage { get; set; }
        public int Current { get; set; }
        public int Total { get; set; }
        public string DisplayName { get; set; }
        public int Percent { get; set; }
    }
    public sealed class DriverScanResult
    {
        public List<InventoryEntry> Inventory { get; set; } = new List<InventoryEntry>();
        public List<DiagnosticLogEntry> Logs { get; set; } = new List<DiagnosticLogEntry>();
        public string Summary { get; set; }
    }
    public sealed class DriverInstallResult
    {
        public List<ActionRecord> Actions { get; set; } = new List<ActionRecord>();
        public List<DiagnosticLogEntry> Logs { get; set; } = new List<DiagnosticLogEntry>();
        public bool RebootRequired { get; set; }
    }

    /// <summary>Device diagnosis and matched driver delivery through Windows Update; never scrapes driver websites.</summary>
    public static class DriverUpdateService
    {
        private const string SourceUrl = "https://support.microsoft.com/windows/update-drivers-manually-in-windows-ec62f46c-ff14-c91d-eead-d7126dc1f7b6";
        private static readonly Regex UpdateIdentity = new Regex(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:[0-9]{1,9}\z", RegexOptions.CultureInvariant);
        public static bool IsValidUpdateId(string updateId) { return updateId != null && UpdateIdentity.IsMatch(updateId); }
        public static Task<DriverScanResult> ScanAsync(Action<DriverUpdateProgress> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() => Scan(progress, cancellationToken), cancellationToken);
        }
        public static Task<DriverInstallResult> InstallAsync(IEnumerable<string> updateIds, bool confirmed, Action<DriverUpdateProgress> progress, CancellationToken cancellationToken)
        {
            if (!confirmed) throw new InvalidOperationException("驱动安装必须先确认组件、Windows Update 来源、管理员权限与重启风险。");
            string[] ids = updateIds == null ? new string[0] : updateIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length == 0 || ids.Length > 64 || ids.Any(x => !IsValidUpdateId(x)))
                throw new ArgumentException("只允许本次 Windows Update 扫描中选择的有版本更新 ID，最多 64 项。", "updateIds");
            if (!AuthorizationService.Load().CanRepair) throw new InvalidOperationException("有效 ysrepair 授权是下载安装驱动的必要条件。");
            return Task.Run(() => Install(ids, progress, cancellationToken), cancellationToken);
        }
        public static Task<DriverInstallResult> EnableUpdateServiceAsync(bool confirmed, CancellationToken cancellationToken)
        {
            if (!confirmed) throw new InvalidOperationException("启用 Windows Update 服务必须先明确确认：将 wuauserv 启动类型设为手动并启动服务。");
            if (!AuthorizationService.Load().CanRepair) throw new InvalidOperationException("有效 ysrepair 授权是更改 Windows Update 服务的必要条件。");
            return Task.Run(() => Install(new[] { "wuauserv" }, null, cancellationToken, true), cancellationToken);
        }
        private static DriverScanResult Scan(Action<DriverUpdateProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logs = new StringBuilder();
            string resultJson = null;
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = PowerShellPath(), Arguments = EncodedArguments(BuildScanScript()),
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data == null) return;
                    if (args.Data.StartsWith("YS_DRIVER_PROGRESS|", StringComparison.Ordinal))
                    { DriverUpdateProgress item = ParseProgress(args.Data); if (item != null && progress != null) progress(item); }
                    else if (args.Data.StartsWith("YS_DRIVER_RESULT|", StringComparison.Ordinal))
                    { try { resultJson = Encoding.UTF8.GetString(Convert.FromBase64String(args.Data.Substring("YS_DRIVER_RESULT|".Length))); } catch (FormatException) { } }
                    else lock (logs) { if (logs.Length < 1024 * 1024) logs.AppendLine(args.Data); }
                };
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (logs) { if (logs.Length < 1024 * 1024) logs.AppendLine(args.Data); } };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using (cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                {
                    while (!process.WaitForExit(250)) cancellationToken.ThrowIfCancellationRequested();
                    process.WaitForExit();
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            var result = new DriverScanResult();
            if (!String.IsNullOrEmpty(resultJson)) result.Inventory = ReportJson.DeserializeReport(resultJson).Inventory;
            else result.Inventory.Add(UnknownDriverEntry("driver-query-failed", "驱动扫描未产生有效结果", "设备信息或 Windows Update 服务不可用。"));
            string scanEvidence = "evidence-driver-scan-" + Guid.NewGuid().ToString("N");
            result.Logs.Add(new DiagnosticLogEntry { Name = "driver-scan.log", Source = "Windows-PnP-and-Windows-Update", CapturedUtc = DateTime.UtcNow.ToString("o"), EvidenceId = scanEvidence, Content = SensitiveDataRedactor.Redact("inventoryCount=" + result.Inventory.Count + "\r\n" + String.Join("\r\n", result.Inventory.Where(x => x.Status == "unknown" || x.Status == "missing").Select(x => x.EvidenceId + "; " + x.Status + "; " + x.Details)) + "\r\n" + logs) });
            result.Summary = "Windows Update 匹配更新 " + result.Inventory.Count(x => x.RepairSupported) + "；无可用修复或未知 " + result.Inventory.Count(x => x.Status == "missing" || x.Status == "unknown") + "。未安装或修改驱动。";
            return result;
        }
        private static DriverInstallResult Install(string[] ids, Action<DriverUpdateProgress> progress, CancellationToken cancellationToken, bool enableUpdateService = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AuthorizationService.Load().CanRepair) throw new InvalidOperationException("授权已失效，未启动驱动安装。");
            string broker = PowerShellPath();
            PackageSecurityService.PackageValidationResult signature = PackageSecurityService.InspectPackage(broker);
            if (!signature.SignatureVerified || (signature.Publisher ?? "").IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Windows PowerShell 签名或 Microsoft 发布者未通过校验；未启动提升权限进程。");
            string operationId = Guid.NewGuid().ToString("N");
            string startedUtc = DateTime.UtcNow.ToString("o");
            string logEvidence = "evidence-driver-install-" + operationId;
            string taskDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSYUSHU", "YushuAfterSales", "DriverTasks", operationId);
            Directory.CreateDirectory(taskDirectory);
            string resultPath = Path.Combine(taskDirectory, "result.json"), progressPath = Path.Combine(taskDirectory, "progress.jsonl"), cancelPath = Path.Combine(taskDirectory, "cancel-request");
            string script = enableUpdateService ? BuildEnableServiceScript(resultPath, progressPath, cancelPath) : BuildInstallScript(ids, resultPath, progressPath, cancelPath);
            int exitCode, lineCount = 0;
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = broker, Arguments = EncodedArguments(script), UseShellExecute = true, Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            }))
            {
                while (!process.WaitForExit(250))
                {
                    if (cancellationToken.IsCancellationRequested && !File.Exists(cancelPath)) File.WriteAllText(cancelPath, "cancel", Encoding.ASCII);
                    ReadProgressFile(progressPath, ref lineCount, progress);
                }
                ReadProgressFile(progressPath, ref lineCount, progress); exitCode = process.ExitCode;
            }
            // Cancellation is cooperative. Preserve partial/completed installation evidence before returning.
            var result = new DriverInstallResult();
            string json = ReadBoundedText(resultPath, 4 * 1024 * 1024);
            if (!String.IsNullOrWhiteSpace(json))
            {
                ReportDocument report = ReportJson.DeserializeReport(json);
                result.Actions = report.Actions; result.RebootRequired = report.Actions.Any(x => x.Reboot == "required");
            }
            else foreach (string id in ids)
                result.Actions.Add(new ActionRecord
                {
                    ActionId = Guid.NewGuid().ToString("N"), Type = enableUpdateService ? "enable-windows-update-service" : "driver-install", ComponentId = id,
                    EvidenceId = "evidence-driver-" + id.Replace(':', '-'), Permission = "administrator-uac",
                    StartedUtc = startedUtc, CompletedUtc = DateTime.UtcNow.ToString("o"), ExitCode = exitCode, Result = "broker-no-result",
                    SourcePolicy = enableUpdateService ? "windows-service-control" : "official-windows-update", AutomaticExecutionAllowed = true,
                    CommandSummary = "Windows Update selected driver broker; no arbitrary URL or shell arguments"
                });
            foreach (ActionRecord action in result.Actions)
            {
                if (action.EvidenceIds == null) action.EvidenceIds = new List<string>();
                action.EvidenceIds.Add(logEvidence);
            }
            result.Logs.Add(new DiagnosticLogEntry { Name = "driver-install.log", Source = "Windows-Update-UAC-broker", CapturedUtc = DateTime.UtcNow.ToString("o"), EvidenceId = logEvidence, Content = SensitiveDataRedactor.Redact("Windows Update broker exit=" + exitCode + "\r\n" + json) });
            File.WriteAllText(resultPath, SensitiveDataRedactor.Redact(json), new UTF8Encoding(false));
            return result;
        }
        private static string PowerShellPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        }
        private static string EncodedArguments(string script) { return "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)); }
        private static string Quote(string text) { return "'" + text.Replace("'", "''") + "'"; }
        private static string ReadBoundedText(string path, int limit)
        {
            if (!File.Exists(path)) return "";
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
            { char[] buffer = new char[limit]; int count = reader.ReadBlock(buffer, 0, buffer.Length); return new string(buffer, 0, count); }
        }
        private static DriverUpdateProgress ParseProgress(string line)
        {
            string[] parts = line.Split('|'); if (parts.Length != 6) return null;
            int current, total, percent;
            if (!Int32.TryParse(parts[2], out current) || !Int32.TryParse(parts[3], out total) || !Int32.TryParse(parts[5], out percent)) return null;
            try { return new DriverUpdateProgress { Stage = parts[1], Current = current, Total = total, DisplayName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4])), Percent = percent }; }
            catch (FormatException) { return null; }
        }
        private static void ReadProgressFile(string path, ref int lineCount, Action<DriverUpdateProgress> progress)
        {
            string[] lines;
            try { lines = ReadBoundedText(path, 1024 * 1024).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); }
            catch (IOException) { return; }
            for (int index = lineCount; index < lines.Length; index++)
            { DriverUpdateProgress item = ParseProgress(lines[index]); if (item == null) break; if (progress != null) progress(item); lineCount = index + 1; }
        }
        private static InventoryEntry UnknownDriverEntry(string id, string name, string details)
        {
            return new InventoryEntry { ComponentId = id, Category = "driver", DisplayName = name, Architecture = "system", Status = "unknown", EvidenceId = "evidence-" + id, SourceUrl = SourceUrl, RepairSupported = false, RepairAction = "manual-investigation-only", RepairSourcePolicy = "official-windows-update", Details = details };
        }
        private static string ScriptHeader()
        {
            return @"
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$items=New-Object 'System.Collections.Generic.List[object]'
$actions=New-Object 'System.Collections.Generic.List[object]'
$report=@{schemaVersion='1.0';reportId=[Guid]::NewGuid().ToString('N');inventory=@();actions=@()}
function Redact([string]$text) {
 $text=$text -replace '(?i)[A-Z]:\\Users\\[^\\]+' ,'%USERPROFILE%'
 return ($text -replace '(?im)(machineguid|serial(?:number)?|password|token|api[-_]?key)\s*[:=]\s*[^\r\n]+','$1=<REDACTED>')
}
function ProgressEvent([string]$stage,[int]$current,[int]$total,[string]$name,[int]$percent) {
 $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($name))
 $line='YS_DRIVER_PROGRESS|'+$stage+'|'+$current+'|'+$total+'|'+$encoded+'|'+$percent
 if ($progressPath) { [IO.File]::AppendAllText($progressPath,$line+[Environment]::NewLine,[Text.Encoding]::UTF8) } else { Write-Output $line }
}
function DriverEntry([string]$id,[string]$name,[string]$status,[bool]$repair,[string]$details) {
 return @{componentId=$id;category='driver';displayName=$name;architecture='system';status=$status;
 evidenceId=('evidence-driver-'+$id.Replace(':','-'));sourceUrl='" + SourceUrl + @"';
 repairSupported=$repair;repairAction=$(if($repair){'driver:'+$id}else{'manual-investigation-only'});
 repairSourcePolicy='official-windows-update';signatureStatus='Windows Update catalog policy';details=(Redact $details)}
}
";
        }
        private static string BuildScanScript()
        {
            return ScriptHeader() + @"
$progressPath=$null
$devices=@()
$problemDevices=@()
$signedDrivers=@()
try {
 ProgressEvent 'devices' 0 0 '检查设备驱动状态' 0
 $devices=@(Get-WmiObject -Class Win32_PnPEntity -ErrorAction Stop)
 $problemDevices=@($devices | Where-Object {$_.ConfigManagerErrorCode -ne 0})
 $signedDrivers=@(Get-WmiObject -Class Win32_PnPSignedDriver -ErrorAction Stop)
} catch { [void]$items.Add((DriverEntry 'pnp-query-error' '设备状态查询失败' 'unknown' $false $_.Exception.Message)) }
$matchedDeviceIds=New-Object 'System.Collections.Generic.HashSet[string]'
try {
 ProgressEvent 'searching' 0 0 '查询微软 Windows Update 适配驱动，可能需要数分钟' 0
 $session=New-Object -ComObject Microsoft.Update.Session
 $session.ClientApplicationID='YushuAfterSales ysrepair'
 $searcher=$session.CreateUpdateSearcher(); $searcher.ServerSelection=2
 $search=$searcher.Search(""IsInstalled=0 and Type='Driver'"")
 if ($search.ResultCode -ne 2 -and $search.ResultCode -ne 3) { throw ('Windows Update query result='+$search.ResultCode) }
 if ($search.ResultCode -eq 3) { [void]$items.Add((DriverEntry 'wu-partial-result' 'Windows Update 查询返回部分结果' 'unknown' $false '部分设备匹配结果可能未返回。')) }
 for($i=0;$i -lt $search.Updates.Count;$i++) {
  $update=$search.Updates.Item($i)
  $id=$update.Identity.UpdateID+':'+$update.Identity.RevisionNumber
  $matches=@($devices | Where-Object { $update.DriverHardwareID -and ($_.HardwareID -contains $update.DriverHardwareID) })
  foreach($device in $matches) { [void]$matchedDeviceIds.Add([string]$device.PNPDeviceID) }
  $state=if(@($matches | Where-Object {$_.ConfigManagerErrorCode -eq 28}).Count -gt 0){'missing'}else{'outdated'}
  $entry=DriverEntry $id $update.Title $state $true ('Windows Update device match; HardwareId='+$update.DriverHardwareID+'; manufacturer='+$update.DriverManufacturer+'; class='+$update.DriverClass)
  $installedDriver=$signedDrivers | Where-Object {$matches.PNPDeviceID -contains $_.DeviceID} | Select-Object -First 1
  $entry.detectedVersion=if($installedDriver){[string]$installedDriver.DriverVersion}else{''}; $entry.expectedVersion=[string]$update.DriverVerDate
  [void]$items.Add($entry)
  ProgressEvent 'matched' ($i+1) $search.Updates.Count $update.Title 100
 }
} catch { [void]$items.Add((DriverEntry 'wu-query-error' 'Windows Update 驱动查询失败' 'unknown' $false $_.Exception.Message)) }
foreach($device in $problemDevices) {
 if($matchedDeviceIds.Contains([string]$device.PNPDeviceID)){continue}
 $sha=[Security.Cryptography.SHA256]::Create()
 try {$hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$device.PNPDeviceID)))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
 $state=if($device.ConfigManagerErrorCode -eq 28){'missing'}else{'unknown'}
 [void]$items.Add((DriverEntry ('device-'+$hash.Substring(0,24)) ([string]$device.Name) $state $false ('ConfigManagerErrorCode='+$device.ConfigManagerErrorCode+'; 当前没有 Windows Update 适配包，不会把官网链接标成可修复。')))
}
$seenInstalled=New-Object 'System.Collections.Generic.HashSet[string]'
foreach($driver in $signedDrivers) {
 if(!$driver.DeviceID -or !$seenInstalled.Add([string]$driver.DeviceID)){continue}
 if($matchedDeviceIds.Contains([string]$driver.DeviceID) -or @($problemDevices | Where-Object {$_.PNPDeviceID -eq $driver.DeviceID}).Count -gt 0){continue}
 $sha=[Security.Cryptography.SHA256]::Create()
 try {$hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$driver.DeviceID)))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
 $name=if($driver.DeviceName){[string]$driver.DeviceName}else{[string]$driver.FriendlyName}
 if(!$name){$name='已安装设备驱动'}
 $entry=DriverEntry ('installed-'+$hash.Substring(0,24)) $name 'installed' $false ('provider='+$driver.DriverProviderName+'; IsSigned='+$driver.IsSigned+'; DriverDate='+$driver.DriverDate+'; 已安装状态不代表厂商最新版，更新以Windows Update设备匹配结果为准。')
 $entry.detectedVersion=[string]$driver.DriverVersion
 $entry.signatureStatus='WMI IsSigned='+$driver.IsSigned
 [void]$items.Add($entry)
}
if($items.Count -eq 0) { [void]$items.Add((DriverEntry 'wu-no-applicable-update' '未发现缺驱动设备或适配更新' 'observed' $false '本次设备检查和 Windows Update 查询没有返回适配更新，不代表所有厂商驱动均为最新版。')) }
$report.inventory=@($items.ToArray())
$json=$report | ConvertTo-Json -Depth 10 -Compress
Write-Output ('YS_DRIVER_RESULT|'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)))
";
        }
        private static string BuildEnableServiceScript(string resultPath, string progressPath, string cancelPath)
        {
            return ScriptHeader() + "$resultPath=" + Quote(resultPath) + ";$progressPath=" + Quote(progressPath) + ";$cancelPath=" + Quote(cancelPath) + @"
$started=[DateTime]::UtcNow.ToString('o'); $previousMode='unknown'; $previousState='unknown'; $result='not-started'; $code=1; $details=''
try {
 $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
 if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator UAC token required'}
 if([IO.File]::Exists($cancelPath)){throw 'Cancelled before service configuration'}
 $before=Get-WmiObject -Class Win32_Service -Filter ""Name='wuauserv'"" -ErrorAction Stop
 if(!$before){throw 'Windows Update service not found'}
 $previousMode=[string]$before.StartMode; $previousState=[string]$before.State
 ProgressEvent 'enable-update-service' 1 1 '设为手动并启动 Windows Update 服务' 0
 Set-Service -Name wuauserv -StartupType Manual -ErrorAction Stop
 Start-Service -Name wuauserv -ErrorAction Stop
 $after=Get-WmiObject -Class Win32_Service -Filter ""Name='wuauserv'"" -ErrorAction Stop
 if($after.State -ne 'Running'){throw 'Windows Update service did not enter Running state'}
 $result='completed';$code=0;$details='currentStartMode='+$after.StartMode+'; currentState='+$after.State
 ProgressEvent 'completed' 1 1 'Windows Update 服务已启动，可重新检测' 100
} catch {
 $result=if([IO.File]::Exists($cancelPath)){'cancelled'}else{'failed'}
 $code=$_.Exception.HResult; $details=$_.Exception.Message
 try { $current=Get-WmiObject -Class Win32_Service -Filter ""Name='wuauserv'"" -ErrorAction Stop; $details+='; currentStartMode='+$current.StartMode+'; currentState='+$current.State } catch {}
} finally {
 [void]$actions.Add(@{actionId=[Guid]::NewGuid().ToString('N');type='enable-windows-update-service';componentId='wuauserv';
 evidenceId='evidence-driver-wu-query-error';startedUtc=$started;completedUtc=[DateTime]::UtcNow.ToString('o');
 permission='administrator-uac';exitCode=$code;reboot='not-required';result=$result;sourcePolicy='windows-service-control';
 automaticExecutionAllowed=$true;commandSummary=(Redact ('Set-Service wuauserv -StartupType Manual; Start-Service wuauserv; previousStartMode='+$previousMode+'; previousState='+$previousState+'; '+$details))})
 $report.actions=@($actions.ToArray());$json=$report | ConvertTo-Json -Depth 10 -Compress
 [IO.File]::WriteAllText($resultPath,$json,(New-Object Text.UTF8Encoding($false)))
}
exit $(if($result -eq 'completed'){0}else{1})
";
        }
        private static string BuildInstallScript(string[] ids, string resultPath, string progressPath, string cancelPath)
        {
            return ScriptHeader() + "$resultPath=" + Quote(resultPath) + ";$progressPath=" + Quote(progressPath) + ";$cancelPath=" + Quote(cancelPath) + ";$selected=@(" + String.Join(",", ids.Select(Quote)) + @")
$exit=0; $restore='not-attempted'
function AddAction([string]$id,[string]$result,[int]$code,[bool]$reboot,[string]$details,[string]$started) {
 [void]$actions.Add(@{actionId=[Guid]::NewGuid().ToString('N');type='driver-install';componentId=$id;
 evidenceId=('evidence-driver-'+$id.Replace(':','-'));startedUtc=$started;completedUtc=[DateTime]::UtcNow.ToString('o');
 permission='administrator-uac';exitCode=$code;reboot=$(if($reboot){'required'}else{'not-required'});result=$result;
 sourcePolicy='official-windows-update';automaticExecutionAllowed=$true;commandSummary=(Redact ('Windows Update selected driver; restorePoint='+$restore+'; '+$details))})
}
try {
 $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
 if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator UAC token required'}
 if([IO.File]::Exists($cancelPath)){throw 'Cancelled before driver repair'}
 ProgressEvent 'restore-point' 0 $selected.Count '创建驱动修复前还原点' 0
 $before=@(Get-ComputerRestorePoint -ErrorAction Stop | Select-Object -ExpandProperty SequenceNumber)
 Checkpoint-Computer -Description 'YushuAfterSales driver repair' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
 $after=@(Get-ComputerRestorePoint -ErrorAction Stop | Where-Object {$before -notcontains $_.SequenceNumber})
 if($after.Count -eq 0){throw 'No new restore point created; driver install not started'}
 $restore='created'
 $session=New-Object -ComObject Microsoft.Update.Session; $session.ClientApplicationID='YushuAfterSales ysrepair'
 $searcher=$session.CreateUpdateSearcher(); $searcher.ServerSelection=2
 ProgressEvent 'searching' 0 $selected.Count '重新校验选定 Windows Update 驱动身份' 0
 $search=$searcher.Search(""IsInstalled=0 and Type='Driver'"")
 if($search.ResultCode -ne 2){throw ('Revalidation query failed or partial: '+$search.ResultCode)}
 for($index=0;$index -lt $selected.Count;$index++) {
  $id=$selected[$index];$started=[DateTime]::UtcNow.ToString('o')
  if([IO.File]::Exists($cancelPath)){AddAction $id 'cancelled' 5 $false 'Cancelled before processing this driver' $started;continue}
  $update=$null
  for($j=0;$j -lt $search.Updates.Count;$j++){ $candidate=$search.Updates.Item($j); if(($candidate.Identity.UpdateID+':'+$candidate.Identity.RevisionNumber) -eq $id){$update=$candidate;break} }
  if(!$update){AddAction $id 'stale-selection' 1168 $false 'Selected update identity/revision no longer available' $started;continue}
  try {
   if(!$update.EulaAccepted){$update.AcceptEula()}
   $collection=New-Object -ComObject Microsoft.Update.UpdateColl; [void]$collection.Add($update)
   $downloader=$session.CreateUpdateDownloader();$downloader.Updates=$collection
   ProgressEvent 'downloading' ($index+1) $selected.Count $update.Title 0
   $job=$downloader.BeginDownload($null,$null,$null)
   while(!$job.IsCompleted){
    if([IO.File]::Exists($cancelPath)){$job.RequestAbort()}
    ProgressEvent 'downloading' ($index+1) $selected.Count $update.Title $job.GetProgress().PercentComplete
    Start-Sleep -Milliseconds 250
   }
   $download=$downloader.EndDownload($job)
   if($download.ResultCode -ne 2){AddAction $id $(if($download.ResultCode -eq 5){'cancelled'}else{'download-failed'}) $download.HResult $false ('Download ResultCode='+$download.ResultCode) $started;continue}
   if([IO.File]::Exists($cancelPath)){AddAction $id 'cancelled' 5 $false 'Cancelled after download' $started;continue}
   $installer=$session.CreateUpdateInstaller();$installer.Updates=$collection;$installer.AllowSourcePrompts=$false
   if($installer.RebootRequiredBeforeInstallation){AddAction $id 'reboot-required-before-install' 3010 $true 'Windows requires restart before installation' $started;continue}
   ProgressEvent 'installing' ($index+1) $selected.Count $update.Title 0
   $installJob=$installer.BeginInstall($null,$null,$null)
   while(!$installJob.IsCompleted){
    if([IO.File]::Exists($cancelPath)){$installJob.RequestAbort()}
    ProgressEvent 'installing' ($index+1) $selected.Count $update.Title $installJob.GetProgress().PercentComplete
    Start-Sleep -Milliseconds 250
   }
   $installed=$installer.EndInstall($installJob);$itemResult=$installed.GetUpdateResult(0)
   $state=if($itemResult.ResultCode -eq 2){'completed'}elseif($itemResult.ResultCode -eq 3){'completed-with-errors'}elseif($itemResult.ResultCode -eq 5){'cancelled'}else{'failed'}
   AddAction $id $state $itemResult.HResult $installed.RebootRequired ('WU ResultCode='+$itemResult.ResultCode+'; title='+$update.Title) $started
   ProgressEvent $state ($index+1) $selected.Count $update.Title 100
  } catch { AddAction $id 'failed' $_.Exception.HResult $false $_.Exception.Message $started; $exit=1 }
 }
} catch {
 $exit=1
 foreach($id in $selected){ if(@($actions | Where-Object {$_.componentId -eq $id}).Count -eq 0){AddAction $id 'not-started' $_.Exception.HResult $false $_.Exception.Message ([DateTime]::UtcNow.ToString('o'))} }
} finally {
 $report.actions=@($actions.ToArray())
 $json=$report | ConvertTo-Json -Depth 10 -Compress
 [IO.File]::WriteAllText($resultPath,$json,(New-Object Text.UTF8Encoding($false)))
}
exit $exit
";
        }
    }
}
