set shell := ["powershell.exe", "-NoLogo", "-Command"]

default:
    just --list

nte:
    dotnet run --project UnrealExporter nte-useful-assets

nte-library:
    dotnet run --project UnrealExporter nte-reusable-library

nte-library-smoke:
    dotnet run --project UnrealExporter nte-reusable-library-smoke

batman:
    dotnet run --project UnrealExporter batman-models

fortnite:
    dotnet run --project UnrealExporter fortnite-useful-assets
