# Local demo — separate preparation and runtime (macOS)

Knowledge preparation is an independent, relatively expensive operation. Starting chat reuses its completed knowledge generation and never indexes source or generates document embeddings. Ollama remains running for **query embeddings**. Copilot receives selected evidence and the conversation for hosted reasoning; this is not an offline chatbot.

## One-time configuration

Install .NET 10 SDK, Git, `jq`, `curl`, Ollama, and either Docker Desktop with Compose or native Qdrant. The scripts use macOS Bash 3.2 (even when launched from zsh). They install no software automatically; preparation can download the configured embedding model through Ollama. No new NuGet dependencies or C# changes were needed.

From the solution directory:

```sh
cp .env.example .env.local
chmod 600 .env.local
```

Edit `.env.local` locally. Supply `COPILOT_GITHUB_TOKEN` with the **Copilot Requests** account permission, the separate `SourceRepository__Path`, and an explicit `APPLICATION_VERSION` label. Do not paste tokens into chat or commit them. See [README authentication instructions](README.md#prerequisites-and-authentication) for account/token prerequisites. The scripts require a token; the application's separate interactive-login option remains available through the manual commands in README. `GH_TOKEN`/`GITHUB_TOKEN` are fallback token variables matching the existing adapter. The scripts check configuration presence/whitespace, not account entitlement, expiry or quota; Copilot validates those when a question is sent.

The file is a **trusted Bash configuration file**, loaded and exported without tracing. Quote values with spaces; do not place commands in it or load one supplied by an untrusted repository. Tokens containing shell-special characters should be single-quoted. `.env.local` is already excluded by `.gitignore`; `.env.example` contains no credentials. Do not use `bash -v`, `env`, `printenv`, or debugging wrappers that print secrets. An optional `BUSINESSCHAT_ENV_FILE=/absolute/private/config.env` selects another local configuration profile.

Configure source selection with a simple Bash array:

```sh
SourceRepository__Path='/Users/your-name/dev/odoo'
APPLICATION_VERSION='odoo-poc-subset'
INDEX_PATHS=(
  'addons/sale/models/sale_order.py'
  'addons/stock/models/stock_picking.py'
  'addons/sale_stock/models/sale_order.py'
  # Or select whole directories recursively:
  # 'addons/sale'
  # 'addons/sale_stock'
)
DEMO_ROOT="$HOME/BusinessChatDemo"
```

Paths are relative to the external checkout. Directories recursively include **tracked Python files**; no globs or numbered .NET arguments are needed. XML/CSV/JavaScript, untracked files and symlinks remain excluded by the existing indexer. Keep source stable during preparation. It is analyzed as data and never compiled or executed. The version label is supplied by you, not inferred as an Odoo release.

`DEMO_ROOT` must be outside both repositories. It holds private `source-indexes/`, `search-indexes/`, `indexing-config.json`, `prepared.json`, and service logs. Generated artifacts inherit owner-only permissions. Prefer a durable directory under your home rather than `/tmp`. Keep the complete search generation, its matching Qdrant storage and the exact embedding model together; these are proprietary assets. The generated settings contain paths, fingerprints, commit/version/dirty metadata, never tokens.

## Preparation: create or refresh knowledge

```sh
./scripts/prepare.sh
```

Preparation:

1. Loads local configuration, checks prerequisites and builds the existing solution.
2. Reuses healthy Ollama/Qdrant instances, starting missing local services only.
3. Checks the exact embedding model (`nomic-embed-text:v1.5` by default), downloading it if missing and `PULL_EMBEDDING_MODEL=1`.
4. Converts `INDEX_PATHS` into private JSON configuration and runs the existing source indexer when required.
5. Runs the existing search builder for SQLite FTS5 plus Ollama embeddings/Qdrant vectors when required.
6. Runs hybrid retrieval with the expected version. Existing validation checks schema, database checksum, model digest/dimensions and Qdrant collection/count; the script additionally requires a nonempty result.
7. Atomically saves completed generation paths, version, Git commit, dirty flag and reuse fingerprints in `prepared.json` after validation succeeds. No manual path copying is needed.

Rerunning preparation rebuilds the solution incrementally but **reuses unchanged source and searchable generations**. Its source fingerprint includes checkout location, commit, Git-tracked working-tree differences/status, scope, version, default indexing settings and built analyzer/application assemblies. This is deliberately conservative: unrelated tracked changes can invalidate reuse. Search reuse additionally considers infrastructure assembly, model name, endpoints, and live compatibility validation. A changed model digest, missing collection or damaged SQLite triggers rebuilding the search generation from reusable source artifacts. Source indexing is repeated when source inputs change. All old completed generations remain available; failed preparation does not replace the previously selected state.

The retrieval smoke question defaults to “Can I cancel a locked sales order?” Set `RETRIEVAL_TEST_QUESTION` to something meaningful for a different scope. Nonempty results confirm service/index operation, not semantic relevance or complete workflow coverage. Review the existing `Chat.Indexer search` diagnostic manually when changing scope; its source evidence is for developers only. See [INDEXING.md](INDEXING.md) and [RETRIEVAL.md](RETRIEVAL.md) for manual commands and limits.

## Runtime: start repeatedly without reindexing

```sh
./scripts/run.sh
```

Open [http://localhost:5112](http://localhost:5112). Optional `WEB_URL='http://localhost:5113'` selects another local port. Stop the web server with Ctrl+C, then rerun the same script whenever needed.

Runtime loads credentials, checks that built applications and `prepared.json` exist, starts missing services, verifies the model and validates retrieval from the existing generation. It automatically exports `Retrieval__IndexPath` and `KnowledgeChat__ExpectedApplicationVersion` from prepared state. If `.env.local` specifies `APPLICATION_VERSION`, it must match. **It does not inspect the source path, run Git against Odoo, build the solution, download models, run source indexing, generate document embeddings, or rebuild SQLite/Qdrant.** The Odoo checkout can be absent. Validation performs a query embedding, as normal chat retrieval does.

Missing/invalid artifacts, incompatible versions, model changes or unavailable collections produce an error directing you to preparation; runtime never repairs knowledge by rebuilding it. Changing source scope only takes effect after running preparation. A missing local service executable/Docker daemon must be installed/started before retrying. Starting chat requires .NET, `jq`, `curl`, `shasum`, the prepared files, matching local services and credentials; Git and the source checkout are preparation-only requirements.

## Service lifecycle and storage

Default local URLs are configured in `.env.example`. Scripts check HTTP readiness before starting a service and serialize service startup to avoid duplicate processes. They leave existing healthy services alone. Ollama starts with `nohup ollama serve`, with a private log under `DEMO_ROOT/logs`. Native Qdrant also runs independently in the background. Do not start another instance manually on the same port.

By default, missing Qdrant starts through the existing `docker compose up -d qdrant`, with the pinned image, loopback binding and persistent named Docker volume from `compose.yaml`. Docker Desktop must already be running. To use native Qdrant instead, configure all three absolute paths:

```sh
QDRANT_BINARY='/absolute/path/to/qdrant'
QDRANT_CONFIG='/absolute/path/to/local.yaml'
QDRANT_WORKDIR='/absolute/path/to/qdrant-data-directory'
```

Use [the native configuration example](RETRIEVAL.md#start-the-required-services) with durable storage paths and loopback binding. The scripts do not download native executables. URLs are restricted to local HTTP endpoints with explicit ports; Docker Compose uses port 6333. A healthy service is reused regardless of whether it was started manually, through Docker or by the scripts.

Stopping chat leaves services/data intact. Stop Docker Qdrant with `docker compose stop qdrant`; do not remove its volume if you want to reuse indexes. Stop native services using their usual process/service management. Scripts never delete completed generations or Qdrant collections from previous runs. If a script was killed abruptly, remove its `prepare.lock` or `service-start.lock` directory only after confirming no corresponding script is running.

## Test the chatbot

Enter this question and click **Send**:

> If I select several sales orders for cancellation and just one is locked, are the unlocked orders cancelled or is the whole action blocked?

At the previously tested Odoo commit (`082a335a52b7594b86dcef7d027aa15929c338bb`), the expected substance is: the whole action is blocked when any selected order is locked; unlock it before retrying. Recheck this expectation if your source differs. The answer should use business language and omit code, file paths, and internal evidence IDs.

Then ask:

> Summarize that in one sentence.

The answer should retain the locked-order condition. To demonstrate evidence limits, ask:

> Does cancellation always refund the customer automatically?

The available evidence should not establish an automatic refund. Expect an explicit limitation instead of an invented refund rule.

For an optional broader demonstration, ask:

> For an unlocked sales order, what does cancellation do to draft invoices and unfinished deliveries? Explain only the verified steps and acknowledge any missing downstream details.

Broader workflow coverage is not consistently reliable with this small selection. A partial answer or safe refusal can be appropriate. See [CODE_AWARE_CHAT.md](CODE_AWARE_CHAT.md#a-source-specific-acceptance-question) for the source-specific acceptance criteria and previously observed limitations. Rehearse the exact questions against your generated index before presenting; successful build/tests alone do not verify live Copilot answers.

## Verify or troubleshoot

```sh
bash -n scripts/common.sh scripts/prepare.sh scripts/run.sh scripts/test-workflows.sh
dotnet build OdooSourceCodeExplorer.sln
dotnet test OdooSourceCodeExplorer.sln
./scripts/test-workflows.sh
```

The offline script tests cover credentials, missing/invalid knowledge, empty results, version mismatch, source-independent runtime, no runtime builds/indexing, secret-safe messages, and service startup/reuse. They use mock service/process boundaries and make no paid AI calls. They require the existing applications to have been built.

Verification completed with a zero-warning/error solution build and **64 passing xUnit tests**, plus passing offline script checks. Real-service verification exercised preparation against three Odoo source files plus the recursive `addons/sale_stock/report` directory (330 chunks), then repeated preparation and confirmed both generation paths were reused without document embeddings. Runtime retrieval/startup succeeded with the source path set to a nonexistent directory, and the actual Blazor interface loaded on port 5113. That startup smoke test used a dummy credential and sent no Copilot questions: authenticated Copilot response verification still requires a valid token and a real UI question. See [CODE_AWARE_CHAT.md](CODE_AWARE_CHAT.md) for previously tested answers and remaining code-aware chat limitations.

| Failure | Action |
| --- | --- |
| Missing config or token | Configure `.env.local`; never print credential values. |
| Missing build/state/index, version mismatch | Run `./scripts/prepare.sh` with the desired scope/version. |
| Ollama model missing/changed | Run preparation, or restore the exact indexed model. |
| Qdrant unavailable | Start Docker Desktop or configure native paths; check private service logs. |
| Qdrant collection/SQLite incompatible | Restore matching storage/artifacts, or run preparation to build a new generation. |
| Retrieval succeeds but chat fails | Check Copilot account permission, token expiry, model access and usage limits. |
| Partial answer | Review indexed scope and the acceptance checks; successful retrieval does not prove complete workflow evidence. |

These scripts are local developer workflows, not production deployment or authentication tooling.
