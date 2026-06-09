# UE 素材库导出优化目标

本文档记录 UnrealExporter 向“可用 3D 游戏素材库”升级的实现目标。对标 AnimeStudio 的核心原则：导出链路必须尽量保留引擎原始关系，默认主格式以 glTF/GLB 服务预览和复用，模型、贴图、骨骼、动画要能被索引、验证和重新组合。

## 当前结论

UnrealExporter 已经能导出大量 UE 模型、贴图和材质 sidecar，也能让 GLB 保留 skin/joint。对 Batman 与 NTE 的真实输出检查显示，模型数量和静态结构基础可用，但原导出缺少素材库级索引、模型/动画关系、动画导出、共享贴图库和验证报告，因此还不能等价于 AnimeStudio 的“模型 + 贴图 + 骨骼 + 动画”完整素材库。

本轮已把一部分能力接入导出主链路：导出时写 `export_manifest.jsonl`、`asset_catalog.jsonl`、`animation_bindings.jsonl`，支持 `ueanim/psa` 动画导出入口，模型 catalog 记录 UE Skeleton 原始引用和骨骼名，索引重建会合并而不是覆盖源关系，并生成 `library_index.db`、`ue_source_index.db`、`model_animations.json`、`model_validation.json`、`skeletons.json`、`texture_links.jsonl`、`LIBRARY_README.md`。贴图可统一复制到 `Textures/_Shared` 并用硬链接复用，catalog 和 SQLite 中也会保留每张贴图的 sha256、共享路径和硬链接状态。源索引已记录材质到贴图槽的真实 UE 关系，包含直接参数、解析参数和原始引用三类来源；也记录 SkeletalMesh/USkeleton 的骨骼层级和 AnimSequence track 到骨骼的映射。

## 完整实现目标

1. 模型导出
   - 默认导出 StaticMesh 与 SkeletalMesh 为 GLB/glTF。
   - SkeletalMesh 必须保留 skin、joint、骨骼名、材质槽和 UE Skeleton 引用。
   - StaticMesh 也要进入素材库，不因“没有骨骼”被排除；建筑、环境、道具、车辆都属于有效 3D 游戏素材。
   - 模型 catalog 必须记录 UE 源包路径、对象路径、输出路径、资源分类、材质数量、骨骼数量、bbox 和验证状态。

2. 贴图与材质
   - PNG/HDR sidecar 继续导出，但要统一进入 `Textures/_Shared`，原目录使用硬链接减少重复。
   - `texture_links.jsonl` 必须记录原贴图、共享贴图、sha256、大小和硬链接状态；`asset_catalog.jsonl` 必须包含 Texture 行。
   - 材质 JSON 必须保留 UE 材质参数、贴图槽、颜色、scalar、switch、blend mode 和 shading model。
   - 后续要支持外部贴图 glTF 输出模式，让模型引用共享贴图；GLB 可继续作为独立预览格式。
   - 不能按游戏私有命名硬猜贴图用途；只能从 UE 材质槽、材质参数和通用纹理语义推断。

3. 骨骼
   - SkeletalMesh catalog 记录 `skeletonPath`、`skeletonName`、`boneNames`、boneCount。
   - GLB 验证阶段计算 skeletonHash，用于发现相同骨架或近似骨架。
   - `skeletons.json` 聚合 GLB skin 骨架组，但模型与动画的默认绑定优先使用 UE Skeleton 原始引用。

4. 动画
   - 支持 `ueanim` 与 `psa` 输出类型，目标对象包括 `UAnimSequence`、`UAnimMontage`、`UAnimComposite`。
   - 导出时必须写 `animation_bindings.jsonl`，记录动画源路径、对象路径、Skeleton、SkeletonGuid、时长、帧数、track 数、track 对应骨骼索引、压缩类型和导出状态。
   - ACL 压缩动画需要 native ACL 支持；如果 DLL 缺少 `nAllocate/nReadACLData`，必须明确标记 blocked，不能吞异常或伪装成功。
   - 动画是否推荐给模型，只能基于 UE Skeleton 引用、SkeletonGuid、兼容骨架和验证结果，不能按文件名前缀强绑。

5. 模型动画关系
   - `model_animations.json` 只按 UE Skeleton 原始引用建立保守匹配。
   - 未匹配时保留 `NoMatchingAnimationExported`，不硬猜。
   - 后续可增加骨架兼容验证：bone 覆盖率、父子关系、track bone index 覆盖、bbox/姿态采样验证。

