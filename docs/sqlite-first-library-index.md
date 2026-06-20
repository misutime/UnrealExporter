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
| UE source relations | `ue_source_index.db`, optionally `library_work.db.component_asset_relations`, then `library_index.db.component_asset_relations` | `component_asset_relations.jsonl` is a compatibility/debug view |
| Browser model/animation list | `library_index.db` | Browser must not depend on `model_animations.json` |
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

`--postprocess-library <root> --sqlite-only-index` disables compatibility JSONL for large machine indexes where SQLite has an equivalent path. It currently routes full component relations through `library_work.db.component_asset_relations` and imports them into `library_index.db.component_asset_relations`.

The mode must preserve row counts and deterministic evidence. For example, when a source index contains one component relation, SQLite-only postprocess must produce:

- no `component_asset_relations.jsonl`
- one row in `library_work.db.component_asset_relations`
- one row in `library_index.db.component_asset_relations`

`--no-compat-json` is an alias for the same behavior.

## Migration state

- `UE5LibraryBrowser` already requires `library_index.db`.
- Main export now writes `export_events.db` alongside compatibility JSONL.
- Postprocess now reads `export_events.db` first for export manifest, asset catalog, animation bindings and auto referenced export diagnostics, then falls back to JSONL.
- Postprocess can now stream full component relations to `library_work.db` and skip `component_asset_relations.jsonl` in SQLite-only mode.
- Remaining JSON usage is mostly glTF/GLB structure editing, material JSON input, validation caches and human-readable reports.
