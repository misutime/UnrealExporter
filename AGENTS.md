# UnrealExporter Agent Notes

- Do not pass `--no-config` when launching F3D from UE5LibraryBrowser or validation helpers. On this workstation it has previously caused misleading black speckles/black patches in model previews, so viewer launches should preserve the user's F3D config while adding only targeted flags such as blending/tone mapping when needed.
