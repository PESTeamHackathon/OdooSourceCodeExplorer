# Milestone 2 — standalone source indexing

The indexer reads the separately maintained Odoo checkout directly. It does not copy or reference that checkout in this solution, run Python, import Odoo, evaluate manifests, compile target code, or call an AI service. Git and .NET 10 are the only prerequisites. No Copilot authentication is needed for indexing.

## Run a representative Odoo subset

From the solution directory:

```sh
dotnet build OdooSourceCodeExplorer.sln
dotnet run --project Chat.Indexer --no-build -- \
  --source /Users/sandra.ord/dev/odoo \
  --output /private/tmp/businesschat-odoo-m2-subset \
  --Indexing:IncludePaths:0 addons/sale \
  --Indexing:IncludePaths:1 addons/stock \
  --Indexing:IncludePaths:2 addons/sale_stock \
  --version odoo-poc-subset
```

For one directory or file, `--include` replaces the entire configured selection:

```sh
dotnet run --project Chat.Indexer -- \
  --source /Users/sandra.ord/dev/odoo \
  --include addons/stock/models/stock_picking.py \
  --output /private/tmp/businesschat-odoo-m2-single
```

These directories are temporary examples. For persistent artifacts omit `--output` to use the platform's local application-data folder under `BusinessChat/indexes`, or specify a protected external directory. Output inside either Git repository is rejected because it contains raw source evidence. Do not commit or expose generated indexes through the chat UI or public endpoints.

Running the same command again rebuilds from the current checkout into a new directory; existing generations are unchanged. There is no active-index switch, publication, deployment or rollback service in this milestone. A completed generation appears only after both artifact files have been written. Ctrl+C cancels with exit code 130; other failures return 1. Failed/canceled runs clean their private staging directory; a forcibly killed process may leave an unused `.building-*` directory. No completed artifact is emitted when the selection produces no chunks.

## Configuration

The existing `Chat.Indexer/appsettings.json` supplies the Odoo path and defaults. It is copied next to the executable, so the indexer can run from another working directory. Optional `--config /absolute/path/settings.json` overlays it; environment variables prefixed `BUSINESSCHAT_` and then command-line keys take precedence. `--include` is an explicit final replacement of IncludePaths. Standard .NET array configuration merges by numeric key; use `--include` when replacing a multi-entry selection with a single path.

```json
{
  "SourceRepository": {
    "Path": "/Users/sandra.ord/dev/odoo",
    "ApplicationVersion": null
  },
  "Indexing": {
    "IncludePaths": ["addons/sale", "addons/stock", "addons/sale_stock"],
    "MaxFileBytes": 2097152,
    "MaxChunkCharacters": 12000,
    "OutputPath": "/absolute/external/knowledge-indexes"
  }
}
```

Use `BUSINESSCHAT_SourceRepository__Path` for an environment override. `--source`, `--output` and `--version` are aliases for the corresponding keys; `--help` lists them. Include paths must be existing paths relative to the checkout root; directory selections are recursive, without glob patterns. `.` deliberately selects the whole repository. Paths escaping the root are rejected. Symlinked roots, selections and output ancestors are rejected; symlinked source files are recorded as skipped. The legacy `PreprocessorSymbols` setting in the starting configuration is unused: no C# preprocessing or Roslyn analysis is performed.

Only Git-tracked `.py` files are eligible. Untracked files are not indexed. XML, CSV, JS and other unsupported files are listed as skipped, as are missing, oversized, invalid UTF-8 and NUL-containing source files. This is intentionally a partial picture of Odoo: view rules, access control CSV files, and other non-Python behavior are not covered. Empty/whitespace-only Python files produce no chunks.

## Architecture and chunking

`Chat.App.Indexing` defines the provider-independent `SourceDocument`, `CodeChunk` and `ISourceChunker` contracts. `Chat.Indexer` depends on App; the web and Infrastructure projects do not reference Indexer. Adding a C# adapter later means implementing `ISourceChunker` and registering it with `RepositoryIndexer`; the artifact shape and runtime chat abstractions can stay independent of Roslyn.

