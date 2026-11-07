param([string]$HandBrakeCli)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\app'
if ($HandBrakeCli) { $HandBrakeCli = (Resolve-Path -LiteralPath $HandBrakeCli).Path }
Push-Location $root
try {
    & dotnet publish 'src\Ripley\Ripley.csproj' -c Release -o $output --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw 'Ripley build failed.' }
    if ($HandBrakeCli) {
        $runtime = Join-Path $output 'handbrake'
        New-Item -ItemType Directory -Force $runtime | Out-Null
        $destination = Join-Path $runtime 'HandBrakeCLI.exe'
        if ($HandBrakeCli -ne $destination) {
            Copy-Item -LiteralPath $HandBrakeCli -Destination $destination -Force
        }
    }
    Write-Output (Join-Path $output 'Ripley.exe')
} finally { Pop-Location }
