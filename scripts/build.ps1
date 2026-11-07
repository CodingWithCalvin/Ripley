param([string]$HandBrakeCli)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& dotnet publish (Join-Path $root 'src\Reel\Reel.csproj') -c Release -o (Join-Path $root 'app') --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'Reel build failed.' }
if ($HandBrakeCli) {
    $runtime = Join-Path $root 'app\handbrake'
    New-Item -ItemType Directory -Force $runtime | Out-Null
    Copy-Item -LiteralPath $HandBrakeCli -Destination (Join-Path $runtime 'HandBrakeCLI.exe') -Force
}
Write-Output (Join-Path $root 'app\Reel.exe')
