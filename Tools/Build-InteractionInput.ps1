[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'Native/WindowInteraction'
$output = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($architecture in @('x64', 'x86')) {
    $platform = if ($architecture -eq 'x86') { 'Win32' } else { 'x64' }
    $build = Join-Path $output $architecture
    $binaries = Join-Path $build 'bin'
    & cmake -S $source -B $build -A $platform "-DCANVAS_OUTPUT_DIRECTORY=$binaries"
    if ($LASTEXITCODE -ne 0) { throw "Native input configuration failed for $architecture." }
    & cmake --build $build --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Native input build failed for $architecture." }
}
