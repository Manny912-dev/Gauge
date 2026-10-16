# Builds every shipping package for Gauge:
#   release\Gauge-<version>-Setup.exe          installer; the installed copy updates itself
#   release\Gauge-<version>-beta-win-x64.zip   full folder build with README and notices (no self-update)
#   release\updates\                           the update feed: upload this folder's contents to the release host
#
# Usage:  powershell -ExecutionPolicy Bypass -File release.ps1
#   -Version 0.3.1   build that version instead of the one in Gauge.csproj
#   -Feed <folder>   write the update feed somewhere else (used to test updates locally)
#   -FeedOnly        only build the installer and update feed, leave the other packages alone
#   -NoUpload        build everything but do not publish to the release host
#
# Publishing: when the two values below are stored as user environment variables, the new
# version is published as a GitHub release at the end, which is what makes installed copies
# update. Without them the script only builds.
#   GAUGE_GITHUB_REPO  https://github.com/<owner>/<repo>  (the public releases repository)
#   VPK_TOKEN          GitHub access token with write access to that repository's contents
#                      (never put this in a file or a chat)
#
# Everything is built in obj\stage first, so a failed build never leaves release\ half-updated.
# release\updates is never cleared: the previous release has to stay there for the next one
# to produce a small delta patch against it.

param([string]$Version, [string]$Feed = 'release\updates', [switch]$FeedOnly, [switch]$NoUpload)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content Gauge.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $Version) { throw 'No <Version> found in Gauge.csproj' }
$name = "Gauge-$Version-beta"
Write-Host "Packaging Gauge $Version"

$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) { throw 'The Velopack packaging tool is missing. Install it with: dotnet tool install -g vpk' }

# A copy running from the build folder locks the files the build has to replace.
$binDir = Join-Path $PSScriptRoot 'bin'
$blocking = Get-Process Gauge -ErrorAction SilentlyContinue | Where-Object { try { $_.MainModule.FileName -like "$binDir*" } catch { $false } }
if ($blocking) { throw "Close the Gauge window running from the build folder first (pid $($blocking.Id -join ', '))." }

function Publish([string]$out, [string[]]$extra) {
    $log = & dotnet publish Gauge.csproj -c Release -r win-x64 --self-contained true -p:DebugType=embedded "-p:Version=$Version" -o $out -v q @extra 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw "publish failed for $out" }
}

$stage = 'obj\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory $stage | Out-Null

# Folder build: the base of the zip, the installer and the update package
Publish "$stage\Gauge" @()
(Get-Content package\README.txt -Raw).Replace('{VERSION}', $Version) | Set-Content "$stage\Gauge\README.txt" -Encoding utf8 -NoNewline
Copy-Item package\THIRD-PARTY-NOTICES.txt "$stage\Gauge\"

if (-not $FeedOnly) {
    Compress-Archive -Path "$stage\Gauge" -DestinationPath "$stage\$name-win-x64.zip" -CompressionLevel Optimal
}

