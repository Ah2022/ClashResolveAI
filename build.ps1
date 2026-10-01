param([string]$Configuration = 'Release', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
$stage = Join-Path $artifactRoot ('build-' + [Guid]::NewGuid().ToString('N'))
$release = Join-Path $artifactRoot 'release'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$buildArgs = @('build', (Join-Path $PSScriptRoot 'ClashResolveAI.csproj'), '-c', $Configuration, '-o', $stage)
if ($NoRestore) { $buildArgs += '--no-restore' }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE. Diagnostic output retained in $stage" }
# Replace only generated output under this project's artifacts directory.
# Do not accumulate old build archives. Deployment backups are managed separately.
foreach ($path in @($stage, $release)) {
    if (![IO.Path]::GetFullPath($path).StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid artifact path'
    }
}
if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
Move-Item -LiteralPath $stage -Destination $release
Get-ChildItem -LiteralPath $release -Filter '*.dll' -Recurse -File | Get-FileHash |
    ConvertTo-Json | Set-Content (Join-Path $release 'build-hashes.json')
Write-Output "Fresh build: $release"
