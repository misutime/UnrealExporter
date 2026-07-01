# NTE 可复用素材库烟测报告

日期：2026-06-20

目标不是原始全量转储，而是确认 UnrealExporter 能在小范围内导出满足“可复用素材库”标准的资源：独立模型、共享贴图、材质贴图槽、骨骼、独立动画、确定性模型动画关系和验证报告。

2026-06-20 复评修订：上一版报告把 `NPC_018` 的数据层通过和截帧有变化误判为“人形动画通过”。复评后该结论撤销：`NPC_018` 视觉上存在明显身体扭曲、附件/骨骼拉飞和姿态异常，必须标记为视觉验收失败。随后修正了动画预览的 rotation 处理、Blender 场景清理和 preview GLB morph target 兼容性，并新增 10 条“同 Skeleton、无 retarget/skip”的人形样本批量预览烟测。当前结论是：这些 v2 样本只能证明同 Skeleton / 无自动修补路径可以批量生成可打开且有动画变化的 preview；视觉正确性尚未充分证明，需要进一步近景截图、结构异常检查和逐样本人工判定。跨体型、跨上下文或需要自动 retarget 的动画仍必须标记为不确定/待人工检查，不能自动宣称成功。

2026-06-20 根因修复补充：人形动画大面积异常的主因已定位为 UE -> glTF 坐标换基中的四元数符号错误。`SwapYZ` 是改变 handedness 的反射换基，不能只交换 `Y/Z` 分量；四元数需要同步翻转 `W`。已修复 CUE4Parse glTF 骨骼导出和 `--preview-ue-animation` 动画写入。用 Cang/Haniel 11 个样本重新导出/合成后，Blender 近景复验显示主骨架、四肢、脊柱、头部姿态恢复为视觉可读状态；其中 10 个 pass，`Cang_HitDown` 因头发/衣摆长条几何产生顶点离群警告，标记为 secondary-motion suspicious，不作为最高置信 pass。

## 验收口径

以下条件只能证明管线跑通，不能证明动画正确：

- matchedTracks 数量高
- missingBones 为 0
- 文件能被 Blender/F3D 打开
- 动画帧有变化
- GLB/FBX 成功生成

人形动画必须同时通过结构和视觉验收：

- 静态模型正常，白模形状完整，材质贴图正常，骨骼层级合理，skin 没有明显错绑。
- 动画播放时人体比例不能明显拉伸、扭曲、断裂、飞骨、钻地或缩放异常。
- 手、脚、头、脊柱、肩、髋等主要骨骼姿态必须合理。
- 附件、武器、头发、衣摆等不能被错误骨骼拉飞。
- 有 retarget、bind pose、bone space、scale 等 warning 的样本不能作为最高置信度成功样本。
- 必须提供 rest pose、动画中间帧、末帧截图；截图肉眼可见变形即判失败。
- 烟测至少覆盖 10 个模型与动画组合，且优先包含多个不同人形角色和不同动作类型；跨骨架/跨体型样本只有在无 retarget warning 且视觉通过时才能作为高置信度成功证据。

## 调研依据

- glTF 2.0 规范要求 animation channel 写入目标 node 的 TRS，translation/rotation/scale 都是节点属性，不是“任意骨骼轨迹 blob”。参考：<https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html>
- Khronos glTF skin 教程说明 skinning 依赖当前 joint global transform 与 inverseBindMatrix 的组合；如果动画 TRS 与 bind pose / inverse bind matrix / joint hierarchy 不一致，即使骨骼名匹配也会变形。参考：<https://github.com/KhronosGroup/glTF-Tutorials/blob/main/gltfTutorial/gltfTutorial_020_Skins.md>
- Unreal 官方文档说明 Animation Sequence 包含 Skeleton 的 position/rotation/scale key，同 Skeleton 可以共享动画，但不同体型或不同 Skeleton 需要 retarget / compatible skeleton 机制。参考：<https://dev.epicgames.com/documentation/en-us/unreal-engine/animation-sequences-in-unreal-engine>
- 本地 CUE4Parse 代码也显示动画转换会处理 `RetargetBasePose`、translation retarget mode、additive pose、骨骼 scale 调整等逻辑；因此不能只按骨骼名把 `.ueanim` track 写入 glTF node TRS 就宣称正确。
- 本地数学复核：`SwapYZ` 对向量是矩阵 `[[1,0,0],[0,0,1],[0,1,0]]`，行列式为 `-1`，属于反射换基。对任意 UE 四元数 `q=(x,y,z,w)`，换基后与 `S * R(q) * S` 等价的 glTF 四元数为 `(x,z,y,-w)`（或整体取负的等价四元数）。旧代码写成 `(x,z,y,w)`，会产生“文件能动但姿态错”的典型症状。

