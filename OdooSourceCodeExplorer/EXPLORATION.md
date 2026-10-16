# Hackathon: isolated agentic Odoo exploration

This developer-only experiment uses the existing GitHub Copilot SDK **1.0.22** to investigate the local Odoo checkout. The console command remains independent of RAG. The Blazor chatbot can now opt into the same explorer through a backend flag; see [AGENTIC_CHAT.md](AGENTIC_CHAT.md). Indexing commands and the default RAG path are unchanged. No new packages, services, vector index or embedding model are required.

## Run

Build once from the solution directory:

```sh
dotnet build OdooSourceCodeExplorer.sln
dotnet test OdooSourceCodeExplorer.sln
```

Use the existing `.env.local` with `SourceRepository__Path` and `COPILOT_GITHUB_TOKEN`, then:

```sh
bash scripts/explore.sh
bash scripts/explore.sh "Can a locked sales order be cancelled, and what happens to its invoices and deliveries?"
```

The script loads only trusted developer configuration and runs the already built console command. It does not start Ollama/Qdrant, prepare an index or change the web application's mode. An optional `Copilot__Model` in `.env.local` selects the exploration model. The default timeout is 240 seconds. Ctrl+C cancels the investigation.

Alternatively, with Copilot authentication already exported or the existing SDK profile logged in (see README):

```sh
dotnet run --project Chat.Indexer --no-build -- explore \
  --source /absolute/path/to/odoo \
  --question "What happens when I cancel an unlocked sales order with draft and posted invoices, plus completed and unfinished deliveries?"
```

`--source` maps to `SourceRepository:Path`. The existing console configuration layers still apply: appsettings, optional `--config`, `BUSINESSCHAT_` environment variables, then command-line options. For example, use `BUSINESSCHAT_SourceRepository__Path`, `BUSINESSCHAT_Copilot__Model`, `BUSINESSCHAT_Copilot__BaseDirectory`, or `--Copilot:TimeoutSeconds 360`. Authentication uses the same token precedence and default `~/.copilot-business-chat` profile as chat. Hosted Copilot calls consume the developer's allowance and transmit the selected source evidence.

## What happens

The model chooses successive searches and source reads using the SDK's agent loop. It receives two custom tools:

- `search_source`: case-insensitive literal search, optionally scoped to a relative directory/file. At most 30 matches, with line numbers and short discovery snippets; an explicit flag indicates more matches.
- `read_source`: reads 1–200 lines from an approved file, up to 24,000 characters per result, and assigns an internal evidence ID. `nextLine` allows continued reads without silently truncating evidence.

Investigation runs without a response schema so the model can make successive source tool calls. After each investigation turn, a **separate tools-disabled coverage review** checks the actual accumulated read evidence against the question. The review records six coverage areas: entry points, blockers, inherited extensions, downstream effects, alternative outcomes, and module/configuration applicability. Resolved findings must reference registered evidence IDs; unknown areas require explicit material questions, proposed targeted source operations, and whether a useful source path remains. Each investigable gap also carries a structured next search or read (term/scope or path/range).

Material gaps with useful source paths trigger a continuation in the original investigation session. The previous review and question are supplied as data, and all earlier evidence stays available. The reviewer sees the full accumulated evidence and discovery activity, including search terms/scopes and truncation flags. A base implementation alone is insufficient when relevant overrides or called business operations remain unread. There is no minimum file or tool-call count, and no workflow-specific cancellation logic.

There is one per-run budget: **40 operations shared by investigation turns, coverage reviews, source tool attempts, and final synthesis**, plus **180,000 read characters**, a maximum of six coverage reviews, and one total timeout (default 240 seconds). Source tools reserve operations for a final review and synthesis. Duplicate searches (case-insensitive term, same case-sensitive scope), identical read attempts, and ranges wholly covered by earlier excerpts are suppressed; attempts still consume operations. If a continuation adds no new searches or evidence, the controller attempts a concrete remaining gap action through the same bounded source tools before accepting the stall. Search results remain discovery hints, supplied to the next investigator/reviewer; they never become evidence until read. Already attempted actions are skipped. If neither the continuation nor its targeted paths add information, investigation stops. New searches with no matches can eliminate useful paths but do not prove absence of behavior. A read-character limit, exhausted useful paths, operation limit, or review limit also stops investigation.

