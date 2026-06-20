# UE 模型与动画确定性关系调研

日期：2026-06-20

本文记录 UnrealExporter 当前如何建立模型与动画关系，以及结合 Unreal 官方资料、社区经验、GitHub 项目和本地 CUE4Parse 能力后，后续提升关系置信度的实现方向。

## 当前实现结论

当前工具没有按文件名、角色名或目录前缀硬猜模型与动画关系。关系主要来自 UE 原始数据和结构验证：

1. `componentOwner`
   - 同一个 UE owner/component 同时显式引用 `SkeletalMesh` 和 `Animation`。
   - 来源：`UESourceIndexBuilder.BuildComponentAssetTargets` 从 `USkeletalMeshComponent` 读取 `SkeletalMesh`、`AnimationData.AnimToPlay`。
   - 可信度：高。可视为确定性使用关系。

2. `componentOwnerBlendSpaceSample`
   - 组件引用 BlendSpace、Montage、Composite 等动画容器，工具再从 `animation_segments` 展开其中显式引用的 `AnimSequence`。
   - 可信度：高。容器本身和子动画关系来自 UE 结构。

3. `componentAnimClass`
   - 组件引用 `AnimClass` 或 `AnimBlueprintGeneratedClass`，再尝试从该 AnimClass owner 下找到动画引用。
   - 可信度：中到高，但当前还不完整。原因是目前还没有完整解析 AnimBlueprint 图、状态机、SequencePlayer、BlendSpacePlayer、Linked Anim Layer、运行时变量驱动等引用。

4. `uniqueSkeleton`
   - 某个 `USkeleton` 在当前素材库中只对应一个模型时，将同 Skeleton 动画挂到这个模型。
   - 可信度：中。可以作为可复用候选，但严格说它证明的是结构兼容，不是“这个模型一定使用这些动画”。

5. `sharedSkeleton`
   - 多个模型共享同一个 `USkeleton` 时，所有同 Skeleton 且验证通过的动画作为可复用候选。
   - 可信度：低到中。它是素材库可复用性的基础，但不是确定性绑定。
   - 当前实现会用路径前缀相似度排序展示，但这只是排序信号，不是关系证据。

`AddModelAnimationCandidate` 会要求模型和动画的 `skeletonPath` 完全一致；`ValidateAnimationPair` 会检查模型骨骼、动画 track、缺失骨骼、track 覆盖率、层级兼容性和容器子动画完整度。

## 当前样本数据

对 `F:\UE-Assets\nte-useful-assets\library_index.db` 的当前关系表统计如下：

```text
relation_source:
  sharedSkeleton       193656
  uniqueSkeleton        12445
  componentAnimClass      260
  componentOwner          240

usage_evidence:
  skeletonCompatibility 206101
  explicitUsage            500

validation:
  ok/directTrack        204166
  warning/partialTrackCoverage 2430
  error/missingTrackBones       5
```

这说明当前导出的模型-动画关系里，大多数是 `skeletonCompatibility`，即“同 Skeleton 可复用候选”。真正的显式使用关系目前只有约 500 条。报告、UI 和后续筛选必须把这两类分开，不能把 `sharedSkeleton` 结果说成确定性匹配。

## 官方与社区结论

Unreal 官方文档支持“同 Skeleton 可共享动画”，但也说明这不是视觉正确性的充分条件：

- Animation Sequence 绑定到 Skeleton，并可在使用同 Skeleton 的 Skeletal Mesh 之间共享。
  来源：https://dev.epicgames.com/documentation/unreal-engine/animation-sequences-in-unreal-engine
- 导入动画时，UE 会根据骨骼名和层级匹配 Skeleton。
  来源：https://dev.epicgames.com/documentation/unreal-engine/animation-sequences-in-unreal-engine
- Retargeting 文档说明，即使同 Skeleton，不同比例模型也可能出现拉伸或压缩；UE 的 retargeting 主要处理骨骼 translation，rotation 仍来自动画数据。
  来源：https://dev.epicgames.com/documentation/unreal-engine/animation-retargeting-in-unreal-engine
- Animation Blueprint 是控制 Skeletal Mesh 动画行为的核心资产，AnimClass 需要被赋给 SkeletalMeshComponent 才会驱动角色。
  来源：https://dev.epicgames.com/documentation/unreal-engine/animation-blueprints-in-unreal-engine