`PythonChunker` uses a conservative lexical scanner based on indentation and logical lines. It masks comments and string literals and tracks bracket continuations before finding class/function declarations. See Python's [lexical specification](https://docs.python.org/3/reference/lexical_analysis.html). Decorators and multiline signatures remain with functions/methods. Nested functions stay inside their parent method; nested classes receive qualified names. Module-level imports/constants and class-level fields are preserved as separate context chunks rather than discarded. Chunk ranges cover nonblank source lines once, without overlapping whole-class copies.

Each method carries the enclosing class declaration and raw `_name`, `_inherit`, and `_description` assignments as context. These are evidence, not evaluated Python values. The nearest ancestor containing `__manifest__.py` identifies the Odoo module; module name and repository-relative module path are attached to each chunk. Manifests themselves are indexed as module chunks when selected. No dependencies are automatically added outside the requested include paths.

`MaxChunkCharacters` is a soft reporting threshold. Oversized methods/context blocks are retained whole and counted, so rules or exception paths are not silently truncated. `MaxFileBytes` is a hard file exclusion limit with a recorded reason. No arbitrary fixed-size splitting is performed.

This is not a full Python parser. It preserves source evidence but does not validate Python syntax, resolve imports/calls, interpret decorators, reconstruct dynamic ORM inheritance, or establish business workflows. Conditional definitions remain in surrounding source text and may lack separate symbol metadata. Future language constructs may require a stronger parser. No business summaries are fabricated during indexing.

## Artifact format and provenance

Each generation contains:

- `chunks.jsonl`: one code chunk per line, with deterministic content-based ID, language, relative path, module name/path, kind, symbol/qualified name, enclosing class, 1-based inclusive line range, class context, and exact source lines normalized to LF.
- `manifest.json`: schema/chunker versions, generation ID/time, repository path, HEAD commit, working-tree dirty flag, optional supplied application version, effective include paths/size limits, file/chunk/oversized counts, input SHA-256 hashes, chunks-file SHA-256, skip reasons and limitations.

Chunk IDs are deterministic for the same file path/range/content and algorithm version; generation IDs are unique. The indexer reads working-tree content, so dirty checkouts are allowed but clearly flagged and warned about. The commit is the provenance baseline, not a claim that dirty content belongs to that commit. Selected file hashes, HEAD and Git status are checked again before completing a generation; changes detected during indexing fail the run. For reproducible future CI jobs, use an isolated clean checkout. These checks do not lock a checkout against concurrent editors.

`ApplicationVersion` is an explicit label, not inferred from a filename or Python execution. Null means unknown. A later retrieval/runtime integration must validate the artifact schema, supported chunker, application-version compatibility and scope before consuming evidence. This milestone does not connect an index to chat or silently activate any generation.

## Tests

```sh
dotnet test OdooSourceCodeExplorer.sln
```

The xUnit suite covers conversation behavior plus index configuration, Python declarations/decorators/strings/multiline signatures/nesting, exact ranges and stable IDs, temporary Git provenance, Odoo module metadata, dirty reindexing, independent generations, scope validation, size exclusions, symlinks, and failure/cancellation cleanup. Synthetic fixture source is created outside this repository and is never executed.

Verified against the local Odoo checkout at commit `082a335a52b7594b86dcef7d027aa15929c338bb`: the three-module subset above produced **211 indexed Python files and 3,422 chunks**, with **570 unsupported files recorded as skipped** and **9 oversized chunks retained whole**. File/artifact hashes, unique IDs, exact line slices and nonblank-line coverage were checked against the checkout, including `SaleOrder.action_confirm`, `StockPicking.button_validate` and the `sale_stock` confirmation extension. The full solution builds with **zero warnings/errors** and **all 33 xUnit tests pass**. The checkout was clean; its code was neither executed nor compiled.

The source-indexing command still has no embedding or retrieval work. [Milestone 3](RETRIEVAL.md) adds explicit `build-search` and `search` commands that consume its completed artifacts independently. AI-generated descriptions and production services remain unimplemented.
