param(
  [string]$Version = "1.0.0",
  # Where the installer will be hosted (https). Only used to write dist\latest.json; the installer itself does not contain it.
  [string]$BaseUrl = "https://example.com/tidywin",
  [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish'
$dist = Join-Path $root 'dist'
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup" }

if (-not $SkipPublish) {
  if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
  dotnet publish (Join-Path $root 'src\TidyWin.App') -c Release -o $publish -p:Version=$Version --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

New-Item -ItemType Directory -Force $dist | Out-Null
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutDir=$dist" (Join-Path $PSScriptRoot 'TidyWin.iss')
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

$setup = Join-Path $dist "TidyWin-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $setup).Length
$feed = [ordered]@{
  version = $Version
  url     = "$($BaseUrl.TrimEnd('/'))/TidyWin-Setup-$Version.exe"
  sha256  = $hash
  size    = $size
  notes   = [ordered]@{ zh = "(在这里写这一版的更新说明)"; en = "(Write what changed in this version here.)" }
}
[IO.File]::WriteAllText((Join-Path $dist 'latest.json'), ($feed | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
"installer: $setup  ($([math]::Round($size/1MB,1)) MB)"
"sha256:    $hash"
"feed:      $(Join-Path $dist 'latest.json')  (upload it next to the installer, then put its address in settings.json as updateFeed)"
