$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & dotnet build 'tests\FakeCli\FakeCli.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Test runtime build failed.' }
    $fakeTarget = & dotnet msbuild 'tests\FakeCli\FakeCli.csproj' -getProperty:TargetPath -p:Configuration=Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the test runtime.' }
    $fakeExecutable = [System.IO.Path]::ChangeExtension(($fakeTarget -join '').Trim(), '.exe')
    & dotnet run --project 'tests\Reel.Tests\Tests.csproj' -c Release -- $fakeExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Reel checks failed.' }
} finally { Pop-Location }
