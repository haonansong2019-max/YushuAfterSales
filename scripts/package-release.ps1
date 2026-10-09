[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$buildOutput = Join-Path $projectRoot 'bin/Release/net48'
$executable = Join-Path $buildOutput 'YushuAfterSales.exe'
$executableConfig = Join-Path $buildOutput 'YushuAfterSales.exe.config'
$configTemplate = Join-Path $projectRoot 'app.config'
$appSettings = Join-Path $projectRoot 'appsettings.json'
$projectLicense = Join-Path $projectRoot 'LICENSE'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactsRoot 'YushuAfterSales-portable.zip'
} elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $projectRoot $OutputPath
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputParent = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [string]::Equals($outputParent, $artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Output must be a direct child of the project artifacts directory: $artifactsRoot"
}
if (-not [string]::Equals([System.IO.Path]::GetExtension($resolvedOutput), '.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output file must use the .zip extension.'
}

foreach ($requiredFile in @($executable, $executableConfig, $configTemplate, $appSettings, $projectLicense)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required package input is missing: $requiredFile. Build the Release configuration first."
    }
}

if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $artifactsRoot | Out-Null
}
$artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
if (($artifactsItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing to package through an artifacts reparse point: $artifactsRoot"
}
if (Test-Path -LiteralPath $resolvedOutput) {
    $existingOutput = Get-Item -LiteralPath $resolvedOutput -Force
    throw "Output already exists; choose another filename to avoid overwriting it: $resolvedOutput"
}

$packageId = [Guid]::NewGuid().ToString('N')
$stagingDirectory = Join-Path $artifactsRoot ".package-$packageId"
$temporaryArchive = Join-Path $artifactsRoot ".package-$packageId.zip"

try {
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $stagingDirectory 'YushuAfterSales.exe')
    Copy-Item -LiteralPath $executableConfig -Destination (Join-Path $stagingDirectory 'YushuAfterSales.exe.config')
    Copy-Item -LiteralPath $configTemplate -Destination (Join-Path $stagingDirectory 'app.config')
    Copy-Item -LiteralPath $appSettings -Destination (Join-Path $stagingDirectory 'appsettings.json')
    Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $stagingDirectory 'LICENSE')

    $readme = @'
钰叔售后 - 便携版

运行方式
双击 YushuAfterSales.exe 启动。应用使用 .NET Framework 4.8；目标计算机若未安装该框架，请先从 Microsoft 官方渠道安装。

安全说明
扫描默认只读。只有执行需要系统权限的修复时才会触发 Windows 权限提示。运行库和系统组件请通过应用提供的官方来源与校验流程获取；不要用来源不明的 DLL 覆盖系统文件。

支持范围
项目目标为 Windows 7 SP1、Windows 10 和 Windows 11。具体组件支持情况以应用内提示为准。

日志与报告
请先检查报告中的路径、系统信息和日志，再自行决定是否分享给客服。该便携包不会自动上传报告。
'@
    [System.IO.File]::WriteAllText((Join-Path $stagingDirectory 'README.txt'), $readme, [System.Text.UTF8Encoding]::new($false))

    $licensesDirectory = Join-Path $stagingDirectory 'LICENSES'
    New-Item -ItemType Directory -Path $licensesDirectory | Out-Null
    $licenseTemplate = @'
第三方组件许可证清单模板

本文件用于记录本发行包内实际包含的第三方组件。发布前请逐项填写并附上适用的完整许可证文本；没有随包分发的系统组件或在线下载内容不要列为随包组件。

组件名称：
版本：
来源 URL：
许可证名称及链接：
版权声明：
随包文件/用途：
许可证正文：

'@
    [System.IO.File]::WriteAllText((Join-Path $licensesDirectory 'THIRD-PARTY-LICENSES-TEMPLATE.txt'), $licenseTemplate, [System.Text.UTF8Encoding]::new($false))

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stagingDirectory,
        $temporaryArchive,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        foreach ($requiredEntry in @(
            'YushuAfterSales.exe',
            'YushuAfterSales.exe.config',
            'app.config',
            'appsettings.json',
            'LICENSE',
            'README.txt',
            'LICENSES/THIRD-PARTY-LICENSES-TEMPLATE.txt'
        )) {
            if ($entryNames -notcontains $requiredEntry) {
                throw "Portable package is missing required entry: $requiredEntry"
            }
        }
        if (@($entryNames | Where-Object { $_ -match '(^|/)obj(/|$)|\.pdb$' }).Count -gt 0) {
            throw 'Portable package unexpectedly contains obj files or PDBs.'
        }
    } finally {
        $archive.Dispose()
    }
    Move-Item -LiteralPath $temporaryArchive -Destination $resolvedOutput
    Write-Output "PORTABLE_PACKAGE=$resolvedOutput"
} finally {
    if (Test-Path -LiteralPath $temporaryArchive) {
        Remove-Item -LiteralPath $temporaryArchive -Force
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