6. 索引与报告
   - `asset_catalog.jsonl` 是素材库 JSONL 总入口，必须合并导出主链路数据和验证数据。
   - `library_index.db` 是已导出素材库的 SQLite 查询入口，必须包含 assets、texture_links、model_validation、model_animation_relations 和 relation_animations。
   - `export_manifest.jsonl` 记录每个实际导出文件来自哪个 UE 包和对象。
   - `model_validation.json` 验证 GLB mesh、material、image、skin、bbox。
   - `ue_source_index.db` 面向完整 UE 源目录，记录 source_files、source_objects、source_relations、material_texture_slots、skeleton_bones、animation_tracks 和 source_index_errors。
   - `library_index.db` 面向已导出素材库，记录 assets、texture_links、model_validation、model_animation_relations 和 relation_animations。

## 优化列表

### P0：让动画真正可导出

- 补齐 CUE4Parse-Natives 的 ACL 依赖并重建，确保 DLL 导出 `nAllocate`、`nDeallocate`、`nReadACLData`、`nReadCurveACLData`。
- 增加 native feature 自检命令，启动时报告 ACL/Oodle 是否可用。
- 用 Batman/NTE 各选 1 个明确匹配同 Skeleton 的模型和动画做 smoke：期望输出 GLB + UEAnim/PSA + model_animations 匹配。

### P1：源关系索引

- 已支持扫描完整 pak/io store 文件表，并按配置路径检查 UE 包对象，记录材质、SkeletalMesh Skeleton、Animation Skeleton 的源索引。
- 支持从源索引反查“某个模型/动画引用哪个 Skeleton、模型引用哪些 Material”。
- 已支持 `material_texture_slots`，记录材质名、slot 名、贴图路径、贴图对象路径和关系来源：`DirectParameter`、`ResolvedParams`、`ReferencedTexture`。
- 已支持 `skeleton_bones` 和 `animation_tracks`，记录模型/骨架的 boneName、parentIndex，以及动画 track 到 skeleton bone index/boneName 的映射。
- 下一步继续扩展 Import/Export、蓝图/组件引用和更完整依赖图，减少每次靠目录扫描和临时加载。

### P1：共享贴图主链路

- 当前硬链接已经记录纹理哈希和共享路径；源索引已经补充材质到具体贴图槽的依赖关系。
- 增加 `gltf_external_textures` 模式，让 glTF 引用共享贴图；GLB 保持可选内嵌预览。
- 材质 JSON 增加 shared texture path 字段，便于外部工具重建材质。

### P1：模型与动画兼容验证

- 在 UE Skeleton 引用匹配后，增加骨骼名覆盖率、track bone index 覆盖率和骨架层级检查。
- 当前源索引已具备骨骼层级和 track 映射数据，下一步把这些数据接入 `model_animations.json` 的兼容验证和 `animation_validation.json` 报告。
- 对 Montage/Composite 展开 segment，保留 slot、section、segment 时间范围。
- 输出 `animation_validation.json`，区分可导出、缺 native、骨架不匹配、缺依赖。

### P2：素材库浏览质量

- 给模型、材质、动画生成更稳定的分类和标签：Character、Vehicle、Weapon、Environment、Prop、VFX 等。
- 分类只使用跨 UE 项目通用路径和对象类型；信号不足保留 Unknown。
- 生成每个素材目录的 `ASSET_README.md`，方便人工快速判断可用性。

### P2：性能与稳定性

- 大批量导出时记录候选数、跳过数、耗时、失败原因和 native 能力。
- 并发写 manifest/catalog 已加锁，后续应把大规模 catalog 写入改为批量队列，减少频繁 IO。
- 对异常保持根因可见：缺 mapping、缺 AES、缺 ACL、材质缺贴图、GLB 验证失败都要分开统计。

## 验收标准

- 对 Batman/NTE 的真实目录，模型 GLB 能被解析，skin/bone/material/bbox 验证可通过。
- 对至少一个 SkeletalMesh，能导出同 Skeleton 的动画，并在 `model_animations.json` 中建立 ExplicitSkeleton 匹配。
- `asset_catalog.jsonl` 同时包含模型、贴图、材质、动画，并保留 UE 源路径和对象路径。
- 共享贴图库能减少重复 PNG/HDR 文件，原路径可继续被旧流程访问。
- 任何不能导出的动画或模型都有明确 blocked/error 原因，而不是静默缺失。