- BlendSpace Player 是 AnimGraph 里的节点，引用 BlendSpace asset；BlendSpace Graph 也可能嵌在 AnimBlueprint 内部。
  来源：https://dev.epicgames.com/documentation/unreal-engine/blend-spaces-in-animation-blueprints-in-unreal-engine

因此，合理分层应该是：

- 同 Skeleton：结构兼容证据。
- 组件直接引用动画：确定性使用证据。
- 组件引用 AnimBlueprint，AnimBlueprint 直接引用 AnimSequence/BlendSpace/Montage：强确定性使用证据。
- DataAsset、PrimaryAsset、AssetManager、DataTable 同时管理模型和动画集：强上下文证据，但需要保留证据链。

## GitHub 与工具链启发

### AssetRegistry 依赖图

Unreal Asset Registry 提供 hard package references、soft package references、searchable names、hard/soft management references 等依赖类别。

来源：

- https://dev.epicgames.com/documentation/unreal-engine/BlueprintAPI/Utilities/Struct/MakeAssetRegistryDependencyOptio-
- https://dev.epicgames.com/documentation/en-us/unreal-engine/python-api/class/AssetRegistryDependencyOptions

GitHub 上的 `DependencyAnalyser` UE5 插件也把 asset dependency chain 作为正式分析对象，说明在 UE 项目里依赖图是成熟且常见的资产关系分析方式。

来源：https://github.com/alessianigretti/DependencyAnalyser

这对本工具的启发是：应该把 cooked `AssetRegistry.bin` 或包内 AssetRegistry 数据导入 `ue_source_index.db`，作为另一条确定性证据链。

建议新增表：

```sql
CREATE TABLE asset_registry_dependencies (
    source_package TEXT NOT NULL,
    source_asset_name TEXT,
    source_asset_class TEXT,
    dependency_package TEXT NOT NULL,
    dependency_asset_name TEXT,
    dependency_asset_class TEXT,
    dependency_category TEXT NOT NULL,
    dependency_flags TEXT,
    is_hard_package INTEGER NOT NULL DEFAULT 0,
    is_soft_package INTEGER NOT NULL DEFAULT 0,
    is_searchable_name INTEGER NOT NULL DEFAULT 0,
    is_hard_manage INTEGER NOT NULL DEFAULT 0,
    is_soft_manage INTEGER NOT NULL DEFAULT 0,
    relation_source TEXT NOT NULL DEFAULT 'AssetRegistry'
);
```

可产生的新关系来源：

- `assetRegistryHardDependency`
- `assetRegistrySoftDependency`
- `assetRegistrySearchableName`
- `assetRegistryHardManage`
- `assetRegistrySoftManage`

其中 hard package reference 和 hard management reference 的置信度最高；soft reference 需要结合具体 class 和 owner 上下文判断。

### 本地 CUE4Parse 能力

当前仓库内 CUE4Parse 已经具备基础结构：

- `CUE4Parse/CUE4Parse/UE4/AssetRegistry/FAssetRegistryState.cs`
  - 包含 `PreallocatedAssetDataBuffers`
  - 包含 `PreallocatedDependsNodeDataBuffers`
  - 包含 `PreallocatedPackageDataBuffers`
- `CUE4Parse/CUE4Parse/UE4/AssetRegistry/Objects/FDependsNode.cs`
  - 包含 `Identifier`
  - 包含 `PackageDependencies`
  - 包含 `NameDependencies`
  - 包含 `ManageDependencies`
  - 包含 `Referencers`
  - 包含 `PackageFlags`
  - 包含 `ManageFlags`
- `CUE4Parse/CUE4Parse/UE4/AssetRegistry/Objects/FAssetData.cs`
  - 包含 `PackageName`
  - 包含 `PackagePath`
  - 包含 `AssetName`
  - 包含 `AssetClass`
  - 包含 `TagsAndValues`
  - 包含 `TaggedAssetBundles`

所以实现 AssetRegistry 依赖图不需要换技术栈。主要工作是把这些结构转换成稳定 SQLite 表，并把 package-level 依赖与当前 object-level `source_objects`、`source_relations`、`component_asset_relations` 做 join。

## 推荐的置信度模型

建议不要继续用单一 `explicitUsage` / `skeletonCompatibility` 表达全部关系，而是增加 `confidenceTier` 和 `evidenceChain`。

### 字段语义

`model_animations.json.relations[].animations[]` 和 `library_index.db.relation_animations` 会保留一组兼容旧报告、同时支持新置信度分层的字段。团队后续写 UI、过滤器、验收脚本时应优先使用新字段，旧字段只用于兼容和粗略统计。

