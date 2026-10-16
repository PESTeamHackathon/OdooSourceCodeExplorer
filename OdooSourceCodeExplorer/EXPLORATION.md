# Hackathon: isolated agentic Odoo exploration

This developer-only experiment uses the existing GitHub Copilot SDK **1.0.22** to investigate the local Odoo checkout. The Blazor chatbot, RAG adapter, indexing commands and retrieval configuration are unchanged. No new packages, services, vector index or embedding model are required.

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

Investigation runs without a response schema so the model can perform successive tool calls and then stop naturally. A second, tools-disabled SDK session uses the **existing `CopilotAnswerGenerator` unchanged** to write the answer from the question and the collected read evidence. The investigation's free-form summary is not promoted to evidence. This adds one synthesis request but preserves the current business instructions exactly and avoids mixing structured final formatting with tool investigation.

Business instructions preserve the current evidence-only behavior: ordered steps, conditions, status changes, exceptions, no invented refunds or downstream effects, and explicit insufficient/partial/conflicting outcomes. Source text is untrusted data. Search snippets cannot be cited: final claims must reference evidence registered by successful reads. The existing `BusinessAnswerPolicy` validates references and renders the answer. No source excerpts or internal citations are included in the answer.

The business answer is printed to stdout. Developer diagnostics on stderr show tool calls, search/read counts and source file ranges. A useful demonstration should show several searches/reads and more than one file. These diagnostics prove files were accessed, not that every interpretation is correct.

## Compare with RAG

1. Run `bash scripts/explore.sh` and keep its answer plus developer file trace.
2. Start the existing RAG chatbot normally with `bash scripts/run.sh` (or the README's manual setup).
3. Ask the identical cancellation question in a new chat. Use the same Copilot model where practical.
4. Compare lock checks, draft versus posted invoices, completed versus unfinished deliveries, downstream cancellation effects, and acknowledged gaps.

The current demo RAG index may cover only three files; exploration can read the whole approved checkout. A fuller answer can result from broader source coverage as well as agentic investigation. RAG uses a fixed indexed snapshot; this experiment reads live working-tree files, including local/untracked source. Align the checkout/index versions for a meaningful comparison. This is a manual demonstration, not a benchmark.

## Deliberate limits and safety boundary

Only `.py`, `.xml` and `.csv` files under the configured root are catalogued. Hidden paths, linked files/directories and common generated directories are excluded; root ancestors and each read are checked for symbolic links. Absolute paths, parent traversal and backslash paths are rejected. Files over 2 MiB, invalid UTF-8 and NUL-containing data are not returned. The root must be an existing absolute directory, not a filesystem root.

No SDK built-in, shell, network, editing, execution, MCP, skill or plugin tools are exposed. Empty client mode plus a source-qualified allowlist exposes only the two custom tools; the permission callback approves only those tool names and rejects managed-approval requirements. Setting a working directory alone is not used as a filesystem sandbox. The custom tools use .NET file reads and never execute Odoo/Python code or Git hooks. SDK credential/session storage remains in its own existing profile outside Odoo.

An investigation has a shared 40-tool-call limit, 180,000-character read budget and timeout. This is a local PoC: do not concurrently replace source paths while it runs. The path checks are not an OS sandbox against a malicious local process racing filesystem changes. No immutable snapshot, installed-module validation, workflow graph, follow-up conversation store or production authorization is implemented. Runtime Odoo configuration and dynamic inheritance can leave uncertainty even after reading multiple files. SDK sessions may persist source evidence in the configured profile; protect it as before.

SDK capabilities were checked against the installed package documentation and [official versioned .NET documentation](https://raw.githubusercontent.com/github/copilot-sdk/v1.0.22/dotnet/README.md). Custom tools keep repository path restrictions explicit without granting general-purpose filesystem tools.

## Verification in this workspace

The solution built with zero warnings/errors and all 74 offline tests passed, including 10 exploration-tool cases covering multi-file evidence, traversal/absolute-path rejection, scope/literal search, symlink rejection and rechecking, invalid ranges, oversized lines, a shared tool-call budget, and cancellation.

An authenticated run of the default cancellation question completed with **2 searches, 2 reads and 2 files read**: `addons/sale/models/sale_order.py` (1740–1839) and `addons/sale_stock/models/sale_order.py` (230–259). It returned a business-language explanation covering the lock restriction, draft versus posted invoices, unfinished versus completed deliveries, and order cancellation. The trace demonstrates real multi-step source access. This run did not read the full stock/accounting cancellation implementations; it does not establish exhaustive downstream workflow coverage or prove every generated claim. Model behavior and tool counts can vary on later runs.
