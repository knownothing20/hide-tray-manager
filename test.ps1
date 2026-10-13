$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testDirectory = Join-Path $PSScriptRoot ('local\tests\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testExe = Join-Path $testDirectory 'HideTrayManager.SmokeTests.exe'
$arguments = @('/nologo', '/target:exe', '/platform:anycpu', '/optimize+', '/debug-',
    '/main:SmokeTests', ('/out:' + $testExe),
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll')
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $PSScriptRoot 'tests\SmokeTests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
Write-Output 'Tests passed. A sanitized editor preview is saved under local/tests.'
