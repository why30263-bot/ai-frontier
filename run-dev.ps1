$ErrorActionPreference = 'Stop'

$dDriveDotnet = 'D:\DevTools\dotnet\dotnet.exe'
if (Test-Path $dDriveDotnet) {
    $env:DOTNET_ROOT = 'D:\DevTools\dotnet'
    $env:NUGET_PACKAGES = 'D:\DevTools\nuget-packages'
    $env:DOTNET_CLI_HOME = 'D:\DevTools\dotnet-home'
    $env:TEMP = 'D:\DevTools\temp'
    $env:TMP = 'D:\DevTools\temp'
    $dotnet = $dDriveDotnet
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnet run --project "$PSScriptRoot\AIFrontier.csproj" -c Debug -r win-x64