## 使用命令

候选素材库导出：

```powershell
cd D:\misutime\UnrealExporter
just nte-library
```

小范围烟测：

```powershell
cd D:\misutime\UnrealExporter
just nte-library-smoke
```

模型 + 独立动画合成预览：

```powershell
dotnet run --project UnrealExporter -- --preview-ue-animation --model "F:\UE-Assets\nte-reusable-library-smoke\HT\Content\Characters\Animal\Fish\SK_Fish_41_skin.gltf" --animation "F:\UE-Assets\nte-reusable-library-smoke\HT\Content\Characters\Animal\Fish\SK_Fish_41_Anim.ueanim" --output "F:\UE-Assets\nte-reusable-library-smoke\_preview\SK_Fish_41_skin__SK_Fish_41_Anim.preview.glb"
```

人形模型 + 独立动画视觉烟测：

```powershell
dotnet run --project UnrealExporter -- --preview-ue-animation --model "F:\UE-Assets\nte-useful-assets\HT\Content\Characters\Npc\NPC_018\NPC_018_skin.glb" --animation "F:\UE-Assets\nte-useful-assets\HT\Content\Characters\Npc\NPC_018\animationDH\NPC018_B_cytoL_loop.ueanim" --output "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_cytoL_loop.preview.glb"

dotnet run --project UnrealExporter -- --preview-ue-animation --model "F:\UE-Assets\nte-useful-assets\HT\Content\Characters\Npc\NPC_018\NPC_018_skin.glb" --animation "F:\UE-Assets\nte-useful-assets\HT\Content\Characters\Npc\NPC_018\animationDH\NPC018_B_pxcy_start.ueanim" --output "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_pxcy_start.preview.glb"
```

Blender/F3D 截帧验证：

```powershell
& "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" --background --python "D:\misutime\HumanoidRetargeter\tools\blender_visual_validate.py" -- --summary "F:\UE-Assets\nte-visual-validation\blender\summary.json" --case npc018_loop "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_cytoL_loop.preview.glb" "F:\UE-Assets\nte-visual-validation\blender\npc018_loop" --case npc018_start "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_pxcy_start.preview.glb" "F:\UE-Assets\nte-visual-validation\blender\npc018_start" --case fish "F:\UE-Assets\nte-reusable-library-smoke\_preview\SK_Fish_41_skin__SK_Fish_41_Anim.preview.glb" "F:\UE-Assets\nte-visual-validation\blender\fish" --case boss06_act01 "F:\UE-Assets\nte-character-smoke\_preview\Boss_06_skin__mon_h_act_01.preview.glb" "F:\UE-Assets\nte-visual-validation\blender\boss06_act01"

& "C:\Program Files\F3D\bin\f3d-console.exe" --output "F:\UE-Assets\nte-visual-validation\f3d\npc018_loop\npc018_loop_t0.png" --resolution 960,720 --animation-time 0 --animation-indices 0 "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_cytoL_loop.preview.glb"
& "C:\Program Files\F3D\bin\f3d-console.exe" --output "F:\UE-Assets\nte-visual-validation\f3d\npc018_loop\npc018_loop_t1.png" --resolution 960,720 --animation-time 1 --animation-indices 0 "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_cytoL_loop.preview.glb"
& "C:\Program Files\F3D\bin\f3d-console.exe" --output "F:\UE-Assets\nte-visual-validation\f3d\npc018_start\npc018_start_t0.png" --resolution 960,720 --animation-time 0 --animation-indices 0 "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_pxcy_start.preview.glb"
& "C:\Program Files\F3D\bin\f3d-console.exe" --output "F:\UE-Assets\nte-visual-validation\f3d\npc018_start\npc018_start_t2_5.png" --resolution 960,720 --animation-time 2.5 --animation-indices 0 "F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_pxcy_start.preview.glb"
```