# Installer and update feed. A version can be published to a feed only once.
if (-not (Test-Path $Feed)) { New-Item -ItemType Directory $Feed | Out-Null }
if (Test-Path (Join-Path $Feed "Gauge-$Version-full.nupkg")) {
    Write-Host "Update feed already holds $Version; left as is. Raise <Version> in Gauge.csproj to publish an update."
} else {
    # The installer's panel names the version it installs, so it is drawn now by the build itself;
    # package\splash.png (no version) is the fallback if that fails.
    $splash = Join-Path (Resolve-Path $stage).Path 'splash.png'
    Start-Process "$stage\Gauge\Gauge.exe" -ArgumentList '--splash', "`"$splash`"", $Version -Wait -WindowStyle Hidden
    if (-not (Test-Path $splash)) { Write-Host 'Could not draw the versioned installer panel; using package\splash.png.'; $splash = 'package\splash.png' }
    $log = & $vpk pack --packId Gauge --packVersion $Version --packDir "$stage\Gauge" --mainExe Gauge.exe --packTitle Gauge --packAuthors Gauge --icon gauge.ico --splashImage $splash --splashProgressColor '#FFA53C' --outputDir $Feed 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw 'building the installer and update package failed' }
}

if (-not $FeedOnly) {
    # Swap into release\. A package that is currently running cannot be deleted, but it can be
    # renamed aside; those leftovers are cleared on the next run.
    if (-not (Test-Path release)) { New-Item -ItemType Directory release | Out-Null }
    Get-ChildItem release -Force | Where-Object { $_.Name -ne 'updates' } | ForEach-Object {
        try { Remove-Item $_.FullName -Recurse -Force -Confirm:$false }
        catch { } # still running; handled below
    }
    Get-ChildItem release -Force -File | Where-Object { $_.Extension -ne '.old' } | ForEach-Object {
        Rename-Item $_.FullName ("{0}.{1}.old" -f $_.Name, (Get-Date -Format 'HHmmss'))
        Write-Host "In use, moved aside: $($_.Name)"
    }
    Copy-Item "$stage\$name-win-x64.zip" release\
    Copy-Item (Join-Path $Feed 'Gauge-win-Setup.exe') "release\Gauge-$Version-Setup.exe"
    Copy-Item package\THIRD-PARTY-NOTICES.txt release\
    Get-ChildItem release -File | Where-Object { $_.Extension -ne '.old' } | ForEach-Object { '{0,-40} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }
}

# Publish. Only the real feed is ever uploaded; a test feed elsewhere stays local.
$isRealFeed = (Resolve-Path $Feed).Path -eq (Join-Path $PSScriptRoot 'release\updates')
$repo = [Environment]::GetEnvironmentVariable('GAUGE_GITHUB_REPO', 'User')
$token = [Environment]::GetEnvironmentVariable('VPK_TOKEN', 'User')
if ($NoUpload -or -not $isRealFeed) {
    Write-Host 'Upload skipped.'
} elseif (-not $repo -or -not $token) {
    Write-Host 'Not published: GAUGE_GITHUB_REPO and VPK_TOKEN must both be set.'
} else {
    # The tool reads the token from the environment, so it never appears on a command line.
    $env:VPK_TOKEN = $token
    # OpenRGB is GPL: its exact source is published once, as its own release, beside Gauge's.
    $api = $repo -replace '^https://github\.com/', 'https://api.github.com/repos/'
    $headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'gauge-release'; Accept = 'application/vnd.github+json' }
    $sourceTag = 'openrgb-1.0-source'
    $existing = try { Invoke-RestMethod "$api/releases/tags/$sourceTag" -Headers $headers } catch { $null }
    if (-not $existing) {
        Write-Host 'Publishing the OpenRGB source code release'
        $body = @{ tag_name = $sourceTag; name = 'OpenRGB 1.0 source code'; prerelease = $true
                   body = 'Source code of OpenRGB 1.0, which Gauge includes unmodified as its lighting engine. OpenRGB is free software under the GNU General Public License v2 or later; see LICENSE.txt in the engine folder of any Gauge install.' } | ConvertTo-Json
        $rel = Invoke-RestMethod "$api/releases" -Method Post -Headers $headers -Body $body -ContentType 'application/json'
        $uploadUrl = ($rel.upload_url -replace '\{.*$', '') + '?name=OpenRGB-release_1.0-source.tar.gz'
        Invoke-RestMethod $uploadUrl -Method Post -Headers $headers -InFile 'third-party\OpenRGB-release_1.0-source.tar.gz' -ContentType 'application/gzip' | Out-Null
    }
    Write-Host "Publishing Gauge $Version to $repo"
    $log = & $vpk upload github --outputDir $Feed --repoUrl $repo --publish --releaseName "Gauge $Version" --tag "v$Version" 2>&1
    $code = $LASTEXITCODE
    $env:VPK_TOKEN = $null
    if ($code -ne 0) { $log | Write-Host; throw 'publishing the GitHub release failed; nothing else was changed' }
    Write-Host "Published Gauge $Version."
}

Write-Host "Update feed ($Feed):"
Get-ChildItem $Feed -File | ForEach-Object { '  {0,-38} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }
