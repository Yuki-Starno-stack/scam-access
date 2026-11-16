# Builds Scam Access and copies it into the game's BepInEx plugins folder.
# Close the game first: the DLL is locked while it runs.
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
& "$env:DOTNET_ROOT\dotnet.exe" build "$PSScriptRoot\ScamAccess.csproj" -c Release -nologo -v q
$dest = 'C:\Program Files (x86)\Steam\steamapps\common\Scam With Your Friends\BepInEx\plugins\ScamAccess'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$PSScriptRoot\bin\Release\netstandard2.1\ScamAccess.dll" $dest -Force
Write-Output "Deployed to $dest"
