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
| Asset catalog | `export_events.db.asset_catalog`, then `library_index.db.assets` | `asset_catalog.jsonl` is a compatibility view |
| Animation bindings | `export_events.db.animation_bindings`, then `library_index.db.animation_bindings` | `animation_bindings.jsonl` is a compatibility view |
| Auto referenced export diagnostics | `export_events.db.auto_referenced_exports`, then `library_index.db.auto_referenced_exports` | `auto_referenced_exports.jsonl` is a compatibility view |
| Texture dedupe links | `library_work.db.texture_links`, then `library_index.db.texture_links` | `texture_links.jsonl` is a compatibility/debug view |
| Material texture slots | `ue_source_index.db.material_texture_slots`, optionally `library_work.db.material_texture_slots`, then `library_index.db.material_texture_slots` | `material_texture_slots.jsonl` is a compatibility/debug view |
| Shared glTF texture rewrites | `library_work.db.shared_gltf_texture_links`, then `library_index.db.shared_gltf_texture_links` | `shared_texture_gltf_links.jsonl` is a compatibility/debug view |
| UE source relations | `ue_source_index.db`, optionally `library_work.db.component_asset_relations`, then `library_index.db.component_asset_relations` | `component_asset_relations.jsonl` is a compatibility/debug view |
| UE package object maps | `ue_source_index.db.package_object_maps`, then optional sampled rows in `library_index.db.package_object_maps` | `package_object_maps.jsonl` is a compatibility/debug view |
| Model validation | `library_index.db.model_validation` | `model_validation.json` is a compatibility/debug view |
| Skeleton groups | `library_index.db.skeleton_groups` | `skeletons.json` is a compatibility/debug view |
| Animation validation | `library_index.db.animation_validation` | `animation_validation.json` and `animation_validation.jsonl` are compatibility/debug views |
| Browser model/animation list | `library_index.db.model_animation_relations` and `library_index.db.relation_animations` | Browser must not depend on `model_animations.json` |
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

## SQLite-only mode

`--postprocess-library <root> --sqlite-only-index` disables compatibility JSONL for large machine indexes where SQLite has an equivalent path. It routes texture dedupe links, material texture slot links, shared glTF texture rewrite links and full component relations through `library_work.db`, then imports them into the matching `library_index.db` tables.

The mode must preserve row counts and deterministic evidence. For example, when a source index contains one component relation, SQLite-only postprocess must produce:

- no compatibility JSONL for the SQLite-backed machine indexes
- matching rows in the relevant `library_work.db` tables
- matching rows in the relevant `library_index.db` tables

`--no-compat-json` is an alias for the same behavior.

Full export configs can enable the same behavior with `sqliteOnlyIndex: true` or `writeCompatibilityJson: false`. This is recommended for large UE5 libraries so one-command exports do not duplicate large machine indexes as JSONL before importing them back into SQLite. In this mode the main export pass still writes `export_events.db`, but skips the compatibility views `export_manifest.jsonl`, `asset_catalog.jsonl`, `animation_bindings.jsonl` and `auto_referenced_exports.jsonl`. Postprocess also keeps the merged catalog, model validation, skeleton groups, animation validation and model-animation relations in memory for validation and writes them to `library_index.db`, but skips `asset_catalog.jsonl`, `package_object_maps.jsonl`, `model_validation.json`, `skeletons.json`, `animation_validation.json`, `animation_validation.jsonl` and `model_animations.json`.

## Migration state

- `UE5LibraryBrowser` already requires `library_index.db`.
- Main export now writes `export_events.db` alongside compatibility JSONL.
- Postprocess now reads `export_events.db` first for export manifest, asset catalog, animation bindings and auto referenced export diagnostics, then falls back to JSONL.
- Postprocess can now stream full component relations to `library_work.db` and skip `component_asset_relations.jsonl` in SQLite-only mode.
- Postprocess can now write texture links, material texture slots and shared glTF texture links to `library_work.db` and skip their JSONL views in SQLite-only mode.
- Postprocess can now skip animation validation and model-animation compatibility JSON views in SQLite-only mode; query `animation_validation`, `model_animation_relations` and `relation_animations` in `library_index.db`.
- Postprocess can now skip model validation and skeleton compatibility JSON views in SQLite-only mode; query `model_validation` and `skeleton_groups` in `library_index.db`.
- Remaining JSON usage is mostly glTF/GLB structure editing, material JSON input, validation caches and human-readable reports.
