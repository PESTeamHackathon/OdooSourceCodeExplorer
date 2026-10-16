# Local chatbot backend selection

The Blazor chatbot selects one backend at startup using standard .NET configuration:

```json
{ "KnowledgeChat": { "UseAgenticExploration": false } }
```

`false` is the default and retains the existing RAG pipeline. `true` uses the existing bounded source explorer. There is no UI selector and no fallback between backends. Restart the web process after changing the flag; rebuild after code changes because the scripts use `--no-build`.

## Agentic demonstration

In the trusted `.env.local` (copy `.env.example` if needed), set:

```sh
KnowledgeChat__UseAgenticExploration=true
SourceRepository__Path='/absolute/path/to/odoo'
# Existing Copilot authentication, never committed:
COPILOT_GITHUB_TOKEN='your-token'
# Optional: the existing console exploration timeout, not a larger investigation budget.
Copilot__TimeoutSeconds=240
```

Then run from the solution directory:

```sh
dotnet build OdooSourceCodeExplorer.sln
bash scripts/run.sh
```

Open http://localhost:5112. This path needs .NET, Copilot authentication/network access and the source checkout on the **server/developer machine**. It does not require a prepared index, expected index version, Ollama, Qdrant, jq, curl or embedding preparation. `run.sh` loads/exports `.env.local` before starting the web host. Direct `dotnet run` does not parse dotenv files; export settings first:

```sh
set -a
source .env.local
set +a
dotnet run --project Chat.Web --launch-profile http
```

The web host's existing `Copilot:TimeoutSeconds` defaults to 120 seconds; the optional setting above uses the existing console demo's 240-second timeout. Operation/read/review limits are unchanged. Copilot reasoning is hosted; selected source evidence is transmitted to Copilot, and SDK session storage remains private in the configured profile.

## RAG demonstration

Set this in `.env.local`, stop the current web process with Ctrl+C, and restart:

```sh
KnowledgeChat__UseAgenticExploration=false
```

For initial preparation or an intentional rebuild of knowledge:

```sh
bash scripts/prepare.sh
```

For subsequent launches:

```sh
bash scripts/run.sh
```

RAG still requires the prepared state, matching application version and index files, Ollama/model and Qdrant. The script performs the original service startup, model check and retrieval validation. It does not read the source checkout at chat time. A direct host launch instead requires exported `Retrieval__IndexPath` and `KnowledgeChat__ExpectedApplicationVersion` plus the search services; see [CODE_AWARE_CHAT.md](CODE_AWARE_CHAT.md). See [DEMO.md](DEMO.md) for preparation and credentials.

## Shared implementation and answer completeness

`ChatService` remains scoped per Blazor circuit and owns input validation, attachments, history, overlap protection and atomic turn commits. It delegates agentic turns through the provider-independent `IConversationAnswerer` contract. Infrastructure's thin adapter calls the same `CopilotSourceExplorer` used by `scripts/explore.sh`, with conversation history. Every request creates fresh tools, evidence and budget; earlier assistant messages help resolve follow-ups but never become source evidence. Failures and cancellation do not commit turns.

The existing tool confinement and read-only allowlist are unchanged: approved Python/XML/CSV searches and reads only, no shell, editing, arbitrary filesystem tools or execution of Odoo code. No new packages, database detection or production infrastructure are added. Offline DI tests reference the installed ASP.NET shared framework.

Coverage distinguishes **source gaps** (unread implementations, unclear guards, downstream effects, or applicability conditions) from **runtime gaps** (which known optional modules/settings are active). Unknown source coverage requires a source gap and continues investigation when useful paths remain. Runtime gaps cannot request source actions. A general source question can finish when source coverage and conditional applicability are established, even while runtime configuration remains unknown. It receives conditional explanations and one caveat: “Enabled modules and settings were not checked.” A question about a particular running database remains limited by runtime uncertainty, which is stated explicitly. Applicability coverage is not automatically marked resolved: its source conditions still need evidence.

