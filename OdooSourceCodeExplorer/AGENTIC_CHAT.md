# Thinking toggle and local source exploration

The Blazor chat has a **Thinking** switch beside Send. Off uses the version-checked indexed RAG pipeline; on uses the bounded live source explorer. Both ground application claims in evidence and acknowledge missing/conflicting information. Thinking investigates related workflows and usually takes longer; it can still return a partial answer or misinterpret evidence. It does not guarantee a complete or more accurate answer.

The selection stays within the conversation, including follow-ups and attachments. Each send captures the selection; the switch, input, attachments and Send are disabled while busy. Cancel leaves the pending question and attachments available for retry without committing an incomplete turn. Busy messages describe answering or investigation without internal tool details. Reloading loses the selection and conversation. An unavailable choice is disabled with a safe explanation; the server also rejects unavailable/invalid modes and never falls back.

## Configuration and migration

Availability and default selection are separate server settings under `KnowledgeChat`:

| Setting | Meaning |
| --- | --- |
| `IndexedAnswersEnabled` | Enables the existing RAG backend and its dependencies. |
| `ExplorationEnabled` | Enables bounded source exploration and requires an existing absolute `SourceRepository:Path` on the server. |
| `DefaultMode` | `Indexed` (Thinking off) or `Exploration` (Thinking on); must select an enabled backend. |
| `UseAgenticExploration` | Legacy defaults only: absent/false means indexed only, default Indexed; true means exploration only, default Exploration. |

Each new setting overrides its corresponding legacy-derived default independently. With no settings the switch is off and only indexed answers are enabled. Explicitly enabling exploration does not change the default. To migrate from legacy true to dual mode with Thinking off, set all three new settings below. Existing legacy true/false single-backend launches continue to work; no shared configuration changes when a user switches modes. Invalid defaults, retrieval limits/URLs or missing exploration paths fail startup; index/service compatibility is also checked at request time. Availability describes configured capabilities, not live service health; request failures remain safe and retryable.

For **dual mode**, add to the trusted `.env.local`:

```sh
KnowledgeChat__IndexedAnswersEnabled=true
KnowledgeChat__ExplorationEnabled=true
KnowledgeChat__DefaultMode=Indexed
SourceRepository__Path='/absolute/path/to/odoo'
# Optional existing provider timeout (10–600 seconds):
# Copilot__TimeoutSeconds=240
```

Run `bash scripts/prepare.sh` once if indexed knowledge is not already prepared, then `dotnet build OdooSourceCodeExplorer.sln` and `bash scripts/run.sh`. Preparation is an explicit developer action, never performed by sending a question or by `run.sh`. Dual mode requires the prepared index/version, Ollama/model, Qdrant, source checkout, and Copilot authentication/network access. `run.sh` checks both dependency sets and starts the existing search services when required. Stop/restart for configuration changes; scripts use `--no-build`. Direct `dotnet run` reads standard .NET configuration, not `.env.local`; export settings and supply `Retrieval__IndexPath` and `KnowledgeChat__ExpectedApplicationVersion` yourself.

Explicit single-backend configurations are supported: indexed only uses `IndexedAnswersEnabled=true`, `ExplorationEnabled=false`, `DefaultMode=Indexed`; exploration only uses `false`, `true`, `Exploration` respectively. Remove any new overrides when using the legacy examples below. Exploration-only launch skips index/search-service prerequisites; indexed-only chat does not need a source checkout. The standalone `scripts/explore.sh` command always explores source independently of these web settings.

## Version and provenance limitation

Indexed answering reads the configured completed generation and checks its expected application-version label on every request. Its trace retains generation, commit and dirty metadata. Exploration reads the **current live checkout** and retains the existing `unverified-live-checkout` provenance marker; it does not verify Git identity or a stable snapshot. It must not inherit the index's commit/version label. Switching modes preserves conversational context, not source-version equivalence; previous assistant messages are context, never fresh evidence.

Before manually comparing modes, require the same application version and verify the checkout HEAD against the selected `search-manifest.json` `info.gitCommit` plus the source generation's `manifest.json`. Check working-tree status and the recorded input hashes as well: a matching commit alone does not establish equality for modified inputs. Prefer a clean checkout and an index built from that clean commit, keep the checkout unchanged during comparison, and account for the indexed scope (exploration can inspect additional files). If they do not match, do not treat different answers as a comparison of the same application snapshot. No automatic index rebuilding, checkout pinning or production version-management system is added.

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

