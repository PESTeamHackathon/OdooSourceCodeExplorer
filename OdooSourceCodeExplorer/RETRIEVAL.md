# Milestone 3 — local semantic and keyword retrieval

This milestone adds retrieval infrastructure and an internal console diagnostic command. **The chat UI, chat orchestration and Copilot prompt are unchanged.** No retrieval endpoint exposes raw code to business users. No paid model API, Copilot call, Roslyn, terminology dictionary, relationship expansion or production deployment is introduced.

## Start the required services

Install .NET 10, [Ollama](https://ollama.com/download), and either Docker with Compose or a native Qdrant binary. Run Ollama on its default loopback endpoint (skip `serve` if the Ollama app/service is already running):

```sh
ollama serve
```

In another terminal download the local embedding model:

```sh
ollama pull nomic-embed-text:v1.5
```

This is an approximately 274 MB embedding model, not a chat model. The initial download needs internet access; embedding requests go to the configured local Ollama service. See the [Ollama model page](https://ollama.com/library/nomic-embed-text) and [embedding API](https://docs.ollama.com/api/embed). The client uses `/api/embed` with batched input and `truncate: false` so overlong inputs fail explicitly.

Start Qdrant from the solution directory:

```sh
docker compose up -d qdrant
```

The provided `compose.yaml` pins Qdrant **1.19.2**, binds REST port 6333 to localhost, disables telemetry, and persists vectors in a Docker volume. Stop with `docker compose stop qdrant`; avoid deleting the volume if you want to keep searchable generations. SQLite is embedded in the .NET process and needs no server.

For native macOS Apple Silicon testing, this workspace instead used the [official Qdrant release](https://github.com/qdrant/qdrant/releases/tag/v1.19.2), extracted outside the repositories. Start it **from that external directory**, with a configuration such as:

```yaml
storage:
  storage_path: /absolute/external/qdrant/storage
  snapshots_path: /absolute/external/qdrant/snapshots
service:
  host: 127.0.0.1
  http_port: 6333
  grpc_port: 6334
telemetry_disabled: true
```

```sh
./qdrant --config-path /absolute/external/qdrant/local.yaml
```

Only one Qdrant instance should use port 6333. The native executable and data for this session are under `/private/tmp/businesschat-qdrant`; this is temporary test storage. No Docker installation was required for the completed local test.

## Build a searchable generation

First create a source index using [Milestone 2](INDEXING.md). Existing completed source indexes can be reused. For a small, representative Odoo test:

```sh
dotnet build OdooSourceCodeExplorer.sln
dotnet run --project Chat.Indexer --no-build -- \
  --source /Users/sandra.ord/dev/odoo \
  --output /private/tmp/businesschat-odoo-m3-source \
  --Indexing:IncludePaths:0 addons/sale/models/sale_order.py \
  --Indexing:IncludePaths:1 addons/stock/models/stock_picking.py \
  --Indexing:IncludePaths:2 addons/sale_stock/models/sale_order.py \
  --version odoo-poc-subset
```

Use the generation directory printed by that command:

```sh
dotnet run --project Chat.Indexer --no-build -- build-search \
  --artifact /absolute/source-generation \
  --search-output /absolute/external/search-indexes
```

This consumes `manifest.json` and `chunks.jsonl`, validates the source checksum/count/unique IDs, builds SQLite, generates embeddings, creates a uniquely named Qdrant collection, and writes `search-manifest.json`. The new search-generation directory is printed on completion. The source checkout is not needed by this step. It neither modifies the source artifact nor replaces earlier search generations.

The output folder must be outside both repositories and the source-generation directory; linked output ancestors are rejected. Each completed generation contains:

- `knowledge.sqlite`: original chunks with their complete metadata and source content, plus an FTS5 index.
- `search-manifest.json`: source index ID/commit/application version/dirty flag, chunk count, Qdrant collection and point count, embedding model/digest/dimensions, embedding-text format/windows, source checksum, and SQLite checksum.

Qdrant stores vectors plus chunk IDs/module names, while SQLite retains full source evidence. Keep the SQLite generation, its manifest, and its Qdrant collection together. Copying only SQLite does not reproduce semantic search.

Builds use private staging directories. No completed manifest is exposed until all vectors have been written with `wait=true` and verified. Failed/canceled builds clean their own staging directory and collection where possible; abrupt process termination/network failures may leave an unreferenced collection or `.building-*` directory. There is no automatic index activation, alias, CI/CD publication or production rollout implementation.

## Run retrieval diagnostics

```sh
dotnet run --project Chat.Indexer --no-build -- search \
  --index /absolute/search-generation \
  --question "How do I cancel a customer sales order?" \
  --module sale --limit 5 --expected-version odoo-poc-subset
```

Default `--mode hybrid` combines both retrieval branches. Inspect them separately with:

```sh
dotnet run --project Chat.Indexer --no-build -- search \
  --index /absolute/search-generation --mode semantic \
  --question "Which goods are ready to ship to the customer?" --module stock

dotnet run --project Chat.Indexer --no-build -- search \
  --index /absolute/search-generation --mode keyword \
  --question button_validate --module stock
```

`--module` is an optional exact Odoo module filter applied to both branches. `--limit` accepts 1–20; questions accept 1–4,000 characters. The command prints provenance, ranked symbols, paths/lines and the contributing ranks. `--format json` emits the full **internal diagnostic evidence**, including code; it is not a business-user API format. None of these searches reads the source repository or calls Copilot. Both services must be available because the CLI validates the selected generation even in single-branch diagnostic modes. Failures do not silently fall back to a different branch/index.

`--expected-version` rejects mismatching or unknown application versions. If omitted, no application-version compatibility assertion is made; provenance is still shown. Model-name/digest, format, dimensions, database checksum, Qdrant distance and point count are checked. A model update requires restoring the indexed digest or rebuilding; a same-dimensional incompatible model is not silently accepted.

## Configuration

The same JSON/environment/command-line configuration layering as the source indexer applies. Add settings under `Retrieval` in an external `--config` file, or use environment variables such as `BUSINESSCHAT_Retrieval__QdrantUrl`. Defaults:

```json
{
  "Retrieval": {
    "OllamaUrl": "http://localhost:11434/",
    "EmbeddingModel": "nomic-embed-text:v1.5",
    "QdrantUrl": "http://localhost:6333/",
    "BatchSize": 8,
    "WindowCharacters": 1800,
    "WindowOverlap": 200
  }
}
```

`Retrieval:IndexPath` selects a completed searchable generation (`--index`). `Retrieval:SourceIndexPath` supplies a source generation (`--artifact`). `Retrieval:OutputPath` controls build output (`--search-output`); its default is local application data under `BusinessChat/search-indexes`. `Retrieval:QdrantApiKey` optionally supplies the HTTP API key; use an environment variable or secret provider, not committed configuration. Ollama/Qdrant URLs must be absolute HTTP(S) URLs. Local endpoints are the PoC default; changing them determines where source-derived text/vectors are transmitted.

The default format uses Nomic's documented [document/query prefixes](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5). Changing models may require adapting that formatting and rebuilding. Batch size accepts 1–64, window size 128–4,000 characters, and overlap 0–half the window size. If a model rejects a window, reduce its size and rebuild; no text is silently truncated.

## Architecture and ranking

`Chat.App.Retrieval` contains `ICodeRetriever`, `IKnowledgeSearch`, internal evidence DTOs and `HybridCodeRetriever`. It references no AI/database/provider packages. Hybrid retrieval validates the request/version, asks the index adapter for semantic and keyword candidates, deduplicates chunk IDs, applies equal-weight reciprocal rank fusion (`1 / (60 + rank)` per branch), and loads full evidence. Raw cosine and BM25 scores are not compared directly.

`Chat.Infrastructure.Retrieval` implements Ollama/Qdrant HTTP clients, SQLite FTS5 storage, the search builder and `KnowledgeSearch`. `Chat.Indexer` provides the two explicit console commands. The only new runtime NuGet dependency is `Microsoft.Data.Sqlite` **10.0.13**; native FTS5 is included and used in tests. No EF Core, agent framework or extra vector client package is needed. HTTP usage was checked against the [Qdrant create API](https://api.qdrant.tech/api-reference/collections/create-collection), [upsert API](https://api.qdrant.tech/api-reference/points/upsert-points) and [query API](https://api.qdrant.tech/api-reference/search/query-points).

Embedding text includes module/symbol names, class context and code, with camel/snake identifiers separated into words. Long chunks are covered by overlapping embedding windows; each vector points back to the original intact chunk. Windows are deduplicated back into chunk candidates. SQLite indexes symbols and body/context text using FTS5/BM25, gives exact symbols precedence over incidental mentions, and treats user input as quoted tokens rather than executable FTS/SQL syntax. Common question words are omitted from keyword queries. This is simple hybrid retrieval, without hand-written business synonyms or AI-generated descriptions.

An opt-in `AddCodeRetrieval(configuration)` DI registration is available for a future host; **Chat.Web does not invoke it**. Retrieval consumes only deployed search artifacts and services. Hash/count checks are intentionally straightforward for the PoC and run per retrieval; larger indexes may need validated-generation caching later.

## Validation and limits

```sh
dotnet test OdooSourceCodeExplorer.sln
```

The tests exercise actual SQLite FTS5, fusion/deduplication/version rejection, unsafe query text, module filtering, exact identifiers, embedding-window tail coverage, Ollama request/response validation, Qdrant filtering, incompatible model/database rejection, and failed-build cleanup. HTTP contract tests use local handlers and do not require services or model downloads; Odoo smoke tests use the real services separately.

Verified locally: all **50 xUnit tests pass**. The three-file Odoo subset at commit `082a335a52b7594b86dcef7d027aa15929c338bb` produced **318 chunks and 369 vectors of 768 dimensions**, using real Ollama and Qdrant 1.19.2. The final tested generation is `/private/tmp/businesschat-odoo-m3-search/search-e7387c000e9f431dbd9f819c58a7cb73` (temporary test data). Native SQLite checksum and row count matched the manifest.

| Check | Observed result |
| --- | --- |
| Hybrid: “How do I cancel a customer sales order?” in `sale` | `SaleOrder.action_cancel` ranked first |
| Keyword: `button_validate` in `stock` | `StockPicking.button_validate` ranked first |
| Semantic: “Which goods are ready to ship to the customer?” in `stock` | Availability computation ranked second; transfer completion ranked third; first result was stock field/context evidence |
| Source repository configured to a nonexistent path during search | Retrieval succeeded from the search generation alone |
| Expected application version set to an incompatible value | Command rejected the request with exit code 1 |

These are retrieval smoke checks, not proof that complete business workflows can already be answered.

Semantic nearest neighbours can be irrelevant, and fusion scores are rankings rather than confidence or proof of a business rule. A small source selection may omit related workflows and all XML/CSV behavior. Dense window results are oversampled before chunk deduplication but may return fewer distinct chunks than the requested limit. The indexer still does not resolve dynamic Odoo inheritance/calls. Milestone 4 must enforce evidence sufficiency and business-oriented answer generation before exposing this through chat. Production authentication, index-access authorization and provider licensing remain separate future work.
