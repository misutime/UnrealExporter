set shell := ["powershell.exe", "-NoLogo", "-Command"]

default:
    just --list

nte:
    dotnet run --project UnrealExporter nte-all-assets

batman:
    dotnet run --project UnrealExporter batman-all-assets

browser-project:
    Write-Host "AssetLibraryBrowser moved to D:\misutime\AssetLibraryBrowser"

humanoid-project:
    Write-Host "HumanoidRetargeter moved to D:\misutime\HumanoidRetargeter"