`ChatMode` and immutable `ChatCapabilities` live in Chat.App. The scoped `ChatService` routes each request to a stateless `IConversationAnswerer`; `IndexedConversationAnswerer` retains the existing retriever/generator/version/evidence policy, and `AgenticConversationAnswerer` retains the existing explorer. There is one transcript and no second stateful chat service.

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

### Thinking toggle verification (2026-10-16)

The solution build completed with zero warnings/errors. Modified scripts passed `bash -n` and `git diff --check` passed. Only the requested existing startup-selection test source was updated for legacy defaults and explicit overrides; no automated tests were run for this task.

A temporary dual-mode `scripts/run.sh` launch on localhost:5113 used existing credentials without changing `.env.local`. Both dependency sets passed startup checks. In the actual Blazor interface, Thinking started off; the indexed question returned the safe insufficient-evidence answer. Switching on for “What if I unlock it first?” kept the earlier question and answer. Busy controls were disabled with the investigation message. Cancellation preserved the transcript and editable question; retry kept Thinking selected and passed the same two prior messages to exploration. Switching back off for a further follow-up used indexed answering in the same transcript and again acknowledged insufficient evidence. The indexed-only UI disabled Thinking and displayed the safe availability explanation. Exploration-only launch skipped all index/search checks and started with Thinking on, disabled, explaining that quick answers were unavailable. The final responsive layout was visually checked. Temporary web servers were stopped after verification. The retried exploration completed with a partial business answer and explicit unresolved questions/runtime caveat. These observations verify routing and UI behavior, not the accuracy of every generated claim. Source/index baseline commits matched, but input hashes and working-tree equality were not established, so this was not a controlled snapshot comparison. Attachment handling remains the existing implementation; file uploads were not separately exercised in this check.

### Historical results before the Thinking toggle

These checks describe the previous startup-only implementation, not verification of the current toggle.

- Final solution build: **zero warnings and errors**. Full offline suite: **98 passed**, including all existing RAG tests and the new mode-selection, conversation and completeness cases. Shell syntax checks and `git diff --check` passed.
- Authenticated Blazor testing used a separate server on port 5113 and a private temporary environment file loading the existing `.env.local`; the actual `.env.local` and the existing server were left alone. The temporary demo server was stopped after verification.
- With the flag true, `run.sh` skipped prepared-index and Ollama/Qdrant checks. The first browser question completed with `supported` status after 4 searches and 12 registered reads. Its internal trace showed several coverage/continuation rounds, sales/delivery inheritance, stock cancellation, purchasing, loyalty, repair and manufacturing/subcontracting reads. The UI explained optional behavior conditionally and displayed the single configuration caveat, without a generic partial disclaimer.
- The same browser conversation then asked the completed-delivery follow-up. A separate exploration run received two prior messages and gathered fresh evidence. It read the sales/delivery filter, delivery and stock cancellation, warning-template and upstream-document implementations. It stopped at the six-review limit with specific remaining source questions about optional warning behavior and retained a partial answer. Unknown runtime configuration was not the reason for that partial status.
- Source comparison confirmed that the completed-delivery filter excludes finished transfers, and the lower-level completed-stock-movement guard has an inventory-destination exception. The browser answers retained that distinction. Repair cancellation was checked against the caller's exclusion of completed repairs and the called method's completed-repair guard, quantity update and parts cancellation. Purchasing warning recipients and upstream stock/manufacturing filters were also compared with the read source. This is a manual check of selected claims, not proof of every extension or all possible outcomes.
- `bash scripts/explore.sh "What happens when I cancel a sales order?"` still completed independently of the web flag. It returned partial after the investigation deadline, with specific manufacturing/subcontracting source gaps and the concise configuration caveat. One generated sentence blurred the repair caller's completed-record exclusion with the callee's exception: a linked completed repair is excluded by the inspected sales caller; the direct repair cancellation method rejects completed repairs. This remains an observed model interpretation limitation. Do not treat valid references or a `supported` review as semantic proof.
- After changing the temporary flag to false and restarting, `run.sh` performed its original Ollama, Qdrant, embedding-model and prepared-retrieval validation. The actual RAG Blazor interface answered the cancellation question from its existing index, with its normal partial-evidence handling. No external dependency prevented these live checks.