#### 推荐优先使用字段

`confidenceTier`

- JSON 字段名：`confidenceTier`
- SQLite 字段名：`confidence_tier`
- 作用：表达模型与动画关系的最高语义等级。
- 当前取值：
  - `ExplicitComponent`：组件直接引用模型和动画，或组件引用动画容器并能展开子动画。
  - `AnimBlueprintDirect`：AnimBP/GeneratedClass/CDO 静态引用动画，并且该 AnimBP 通过 `TargetSkeleton` 或组件 AnimClass 关联到模型 Skeleton。
  - `CharacterDataSet`：同一个 DataAsset/DataTable/PrimaryAsset 类上下文同时引用模型和动画集。
  - `AnimClassContext`：模型组件引用 AnimClass，但还没有解析出更直接的 AnimBP 动画节点或属性证据。
  - `UniqueSkeletonCompatible`：当前素材库内某 Skeleton 只对应一个模型，同 Skeleton 动画通过验证。
  - `SharedSkeletonCompatible`：多个模型共享同 Skeleton，同 Skeleton 动画通过验证；只能算兼容候选。
  - `Unknown` / `RelatedButNotUsable` / `NoMatchingAnimationExported`：证据不足或没有可用动画。

`evidenceChain`

- JSON 字段名：`evidenceChain`
- SQLite 字段名：`evidence_chain_json`
- 作用：列出该关系实际使用的证据链，便于团队和 UI 展示“为什么这条关系可信”。
- 示例：

```json
[
  "AnimBlueprintGeneratedClass.TargetSkeleton",
  "matchedModelSkeleton",
  "anim_blueprint_animation_refs",
  "sameUSkeleton",
  "trackValidation"
]
```

`isDeterministicUsage`

- JSON 字段名：`isDeterministicUsage`
- SQLite 字段名：`is_deterministic_usage`
- 作用：标记这条动画是否来自确定性使用/上下文证据。
- `true` 的来源包括：
  - `componentOwner`
  - `componentOwnerBlendSpaceSample`
  - `animBlueprintDirect`
  - `animBlueprintTargetSkeleton`
  - `animBlueprintDependency`
  - `characterDataSet`
- 用法：这是关系来源布尔值，不是最终推荐状态。UI 默认“可信动画”列表应优先使用 `recommendedUse = defaultTrusted`；需要兼容旧库时，才退回到 `isDeterministicUsage = true` 且 `validationStatus != error`。

`isCompatibilityCandidate`

- JSON 字段名：`isCompatibilityCandidate`
- SQLite 字段名：`is_compatibility_candidate`
- 作用：标记这条动画只是结构兼容候选，不应伪装成确定使用关系。
- `true` 的来源包括：
  - `uniqueSkeleton`
  - `sharedSkeleton`
- 用法：UI 应放到“同 Skeleton 兼容候选”区域，默认可折叠；报告不能把它统计为确定匹配。

`relationshipKind`

- JSON 字段名：`relationshipKind`
- SQLite 字段名：`relationship_kind`
- 作用：给 UI 和查询脚本一个更直接的关系大类，避免每次都反推 `confidenceTier`、`usageEvidence` 和多个布尔字段。
- 当前取值：
  - `deterministicUsage`：确定性使用关系。来自组件直接引用、AnimBP 直接/依赖引用、AnimBP TargetSkeleton 匹配、Character/DataAsset 动画集等证据。
  - `contextualUsage`：上下文关系。当前主要是组件引用 AnimClass，但尚未解析到具体 AnimBP 节点或动画属性引用。
  - `compatibilityCandidate`：同 Skeleton 或唯一 Skeleton 的结构兼容候选。
  - `unknown`：证据不足。
- 用法：浏览器可以先按 `relationshipKind` 分区展示，避免把“能播放候选”和“确定使用动画”混在一个数字里。

`recommendedUse`

- JSON 字段名：`recommendedUse`
- SQLite 字段名：`recommended_use`
- 作用：给默认筛选一个最终推荐状态。它会综合导出状态、结构验证和关系大类，比单独看 `preview ok`、`usageEvidence` 或 `confidenceTier` 更适合 UI 默认列表。
- 当前取值：
  - `defaultTrusted`：默认可信列表。要求动画可用、验证通过，并且关系属于 `deterministicUsage`。
  - `compatibleCandidate`：结构兼容候选。验证通过，但只证明同 Skeleton/唯一 Skeleton 兼容，不代表原游戏确定使用。
  - `manualReview`：需要人工复查。常见于 AnimClass 上下文关系、未知关系，或确定性关系但验证不是完全 `ok`。
  - `compatibleNeedsReview`：兼容候选存在 warning，需要人工复查。
  - `notUsable`：导出失败、验证 error、缺动画文件或其它不可用状态。
