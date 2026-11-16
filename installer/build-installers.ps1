# Builds the installer "Scam Access.exe" into this folder.
# It carries BepInEx (copied from the local game install), the mod and the NVDA client library,
# and checks GitHub for a newer version every time it runs. The version number comes from Updater.cs.
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$proj = Split-Path $PSScriptRoot -Parent
$game = 'C:\Program Files (x86)\Steam\steamapps\common\Scam With Your Friends'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$work = "$PSScriptRoot\build"
if (Test-Path $work) { Remove-Item $work -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $work | Out-Null

$m = [regex]::Match([IO.File]::ReadAllText("$proj\Updater.cs"), 'public const string Version = "([^"]+)"')
if (-not $m.Success) { throw 'No version in Updater.cs' }
$version = $m.Groups[1].Value
[IO.File]::WriteAllText("$work\BuildInfo.cs", "static class BuildInfo { public const string Version = `"$version`"; }")

foreach ($v in @(@{ Name = 'plain'; Exe = 'Scam Access.exe'; Define = '' })) {
    $out = "$work\$($v.Name)"
    & "$env:DOTNET_ROOT\dotnet.exe" build "$proj\ScamAccess.csproj" -c Release -nologo -v q --no-incremental -o "$out\dll" "-p:IntermediateOutputPath=$out\obj\"
    if ($LASTEXITCODE -ne 0) { throw "Mod build failed ($($v.Name))" }

    $stage = "$out\stage"
    New-Item -ItemType Directory -Force "$stage\BepInEx\core", "$stage\BepInEx\plugins\ScamAccess" | Out-Null
    Copy-Item "$game\winhttp.dll", "$game\doorstop_config.ini", "$game\.doorstop_version" $stage
    Copy-Item "$game\BepInEx\core\*.dll" "$stage\BepInEx\core"
    Copy-Item "$out\dll\ScamAccess.dll" "$stage\BepInEx\plugins\ScamAccess"
    Copy-Item "$game\BepInEx\plugins\ScamAccess\nvdaControllerClient.dll" "$stage\BepInEx\plugins\ScamAccess"

    $zip = "$out\payload.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:$PSScriptRoot\$($v.Exe)", "/resource:$zip,payload.zip",
        "/r:$fw\System.Windows.Forms.dll", "/r:$fw\System.IO.Compression.dll", "/r:$fw\System.IO.Compression.FileSystem.dll")
    if ($v.Define) { $cscArgs += $v.Define }
    $cscArgs += "$PSScriptRoot\Installer.cs", "$work\BuildInfo.cs"
    & $csc $cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($($v.Name))" }
    Write-Output "Built $($v.Exe) (version $version)"
}
