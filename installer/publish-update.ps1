# Publishes a new Scam Access version on GitHub.
#   powershell -ExecutionPolicy Bypass -File publish-update.ps1 0.2.2 "What changed"
# It writes the version number into Updater.cs, builds both installers, and makes a GitHub release.
# Anyone who runs any copy of Scam Access.exe afterwards gets this version, because the exe checks the newest release.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ''
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'The version must look like 0.2.2' }
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
$repo = 'Yuki-Starno-stack/scam-access'
$updater = "$(Split-Path $PSScriptRoot -Parent)\Updater.cs"

$code = [IO.File]::ReadAllText($updater)
$code = [regex]::Replace($code, 'public const string Version = "[^"]+"', "public const string Version = `"$Version`"")
[IO.File]::WriteAllText($updater, $code, (New-Object Text.UTF8Encoding($false)))

& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\build-installers.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Building the installers failed' }

# GitHub turns spaces in file names into dots, so name the upload that way ourselves.
$up = "$PSScriptRoot\build\upload"
New-Item -ItemType Directory -Force $up | Out-Null
Copy-Item "$PSScriptRoot\Scam Access.exe" "$up\Scam.Access.exe"
# The README goes on the release page as Scam.Access.readme.txt, never into the source code (the user's choice).
# GitHub lists release files alphabetically (ignoring case), so this name puts it right after Scam.Access.exe
# and before version.txt - the order the user asked for.
Copy-Item "$(Split-Path $PSScriptRoot -Parent)\README.md" "$up\Scam.Access.readme.txt"
[IO.File]::WriteAllText("$up\version.txt", $Version)

# Upload this version's code first, so the release's "Source code" downloads contain it.
$commit = (& powershell -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\upload-source.ps1" "Scam Access $Version" | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Uploading the source code failed' }

if (-not $Notes) { $Notes = "Scam Access $Version" }
$Notes += "`n`nDownload Scam.Access.exe and run it. Run it again any time to update."
& $gh release create "v$Version" (Get-ChildItem $up).FullName --repo $repo --target $commit --title "Scam Access $Version" --notes $Notes --latest
if ($LASTEXITCODE -ne 0) { throw 'Making the GitHub release failed' }
Write-Output "Published Scam Access $Version to https://github.com/$repo/releases/latest"
