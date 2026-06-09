# UE 素材库导出优化目标

本文档记录 UnrealExporter 向“可用 3D 游戏素材库”升级的实现目标。对标 AnimeStudio 的核心原则：导出链路必须尽量保留引擎原始关系，默认主格式以 glTF/GLB 服务预览和复用，模型、贴图、骨骼、动画要能被索引、验证和重新组合。

## 当前结论

UnrealExporter 已经能导出大量 UE 模型、贴图和材质 sidecar，也能让 GLB 保留 skin/joint。对 Batman 与 NTE 的真实输出检查显示，模型数量和静态结构基础可用，但原导出缺少素材库级索引、模型/动画关系、动画导出、共享贴图库和验证报告，因此还不能等价于 AnimeStudio 的“模型 + 贴图 + 骨骼 + 动画”完整素材库。

本轮已把一部分能力接入导出主链路：导出时写 `export_manifest.jsonl`、`asset_catalog.jsonl`、`animation_bindings.jsonl`，支持 `glb/gltf` 模型输出和 `ueanim/psa` 动画导出入口，模型 catalog 记录 UE Skeleton 原始引用和骨骼名，索引重建会合并而不是覆盖源关系，并生成 `library_index.db`、`ue_source_index.db`、`model_animations.json`、`animation_validation.json`、`model_validation.json`、`skeletons.json`、`texture_links.jsonl`、`material_texture_slots.jsonl`、`shared_texture_gltf_links.jsonl`、`component_asset_relations.jsonl`、`component_groups.json`、`auto_referenced_exports.jsonl`、`LIBRARY_README.md`。贴图可统一复制到 `Textures/_Shared` 并用硬链接复用，catalog 和 SQLite 中也会保留每张贴图的 sha256、共享路径和硬链接状态；材质 slot 会关联到 UE 贴图对象、已导出贴图和共享贴图，未导出的贴图按 `missingExportedTexture`、`unresolvedTexturePackage`、`nonExportableTexture` 细分，不能把运行时渲染目标、曲线/数据贴图误判成普通贴图缺失；文本 glTF 会在关系明确时把 image URI 改写到共享贴图。源索引已记录材质到贴图槽的真实 UE 关系，包含直接参数、解析参数和原始引用三类来源；也记录 SkeletalMesh/USkeleton 的骨骼层级、Mesh/Skeleton socket、AnimSequence track 到骨骼的映射、AnimNotify 事件、FloatCurve 曲线，以及 Montage/Composite segment、slot、section 与子动画引用。`skeletons.json` 已把 glTF skin 预览骨架和 UE Skeleton 原始路径、源索引骨架对象、同 Skeleton 动画列表合并输出。蓝图、组件、Level/Actor 实例层面开始记录 ComponentTemplate、SCS、继承组件覆盖、导出组件、关卡 Actor、Actor 组件属性和 cooked 蓝图/CDO 属性里的显式资源 PPtr；素材库侧会把这些关系提升为可查询的组件关系和组合 group，并记录模型、材质、动画等引用的已导出数量和缺口明细，用于后续组合模型、挂点、任务道具和动画蓝图关系重建。导出主链路新增 `autoExportReferencedAssets` 开关，开启后会把源索引中的显式组件/蓝图引用转成额外导出候选，优先补齐任务道具和组合模型引用的模型、材质、贴图与动画；`auto_referenced_exports.jsonl` 会记录自动补导计划、源包匹配、显式配置覆盖和导出结果，便于追踪缺口根因。

## 完整实现目标

1. 模型导出
   - 默认导出 StaticMesh 与 SkeletalMesh 为 GLB/glTF。
   - SkeletalMesh 必须保留 skin、joint、骨骼名、材质槽和 UE Skeleton 引用。
   - StaticMesh 也要进入素材库，不因“没有骨骼”被排除；建筑、环境、道具、车辆都属于有效 3D 游戏素材。
   - 模型 catalog 必须记录 UE 源包路径、对象路径、输出路径、资源分类、材质数量、UE 原始材质槽、骨骼数量、bbox 和验证状态。

2. 贴图与材质
   - PNG/HDR sidecar 继续导出，但要统一进入 `Textures/_Shared`，原目录使用硬链接减少重复。
   - `texture_links.jsonl` 必须记录原贴图、共享贴图、sha256、大小和硬链接状态；`asset_catalog.jsonl` 必须包含 Texture 行。
   - `material_texture_slots.jsonl` 和 `library_index.db.material_texture_slots` 必须记录材质 slot、UE 贴图对象、导出贴图、共享贴图、sha256 和匹配状态。
   - 材质 JSON 必须保留 UE 材质参数、贴图槽、颜色、scalar、switch、blend mode 和 shading model。
   - 已支持 `:gltf` 输出文本 glTF + `.bin`，并在材质槽和共享贴图明确匹配时改写 glTF image URI；GLB 继续作为独立预览格式。
   - 不能按游戏私有命名硬猜贴图用途；只能从 UE 材质槽、材质参数和通用纹理语义推断。

3. 骨骼
   - SkeletalMesh catalog 记录 `skeletonPath`、`skeletonName`、`boneNames`、boneCount。
   - GLB 验证阶段计算 skeletonHash，用于发现相同骨架或近似骨架。
   - `skeletons.json` 聚合 GLB skin 骨架组，并合并 UE Skeleton 原始引用、源索引骨架对象和同 Skeleton 动画列表；模型与动画的默认绑定优先使用 UE Skeleton 原始引用。

4. 动画
   - 支持 `ueanim` 与 `psa` 输出类型，目标对象包括 `UAnimSequence`、`UAnimMontage`、`UAnimComposite`。
   - 导出时必须写 `animation_bindings.jsonl`，记录动画源路径、对象路径、Skeleton、SkeletonGuid、时长、帧数、track 数、track 对应骨骼索引、压缩类型和导出状态。
   - ACL 压缩动画需要 native ACL 支持；如果 DLL 缺少 `nAllocate/nReadACLData`，必须明确标记 blocked，不能吞异常或伪装成功。
   - 动画是否推荐给模型，只能基于 UE Skeleton 引用、SkeletonGuid、兼容骨架和验证结果，不能按文件名前缀强绑。

5. 模型动画关系
   - `model_animations.json` 只按 UE Skeleton 原始引用建立保守匹配，并回填 `animation_validation.json` 的覆盖率和层级验证结果；Montage/Composite 这类容器动画必须保留 segment、section、子动画引用和子动画导出完整度，不能只报一个无 track 的 warning。
   - 未匹配时保留 `NoMatchingAnimationExported`，不硬猜。
   - 已增加骨架兼容验证：bone 覆盖率、父子关系、track bone index 覆盖；后续继续补 bbox/姿态采样验证。

6. 索引与报告
   - `asset_catalog.jsonl` 是素材库 JSONL 总入口，必须合并导出主链路数据和验证数据。
   - `library_index.db` 是已导出素材库的 SQLite 查询入口，必须包含 assets、texture_links、material_texture_slots、shared_gltf_texture_links、component_asset_relations、component_groups、skeleton_groups、model_validation、model_animation_relations、relation_animations 和 animation_validation。
   - `export_manifest.jsonl` 记录每个实际导出文件来自哪个 UE 包和对象。
   - `auto_referenced_exports.jsonl` 记录自动补导的计划和执行结果，包括关系来源、目标对象、源包、输出类型和失败原因。
   - `model_validation.json` 验证 GLB/glTF mesh、material、image、skin、bbox。
   - `ue_source_index.db` 面向完整 UE 源目录，记录 source_files、source_objects、source_relations、material_texture_slots、skeleton_bones、mesh_sockets、component_asset_relations、animation_tracks、animation_notifies、animation_curves、animation_segments、animation_sections 和 source_index_errors。
   - `library_index.db` 面向已导出素材库，记录 assets、texture_links、material_texture_slots、shared_gltf_texture_links、component_asset_relations、component_groups、skeleton_groups、model_validation、model_animation_relations、relation_animations 和 animation_validation。

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
- 已支持 `mesh_sockets`，记录 StaticMesh、SkeletalMesh、USkeleton 的 socket 名、绑定骨骼和相对 TRS，服务挂件、武器、特效和任务道具组合关系。
- 已支持 `component_asset_relations`，从 BlueprintGeneratedClass 的 ComponentTemplates、SimpleConstructionScript、InheritableComponentHandler、World/Level/LevelStreaming/WorldPartition/RuntimeCell、Level Actors、Actor 组件属性、导出组件，以及 cooked 蓝图/CDO 属性里的显式 PPtr 记录 StaticMesh、SkeletalMesh、Material、Texture、Animation、Skeleton、AnimClass、BlueprintClass、Actor、关卡和分区关系。
- 已支持素材库侧 `component_asset_relations.jsonl`、`component_groups.json` 和 `library_index.db` 同名表，把源索引中的蓝图/组件关系匹配到已导出资产；组件实例/模板节点标记为 `componentOnly`，不再当作缺失素材；组件组会写出组件节点、父子关系、socket、transform、模型/材质/动画缺口计数和 `missingReferences` 明细，用于判断任务模型还缺哪些部件。
- 已支持 `autoExportReferencedAssets`，在源索引完成后按显式组件/蓝图 PPtr 自动补导被引用的 StaticMesh、SkeletalMesh、Material、Texture、Animation 和 AnimBlueprint JSON；遇到 Skeleton 引用时，会从源索引骨骼表反查同 Skeleton 的 SkeletalMesh 补导，避免任务道具只导出主模型而漏掉组合部件。
- 已支持 `package_object_maps`，在源索引阶段记录 UE 包 ImportMap/ExportMap，并同步生成素材库侧 `package_object_maps.jsonl` 和 `library_index.db.package_object_maps`，用于分析包级依赖、导出对象、class/outer/super/template 关系。
- 已支持 `animation_notifies` 和 `animation_curves`，记录通知事件、曲线名、曲线 key 数和值域，便于区分事件/表情/材质驱动类动画。
- 已支持 `animation_segments` 和 `animation_sections`，记录 Montage/Composite 的子动画引用、slot、section、时间范围、播放速度和循环次数。
- `model_animations.json` 和 `library_index.db.relation_animations` 已保留动画 segment/section 摘要和完整 raw_json，用于区分直接可采样 AnimSequence 与 Montage/Composite 容器动画。
- 下一步继续扩展 ExternalActor 描述数据和更完整依赖图，减少每次靠目录扫描和临时加载。

### P1：共享贴图主链路

- 当前硬链接已经记录纹理哈希和共享路径；源索引已经补充材质到具体贴图槽的依赖关系。
- 已生成 `material_texture_slots.jsonl` 和 SQLite `material_texture_slots`，把材质 slot 连接到导出贴图与 `Textures/_Shared` 共享贴图；未导出的贴图保留 `missingExportedTexture`，不伪造关系。
- 文本 glTF 已能按 UE 材质槽引用共享贴图，并通过 `shared_texture_gltf_links.jsonl` 记录改写来源；后续继续扩展更多材质语义和冲突报告。
- 材质 JSON 增加 shared texture path 字段，便于外部工具重建材质。

### P1：模型与动画兼容验证

- 在 UE Skeleton 引用匹配后，增加骨骼名覆盖率、track bone index 覆盖率和骨架层级检查。
- 当前源索引已具备骨骼层级和 track 映射数据，并已接入 `model_animations.json` 的兼容验证和 `animation_validation.json` 报告。
- 已对 Montage/Composite 展开 segment，保留 slot、section、segment 时间范围；`autoExportReferencedAssets` 已把组合动画自身和子动画纳入导出候选。
- 已输出 `animation_validation.json`，区分 ok、warning、error，并记录缺源索引、缺模型骨骼、缺动画 track、骨骼缺失和层级不一致等原因。

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
- SQLite 重建前应清理旧 `.db-wal/.db-shm`，输出完成后应截断 WAL，避免 `ue_source_index.db-wal` / `library_index.db-wal` 长期重复占用磁盘空间。
- 任何不能导出的动画或模型都有明确 blocked/error 原因，而不是静默缺失。
