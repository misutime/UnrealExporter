set shell := ["powershell.exe", "-NoLogo", "-Command"]

default:
    just --list

nte:
    dotnet run --project UnrealExporter nte-all-assets

batman:
    dotnet run --project UnrealExporter batman-all-assets

