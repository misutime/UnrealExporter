set shell := ["powershell.exe", "-NoLogo", "-Command"]

default:
    just --list

nte:
    dotnet run --project UnrealExporter nte-useful-assets

batman:
    dotnet run --project UnrealExporter batman-models

fortnite:
    dotnet run --project UnrealExporter fortnite-useful-assets
