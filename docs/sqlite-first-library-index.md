# SQLite-first library index

## Goal

Large UE libraries should not require browser or postprocess code to parse multi-GB JSON/JSONL files for normal workflows. The authoritative machine-readable index is SQLite:

- `ue_source_index.db`: original UE source evidence, object maps, component relations, material slots, skeleton bones, animation tracks and animation segments.
- `export_events.db`: raw export-time events written during the main export pass.
- `library_work.db`: postprocess work database for large streamed intermediate rows that should not be serialized to JSONL.
- `library_index.db`: postprocessed browser/query index for reusable asset browsing, validation and model-animation relations.

JSON and JSONL files are compatibility and human-inspection views unless a specific command explicitly says otherwise.

## Current data ownership

| Data | SQLite source of truth | JSON/JSONL status |
| --- | --- | --- |
| Export manifest | `export_events.db.export_manifest`, then `library_index.db.export_manifest` | `export_manifest.jsonl` is a compatibility view |
| Export resume state | `export_resume_state.db.export_jobs` and `export_resume_state.db.export_job_outputs` | older `outputs_json` rows are discarded when the resume DB is opened |
| Asset catalog | `export_events.db.asset_catalog`, then `library_index.db.assets` | `asset_catalog.jsonl` is a compatibility view |
| Animation bindings | `export_events.db.animation_bindings`, then `library_index.db.animation_bindings` | `animation_bindings.jsonl` is a compatibility view |
| Auto referenced export diagnostics | `export_events.db.auto_referenced_exports`, then `library_index.db.auto_referenced_exports` | `auto_referenced_exports.jsonl` is a compatibility view |
| Export update checkpoints | `checkpoints/*.checkpoint.db.checkpoint_files` | legacy `.ckpt` JSON files are not written by new exports |
| Texture dedupe links | `library_work.db.texture_links`, then `library_index.db.texture_links` | `texture_links.jsonl` is a compatibility/debug view |
| Texture dedupe summary | `library_work.db.library_reports(name='texture_dedupe')`, then `library_index.db.library_reports` | `texture_dedupe_summary.json` is a compatibility/human-inspection view |
| Material sidecar summaries | `library_work.db.material_sidecars`, then `library_index.db.material_sidecars` | Material JSON files remain optional human/export sidecars; postprocess should prefer SQLite/cache when present |
| Material texture slots | `ue_source_index.db.material_texture_slots`, optionally `library_work.db.material_texture_slots`, then `library_index.db.material_texture_slots` | `material_texture_slots.jsonl` is a compatibility/debug view |
| Shared glTF texture rewrites | `library_work.db.shared_gltf_texture_links`, then `library_index.db.shared_gltf_texture_links` | `shared_texture_gltf_links.jsonl` is a compatibility/debug view |
| UE source index metadata | `ue_source_index.db.source_index_metadata` | `ue_source_index.metadata.json` is a legacy compatibility view |
| UE source relations | `ue_source_index.db`, optionally `library_work.db.component_asset_relations`, then `library_index.db.component_asset_relations` | `component_asset_relations.jsonl` is a compatibility/debug view |
| Component groups | `library_index.db.component_groups` for owner-level counts; full details in `library_index.db.component_asset_relations` | `component_groups.json` is a compatibility/debug view |
| UE package object maps | `ue_source_index.db.package_object_maps`, then optional sampled rows in `library_index.db.package_object_maps` | `package_object_maps.jsonl` is a compatibility/debug view |
| Model validation cache | `library_work.db.model_validation_cache` | per-model `.ue_model_validation_cache.json` files are legacy read-only cache inputs |
| Model validation | `library_index.db.model_validation` and `library_index.db.model_validation_notes` | `model_validation.json` is a compatibility/debug view |
| Model coverage | `library_index.db.model_coverage` | `model_coverage.json` is a compatibility/debug view |
| Task model quality | `library_index.db.model_coverage`, `library_index.db.model_coverage_task_signals` and `library_index.db.model_coverage_review_reasons` | `task_model_quality.json` is a compatibility/debug view |
| Skeleton groups | `library_index.db.skeleton_groups` | `skeletons.json` is a compatibility/debug view |
| Animation validation | `library_index.db.animation_validation`, `library_index.db.animation_validation_missing_track_bones` and `library_index.db.animation_validation_hierarchy_mismatches` | `animation_validation.json` and `animation_validation.jsonl` are compatibility/debug views |
| Browser model/animation list | `library_index.db.model_animation_relations`, `library_index.db.relation_animations` and `library_index.db.relation_animation_evidence` | Browser must not depend on `model_animations.json`; model relation summaries are explicit columns, per-animation rows live in `relation_animations`, and ordered evidence steps live in `relation_animation_evidence` |
| Animation preview validation | `preview_validation.db.preview_validation_reports` in the preview cache/output directory | `preview_validation.json` is only a manually requested debug view when `--report` is used |
| Library health and acceptance reports | `library_index.db.library_reports` | `library_health.json` and `library_acceptance.json` are compatibility/human-inspection views |
| Human reports | `library_index.db` for queries, JSON for readable summaries | JSON remains acceptable |

