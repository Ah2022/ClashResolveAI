param(
    [string]$BuildOutput = (Join-Path $PSScriptRoot 'artifacts\release'),
    [string]$BackupRoot = (Join-Path $PSScriptRoot 'backups'),
    [switch]$DryRun,
    [string]$VerificationStage = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$buildRoot = [IO.Path]::GetFullPath($BuildOutput)
$revitRoot = 'C:\Program Files\Autodesk\Revit 2024'
$userRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'
$sharedRoot = Join-Path $env:ProgramData 'Autodesk\Revit\Addins\2024'
$installFolder = Join-Path $userRoot 'ClashResolveAI'
$manifestPath = Join-Path $userRoot 'ClashResolveAI.addin'
$assembly = Join-Path $buildRoot 'ClashResolveAI.dll'
if (!(Test-Path -LiteralPath $assembly)) { throw "Build missing: $assembly" }
$version = [Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString()
if ($version -notin @('9.4.0.0')) { throw "Unexpected assembly version: $version" }
if ($VerificationStage) {
    # Disposable-session deployment: never replace binaries loaded by an existing Revit.
    $verificationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'verification')) + '\'
    $destination = [IO.Path]::GetFullPath($VerificationStage)
    if (!$destination.StartsWith($verificationRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Verification stage must be inside workspace verification.' }
    if (Test-Path -LiteralPath $destination) { throw 'Verification stage must be new.' }
    if ($DryRun) { Write-Output "Verification stage: $destination"; return }
    $backup = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) ('verification-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $backup 'original.addin')
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $buildRoot -Filter '*.dll' -File | Where-Object Name -notin @('RevitAPI.dll','RevitAPIUI.dll','Newtonsoft.Json.dll') | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination }
    Copy-Item -LiteralPath (Join-Path $buildRoot 'x64') -Destination $destination -Recurse
    foreach ($file in Get-ChildItem -LiteralPath $destination -Filter '*.dll' -Recurse -File) {
        $relative = $file.FullName.Substring($destination.Length + 1)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $buildRoot $relative)).Hash) { throw "Hash mismatch: $relative" }
    }
    Get-ChildItem -LiteralPath $destination -Filter '*.dll' -Recurse -File | Get-FileHash | ConvertTo-Json | Set-Content (Join-Path $backup 'installed-hashes.json')
    [xml]$verificationManifest = Get-Content -LiteralPath $manifestPath -Raw
    if (@($verificationManifest.RevitAddIns.AddIn).Count -ne 1 -or $verificationManifest.RevitAddIns.AddIn.FullClassName -ne 'ClashResolveAI.App') { throw 'Unexpected manifest ownership.' }
    $verificationManifest.RevitAddIns.AddIn.Assembly = Join-Path $destination 'ClashResolveAI.dll'
    $noHotLoad = $verificationManifest.CreateElement('AllowLoadingIntoExistingSession')
    $noHotLoad.InnerText = 'No'
    $verificationManifest.RevitAddIns.AddIn.AppendChild($noHotLoad) | Out-Null
    $verificationManifest.Save((Join-Path $destination 'ClashResolveAI.addin'))
    @{Manifest=$manifestPath;Backup=$backup;Assembly=(Join-Path $destination 'ClashResolveAI.dll')} | ConvertTo-Json | Set-Content (Join-Path $destination 'deployment.json')
    Write-Output "Disposable build staged and hash-verified: $destination; backup: $backup"
    return
}
foreach ($proc in @(Get-Process Revit -ErrorAction SilentlyContinue)) {
    if (!$DryRun) { throw 'Close Revit before replacing the add-in.' }
    $loaded = @($proc.Modules | Where-Object ModuleName -eq 'ClashResolveAI.dll')
    if ($loaded.Count) { throw "Revit PID $($proc.Id) has ClashResolveAI loaded; close it first." }
}
function Assert-OwnedPath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    $underRoot = $false
    foreach ($r in @($userRoot,$sharedRoot)) {
        if ($resolved.StartsWith($r.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $underRoot = $true }
    }
    if (!$underRoot) { throw "Refusing cleanup outside Revit add-in roots: $resolved" }
    return $resolved
}
$oldManifests = @()
$oldFolders = @()
foreach ($r in @($userRoot,$sharedRoot)) {
    if (!(Test-Path -LiteralPath $r)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $r -Filter '*.addin' -Recurse -File) {
        try { [xml]$xml = Get-Content -LiteralPath $file.FullName -Raw } catch { continue }
        $entries = @($xml.SelectNodes('/RevitAddIns/AddIn'))
        $owned = @($xml.SelectNodes('/RevitAddIns/AddIn[FullClassName="ClashResolveAI.App" or AddInId="7F5F0D2D-4B2D-4F59-9A90-2F2D5B0C4E76"]'))
        if (!$owned.Count) { continue }
        if ($owned.Count -ne $entries.Count) { throw "Mixed vendor manifest needs separate migration: $($file.FullName)" }
        $oldManifests += Assert-OwnedPath $file.FullName
        foreach ($entry in $owned) {
            $path = [string]$entry.Assembly
            if (![IO.Path]::IsPathRooted($path)) { $path = Join-Path $file.DirectoryName $path }
            $dir = Split-Path ([IO.Path]::GetFullPath($path))
            if ((Split-Path $dir -Leaf) -like 'ClashResolveAI*' -and (Test-Path -LiteralPath $dir)) { $oldFolders += Assert-OwnedPath $dir }
        }
    }
    $oldFolders += @(Get-ChildItem -LiteralPath $r -Directory -Filter 'ClashResolveAI*' | ForEach-Object { Assert-OwnedPath $_.FullName })
}
$oldFolders = @($oldFolders | Sort-Object -Unique)
$oldManifests = @($oldManifests | Sort-Object -Unique)
# Detect bundle registrations too; never silently leave another load path enabled.
foreach ($r in @("$env:APPDATA\Autodesk\ApplicationPlugins", "$env:ProgramData\Autodesk\ApplicationPlugins", 'C:\Program Files\Autodesk\ApplicationPlugins')) {
    if (Test-Path -LiteralPath $r) {
        $matches = @(Get-ChildItem -LiteralPath $r -Filter '*.addin' -Recurse -File | Select-String -Pattern 'ClashResolveAI.App|7F5F0D2D-4B2D-4F59-9A90-2F2D5B0C4E76')
        if ($matches.Count) { throw "Additional application bundle registration requires migration: $($matches.Path -join ', ')" }
    }
}
$required = @('ClashResolveAI.dll','ClosedXML.dll','DocumentFormat.OpenXml.dll','ExcelNumberFormat.dll','Irony.dll','XLParser.dll','SixLabors.Fonts.dll','System.Data.SQLite.dll','x64\SQLite.Interop.dll')
foreach ($file in $required) { if (!(Test-Path -LiteralPath (Join-Path $buildRoot $file))) { throw "Missing dependency: $file" } }
Write-Output "Build: $version | DLL: $assembly"
Write-Output "Destination: $installFolder"
Write-Output "Old manifests: $($oldManifests -join ', ')"
Write-Output "Old owned folders: $($oldFolders -join ', ')"
if ($DryRun) { Write-Output 'Dry run complete; no changes.'; return }
$backup = Join-Path ([IO.Path]::GetFullPath($BackupRoot)) ('deployment-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$backupMap = @()
foreach ($path in @($oldManifests) + @($oldFolders)) {
    $target = Join-Path $backup ([string]$backupMap.Count + '-' + (Split-Path $path -Leaf))
    Copy-Item -LiteralPath $path -Destination $target -Recurse -Force
    $backupMap += [pscustomobject]@{Original=$path; Backup=$target}
}
$backupMap | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'restore-map.json') -Encoding UTF8
# Preserve a legacy settings file before replacing the deployment directory.
$settingsFolder = Join-Path $env:APPDATA 'ClashResolveAI'
New-Item -ItemType Directory -Path $settingsFolder -Force | Out-Null
foreach ($dir in $oldFolders) {
    $legacy = Join-Path $dir 'ClashResolveAI_Settings.json'
    $settings = Join-Path $settingsFolder 'ClashResolveAI_Settings.json'
    if ((Test-Path -LiteralPath $legacy) -and !(Test-Path -LiteralPath $settings)) { Copy-Item -LiteralPath $legacy -Destination $settings }
}
$stage = Join-Path $userRoot ('ClashResolveAI-stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Get-ChildItem -LiteralPath $buildRoot -Filter '*.dll' -File | Where-Object Name -notin @('RevitAPI.dll','RevitAPIUI.dll','Newtonsoft.Json.dll') | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage }
Copy-Item -LiteralPath (Join-Path $buildRoot 'x64') -Destination $stage -Recurse
foreach ($sub in @('Resources','src\Rules')) {
    $p = Join-Path $buildRoot $sub
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $stage (Split-Path $sub -Leaf)) -Recurse }
}
try {
    foreach ($path in $oldManifests) { $checked = Assert-OwnedPath $path; if(Test-Path -LiteralPath $checked){ Remove-Item -LiteralPath $checked -Force } }
    foreach ($path in $oldFolders) { $checked = Assert-OwnedPath $path; if(Test-Path -LiteralPath $checked){ Remove-Item -LiteralPath $checked -Recurse -Force } }
    $checkedStage = Assert-OwnedPath $stage
    $checkedInstall = Assert-OwnedPath $installFolder
    Move-Item -LiteralPath $checkedStage -Destination $checkedInstall
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ClashResolveAI.addin') -Raw
    $manifest.RevitAddIns.AddIn.Assembly = Join-Path $installFolder 'ClashResolveAI.dll'
    $manifest.RevitAddIns.AddIn.Name = 'ClashResolve AI ' + ([version]$version).ToString(2)
    $manifest.Save($manifestPath)
    $rules = Join-Path $settingsFolder 'Rules'
    New-Item -ItemType Directory -Path $rules -Force | Out-Null
    foreach($rule in Get-ChildItem (Join-Path $PSScriptRoot 'src\Rules') -Filter '*.json') {
        $dest = Join-Path $rules $rule.Name
        if(!(Test-Path -LiteralPath $dest)){ Copy-Item -LiteralPath $rule.FullName -Destination $dest }
    }
    foreach ($file in Get-ChildItem -LiteralPath $installFolder -Filter '*.dll' -Recurse -File) {
        $relative = $file.FullName.Substring($installFolder.Length + 1)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $buildRoot $relative)).Hash) { throw "Hash mismatch: $relative" }
    }
    Get-ChildItem -LiteralPath $installFolder -Filter '*.dll' -Recurse -File | Get-FileHash | ConvertTo-Json | Set-Content (Join-Path $backup 'installed-hashes.json')
    Write-Output "Installed and hash-verified $version. Backup: $backup"
} catch {
    foreach($item in $backupMap) { Copy-Item -LiteralPath $item.Backup -Destination $item.Original -Recurse -Force }
    throw
}

