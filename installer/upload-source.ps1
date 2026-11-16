# Uploads the mod's source files to the GitHub project in one commit, using the GitHub API (git is not needed).
# publish-update.ps1 runs this before each release, so a release's "Source code" downloads match that version.
# Prints the new commit id as its last line.
param([string]$Message = 'Update the Scam Access source code', [switch]$Fresh)
$ErrorActionPreference = 'Stop'
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
$repo = 'Yuki-Starno-stack/scam-access'
$src = Split-Path $PSScriptRoot -Parent
$tmp = Join-Path $PSScriptRoot 'build\source-upload'
New-Item -ItemType Directory -Force $tmp | Out-Null

function Api($method, $path, $body) {
    $f = Join-Path $tmp 'body.json'
    [IO.File]::WriteAllText($f, ($body | ConvertTo-Json -Depth 10 -Compress), (New-Object Text.UTF8Encoding($false)))
    $out = & $gh api -X $method $path --input $f
    if ($LASTEXITCODE -ne 0) { throw "API call failed: $method $path" }
    return $out | ConvertFrom-Json
}

# Every .cs file in the project, plus the build scripts and the call guide. (The user does not want
# Shortcuts.txt or a README in the source code.)
$files = @(Get-ChildItem $src -Filter *.cs -File | ForEach-Object { $_.Name })
$files += 'ScamAccess.csproj', 'deploy.ps1', 'How to start a call.txt',
          'installer/Installer.cs', 'installer/build-installers.ps1', 'installer/publish-update.ps1', 'installer/upload-source.ps1'

$main = & $gh api "repos/$repo/git/ref/heads/main" | ConvertFrom-Json
$parent = $main.object.sha
$baseTree = (& $gh api "repos/$repo/git/commits/$parent" | ConvertFrom-Json).tree.sha

$entries = @()
foreach ($rel in $files) {
    $path = Join-Path $src ($rel -replace '/', '\')
    if (-not (Test-Path $path)) { continue }
    $blob = Api 'POST' "repos/$repo/git/blobs" @{ content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)); encoding = 'base64' }
    $entries += @{ path = $rel; mode = '100644'; type = 'blob'; sha = $blob.sha }
}

# .gitignore keeps build output out if someone builds from a downloaded copy.
$ignore = Api 'POST' "repos/$repo/git/blobs" @{ content = "bin/`nobj/`ninstaller/build/`n*.exe`n*.dll`n"; encoding = 'utf-8' }
$entries += @{ path = '.gitignore'; mode = '100644'; type = 'blob'; sha = $ignore.sha }

# -Fresh replaces the whole project with exactly these files as a single commit, dropping older history.
if ($Fresh) {
    $tree = Api 'POST' "repos/$repo/git/trees" @{ tree = $entries }
    $commit = Api 'POST' "repos/$repo/git/commits" @{ message = "$Message`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"; tree = $tree.sha; parents = @() }
    Api 'PATCH' "repos/$repo/git/refs/heads/main" @{ sha = $commit.sha; force = $true } | Out-Null
} else {
    $tree = Api 'POST' "repos/$repo/git/trees" @{ base_tree = $baseTree; tree = $entries }
    $commit = Api 'POST' "repos/$repo/git/commits" @{ message = "$Message`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"; tree = $tree.sha; parents = @($parent) }
    Api 'PATCH' "repos/$repo/git/refs/heads/main" @{ sha = $commit.sha; force = $false } | Out-Null
}
Write-Output $commit.sha
