# Milestone 4 — evidence-based Odoo chat

The existing Blazor chat now retrieves knowledge before calling Copilot. Indexing is still an independent console process; chat never reads the Odoo checkout, runs Python, invokes the indexer, or exposes raw source through a business-facing endpoint.

## Configure and run

Use .NET 10, a completed Milestone 3 search generation, its matching Qdrant collection, the indexed Ollama embedding model, and authenticated GitHub Copilot access. See [RETRIEVAL.md](RETRIEVAL.md) for service/index creation and [README.md](README.md#prerequisites-and-authentication) for SDK sign-in. Start Ollama and Qdrant first; do not start duplicate services on the same ports.

Set standard ASP.NET Core configuration keys in the **server process**:

```sh
export Retrieval__IndexPath=/private/tmp/businesschat-odoo-m3-search/search-e7387c000e9f431dbd9f819c58a7cb73
export KnowledgeChat__ExpectedApplicationVersion=odoo-poc-subset
dotnet run --project Chat.Web --launch-profile http
```

The example generation is temporary local test data, with 318 chunks from the three models described in RETRIEVAL.md. Use your own completed generation when that directory is unavailable. Open http://localhost:5112. Neither source-repository configuration nor access to the source checkout is needed by the server.

`KnowledgeChat:ExpectedApplicationVersion` is required and must match the search manifest's application-version label. The example label is a supplied PoC label, not an inferred Odoo release. Missing/unknown/mismatching versions cause a safe failure before Copilot is called. Existing model-digest, SQLite-checksum and Qdrant checks still apply. Missing index configuration is deferred until retrieval so the UI can show a safe error rather than fail during rendering.

Optional configuration defaults in `Chat.Web/appsettings.json`:

```json
{
  "KnowledgeChat": {
    "ExpectedApplicationVersion": "",
    "RetrievalLimit": 20,
    "MaxEvidenceCharacters": 32000,
    "RetrievalTimeoutSeconds": 30
  },
  "Retrieval": {
    "IndexPath": "",
    "OllamaUrl": "http://localhost:11434/",
    "EmbeddingModel": "nomic-embed-text:v1.5",
    "QdrantUrl": "http://localhost:6333/"
  }
}
```

No credentials belong in these files. On macOS/zsh, an eligible token can be entered without putting it in shell history:

```sh
read -s "COPILOT_GITHUB_TOKEN?Paste your GitHub token: "
echo
export COPILOT_GITHUB_TOKEN
```

Launch the server from that same terminal. Exporting a variable there does not update an already running server or another tool session. Check only whether it is present with `[[ -n "$COPILOT_GITHUB_TOKEN" ]] && echo 'token set' || echo 'token missing'`; do not print its value. The README also provides interactive CLI login instructions; use the macOS binary and zsh syntax on this machine, not the PowerShell/Windows example.

## Data flow and boundaries

1. The scoped `ChatService` validates the question/attachments and creates a candidate turn without changing saved history.
2. It asks the existing hybrid `ICodeRetriever` for fresh evidence. Self-contained questions are retrieved directly. For follow-ups, the latest substantive **user question** and last user qualifier, bounded to fit the retrieval query limit, clarify context. Earlier assistant answers are not added to retrieval queries or treated as evidence. The default retrieves up to 20 chunks: in testing, related cancellation implementations ranked 13th and 16th for one phrasing, beyond the earlier 12-hit cutoff.
3. Whole source chunks are selected within the evidence character budget. Oversized chunks are omitted, never silently clipped. The provider is told when evidence was omitted. No selected evidence means a deterministic insufficient-information answer without calling Copilot.
4. `AnswerRequest` carries the transcript, selected source chunks and provenance to `IAnswerGenerator`. Source comments/docstrings, attachments and conversation contents are serialized as untrusted JSON data, separately from the system policy.
5. Copilot returns a structured `AnswerDraft`: supported, partial, insufficient, or conflicting, with ordered claims and internal supporting chunk IDs. It is instructed to ground every fact in the current evidence, prioritize executable conditions over comments, and acknowledge unavailable downstream behavior.
6. `BusinessAnswerPolicy` validates claim/reference structure and checks for technical-detail leakage. Unsupported/conflicting states and invalid references are replaced with fixed business-facing limitation messages. Partial answers add an explicit statement about unverified remaining behavior. Only rendered business text enters `ChatMessage`; source IDs/code/paths stay outside the UI contract.

`ChatService.LastTrace` is a developer-only diagnostic on the concrete scoped service, containing index provenance, selected source references, accepted evidence IDs, result status and omission flag. Inspect it in a debugger for verification; it is not a user API or UI property. Source content, questions, attachments, transcripts, credentials and raw provider exceptions are not logged. The Copilot runtime can persist session data containing evidence in its configured profile: protect that directory like the knowledge index.

The existing SDK 1.0.22 and installed APIs were rechecked against the [official versioned .NET documentation](https://github.com/github/copilot-sdk/blob/v1.0.22/dotnet/README.md). `MessageOptions.ResponseSchema` is experimental in that SDK; its diagnostic suppression is narrowly scoped in the Infrastructure adapter, like the existing permission handler. Application contracts contain no SDK types. Empty client mode, no tools, denied permissions, timeouts, cancellation and safe provider errors remain enabled. No new NuGet package was added for this milestone.

## A source-specific acceptance question

Ask in a new browser conversation:

> If I select several sales orders for cancellation and just one is locked, are the unlocked orders cancelled or is the whole action blocked?

Expected answer: **The whole cancellation action is blocked if any selected order is locked. The unlocked orders do not proceed through cancellation in that request. Unlock the locked order before retrying.** The condition checks the selection before calling the cancellation operation; it does not loop through and cancel the unlocked orders first. Manually verify `/Users/sandra.ord/dev/odoo/addons/sale/models/sale_order.py`, lines **1791–1796** at the tested commit. This exact batch condition provides a focused acceptance check without requiring an entire workflow to be indexed.

For a cross-component check, ask:

> For an unlocked sales order, what does cancellation do to draft invoices and unfinished deliveries? Explain only the verified steps and acknowledge any missing downstream details.

An intentionally broader question is:

> What happens when I cancel an unlocked sales order with draft and posted invoices, plus completed and unfinished deliveries?

Expected substance, based on checkout commit `082a335a52b7594b86dcef7d027aa15929c338bb`:

1. An unlocked order passes the lock check. If any order in the cancellation selection is locked, the operation is blocked and it must be unlocked first.
2. The sales/inventory extension sends related deliveries that are **not completed** through cancellation. Completed deliveries are excluded from that step. It may also prepare/log follow-up activity for affected documents when the confirmed order has lines; do not invent the unprovided details of that activity.
3. The underlying sales cancellation selects related invoices in **draft** and sends them through invoice cancellation. Posted invoices are **not selected by that step**. This does not establish automatic refunds, credit notes, or reversal of posted accounting entries.
4. After those operations succeed, the sales order is marked cancelled.
5. The supplied evidence does not include the full invoice-cancellation and stock-move-cancellation implementations, so the chatbot must not invent their downstream effects or claim that delivered stock is automatically reversed.

Wording may differ. A scoped partial answer is appropriate when downstream steps are missing; a broad question can also cause a conservative refusal. The user answer must use business terms, not the technical references below.

Manual verification locations in the separate checkout:

| File | Lines at the tested commit | Evidence |
| --- | --- | --- |
| `addons/sale/models/sale_order.py` | 1791–1796 | Entry point rejects any locked selected order before invoking cancellation |
| `addons/sale_stock/models/sale_order.py` | 238–254 | Extension selects non-completed related deliveries, handles affected-document activity and calls base cancellation |
| `addons/sale/models/sale_order.py` | 1797–1801 | Selects only draft invoices, requests invoice cancellation, then marks the order cancelled |
| `addons/stock/models/stock_picking.py` | 800–808 | Additional manual context: delegates stock-move cancellation; not guaranteed to be retrieved for the main question |

The batch-lock question must cite the sales cancellation entry point internally. For the cross-component question, the live retrieval should additionally include its base cancellation operation and the sales/inventory extension. Verify this in `LastTrace` or with the existing console diagnostic:

```sh
dotnet run --project Chat.Indexer -- search \
  --index "$Retrieval__IndexPath" --limit 20 \
  --expected-version odoo-poc-subset \
  --question "What happens when I cancel an unlocked sales order with draft and posted invoices, plus completed and unfinished deliveries?"
```

Retrieval worked correctly when the trace identifies the configured generation/commit/version, includes those three source chunks and the answer's accepted evidence IDs refer to those supplied chunks. Correct prose alone is not proof of retrieval: a model may know general Odoo behavior. The state-specific conditions, internal trace and negative checks together provide stronger evidence.

Follow up with “Summarize that in one sentence.” Expect the lock restriction to persist in context without invented exceptions. Summary requests instruct the provider to return one claim; if it returns multiple verified claims, the application formats them as a compact paragraph instead of numbered steps. A partial answer may still add the application's separate uncertainty notice. The provider used in the live test rejects the optional JSON Schema `maxItems` constraint, so claim-count/length limits are enforced in application validation rather than relying on that provider-specific schema feature. As a negative check, ask “Does cancellation always refund the customer automatically?” Expect the assistant to say that an automatic refund is not established by the available evidence. Ask about an unrelated unindexed business process and expect insufficient evidence rather than a generic invented workflow.

## Verification and limitations

Run `dotnet build OdooSourceCodeExplorer.sln` and `dotnet test OdooSourceCodeExplorer.sln`. The offline suite includes follow-up retrieval, conversation isolation/attachments, safe failures/cancellation, version enforcement, evidence budgets, refusal without evidence, internal citation checks, partial/conflicting evidence and technical-detail filtering.

The final build completed with zero warnings/errors and all **67 tests passed**. Authenticated browser testing with the real local search services verified the batch-lock answer, a one-sentence follow-up that retained its condition, and a refund question that explicitly declined to establish automatic refunds. Internal response references matched supplied source chunks, including the sales cancellation guard at line 1791. No source references appeared in the displayed answers.

Live testing also exposed remaining limitations: one cross-component request returned an empty structured claim and was safely rejected with a retry message, despite the relevant implementations being supplied. A differently worded request retrieved sales cancellation but missed the inventory extension and produced a partial answer acknowledging missing inventory behavior. Earlier testing did produce a multi-component explanation, but these observations mean complete workflow coverage is **not consistently reliable** with this small index and bounded retrieval. The cross-component expected substance above is a manual acceptance oracle, not a claim that every phrasing passes. No automatic retries or general-knowledge fallback were added.

Reference validation proves that a claim names a supplied chunk; it cannot mathematically prove that the text entails the claim. The language model can still misread evidence, and the output filter is a conservative guard rather than a full semantic security boundary. Manual/source-based evaluation remains necessary. A small index omits other extensions, XML/CSV behavior and called implementations; no unrestricted investigative tools or relationship expansion were added. Conflicts are acknowledged, not automatically resolved. Requests with a retrieval error do not fall back to unguided model knowledge.

No production authentication, user API, streaming, terminology-mapping framework, CI/CD publication or deployment changes were introduced. Personal Copilot credentials remain a local development arrangement and do not establish production licensing or multi-user rights.