- 用法：UI 默认“可信动画”应优先筛选 `recommendedUse = 'defaultTrusted'`；素材库验收通过数也应优先统计这个值，而不是统计全部 `validation_status = 'ok'`。

#### 兼容旧逻辑字段

`usageEvidence`

- JSON 字段名：`usageEvidence`
- SQLite 字段名：`usage_evidence`
- 作用：粗略归类关系证据。
- 当前取值：
  - `explicitUsage`
  - `animBlueprintDirect`
  - `characterDataSet`
  - `animClassContext`
  - `skeletonCompatibility`
  - `unknown`
- 注意：这是粗粒度字段。新增逻辑后不要只靠它判断最终置信度，应结合 `confidenceTier`。

`isExplicitUsage`

- JSON 字段名：`isExplicitUsage`
- SQLite 字段名：`is_explicit_usage`
- 作用：兼容旧版“组件显式引用”统计。
- 当前只对 `usageEvidence = explicitUsage` 为 true。
- 注意：AnimBP/DataAsset 这类新增强证据不会让 `isExplicitUsage` 为 true。团队不要把 `isExplicitUsage = false` 解读成“不可信”；应看 `isDeterministicUsage`。

`isSkeletonCompatible`

- JSON 字段名：`isSkeletonCompatible`
- SQLite 字段名：`is_skeleton_compatible`
- 作用：兼容旧版“同 Skeleton 兼容候选”统计。
- 当前只对 `usageEvidence = skeletonCompatibility` 为 true。
- 注意：它证明的是结构兼容，不证明动画属于该模型。验收时不能把它等同于 pass。

#### 验证字段仍然必须一起看

`validationStatus` / `validation_status`

- `ok`：轨道、骨骼等结构验证通过。
- `warning`：存在 partial coverage、容器缺子动画等风险。
- `error`：缺关键骨骼或无法建立有效关系。

`validationCategory` / `validation_category`

- 常见值包括 `directTrack`、`partialTrackCoverage`、`missingTrackBones`。
- 用法：`confidenceTier` 只说明关系证据强弱，不能替代视觉和结构验证。最高置信度展示应同时要求 `validationStatus = ok`。

#### 推荐查询方式

确定性动画列表：

```sql
SELECT *
FROM relation_animations
WHERE recommended_use = 'defaultTrusted'
ORDER BY confidence_tier, name;
```

同 Skeleton 兼容候选：

```sql
SELECT *
FROM relation_animations
WHERE recommended_use = 'compatibleCandidate'
ORDER BY confidence_tier, name;
```

需要人工复查的动画：

```sql
SELECT *
FROM relation_animations
WHERE recommended_use IN ('manualReview', 'compatibleNeedsReview', 'notUsable')
ORDER BY recommended_use, validation_status, confidence_tier, name;
```

关系数量概览：

```sql
SELECT relationship_kind, recommended_use, confidence_tier, validation_status, COUNT(*) AS count
FROM relation_animations
GROUP BY relationship_kind, recommended_use, confidence_tier, validation_status
ORDER BY relationship_kind, recommended_use, confidence_tier;
```

### SQLite 与 JSON/JSONL 分工

`library_index.db` 是浏览器、筛选器和验收脚本的优先查询入口；JSON/JSONL 继续保留为流式导出日志、人工 diff 和兼容旧工具的产物。新增字段或新增表时，原则上应该先保证 SQLite 可查，再保留 JSON/JSONL 的原始记录。

当前关键 SQLite 表：

- `assets`：素材总目录，合并模型、贴图、材质、动画等导出资产。
- `relation_animations`：模型与动画的逐条关系明细，包含 `confidence_tier`、`relationship_kind`、`recommended_use`、`evidence_chain_json`、验证状态和完整 `raw_json`。
- `model_animation_relations`：模型维度的关系摘要和数量统计。
- `animation_validation`：动画与模型配对的结构验证结果。
- `export_manifest`：由 `export_manifest.jsonl` 导入，记录每个输出文件来自哪个 UE 包、对象和导出类型。
- `animation_bindings`：由 `animation_bindings.jsonl` 导入，记录动画源对象、Skeleton、时长、track/segment/section 数量、压缩信息和导出状态。
- `auto_referenced_exports`：由 `auto_referenced_exports.jsonl` 导入，记录自动补导计划、关系来源、目标对象、输出类型和失败原因。

