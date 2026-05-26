# UE 游戏素材提取流程

本文档用于团队成员统一处理 Unreal Engine 游戏资源提取。默认目标是提取可预览、可筛选的模型 GLB 和贴图 PNG。

## 核心原则

1. 先验证，后批量：先用 FModel 确认游戏资源能正常解包、浏览、预览，再使用本仓库批量导出。
2. 精简优先：只导出有用模型和贴图，尽量排除占位、碰撞、测试、地图、蓝图、数据表、影片等噪音。宁愿不全，也不要垃圾太多。
3. 可识别性优先：导出的 GLB 首先要能在 F3D、Blender 等工具中快速看清楚。预览阶段不追求 100% 还原 Unreal 复杂材质。
4. 小范围冒烟：新游戏先导出 1-3 个已确认可见的模型做 smoke test，再扩大到模型目录。

## 准备工具

- FModel：用于确认 UE 版本、AES、usmap、虚拟路径、资源类型和单文件预览。
- UnrealExporter：本仓库，用于批量导出 GLB 和 PNG。
- usmap 文件：用于 UE4/UE5 cooked asset 类型映射。
- AES key：加密游戏必需。
- 可选预览工具：F3D、Blender、Windows 3D Viewer。

usmap 可以通过以下方式获得：

- 网上查找对应游戏和版本的 usmap。
- 使用 [UE4SS-RE/RE-UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) 自行从游戏中 dump。

## 一、用 FModel 做前置验证

### 1. 找到游戏 Pak 目录

常见路径类似：

```text
GameName/Content/Paks
ProjectName/Content/Paks
```

目录内可能包含：

```text
.pak
.utoc
.ucas
```

UE5 游戏通常会同时有 `.utoc` 和 `.ucas`。

### 2. 在 FModel 中加载游戏

在 FModel 中配置：

- Paks directory：游戏的 `Content/Paks` 目录。
- UE version：游戏对应 UE 版本，例如 `5.6`。
- AES key：如果游戏加密，需要填入。
- Mappings/usmap：如果资源需要类型映射，需要填入。

### 3. 判断是否需要 AES

如果 FModel 无法列出资源、提示 encrypted、打开 asset 报解密相关错误，通常说明需要 AES。

拿到 AES 后，在 FModel 中重新加载确认：

- 文件树能正常显示。
- 能展开 `ProjectName/Content/...`。
- 能打开模型、材质、贴图等资源。

### 4. 判断是否需要 usmap

如果 FModel 能看到文件，但打开 asset 报类型、属性、序列化、mapping 相关错误，通常需要 usmap。

usmap 放入 FModel 后，再确认：

- StaticMesh / SkeletalMesh 能打开。
- Texture 能预览。
- 材质引用能看到。

### 5. 记录虚拟路径前缀

在 FModel 中确认资源路径前缀，例如：

```text
LEGOBatmanLotDK/Content/...
HT/Content/...
Pal/Content/...
```

这个前缀后面要写进 UnrealExporter 的 `export` 正则。

## 二、准备 UnrealExporter 配置

### 1. 放置 usmap

把 usmap 放到：

```text
D:\misutime\UnrealExporter\mappings
```

文件名必须和配置里的 `gameTitle` 一致：

```text
mappings/LEGOBatmanLotDK.usmap
```

对应配置：

```json
"gameTitle": "LEGOBatmanLotDK"
```

### 2. 创建配置文件

在 `configs` 下创建独立配置，例如：

```text
configs/batman-models.json
configs/game-models.json
configs/game-smoke.json
```

基础字段：

```json
[
  {
    "gameTitle": "LEGOBatmanLotDK",
    "version": "5.6",
    "paksDir": "D:\\Game\\ProjectName\\Content\\Paks",
    "outputDir": "D:\\misutime\\UnrealExporter\\output\\game-models",
    "aes": "0xYOUR_AES_KEY",
    "logOutputs": true,
    "keepDirectoryStructure": true,
    "lang": "English",
    "maxDegreeOfParallelism": 4,
    "createNewCheckpoint": false,
    "useCheckpointFile": "",
    "export": [],
    "exclude": []
  }
]
```

### 3. 先写 smoke 配置

先从 FModel 找一个明确可见的模型，写成单文件导出：

```json
"export": [
  "ProjectName/Content/Models/Props/SM_TestModel\\.uasset:glb"
]
```

运行：

```powershell
cd D:\misutime\UnrealExporter
dotnet run --project UnrealExporter game-smoke
```

确认：

- 能成功扫描。
- GLB 能在 F3D / Blender 中打开。
- 贴图或材质 sidecar 文件有输出。
- 输出目录结构符合预期。

## 三、批量导出模型和贴图

### 推荐导出范围

优先导出正式资源目录：

```text
Characters
Models
Vehicles
Weapons
Props
AdditionalContent/*/Characters
AdditionalContent/*/Models
AdditionalContent/*/Vehicles
```

模型一般优先匹配：

```text
SM_*.uasset
SK_*.uasset
```

贴图一般优先匹配：

```text
Textures/*.uasset
T_*.uasset
```

示例：

```json
"export": [
  "ProjectName/Content/Characters/.*/(?:SM|SK)_[^/]*\\.uasset:glb",
  "ProjectName/Content/Models/.*/(?:SM|SK)_[^/]*\\.uasset:glb",
  "ProjectName/Content/Vehicles/.*/(?:SM|SK)_[^/]*\\.uasset:glb",
  "ProjectName/Content/Characters/.*/Textures/.*\\.uasset:png",
  "ProjectName/Content/Models/.*/Textures/.*\\.uasset:png",
  "ProjectName/Content/Vehicles/.*/Textures/.*\\.uasset:png"
]
```

