[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [ValidateSet('stable','preview')][string]$Channel = 'preview',
    [switch]$RequireSigning,
    [string]$OutputDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $root
try {
    if ($Channel -eq 'stable' -and $Version.Contains('-')) { throw 'Stable releases require a non-prerelease version.' }
    if ($RequireSigning -and !$env:VPK_SIGN_PARAMS -and !$env:VPK_AZURE_TRUSTED_SIGN_FILE -and !$env:VPK_SIGN_TEMPLATE) { throw 'Release signing is required. Configure a Velopack signing provider before publishing.' }
    $output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    $publish = Join-Path $root 'artifacts/publish-win-x64'
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (!$output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($publish, [StringComparison]::OrdinalIgnoreCase) -or
        $publish.StartsWith($output, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a dedicated output directory inside artifacts, separate from publish-win-x64.' }
    # Clean both so another version/channel can never be uploaded accidentally.
    foreach ($path in @($publish, $output)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'Could not restore the pinned packaging tool.' }
    dotnet publish CanvasDesktop.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
    if ($LASTEXITCODE) { throw 'Self-contained publishing failed.' }
    & "$PSScriptRoot/Verify-Publish.ps1" -Directory $publish
    dotnet vpk pack --packId Schnepg1.CanvasWindowComposer --packTitle 'Canvas Window Composer' --packVersion $Version --packDir $publish --mainExe CanvasDesktop.exe --channel $Channel --runtime win-x64 --icon icon.ico --shortcuts StartMenuRoot --outputDir $output --delta None
    if ($LASTEXITCODE) { throw 'Installer packaging failed.' }
    $assets = @(Get-ChildItem -LiteralPath $output -File)
    if (!($assets.Name -match 'Setup\.exe$') -or !($assets.Name -contains "releases.$Channel.json") -or !($assets.Name -match '\.nupkg$')) { throw 'The installer or update feed/package is missing.' }
    if ($RequireSigning) {
        foreach ($file in @((Join-Path $publish 'CanvasDesktop.exe')) + @($assets | Where-Object Name -match 'Setup\.exe$' | ForEach-Object FullName)) {
            if ((Get-AuthenticodeSignature -LiteralPath $file).Status -ne 'Valid') { throw "Release executable does not have a valid trusted signature: $file" }
        }
    }
    $assets | ForEach-Object { '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt')
    Write-Host "Installer, portable bundle, update feed and checksums: $output"
} finally { Pop-Location }