团队约定：

- 新 UI 默认读取 SQLite；只有展示原始日志、排查写入顺序或兼容旧流程时再读 JSON/JSONL。
- `raw_json` 是兜底字段，用于保留 SQLite 列尚未展开的细节；常用筛选条件必须提升为显式列。
- 判断“默认可信动画”优先看 `recommended_use = 'defaultTrusted'`，不要只看 `is_explicit_usage` 或 `validation_status = 'ok'`。

### Tier 1：确定性使用关系

`ExplicitComponentAnimation`

- 同一 `USkeletalMeshComponent` 显式引用 mesh 和 `AnimToPlay`。
- 同一 owner/component 引用 mesh 和动画容器，并能展开到子 `AnimSequence`。

`AnimBlueprintDirectReference`

- SkeletalMeshComponent 引用 AnimClass。
- AnimBlueprint 或 GeneratedClass 的依赖图/节点/属性直接引用 AnimSequence、BlendSpace、Montage 或 Composite。
- Skeleton 一致，骨骼 track 验证通过。

### Tier 2：强上下文关系

`CharacterBlueprintDependencyChain`

- Character BP package hard depends on SkeletalMesh package。
- 同一 BP package 或其 AnimBP package hard/soft depends on animation assets。
- Skeleton 一致，骨骼 track 验证通过。

`DataAssetAnimationSet`

- 同一个 DataAsset、PrimaryAsset、DataTable 或 Gameplay 配置资产同时引用模型/角色 BP/Skeleton 和动画集。
- 需要保留 DataAsset class、字段名、dependency category。

### Tier 3：结构兼容关系

`UniqueSkeletonCompatible`

- 当前素材库中 Skeleton 只对应一个模型。
- 动画同 Skeleton。
- track 覆盖和骨骼层级验证通过。

`SharedSkeletonCompatible`

- 多个模型共享同 Skeleton。
- 动画同 Skeleton。
- track 覆盖和骨骼层级验证通过。
- 默认只能作为可复用候选，不能作为确定使用关系。

### Tier 4：诊断或待人工确认

`PartialTrackCoverage`

- 同 Skeleton 但 track 覆盖不足、缺少面部/附件/武器/IK/裙摆/头发等关键骨骼轨道。

`RetargetWarning`

- Skeleton 一致但 bind pose/ref pose/hash、骨骼长度、scale 或比例差异异常。

`UnknownContext`

- 只有目录/命名/路径相似，或者只有 broad dependency，没有明确 owner/class/field 证据。
- 不能自动判定可复用成功。

## 后续实现优先级

### P0：AssetRegistry 依赖索引

目标：建立 package-level hard/soft/manage/searchable dependency graph。

实现要点：

1. 在源索引阶段寻找 `AssetRegistry.bin`。
2. 使用 CUE4Parse `FAssetRegistryState` 读取资产和依赖节点。
3. 写入 `ue_source_index.db.asset_registry_dependencies`。
4. 建立 package path 到 `source_objects.object_path` 的解析映射。
5. 在 `library_index.db` 中同步可查询的依赖摘要。

验收：

- 能查到某个 AnimBlueprint package 依赖哪些 AnimSequence/BlendSpace/Montage package。
- 能查到某个 Character BP package 依赖哪些 SkeletalMesh、AnimBP、DataAsset。
- 能区分 hard package、soft package、hard manage、soft manage。

### P0：AnimBlueprint 深解析

目标：把 `componentAnimClass` 从“owner 下找动画”升级为“AnimBP 直接引用了哪些动画资产”。

实现要点：

1. 解析 `AnimBlueprintGeneratedClass`、CDO、序列化属性和节点结构。
2. 识别常见动画节点引用：
   - Sequence Player / Sequence Evaluator
   - BlendSpace Player / BlendSpace Evaluator
   - AimOffset
   - Montage / Slot / StateMachine 相关节点
   - Linked Anim Graph / Linked Anim Layer / Parent AnimBP
3. 对 BlendSpace、Montage、Composite 继续展开 `animation_segments`。
4. 记录 `anim_blueprint_animation_refs` 表：

```sql
CREATE TABLE anim_blueprint_animation_refs (
    anim_blueprint_object_path TEXT NOT NULL,
    generated_class_object_path TEXT,
    referenced_animation_path TEXT NOT NULL,
    referenced_animation_type TEXT,
    node_type TEXT,
    property_path TEXT,
    relation_source TEXT NOT NULL,
    skeleton_path TEXT,
    confidence_hint TEXT
);
```

