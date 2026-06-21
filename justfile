set shell := ["powershell.exe", "-NoLogo", "-Command"]

default:
    just --list

nte:
    dotnet run --project UnrealExporter nte-all-assets

batman:
    dotnet run --project UnrealExporter batman-all-assets

pack-browser:
    $ErrorActionPreference = 'Stop'; $dist = Join-Path (Get-Location) 'dist\UE5LibraryBrowser'; if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }; dotnet publish UE5LibraryBrowser\UE5LibraryBrowser.csproj -c Release -r win-x64 --self-contained false -o $dist; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet publish UE5LibraryBrowser.Cli\UE5LibraryBrowser.Cli.csproj -c Release -r win-x64 --self-contained false -o $dist; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; Write-Host "UE5LibraryBrowser packed to $dist"

browser-thumbnails root='D:\UE5-Assets\nte-all-assets' concurrency='4' limit='0':
    dotnet run --project UE5LibraryBrowser.Cli -- build-thumbnails "{{root}}" {{concurrency}} {{limit}}