## NTE fish smoke

输出目录：`F:\UE-Assets\nte-reusable-library-smoke`

结果：

- `library_health.json` 状态：`ok`
- 模型：2 个，其中 1 个 SkeletalMesh、1 个 StaticMesh
- 动画：1 个 `.ueanim`，导出成功
- 模型动画关系：1 对，`validationOk=1`
- 动画关系来源：`UniqueSkeleton`
- 动画验证类别：`directTrack`
- track 覆盖率：`1.0`
- hierarchyCompatible：`true`
- 贴图：19 个 PNG，19 个进入 `Textures/_Shared`，硬链接成功，`linkErrors=0`
- glTF 共享贴图 URI 改写：2 条，状态均为 `rewritten`
- 材质缺口：2 个，均为 runtime/special texture，不是普通 PNG 缺失

合成预览结果：

- 输出：`F:\UE-Assets\nte-reusable-library-smoke\_preview\SK_Fish_41_skin__SK_Fish_41_Anim.preview.glb`
- 状态：`ok`
- matchedTracks：20
- writtenChannels：60
- missingBones：0
- heavyTranslationRetarget：false

结论：该样本证明工具可以保持模型和动画独立导出，并通过确定性 Skeleton 关系重新组合为可播放预览。

视觉复验补充：

- Blender 能导入合成 GLB，识别 4 个 mesh object、776 个顶点、1 个 armature、20 根骨骼、1 个材质、1 张贴图和 1 个 action。
- Blender 截帧文件：`F:\UE-Assets\nte-visual-validation\blender\fish\fish_contact_sheet.png`
- 该鱼动画的帧差很小，`hasVisibleFrameChange=false`、`hasGeometryBoundsChange=false`。因此它只作为“导出、关系、重组流程闭环”烟测，不作为动作视觉变化充分的样本。

## NTE NPC_018 humanoid visual sample

既有输出目录：`F:\UE-Assets\nte-useful-assets`

选择原因：

- `model_animations.json` 中 `NPC_018_skin` 的动画关系来源为 `ExplicitComponent`，不是名称相似度猜测。
- 该模型有 156 个可用动画关系，抽样两个 `.ueanim` 做合成和视觉校验。

合成预览结果：

- `NPC018_B_cytoL_loop`
  - 输出：`F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_cytoL_loop.preview.glb`
  - 状态：`ok`
  - matchedTracks：218
  - writtenChannels：654
  - missingBones：0
  - retargetedTranslationTracks：4
  - retargetedRotationTracks：57
- `NPC018_B_pxcy_start`
  - 输出：`F:\UE-Assets\nte-visual-validation\NPC_018_skin__NPC018_B_pxcy_start.preview.glb`
  - 状态：`ok`
  - matchedTracks：218
  - writtenChannels：654
  - missingBones：0
  - retargetedTranslationTracks：3
  - retargetedRotationTracks：49

Blender 视觉验证：

- `npc018_loop`
  - 状态：`ok`
  - meshObjectCount：2
  - vertexCount：23155
  - armatureCount：1
  - boneCount：218
  - materialCount：3
  - imageCount：4
  - actionCount：1
  - frameRange：`0-48`
  - sampledFrames：`0,24,48`
  - hasVisibleFrameChange：`true`
  - hasGeometryBoundsChange：`true`
  - frame 24 vs frame 0：changedPixels=6792，changedRatio=0.009826
  - 截帧：`F:\UE-Assets\nte-visual-validation\blender\npc018_loop\npc018_loop_contact_sheet.png`
- `npc018_start`
  - 状态：`ok`
  - meshObjectCount：3
  - vertexCount：23197
  - armatureCount：1
  - boneCount：218
  - materialCount：3
  - imageCount：4
  - actionCount：1
  - frameRange：`0-140`
  - sampledFrames：`0,70,140`
  - hasVisibleFrameChange：`true`
  - hasGeometryBoundsChange：`true`
  - frame 70 vs frame 0：changedPixels=9844，changedRatio=0.014242
  - frame 140 vs frame 0：changedPixels=9522，changedRatio=0.013776
  - 截帧：`F:\UE-Assets\nte-visual-validation\blender\npc018_start\npc018_start_contact_sheet.png`

F3D 视觉验证：

- `npc018_loop`：time 0 vs time 1，changedPixels=130274，changedRatio=0.047119，meanAbsRgbDelta=2.751
- `npc018_start`：time 0 vs time 2.5，changedPixels=229817，changedRatio=0.083122，meanAbsRgbDelta=5.082
- 截帧：
  - `F:\UE-Assets\nte-visual-validation\f3d\npc018_loop\npc018_loop_t0.png`
  - `F:\UE-Assets\nte-visual-validation\f3d\npc018_loop\npc018_loop_t1.png`
  - `F:\UE-Assets\nte-visual-validation\f3d\npc018_start\npc018_start_t0.png`
  - `F:\UE-Assets\nte-visual-validation\f3d\npc018_start\npc018_start_t2_5.png`

视觉验收结论：失败。

失败原因：

- 虽然 `matchedTracks=218`、`missingBones=0`，并且 Blender/F3D 都能打开预览 GLB，但截图肉眼可见身体扭曲、附件/骨骼拉飞和姿态异常。
- `retargetedRotationTracks` 分别为 57 和 49，不能忽略；这些 warning 使该样本最多只能作为问题诊断样本，不能作为成功样本。
- `hasVisibleFrameChange=true` 只说明动画有变化，不说明动画正确。

当前价值：该样本证明模型、动画、关系索引和预览合成管线能跑通；同时也证明当前人形动画重组仍存在严重姿态/空间/bind pose/retarget 问题，必须继续追根因。

## NTE Boss_06 character sample

既有输出目录：`F:\UE-Assets\nte-character-smoke`

模型验证：

- 模型：`HT/Content/Characters/Monster/Boss_06/Boss_06_skin.glb`
- 状态：`ok`
- meshes：1
- skins：1
- bones：56
- materials：4
- images：3
- bbox：存在且非空
- skeletonHash：`0cc7287adb7cd51cd40a5e2d4103d99c2deee43fb3ffafe21b7b0eef813f0648`

动画关系验证：

- `animation_validation.json` 状态汇总：84 对，`ok=84`，`warning=0`，`error=0`
- directTrack 动画示例：`mon_h_act_01`
- `mon_h_act_01` 验证：
  - modelBoneCount：56
  - animationTrackCount：56
  - matchedTrackBones：56
  - missingTrackBoneCount：0
  - trackCoverage：1.0
  - hierarchyCompatible：true
- Montage/Composite 容器动画：33 个，均保留 segment/section 和子动画引用；缺失子动画数为 0

合成预览结果：

```powershell
dotnet run --project UnrealExporter -- --preview-ue-animation --model "F:\UE-Assets\nte-character-smoke\HT\Content\Characters\Monster\Boss_06\Boss_06_skin.glb" --animation "F:\UE-Assets\nte-character-smoke\HT\Content\Characters\Monster\Boss_06\animation\mon_h_act_01.ueanim" --output "F:\UE-Assets\nte-character-smoke\_preview\Boss_06_skin__mon_h_act_01.preview.glb"
```

- 输出：`F:\UE-Assets\nte-character-smoke\_preview\Boss_06_skin__mon_h_act_01.preview.glb`
- 状态：`warning`
- matchedTracks：56
- writtenChannels：168
- missingBones：0
- retargetedTranslationTracks：32
- retargetedRotationTracks：7

该 warning 来自预览合成阶段的 translation/rotation retarget 修正；它不是骨骼缺失或动画导出失败。当前证据可以证明骨骼、track 绑定和层级关系完整，但复杂角色动画的最终视觉姿态仍建议在 Blender/F3D/引擎内抽样播放确认。

Blender 视觉复验：

- 状态：`ok`
- meshObjectCount：5
- vertexCount：6068
- armatureCount：1
- boneCount：56
- materialCount：4
- imageCount：3
- actionCount：1
- frameRange：`0-256`
- sampledFrames：`0,128,256`
- hasVisibleFrameChange：`true`
- hasGeometryBoundsChange：`true`
- frame 128 vs frame 0：changedRatio=0.012695
- frame 256 vs frame 0：changedRatio=0.005062
- 截帧：`F:\UE-Assets\nte-visual-validation\blender\boss06_act01\boss06_act01_contact_sheet.png`

结论：`Boss_06` 可以作为复杂骨骼角色的补充视觉证据，但由于预览合成存在 retarget warning，当前报告不把它作为最高置信度的人形动作样本。`NPC_018` 也已判定为视觉验收失败；它们共同证明 warning 样本必须进入诊断/人工复核，而不能作为自动验收通过证据。

## 10 组高风险人形动画复评

样本目录：

- 候选清单：`F:\UE-Assets\nte-visual-validation\humanoid10\candidates.json`
- 预览结果：`F:\UE-Assets\nte-visual-validation\humanoid10\preview_run_results.json`
- Blender 汇总：`F:\UE-Assets\nte-visual-validation\humanoid10\blender\summary.json`
- 截图总览：`F:\UE-Assets\nte-visual-validation\humanoid10\blender\humanoid10_atlas.png`
- 行号映射：`F:\UE-Assets\nte-visual-validation\humanoid10\blender\atlas_manifest.json`

10 组样本覆盖多个角色和动作类型：idle、walk/run、attack/combat、jump/air、skill/special。

| 样本 | 动作类型 | 数据层结果 | 视觉验收 |
| --- | --- | --- | --- |
| `player_023_cang_skin__idle` | idle | warning，matchedTracks=222，missing=0，retarget T=3/R=43 | 不通过：存在 retarget warning，只能人工诊断 |
| `player_023_cang_skin__walk_run` | walk/run | warning，matchedTracks=222，missing=0，retarget T=6/R=117 | 失败：中后帧身体折叠，附件/骨骼拉飞 |
| `player_023_cang_skin__attack_combat` | attack/combat | warning，matchedTracks=222，missing=0，retarget T=0/R=35 | 失败：中后帧姿态卷曲异常 |
| `player_023_cang_skin__jump_air` | jump/air | warning，matchedTracks=222，missing=0，retarget T=1/R=47 | 不通过：存在 retarget warning 和可疑姿态，需根因修复 |
| `player_023_cang_skin__skill_special` | skill/special | warning，matchedTracks=222，missing=0，retarget T=9/R=70 | 失败：角色缩团/姿态异常 |
| `player_004_lacrimosa_skin__idle` | idle | warning，matchedTracks=255，missing=0，retarget T=1/R=56 | 不通过：存在 retarget warning，只能人工诊断 |
| `player_004_lacrimosa_skin__walk_run` | walk/run | warning，matchedTracks=255，missing=0，retarget T=3/R=150 | 失败：中后帧明显卷曲，附件/骨骼拉飞 |
| `player_008_Skia_skin__jump_air` | jump/air | warning，matchedTracks=241，missing=0，retarget T=3/R=56 | 失败：中后帧比例和姿态异常 |
| `player_020_haniel_skin_1__attack_combat` | attack/combat | warning，matchedTracks=291，missing=0，retarget T=0/R=58 | 失败：Blender 导入预览 GLB 报 morph target `IndexError`，无法视觉验收 |
| `player_051_female_skin__walk_run` | walk/run | warning，matchedTracks=319，missing=0，retarget T=7/R=94 | 失败：中后帧附件/身体姿态异常 |

复评结论：

- 这 10 组高风险样本不能作为视觉验收成功证据。
- 旧版预览使用第一帧对齐 rest pose 的 rotation 修正，导致大量姿态被过度改写；现已改为直接写入 UEAnim rotation，并把需要自动修补或跳过的 transform 统一标为 warning。
- `Program.SanitizeGlbForPreview` 现在会移除结构不一致或 `extras` 为字符串的 morph target 数据，并移除 weight animation channel；`player_020_haniel_skin_1__attack_combat` 已能在 Blender 5.1 导入。
- 即使修复 rotation 与 morph target 后，这批样本仍有多条 translation retarget warning，且动作语义包含跨 NPC、Across、Skill、DH 等上下文；因此只作为诊断样本，不作为可复用素材库通过样本。

初步根因方向：

- `.ueanim` track 可以直接写为 glTF node TRS，但只有在模型与动画的 Skeleton/rest pose 上下文兼容时才可信。
- 需要自动 translation retarget、跳过静态 transform 或缺少上下文的样本，必须输出 warning 并进入人工复核。
- 仍需继续验证 UE `RetargetBasePose`、translation retarget mode、root motion、additive pose、IK/附件/布料骨骼和跨体型动画复用策略。

## 10 组同 Skeleton 人形动画预览烟测

样本目录：

- 候选扫描：`F:\UE-Assets\nte-visual-validation\humanoid_ok_scan\scan_results.json`
- 精选清单：`F:\UE-Assets\nte-visual-validation\humanoid_ok10_verified_v2\selected_candidates.json`
- 预览 GLB：`F:\UE-Assets\nte-visual-validation\humanoid_ok10_verified_v2\previews`
- Blender 汇总：`F:\UE-Assets\nte-visual-validation\humanoid_ok10_verified_v2\blender\summary.json`
- 截图总览：`F:\UE-Assets\nte-visual-validation\humanoid_ok10_verified_v2\blender\humanoid_ok10_verified_v2_atlas.png`
- 行号映射：`F:\UE-Assets\nte-visual-validation\humanoid_ok10_verified_v2\blender\atlas_manifest.json`

筛选规则：

- 只选 `Characters/Player/<角色>/animation/Movement|Hit|Skill` 下的同角色动画。
- 排除 Montage、SEQ、DH、face、剧情片段、死亡 pose、metadata-only 动画。
- 预览报告必须满足 `missingBones=0`、`retargetedTranslationTracks=0`、`retargetedRotationTracks=0`、`skippedStaticTranslationTracks=0`、`skippedStaticRotationTracks=0`。
- Blender 必须能导入 GLB，识别 1 个 action，并渲染 rest/start/mid/end 帧。

| 样本 | 角色 | 动作类型 | 数据层结果 | Blender 结构 | 当前判定 |
| --- | --- | --- | --- | --- | --- |
| `cang_char_cang_base_turnL_60` | Cang | turn | ok，matched=222，retarget/skip=0 | mesh=2，bones=222，actions=1 | 待近景复查 |
| `cang_char_cang_base_turnR_135` | Cang | turn | ok，matched=222，retarget/skip=0 | mesh=2，bones=222，actions=1 | 待近景复查 |
| `cang_Cang_HitBack` | Cang | hit | ok，matched=222，retarget/skip=0 | mesh=2，bones=222，actions=1 | 待近景复查，mid 帧姿态可疑 |
| `cang_Cang_HitDown` | Cang | hit/down | ok，matched=222，retarget/skip=0 | mesh=2，bones=222，actions=1 | 待近景复查，mid/end 帧姿态可疑 |
| `cang_char_cang_combat_front` | Cang | combat | ok，matched=222，retarget/skip=0 | mesh=2，bones=222，actions=1 | 待近景复查，mid 帧姿态可疑 |
| `haniel_char_haniel_base_stand_pe` | Haniel | stand | ok，matched=291，retarget/skip=0 | mesh=2，bones=291，actions=1 | 待近景复查 |
| `haniel_char_haniel_base_turnR_60` | Haniel | turn | ok，matched=291，retarget/skip=0 | mesh=2，bones=291，actions=1 | 待近景复查 |
| `haniel_char_haniel_combat_front` | Haniel | combat | ok，matched=291，retarget/skip=0 | mesh=2，bones=291，actions=1 | 待近景复查 |
| `haniel_char_haniel_combat_leftback` | Haniel | combat | ok，matched=291，retarget/skip=0 | mesh=2，bones=291，actions=1 | 待近景复查 |
| `haniel_Haniel_HalfHit` | Haniel | hit | ok，matched=291，retarget/skip=0 | mesh=2，bones=291，actions=1 | 待近景复查 |

