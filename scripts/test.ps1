$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & dotnet build 'CodingWithCalvin.Ripley.slnx' -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $fakeTarget = & dotnet msbuild 'tests\FakeCli\FakeCli.csproj' -getProperty:TargetPath -p:Configuration=Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate the test runtime.' }
    $fakeExecutable = [System.IO.Path]::ChangeExtension(($fakeTarget -join '').Trim(), '.exe')
    & dotnet run --project 'tests\Ripley.Tests\Ripley.Tests.csproj' -c Release --no-build -- $fakeExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Ripley checks failed.' }
    & dotnet run --project 'tests\Ripley.Desktop.Tests\Ripley.Desktop.Tests.csproj' -c Release --no-build -- $fakeExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Ripley desktop checks failed.' }
} finally { Pop-Location }