The first 75% of the total timeout is available for investigation/review; the remaining time is reserved for partial synthesis. Caller cancellation aborts work without publishing an answer. If investigation times out, the latest completed review and all successful reads are retained; synthesis can still fail if its remaining deadline or provider access is exhausted. No retry resets the budget.

A final tools-disabled SDK session uses the **existing `CopilotAnswerGenerator` unchanged** to write the business answer solely from the question and collected read evidence. Investigator summaries and reviewer findings are never promoted to source evidence. The existing `BusinessAnswerPolicy` validates final claim references and business language. A review with material source gaps cannot be upgraded to supported by synthesis. Reviews now distinguish source behavior gaps from unknown runtime configuration. For general source questions, established source behavior and enabling conditions can be complete even while installed modules/settings remain unknown; optional effects are explained conditionally. Database-specific questions retain explicit runtime uncertainty. Source applicability conditions still require evidence; an unread condition or implementation is never reclassified as a runtime gap merely to finish. Reviewed unresolved questions are included separately in plain business language (subject to the same implementation-detail filter), alongside the existing partial disclaimer. The host adds one concise caveat: “Enabled modules and settings were not checked.” The final generator receives exploration-only completeness guidance; RAG instructions and the shared evidence validation policy remain unchanged.

Business instructions retain evidence-only ordered explanations, conditions, status changes and exceptions, with no invented refunds or downstream effects. Source text and model review data are untrusted. Search snippets cannot be cited: final claims must reference evidence registered by successful reads. Reference validation checks membership and presentation, not whether the model's interpretation logically follows from the source.

The business answer is printed to stdout. Developer diagnostics on stderr show search terms and scopes, match counts/truncation, registered source ranges, material business questions that trigger continuation, and the reason investigation stopped. They omit raw source excerpts, technical review findings, credentials, and provider errors. Tool counts establish activity, not completeness or correctness. Protect these diagnostics as internal metadata.

## Compare with RAG