## Accuracy rules

- SQLite migration must not change mesh, texture, material, skeleton or animation export content.
- Relationship confidence still comes only from deterministic UE evidence: component references, AnimBP references, AssetRegistry dependencies, skeleton compatibility and validation results.
- JSON fallback is allowed for old exported libraries, but new code should prefer SQLite when the table exists and has rows.
- Do not replace structured SQLite facts with filename/path guessing.

## Performance rules

- Browser and automation should query `library_index.db`.
- Postprocess should prefer `export_events.db` over JSONL for export-time rows.
- Full component relations must be streamed into SQLite; do not materialize millions of relation rows only to build convenience summaries.
- Large nested JSON summaries are optional. If they would duplicate large SQLite tables, write a small human-readable summary and point to the SQLite table.
- Component relation transforms are structured columns in `component_asset_relations`: `location_x/y/z`, `rotation_pitch/yaw/roll` and `scale_x/y/z`. They come from deterministic UE component data and should not be stored as a nested JSON blob in the SQLite path.

## Large-library memory controls

Full UE5 exports should keep package work bounded with `maxHeavyExportDegreeOfParallelism` and `memorySoftLimitGb`.
`maxDegreeOfParallelism` still controls the outer file scan, while `maxHeavyExportDegreeOfParallelism` limits concurrent `uasset` / `umap` package loads and conversion work. `memorySoftLimitGb` watches process private bytes; when the soft limit is exceeded, the exporter pauses new heavy package work, compacts the managed heap and waits for memory to fall below the resume threshold.

These controls are deliberately scheduling-only: they must not change exported meshes, textures, materials, skeletons, animation files or deterministic relationship rows. `sourceIndexCommitInterval` also helps bound the source-index builder by committing smaller SQLite batches during very large scans.

## SQLite-first default

Exports, standalone shared-texture dedupe, animation preview validation and `--postprocess-library <root>` default to SQLite-backed machine indexes. JSON/JSONL summaries are skipped where SQLite has an equivalent path. The postprocessor routes material sidecar summaries, texture dedupe links and dedupe summary, material texture slot links, shared glTF texture rewrite links and full component relations through `library_work.db`, then imports them into the matching `library_index.db` tables.

This default must preserve row counts and deterministic evidence. For example, when a source index contains one component relation, SQLite-first postprocess must produce:

- no JSONL for SQLite-backed machine indexes unless a human/debug view was explicitly requested
- matching rows in the relevant `library_work.db` tables
- matching rows in the relevant `library_index.db` tables

`sqliteOnlyIndex` defaults to `true` and `writeCompatibilityJson` defaults to `false`. `--postprocess-library` accepts `--compat-json` only when a human-readable/debug JSON view is deliberately needed. In the default mode the main export pass writes `export_events.db`, but skips `export_manifest.jsonl`, `asset_catalog.jsonl`, `animation_bindings.jsonl` and `auto_referenced_exports.jsonl`. The source-index builder writes resume/fingerprint state to `ue_source_index.db.source_index_metadata` and skips `ue_source_index.metadata.json`. Standalone shared-texture dedupe also respects the same compatibility setting and stores its links/summary in `library_work.db`. Postprocess keeps the merged catalog, material sidecar summaries, texture dedupe summary, model validation, skeleton groups, animation validation, model coverage, health/acceptance reports and model-animation relations in memory for validation and writes them to `library_index.db`, but skips `asset_catalog.jsonl`, `package_object_maps.jsonl`, `component_groups.json`, `texture_dedupe_summary.json`, `model_coverage.json`, `task_model_quality.json`, `model_validation.json`, `skeletons.json`, `animation_validation.json`, `animation_validation.jsonl`, `model_animations.json`, `library_health.json` and `library_acceptance.json`.