验收：

- 对 NTE 人形角色，组件引用 AnimClass 后，能列出 AnimBP 直接/间接引用的动画资产。
- `componentAnimClass` 关系数量应明显增加，但必须保留证据链，不能混入纯 Skeleton 扫描。

### P1：DataAsset / DataTable 动画集识别

目标：捕捉游戏项目常见的角色配置资产、动作集配置资产、状态机配置资产。

实现要点：

1. 扫描 DataAsset/DataTable/PrimaryAssetLabel/AssetManager 相关对象。
2. 抽取字段名、字段类型、PPtr、SoftObjectPath、AssetBundle。
3. 当同一个配置资产同时引用角色/mesh/skeleton/animBP 和动画容器时，建立 `DataAssetAnimationSet`。
4. 字段名可作为辅助说明，但不能单独作为关系证据。

验收：

- 输出中能看到“这个动画集关系来自哪个 DataAsset、哪个字段或 bundle”。
- 如果只有名字像但没有引用关系，标记为 `UnknownContext`。

### P1：视觉与结构负证据

目标：避免把“能打开、有变化”误判成“可复用正确”。

实现要点：

1. 对模型 rest/start/mid/end 帧计算 bbox、骨骼 bbox、关键骨骼长度变化和异常 scale。
2. 对手、脚、头、脊柱、肩、髋、武器/IK/附件骨骼建立关键骨骼组检查。
3. 对动画 preview 生成近景四帧和变化最大帧。
4. 一旦出现离群顶点、飞骨、scale 异常、retarget warning、bind pose warning，最高只能到 suspicious。

验收：

- `animation_validation.json` 中除了 track coverage，还能报告 visual/pose/bbox/scale 风险。
- 浏览器能按 pass/suspicious/fail 过滤。

### P2：报告和 UI 分层

目标：让使用者清楚看到关系是“确定使用”还是“兼容候选”。

实现要点：

1. `model_animations.json` 增加：
   - `confidenceTier`
   - `relationshipKind`
   - `recommendedUse`
   - `evidenceChain`
   - `dependencyCategories`
   - `negativeEvidence`
   - `requiresManualReview`
2. 浏览器中默认优先显示 Tier 1/Tier 2。
3. `SharedSkeletonCompatible` 默认折叠到“兼容候选”区域。
4. 数量显示拆开：
   - 确定使用动画数
   - 强上下文动画数
   - 同 Skeleton 兼容候选数
   - suspicious/fail 数

## 风险与边界

1. AssetRegistry 是 package-level，不一定能精确到某个组件或某个节点。
   - 需要和 object-level PPtr、component owner、class、field name 共同使用。

2. Soft reference 不一定表示默认会使用。
   - 可能只是可选加载、商店、活动、皮肤或 DLC 资源。

3. 同 Skeleton 仍然不保证视觉正确。
   - 需要 ref pose、骨骼比例、retarget 设置、bbox 和近景视觉验证。

4. AnimBlueprint 可能通过运行时变量动态切换动画。
   - 如果变量值来自 DataAsset/DataTable，必须继续追踪配置资产。
   - 如果运行时逻辑无法静态解析，标记为 `UnknownRuntimeContext`。

5. 目录名和文件名前缀只能作为展示排序或人工检索信号。
   - 不能进入 `confidenceTier` 的自动判定依据。

## 总结

当前工具已经具备正确方向：不靠文件名硬猜，优先使用 UE 组件、Skeleton、动画容器 segment 和骨骼 track 验证。但当前 NTE 样本里绝大多数关系来自 `sharedSkeleton` / `uniqueSkeleton`，只能证明“可复用候选”，不能证明“确定使用关系”。

最值得优先实现的优化是：

1. 接入 AssetRegistry 依赖图。
2. 深解析 AnimBlueprint 直接/间接动画引用。
3. 识别 DataAsset/DataTable/PrimaryAsset 动画集。
4. 把 `sharedSkeleton` 降级为兼容候选，并在报告和 UI 中明显区分。
5. 增加视觉/结构负证据，避免把异常动画误判为通过。

完成这些以后，工具才能更接近“可复用素材库”的标准：开发者不仅能看到哪些动画结构上可播放，还能看到每条关系来自哪个 UE 确定性证据链，以及哪些关系需要人工复查。