结论：在“同角色/同 Skeleton、无自动 retarget 或 transform skip”的确定性范围内，当前工具可以把独立模型和独立 `.ueanim` 批量合成为可打开、有动画变化的 GLB preview。该批次暂时只能作为批量预览管线闭环证据，不能单独作为动画正确或素材库验收通过证据。下一步必须补充近景截图、最大变化帧、bbox/顶点离群/骨骼长度/scale 检查，以及逐样本 `pass/suspicious/fail` 人工判定。

## 2026-06-20 Quaternion handedness 修复复验

修复内容：

- `CUE4Parse/CUE4Parse-Conversion/Meshes/glTF/Gltf.cs`：骨骼 glTF 导出 `SwapYZ(FQuat)` 从 `(x,z,y,w)` 改为 `(x,z,y,-w)`。
- `UnrealExporter/UEAnimationPreviewBuilder.cs`：`.ueanim` 合成 preview 的 rotation channel 使用同一换基。
- `D:\misutime\HumanoidRetargeter\tools\blender_visual_validate.py`：近景截图使用 trimmed bbox 取景，避免少量头发/衣摆离群点把人物缩成远景；结构检查仍保留完整 bbox 和顶点离群统计。
- `D:\misutime\HumanoidRetargeter\tools\blender_pose_diagnose.py`：新增顶点位移、材质、顶点组、骨骼位移诊断，用于定位异常是否来自主骨架、附件/头发/衣摆或 skin 权重。

复验输出：

- 修复后小范围导出：`F:\UE-Assets\nte-rootcause-cang-yzfix`
- 10 个 preview：`F:\UE-Assets\nte-rootcause-cang-yzfix\_preview10`
- Blender 近景截帧与结构报告：`F:\UE-Assets\nte-rootcause-cang-yzfix\blender_preview10_robust`
- 近景总览图：`F:\UE-Assets\nte-rootcause-cang-yzfix\blender_preview10_robust\preview10_robust_atlas.png`

11 个样本数据层全部满足：`status=ok`、`missingBones=0`、`retargetedTranslationTracks=0`、`skippedStaticTranslationTracks=0`、`skippedStaticRotationTracks=0`。Blender 均能导入，识别 1 个 action，并渲染 rest/start/峰值帧。

| 样本 | 角色 | 近景人工判定 | 结构结果 |
| --- | --- | --- | --- |
| `cang_turnL_60` | Cang | pass | 无结构 warning，骨长/scale 正常 |
| `cang_turnR_135` | Cang | pass | 无结构 warning，骨长/scale 正常 |
| `cang_hitback` | Cang | pass | 无结构 warning，受击弯腰姿态合理 |
| `cang_hitdown` | Cang | suspicious | 主骨架姿态合理，但头发/衣摆长条几何产生 vertex outlier warning |
| `cang_combat_front` | Cang | pass | 无结构 warning，姿态与 hitback 类似但可读 |
| `haniel_stand_pe` | Haniel | pass | 无结构 warning，站立/微动正常 |
| `haniel_turnL_60` | Haniel | pass | 无结构 warning，转身姿态正常 |
| `haniel_turnR_60` | Haniel | pass | 无结构 warning，转身姿态正常 |
| `haniel_combat_front` | Haniel | pass | 无结构 warning，受击/防御姿态可读 |
| `haniel_combat_leftback` | Haniel | pass | 无结构 warning，侧后动作可读 |
| `haniel_halfhit` | Haniel | pass | 无结构 warning，半身受击动作可读 |