## Migration state

- `UE5LibraryBrowser` already requires `library_index.db`.
- `UE5LibraryBrowser` model list queries read explicit SQLite columns and validation tables; it no longer falls back to `json_extract(assets.raw_json, ...)` for model metadata.
- `UE5LibraryBrowser` animation details read `relation_animations.evidence_summary`; ordered evidence steps live in `relation_animation_evidence`, so the UI/runtime path no longer parses an evidence-chain JSON blob.
- `model_animation_relations` is a lightweight model-level summary table without a `raw_json` blob. Query `relation_animations` for per-animation details and `relation_animation_evidence` for ordered deterministic evidence.
- `UE5LibraryBrowser` writes animation preview validation to `preview_validation.db` via `--report-db`; it no longer requests `preview_validation.json`.
- Main export writes `export_events.db` by default and only writes compatibility JSONL when explicitly requested.
- `createNewCheckpoint` writes SQLite `.checkpoint.db` files; `useCheckpointFile: "latest"` loads the newest matching SQLite checkpoint.
- `export_resume_state.db` stores completed job outputs in `export_job_outputs` rows instead of an `outputs_json` blob.
- Postprocess now reads `export_events.db` first for export manifest, asset catalog, animation bindings and auto referenced export diagnostics, then falls back to JSONL.
- Auto referenced export diagnostics now stream to `export_events.db.auto_referenced_exports` in bounded batches during rule planning; `auto_referenced_exports.jsonl` is only written when compatibility JSON is explicitly enabled.
- `--materialize-animation-metadata` updates `export_events.db.asset_catalog` and `export_events.db.animation_bindings` by default; `.ueanim.metadata.json`, `asset_catalog.jsonl` and `animation_bindings.jsonl` are only written or updated when `--compat-json` is explicitly requested.
- Model validation cache now writes to `library_work.db.model_validation_cache`. Old per-model `.ue_model_validation_cache.json` files are read once for compatibility and migrated into SQLite, then deleted when possible.
- Material sidecar summaries now write to `library_work.db.material_sidecars` and synchronize to `library_index.db.material_sidecars`; new postprocess runs prefer export-event Material rows over recursive `*.json` scans.
- Postprocess can now stream full component relations to `library_work.db` and skips `component_asset_relations.jsonl` by default. Component transforms are stored as explicit location/rotation/scale numeric columns in both `library_work.db.component_asset_relations` and `library_index.db.component_asset_relations`.
- Postprocess can now write texture links, material texture slots and shared glTF texture links to `library_work.db` and skips their JSONL views by default.
- Texture dedupe summary now writes to `library_work.db.library_reports(name='texture_dedupe')` and syncs to `library_index.db.library_reports`; `texture_dedupe_summary.json` is only an explicitly requested debug view.
- Standalone shared-texture dedupe now honors the SQLite-first defaults from export configs instead of always writing JSONL/JSON.
- Postprocess skips animation validation and model-animation JSON views by default; query `animation_validation`, `animation_validation_missing_track_bones`, `animation_validation_hierarchy_mismatches`, `model_animation_relations` and `relation_animations` in `library_index.db`.
- Postprocess skips model validation and skeleton JSON views by default; query `model_validation`, `model_validation_notes` and `skeleton_groups` in `library_index.db`. Model bounding boxes are explicit numeric columns in `model_validation`, while validation notes are rows in `model_validation_notes`.
- Postprocess skips model coverage, task model quality and component group JSON views by default; query `model_coverage`, `model_coverage_task_signals`, `model_coverage_review_reasons`, `component_groups` and `component_asset_relations` in `library_index.db`. `component_groups` is an owner-level summary table without a nested raw JSON blob; detailed component/model/animation/material references live in `component_asset_relations`. `--refresh-task-model-quality` rebuilds its Markdown report directly from explicit `library_index.db.model_coverage` columns plus `model_coverage_task_signals`, and no longer reads `model_coverage.json`, `model_coverage.raw_json`, task-signal JSON arrays or review-reason JSON arrays.
- Postprocess stores library health and acceptance summaries in `library_index.db.library_reports` and skips `library_health.json` / `library_acceptance.json` by default.
- Source-index resume metadata now lives in `ue_source_index.db.source_index_metadata`; `ue_source_index.metadata.json` is only written when compatibility JSON is explicitly enabled.
- Remaining JSON usage is mostly glTF/GLB structure editing, material JSON input and human-readable reports.