The coverage ledger, completion decision and final synthesis receive the same distinction in console and chat exploration. The shared tools-disabled answer generator is called once; only exploration requests receive the extra completeness guidance. RAG instructions, evidence validation and behavior are retained. A synthesis draft that identifies additional source uncertainty remains partial; it is never blindly upgraded by the host. Incomplete source investigations retain specific unresolved business questions and partial-answer handling.

## Verification and diagnostics

Run offline checks:

```sh
dotnet build OdooSourceCodeExplorer.sln
dotnet test OdooSourceCodeExplorer.sln
bash -n scripts/common.sh scripts/run.sh scripts/explore.sh
```

For a browser acceptance check, ask “What happens when I cancel a sales order?” followed by “What if a delivery is already completed?” in the same chat. Agentic start/finish summaries are server-side Information logs. For temporary internal search/range/coverage diagnostics set `Logging__LogLevel__Chat=Debug` in `.env.local` and restart; remove it afterward. These logs contain internal metadata, not raw source excerpts, provider errors or credentials; protect them. The business transcript contains no diagnostic trace. No diagnostic HTTP endpoint is added.

Console compatibility check:

```sh
bash scripts/explore.sh "What happens when I cancel a sales order?"
```

This command always explores source, independently of the web flag. Its stderr trace and stdout business answer remain separate. Source claims must be compared to the actual read evidence: activity counts and valid evidence IDs are not proof of semantic accuracy. Source-only analysis cannot verify deployed code, installed modules, database settings or dynamic extension order. The bounded investigation can still end partially, and the model can misinterpret a qualified rule.

### Results in this workspace

- Final solution build: **zero warnings and errors**. Full offline suite: **98 passed**, including all existing RAG tests and the new mode-selection, conversation and completeness cases. Shell syntax checks and `git diff --check` passed.
- Authenticated Blazor testing used a separate server on port 5113 and a private temporary environment file loading the existing `.env.local`; the actual `.env.local` and the existing server were left alone. The temporary demo server was stopped after verification.
- With the flag true, `run.sh` skipped prepared-index and Ollama/Qdrant checks. The first browser question completed with `supported` status after 4 searches and 12 registered reads. Its internal trace showed several coverage/continuation rounds, sales/delivery inheritance, stock cancellation, purchasing, loyalty, repair and manufacturing/subcontracting reads. The UI explained optional behavior conditionally and displayed the single configuration caveat, without a generic partial disclaimer.
- The same browser conversation then asked the completed-delivery follow-up. A separate exploration run received two prior messages and gathered fresh evidence. It read the sales/delivery filter, delivery and stock cancellation, warning-template and upstream-document implementations. It stopped at the six-review limit with specific remaining source questions about optional warning behavior and retained a partial answer. Unknown runtime configuration was not the reason for that partial status.
- Source comparison confirmed that the completed-delivery filter excludes finished transfers, and the lower-level completed-stock-movement guard has an inventory-destination exception. The browser answers retained that distinction. Repair cancellation was checked against the caller's exclusion of completed repairs and the called method's completed-repair guard, quantity update and parts cancellation. Purchasing warning recipients and upstream stock/manufacturing filters were also compared with the read source. This is a manual check of selected claims, not proof of every extension or all possible outcomes.
- `bash scripts/explore.sh "What happens when I cancel a sales order?"` still completed independently of the web flag. It returned partial after the investigation deadline, with specific manufacturing/subcontracting source gaps and the concise configuration caveat. One generated sentence blurred the repair caller's completed-record exclusion with the callee's exception: a linked completed repair is excluded by the inspected sales caller; the direct repair cancellation method rejects completed repairs. This remains an observed model interpretation limitation. Do not treat valid references or a `supported` review as semantic proof.
- After changing the temporary flag to false and restarting, `run.sh` performed its original Ollama, Qdrant, embedding-model and prepared-retrieval validation. The actual RAG Blazor interface answered the cancellation question from its existing index, with its normal partial-evidence handling. No external dependency prevented these live checks.
