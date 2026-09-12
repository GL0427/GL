[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [ValidateNotNullOrEmpty()]
  [string]$ImagePath,
  [string]$OutputRoot = (Join-Path $env:USERPROFILE 'Pictures\CodexWallpaperThemes')
)

$ErrorActionPreference = 'Stop'
$source = [System.IO.Path]::GetFullPath($ImagePath)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
  throw "Image not found: $source"
}
$extension = [System.IO.Path]::GetExtension($source).ToLowerInvariant()
if ($extension -notin @('.jpg', '.jpeg', '.png', '.webp')) {
  throw "Unsupported image format: $extension (use JPG, PNG, or WebP)."
}
$bytes = [System.IO.File]::ReadAllBytes($source)
if ($bytes.Length -lt 1 -or $bytes.Length -gt 16MB) {
  throw 'Image must be between 1 byte and 16 MB.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$id = "codex-wallpaper-$stamp"
$root = [System.IO.Path]::GetFullPath($OutputRoot)
$themeDir = Join-Path $root $id
New-Item -ItemType Directory -Path $themeDir -Force | Out-Null
$imageName = 'background' + $extension
$target = Join-Path $themeDir $imageName
Copy-Item -LiteralPath $source -Destination $target -Force

$theme = [ordered]@{
  schemaVersion = 1
  id = $id
  name = 'Codex Custom Wallpaper'
  appearance = 'auto'
  art = [ordered]@{ focusX = 0.5; focusY = 0.45; safeArea = 'left'; taskMode = 'ambient' }
  image = $imageName
}
$json = $theme | ConvertTo-Json -Depth 5
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $themeDir 'theme.json'), $json, $utf8)

$css = @'
[data-ds-part="root"] { background-color: rgba(22,16,28,.76); color: #f5edf7; }
[data-ds-part="sidebar"] { background-color: rgba(20,14,26,.82); border-right-color: rgba(220,170,235,.24); }
[data-ds-part="main"] { background-color: rgba(18,16,24,.34); }
[data-ds-part="header"] { background-color: rgba(24,17,30,.58); border-bottom-color: rgba(220,170,235,.22); }
[data-ds-part="composer"] { background-color: rgba(28,19,35,.82); border-color: rgba(220,170,235,.34); }
[data-ds-part="dialog"] { background-color: rgba(28,19,35,.94); color: #f5edf7; }
'@
[System.IO.File]::WriteAllText((Join-Path $themeDir 'theme.css'), $css.Trim() + [Environment]::NewLine, $utf8)

[pscustomobject]@{
  ThemeId = $id
  ThemeDir = $themeDir
  ImagePath = $target
  ThemeJson = (Join-Path $themeDir 'theme.json')
  ThemeCss = (Join-Path $themeDir 'theme.css')
} | ConvertTo-Json -Compress
