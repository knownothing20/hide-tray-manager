# HideTrayManager 一键构建部署
# 流程：停止运行中的实例 -> 备份当前 exe 到 backup\ -> 编译 -> 替换根目录 exe -> 重启
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 [-NoRestart]

param(
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$rootExe = Join-Path $repo 'HideTrayManager.exe'

if (Get-Process HideTrayManager -ErrorAction SilentlyContinue) {
    Write-Output "== stop running instance =="
    Stop-Process -Name HideTrayManager -Force
    Start-Sleep -Seconds 1
}

if (Test-Path $rootExe) {
    Write-Output "== backup current exe =="
    New-Item -ItemType Directory -Force (Join-Path $repo 'backup') | Out-Null
    Copy-Item $rootExe (Join-Path $repo ("backup\HideTrayManager-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".exe")) -Force
    # 只保留最近 2 个构建备份，更旧的删除；版本回溯走 GitHub Releases/提交历史。
    Get-ChildItem (Join-Path $repo 'backup') -Filter 'HideTrayManager-*.exe' |
        Sort-Object Name -Descending | Select-Object -Skip 2 |
        ForEach-Object { Remove-Item $_.FullName -Force }
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler was not found. Install .NET Framework 4.x.'
}

$buildDirectory = Join-Path $repo 'local\build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$temporaryExe = Join-Path $buildDirectory ('HideTrayManager-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $arguments = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/debug-',
        ('/out:' + $temporaryExe),
        ('/win32icon:' + (Join-Path $repo 'src\assets\tray.ico')),
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        (Join-Path $repo 'src\AppConfiguration.cs'),
        (Join-Path $repo 'src\ConfigurationForm.cs'),
        (Join-Path $repo 'src\ProcessSnapshot.cs'),
        (Join-Path $repo 'src\TrayProxy.cs')
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE."
    }
    Copy-Item -LiteralPath $temporaryExe -Destination $rootExe -ErrorAction Stop
    Write-Output "Built and deployed: $rootExe"
}
finally {
    if (Test-Path -LiteralPath $temporaryExe) {
        Remove-Item -LiteralPath $temporaryExe
    }
}

if (-not $NoRestart) {
    Start-Process $rootExe
    Start-Sleep -Seconds 2
    if (-not (Get-Process HideTrayManager -ErrorAction SilentlyContinue)) {
        throw 'deployment verification failed: process is not running'
    }
    Write-Output "Restarted. Root HideTrayManager.exe is now the latest build."
}
