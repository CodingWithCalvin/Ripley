$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& dotnet build (Join-Path $root 'tests\FakeCli\FakeCli.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Test runtime build failed.' }
& dotnet run --project (Join-Path $root 'tests\Reel.Tests\Tests.csproj') -c Release -- (Join-Path $root 'tests\FakeCli\bin\Release\net9.0\FakeCli.exe')
if ($LASTEXITCODE -ne 0) { throw 'Reel checks failed.' }
