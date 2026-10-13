param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'HideTrayManager.exe')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler was not found. Install .NET Framework 4.x.'
}

$buildDirectory = Join-Path $PSScriptRoot 'local\build'
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$temporaryExe = Join-Path $buildDirectory ('HideTrayManager-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    $arguments = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/debug-',
        ('/out:' + $temporaryExe),
        ('/win32icon:' + (Join-Path $PSScriptRoot 'src\assets\tray.ico')),
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        (Join-Path $PSScriptRoot 'src\AppConfiguration.cs'),
        (Join-Path $PSScriptRoot 'src\ConfigurationForm.cs'),
        (Join-Path $PSScriptRoot 'src\ProcessSnapshot.cs'),
        (Join-Path $PSScriptRoot 'src\TrayProxy.cs')
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed with exit code $LASTEXITCODE."
    }
    Copy-Item -LiteralPath $temporaryExe -Destination $OutputPath -ErrorAction Stop
    Write-Output "Built: $OutputPath"
} finally {
    if (Test-Path -LiteralPath $temporaryExe) {
        Remove-Item -LiteralPath $temporaryExe
    }
}