1. Run `bash scripts/explore.sh` and keep its answer plus developer file trace.
2. Start the existing RAG chatbot normally with `bash scripts/run.sh` (or the README's manual setup).
3. Ask the identical cancellation question in a new chat. Use the same Copilot model where practical.
4. Compare lock checks, draft versus posted invoices, completed versus unfinished deliveries, downstream cancellation effects, and acknowledged gaps.

The current demo RAG index may cover only three files; exploration can read the whole approved checkout. A fuller answer can result from broader source coverage as well as agentic investigation. RAG uses a fixed indexed snapshot; this experiment reads live working-tree files, including local/untracked source. Align the checkout/index versions for a meaningful comparison. This is a manual demonstration, not a benchmark.

## Deliberate limits and safety boundary

Only `.py`, `.xml` and `.csv` files under the configured root are catalogued. Hidden paths, linked files/directories and common generated directories are excluded; root ancestors and each read are checked for symbolic links. Absolute paths, parent traversal and backslash paths are rejected. Files over 2 MiB, invalid UTF-8 and NUL-containing data are not returned. The root must be an existing absolute directory, not a filesystem root.

No SDK built-in, shell, network, editing, execution, MCP, skill or plugin tools are exposed. Empty client mode plus a source-qualified allowlist exposes only the two custom tools; the permission callback approves only those tool names and rejects managed-approval requirements. Setting a working directory alone is not used as a filesystem sandbox. The custom tools use .NET file reads and never execute Odoo/Python code or Git hooks. SDK credential/session storage remains in its own existing profile outside Odoo.

An investigation uses the shared operation, source-character, review and time limits described above. This is a local PoC: do not concurrently replace source paths while it runs. The path checks are not an OS sandbox against a malicious local process racing filesystem changes. No immutable snapshot, installed-module validation, workflow graph, follow-up conversation store or production authorization is implemented. Runtime Odoo configuration and dynamic inheritance can leave uncertainty even after reading multiple files. SDK sessions may persist source evidence in the configured profile; protect it as before.

SDK capabilities were checked against the installed package documentation and [official versioned .NET documentation](https://raw.githubusercontent.com/github/copilot-sdk/v1.0.22/dotnet/README.md). Custom tools keep repository path restrictions explicit without granting general-purpose filesystem tools.

## Earlier baseline verification

The solution built with zero warnings/errors and all 74 offline tests passed, including 10 exploration-tool cases covering multi-file evidence, traversal/absolute-path rejection, scope/literal search, symlink rejection and rechecking, invalid ranges, oversized lines, a shared tool-call budget, and cancellation.

An authenticated run of the default cancellation question completed with **2 searches, 2 reads and 2 files read**: `addons/sale/models/sale_order.py` (1740–1839) and `addons/sale_stock/models/sale_order.py` (230–259). It returned a business-language explanation covering the lock restriction, draft versus posted invoices, unfinished versus completed deliveries, and order cancellation. The trace demonstrates real multi-step source access. This run did not read the full stock/accounting cancellation implementations; it does not establish exhaustive downstream workflow coverage or prove every generated claim. Model behavior and tool counts can vary on later runs.

## Review-and-continuation verification

Before chatbot integration, the solution build completed with zero warnings/errors, and all **91 offline tests** passed. Current integration verification is recorded in [AGENTIC_CHAT.md](AGENTIC_CHAT.md). Offline tests exercise the production loop with scripted investigation/review callbacks and real bounded reads of temporary Python fixtures. They cover targeted continuation, retaining later evidence for final reference validation, sufficient-coverage stopping without minimum counts, controller recovery from stalled continuations with targeted reads/searches, suppression of duplicate/covered reads and searches, no-progress stopping, exhausted paths and specific business gaps, cancellation, invalid review references, shared operation/read limits, review limits, and diagnostics without source excerpts. These tests do not prove that a live model correctly identifies every material gap.

For live acceptance, run the original broad question:

```sh
bash scripts/explore.sh "What happens when I cancel a sales order?"
```

Assess the actual read ranges for entry points and lock guards, inherited delivery extensions, draft versus posted invoices, and the implementations of relevant invoice/delivery/stock cancellation operations. Check conditions, exceptions, alternatives, and uncertain module applicability against the source. A longer trace alone is not a passing result. Record the observed stop reason and unresolved business questions; avoid assuming that source-only analysis identifies installed modules or deployed settings.

### Authenticated assessment of the revised explorer

The original question completed against the local checkout with **5 search attempts, 14 registered read excerpts, and 11 distinct files**. It continued through coverage reviews instead of stopping after the base sales-order method. The trace included base sales cancellation, the sales/delivery extension, invoice cancellation, delivery cancellation, stock-movement cancellation, and purchasing, loyalty, repair, manufacturing and subcontracting extensions. Investigation stopped at its time limit with 31 shared operations consumed before final synthesis, and returned a partial answer with specific unresolved questions about further delivery extensions, repair-related lines, purchaser messaging and runtime applicability.

Comparing the excerpts with the local source confirms the draft-invoice selection, invoice state/payment updates, exclusion of completed deliveries from the selected cancellation path, and stock cancellation's conditions and propagation branches. Coverage remains incomplete: the reservation-release helper, repair-order cancellation implementation, manufacturing-order cancellation implementation, and purchaser message template were not all followed. Module installation, configuration and extension execution order were not verified.

The generated answer also **overgeneralized the completed-stock-movement restriction**: the read guard includes an inventory-destination exception, whereas the answer described a blanket prohibition. It stated a repair cancellation outcome after reading its caller but without reading the repair cancellation implementation. These are remaining model interpretation limitations, not verified end-to-end business behavior. Reference membership and coverage review are not semantic proof; this run should not be treated as a fully validated workflow answer. The controller's stalled-investigation fallback was verified by offline tests; this live run made autonomous progress until the time limit and did not separately establish that fallback's live behavior.

Earlier development runs exposed incomplete coverage arrays and a model continuation that stopped despite concrete remaining paths. The revised schema requires named coverage fields on every review; the controller now attempts structured gap actions when a continuation stalls. Invalid review references/shape still fail safely rather than publishing an unverified review as supported.
