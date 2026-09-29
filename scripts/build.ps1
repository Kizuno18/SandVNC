$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$env:TEMP = Join-Path $root '.cache'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $root '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.cache/nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
if (!(Test-Path 'third_party/minhook/CMakeLists.txt')) {
    git -c credential.helper= clone --depth 1 --branch v1.3.4 https://github.com/TsudaKageyu/minhook.git third_party/minhook
    if ($LASTEXITCODE) { throw 'MinHook download failed' }
}
$revision = git -C third_party/minhook rev-parse HEAD
if ($revision -ne 'c3fcafdc10146beb5919319d0683e44e3c30d537') { throw 'Unexpected MinHook revision' }
cmake -S . -B build -G 'Visual Studio 17 2022' -A Win32
if ($LASTEXITCODE) { throw 'CMake configuration failed' }
cmake --build build --config Release
if ($LASTEXITCODE) { throw 'Native build failed' }
dotnet build src/SandVnc.csproj -c Release -o out --disable-build-servers
if ($LASTEXITCODE) { throw 'Broker build failed' }
