# UnrealExporter Agent Notes

- AssetLibraryBrowser now lives at `D:\misutime\AssetLibraryBrowser`. Browser/F3D preview behavior and viewer-safe cache rules belong there.
- HumanoidRetargeter now lives at `D:\misutime\HumanoidRetargeter`. ARPG Humanoid standardization, retargeting, Unity/Godot helper export, visual gates, and related Blender/Python tools belong there.
- Formal model/animation export must preserve skeletons, skin joint palettes, bind poses, vertex weights, animation tracks, material slots, and texture references for asset correctness. Do not compact, delete, reorder, or otherwise rewrite skin joints/bones merely to satisfy viewer limits such as an F3D/OpenGL joint palette cap.
- This project should stay focused on Unreal package reading, UE asset export, `.ueanim` export, preview/formal UE animation GLB generation, and library index generation.