复验结论：Quaternion handedness 修复后，同 Skeleton、无 retarget/skip 的 Cang/Haniel 人形样本已达到基础可复用素材库烟测标准：模型与动画可保持独立，通过确定性 Skeleton 关系重新组合，preview 可打开且视觉可读。当前复验为 10 pass + 1 suspicious；`Cang_HitDown` 的 secondary-motion outlier 需要在报告中保留为可疑项，不能作为最高置信样本；但它不再表现为主骨架或全身 skin 错绑。

## 结论

当前工具已从“小范围批量预览管线闭环”推进到“同 Skeleton 人形基础复用烟测通过”：可以导出独立模型、共享贴图、材质/贴图绑定、骨骼、独立 `.ueanim`，并在确定性同 Skeleton 关系下用命令行合成为可打开、视觉可读的 GLB preview。已定位并修复导致人形动画大面积异常的 Quaternion handedness 根因。跨体型、跨 Skeleton、需要 retarget、需要 ControlRig/AnimBP/物理/布料上下文的动画仍必须标记为不确定或待人工检查，不能自动宣称成功。

已经成立的证据：

- 模型、贴图、材质、骨骼和动画可以独立导出。
- 贴图能集中进入 `Textures/_Shared` 并通过硬链接/相对 URI 复用。
- 材质到贴图槽、模型到 Skeleton、动画到 Skeleton、Montage/Composite 到子动画的关系来自 UE 源索引和显式结构，不靠名称硬猜。
- `model_animations.json`、`animation_validation.json`、`library_health.json`、`library_acceptance.json`、`library_index.db` 能标记可用、缺失、特殊贴图、容器动画和需要人工检查的情况。
- 已验证可以把独立模型和独立 `.ueanim` 重新合成为预览 GLB。
- 已完成 11 个同 Skeleton 人形模型动画组合的 Blender 近景 rest/start/峰值帧截帧，覆盖 Cang 和 Haniel 两个人形角色，以及 turn、stand、hit、combat 等动作类型。
- 已逐样本给出 `pass/suspicious/fail` 人工判定：10 个 pass，1 个 suspicious；suspicious 样本为 `Cang_HitDown`，原因是 secondary-motion 顶点离群，而非主骨架错绑。
- 已完成 bbox、顶点离群、骨骼长度和异常 scale 检查；11 个样本均无骨骼长度爆炸或异常 scale。
- 已证明本轮大面积人形姿态异常的主要根因来自 UE -> glTF `SwapYZ` 反射换基下四元数 `W` 未翻转。

限制和未成立的证据：

- `Cang_HitDown` 仍有头发/衣摆/附件类 secondary-motion 离群，必须保留为 suspicious，不能作为最高置信 pass。
- `NPC_018` 人形样本必须标记为视觉验收失败。
- `Boss_06`、鱼和 warning 样本只能证明管线闭环或复杂骨骼补充诊断，不能替代高置信人形验收。
- 需要自动 translation retarget、transform skip、跨 NPC/跨体型/剧情上下文的动画，尚不能自动标记为成功。
- 尚未把 Unreal 的 `RetargetBasePose`、translation retarget mode、root motion、additive pose 和 IK/附件/布料骨骼语义完整还原为通用 glTF 复用策略。

当前尚未把全量 `HT/Content` 放开导出；推荐命令 `just nte-library` 是面向高价值候选目录的素材库导出入口，避免一开始被大量零碎 mesh、分片模型和中间资源污染。若后续要扩展范围，应继续坚持“确定性关系 + 验证报告 + 视觉抽样”的门槛，而不是靠名称或目录相似度自动宣称匹配成功。