### 推荐排除项

默认排除噪音：

```json
"exclude": [
  "ProjectName/Content/.*/Blueprints/.*",
  "ProjectName/Content/.*/Data/.*",
  "ProjectName/Content/.*/Maps/.*",
  "ProjectName/Content/.*/Movies/.*",
  "ProjectName/Content/.*/PlaceHolder/.*",
  "ProjectName/Content/.*/Tests/.*",
  "ProjectName/Content/FunctionalTests/.*",
  ".*Blockout.*",
  ".*[/_]BO[/_].*",
  ".*SM_COL_.*\\.uasset",
  ".*SK_COL_.*\\.uasset",
  ".*_Proxy.*\\.uasset",
  ".*ProxyMesh.*\\.uasset"
]
```

如果项目命名规则不同，根据 FModel 观察到的路径调整。

### 运行批量导出

```powershell
cd D:\misutime\UnrealExporter
dotnet run --project UnrealExporter game-models
```

当前仓库内已纳入 git 管理的示例配置放在 `configs/examples`。实际运行前先复制到 `configs`，再使用不带 `.json` 后缀的配置名运行：

```powershell
Copy-Item configs\examples\nte-useful-assets.json configs\nte-useful-assets.json
Copy-Item configs\examples\batman-models.json configs\batman-models.json
```

```powershell
dotnet run --project UnrealExporter nte-useful-assets
dotnet run --project UnrealExporter batman-models
```

输出通常在：

```text
output/game-models
```

## 四、检查导出结果

### 1. 快速查看数量

```powershell
Get-ChildItem output\game-models -Recurse -File | Group-Object Extension | Sort-Object Count -Descending
```

重点看：

- `.glb` 数量是否合理。
- `.png` 数量是否合理。
- 是否混入大量 `.json`、`.hdr` 或噪音资源。

### 2. 用 F3D 快速预览

优先用 F3D 检查 GLB 是否可快速识别。

如果 F3D 空白，但 Blender 能看到，常见原因：

- 顶点色 `COLOR_0` 为全黑或 alpha 接近 0。
- Unreal 复杂材质没有映射到 glTF 标准材质槽。
- 模型是附件、碰撞、代理或极小部件。

本导出器默认以可识别性优先，会对明显不可见的顶点色做兜底处理。

### 3. 抽样检查目录

优先检查：

```text
Characters
Models
Vehicles
AdditionalContent
```

如果大量输出集中在：

```text
PlaceHolder
Blockout
FunctionalTests
Collision
Proxy
```

说明配置过宽，需要收紧 `export` 或增加 `exclude`。

## 五、常见问题

### FModel 能打开，但 UnrealExporter 导不出来

检查：

- `version` 是否和 FModel 中选择的 UE 版本一致。
- AES 是否填写完整，带 `0x`。
- usmap 文件名是否和 `gameTitle` 一致。
- `export` 正则是否匹配虚拟路径，而不是 Windows 本地路径。

### 输出 GLB 很少

可能原因：

- 正则太窄。
- 游戏模型不以 `SM_` / `SK_` 命名。
- 很多资源不是 StaticMesh / SkeletalMesh。
- usmap 或 UE 版本不匹配导致部分 asset 解析失败。

先回 FModel 找几个明确模型路径，再补充配置。

### 输出噪音太多

不要直接全量：

```json
"ProjectName/Content/.*\\.uasset:glb"
```

改用目录和命名约束，例如：

```json
"ProjectName/Content/Models/.*/(?:SM|SK)_[^/]*\\.uasset:glb"
```

并增加 `exclude`。

### 内存占用过高

批量导出会同时解析 mesh、材质和贴图。本仓库已限制并发，仍建议：

- 先跑 smoke。
- 再跑 models。
- 不要全量扫 Content。
- 分目录拆多个配置运行。

可以用 `maxDegreeOfParallelism` 调整并发。默认值是 `4`。64GB 内存机器可以先试 `8`，如果内存、CPU、磁盘读写都稳定，再逐步试 `12` 或 `16`。如果内存快速上涨、系统开始明显卡顿，或导出速度没有继续提升，就降回上一个稳定值。

### GLB 贴图看起来不完整

Unreal 材质可能非常复杂，glTF 只能表达标准 PBR 材质槽。导出器会尽量嵌入常见贴图，并保留 Unreal 材质路径和贴图槽到：

```text
material.extras.textureSlots
```

如果需要在 Unity 或自研工具里重建材质，应结合 sidecar PNG、材质 JSON 和 `textureSlots` 处理。

## 六、推荐交付结构

每个游戏建议至少保留：

```text
configs/game-smoke.json
configs/game-models.json
mappings/GameTitle.usmap
output/game-models
```

如果需要记录来源信息，建议补充：

```text
AES 来源
usmap 来源
UE 版本
FModel 测试结果
关键资源目录
已知排除规则
```

## 七、最小操作清单

1. 找到 `Content/Paks`。
2. 用 FModel 加载游戏。
3. 确认 UE 版本。
4. 判断并填写 AES。
5. 判断并准备 usmap。
6. 在 FModel 中确认虚拟路径和样例模型。
7. 写 `game-smoke.json` 并单模型测试。
8. 写 `game-models.json`，只导有用模型和贴图。
9. 用 F3D / Blender 抽样检查。
10. 根据噪音和空白模型继续收紧配置或修导出器。
