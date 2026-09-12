$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$distDir = Join-Path $projectDir 'dist'
$packageDir = Join-Path $distDir 'iQing校园网自动登录-v1.0'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$manifestPath = Join-Path $projectDir 'app.manifest'
$sourcePath = Join-Path $projectDir 'CampusAutoLogin.cs'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -Path $packageDir -ItemType Directory -Force | Out-Null
$exePath = Join-Path $packageDir 'iQing校园网自动登录.exe'

$compilerArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    ('/win32manifest:' + $manifestPath)
    ('/out:' + $exePath)
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.ServiceProcess.dll'
    '/reference:System.Security.dll'
    '/reference:System.Web.Extensions.dll'
    $sourcePath
)

& $compiler $compilerArguments

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exePath)) {
    throw 'Compilation failed.'
}

Copy-Item -LiteralPath (Join-Path $projectDir '使用说明.txt') -Destination (Join-Path $packageDir '使用说明.txt') -Force
$hash = Get-FileHash -LiteralPath $exePath -Algorithm SHA256
Set-Content -LiteralPath (Join-Path $packageDir 'SHA256.txt') -Value ($hash.Hash + '  iQing校园网自动登录.exe') -Encoding ASCII

$zipPath = Join-Path $distDir 'iQing校园网自动登录-v1.0.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath $packageDir -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ('EXE: ' + $exePath)
Write-Host ('ZIP: ' + $zipPath)
