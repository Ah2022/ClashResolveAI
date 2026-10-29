param([string]$BuildOutput='',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$BuildOutput){$BuildOutput=Join-Path $workspace 'artifacts/release'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $workspace 'artifacts/packages'}
$build=[IO.Path]::GetFullPath($BuildOutput)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$assembly=Join-Path $build 'ClashResolveAI.dll'
$version=[Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString()
if($version -ne '9.4.0.0'){throw "Unsupported package version: $version"}
foreach($required in @('ClashResolveAI.dll','ClosedXML.dll','System.Data.SQLite.dll','x64/SQLite.Interop.dll')){
    if(!(Test-Path -LiteralPath (Join-Path $build $required))){throw "Missing dependency: $required"}
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage=Join-Path $output ('stage-'+[Guid]::NewGuid().ToString('N'))
$binaries=Join-Path $stage 'artifacts/release'
New-Item -ItemType Directory -Path $binaries -Force | Out-Null
Get-ChildItem -LiteralPath $build -File -Filter '*.dll' | Where-Object Name -notin @('RevitAPI.dll','RevitAPIUI.dll','Newtonsoft.Json.dll') | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $binaries}
Copy-Item -LiteralPath (Join-Path $build 'x64') -Destination $binaries -Recurse
foreach($name in @('deploy-clean.ps1','ClashResolveAI.addin','README.md','CHANGELOG.md')){Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $stage}
New-Item -ItemType Directory -Path (Join-Path $stage 'src/Rules'),(Join-Path $stage 'docs') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $workspace 'src/Rules') -Filter '*.json' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage 'src/Rules')}
Copy-Item -LiteralPath (Join-Path $workspace 'docs/images') -Destination (Join-Path $stage 'docs') -Recurse
Get-ChildItem -LiteralPath (Join-Path $workspace 'docs') -Filter '*.md' -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage 'docs')}
'Close Revit, extract the entire package, then run .\deploy-clean.ps1 -DryRun followed by .\deploy-clean.ps1. Requires Revit 2024. This package does not contain Autodesk binaries or user/project data.' | Set-Content (Join-Path $stage 'INSTALL.txt')
$hashes=@(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {[ordered]@{Path=$_.FullName.Substring($stage.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
[ordered]@{Version=$version;CandidateSHA256=(Get-FileHash -LiteralPath $assembly).Hash;Files=$hashes}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $stage 'package-manifest.json')
$archive=Join-Path $output ('ClashResolveAI-9.4.0-coordination-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
Write-Output "Package: $archive"
Write-Output "Manifest: $(Join-Path $stage 'package-manifest.json')"
Write-Output 'Package created only; nothing installed.'
