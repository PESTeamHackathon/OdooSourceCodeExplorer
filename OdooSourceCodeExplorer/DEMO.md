# Local demo walkthrough (macOS / zsh)

Follow these steps from authentication through the first code-aware question. Commands target this workspace on macOS Apple Silicon. On another machine, change the checkout paths and use the Copilot runtime directory matching your platform.

You need .NET 10 SDK, Git, Ollama, Docker Desktop with Compose (running), an eligible GitHub Copilot account, and the separate Odoo checkout at `/Users/sandra.ord/dev/odoo`. Install these before starting. Internet access is needed for initial downloads and Copilot answers. Ollama generates embeddings locally; Copilot receives selected source evidence and the conversation for hosted reasoning.

The walkthrough indexes three Python files for a small demo. It does not run Odoo or require an Odoo database. It covers only part of the application; XML/CSV rules and many downstream implementations are outside this selection.

## 1. Build and authenticate Copilot

Open **Terminal A** and keep it for indexing and running the web app:

```sh
cd /Users/sandra.ord/dev/OdooSourceCodeExplorer/OdooSourceCodeExplorer
dotnet --version
dotnet build OdooSourceCodeExplorer.sln
dotnet test OdooSourceCodeExplorer.sln
```

Confirm that the SDK version starts with `10.`, the build succeeds, and tests pass before continuing. The build downloads the pinned Copilot runtime; a separate Copilot CLI installation is unnecessary.

Authenticate using the app's dedicated credential directory:

```sh
export COPILOT_HOME="$HOME/.copilot-business-chat"
export COPILOT_DISABLE_KEYTAR=1
export Copilot__BaseDirectory="$COPILOT_HOME"
./Chat.Web/bin/Debug/net10.0/runtimes/osx-arm64/native/copilot
```

At the Copilot prompt:

1. Enter `/login`.
2. Follow the displayed browser/device sign-in steps with your Copilot account.
3. Wait for successful authentication, then enter `/exit`.

Use the same directory for login and the app. Editor sign-in alone is insufficient. This application's empty SDK mode disables keychain storage, hence the matching `COPILOT_DISABLE_KEYTAR` setting during login. Keep the credential directory private; it can also contain source evidence and conversation data.

If authentication behaves unexpectedly, check whether `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, or `GITHUB_TOKEN` is already set: the adapter uses the first nonempty token in that order instead of stored login. Do not print token values. For stored-login authentication, unset unintended token overrides in Terminal A before launching the app:

```sh
unset COPILOT_GITHUB_TOKEN GH_TOKEN GITHUB_TOKEN
```

Alternatively, use a personal fine-grained token with the **Copilot Requests** account permission, as described in [GitHub's authentication instructions](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli). In zsh, enter it without placing the token in shell history:

```sh
read -s "COPILOT_GITHUB_TOKEN?Paste your GitHub token: "
echo
export COPILOT_GITHUB_TOKEN
```

Choose either stored login or token authentication. Do not run the `unset` command after choosing token authentication. Your organization must allow Copilot CLI access if it manages your subscription. Copilot calls use your account's allowance.

## 2. Start Ollama and Qdrant

Open **Terminal B**. If the Ollama desktop app/service is already running, leave it running and skip this command. Otherwise run:

```sh
ollama serve
```

Keep Terminal B open if it is serving Ollama. In **Terminal A**, download the embedding model and check the service:

```sh
ollama pull nomic-embed-text:v1.5
curl --fail http://localhost:11434/api/tags
```

The model list should contain `nomic-embed-text:v1.5`. This model provides embeddings; you do not need an Ollama chat model.

Start Qdrant from Terminal A, in the solution directory:

```sh
docker compose up -d qdrant
docker compose ps
curl --fail http://localhost:6333/collections
```

Wait until both HTTP checks succeed. Qdrant runs in the background with persistent Docker storage. If you already have native Qdrant running on port 6333, use that instance instead of starting Docker Qdrant. See [RETRIEVAL.md](RETRIEVAL.md#start-the-required-services) for the native configuration.

## 3. Index the Odoo source

In Terminal A, create a private artifact folder outside both Git repositories and inspect source provenance:

```sh
export DEMO_ROOT="$HOME/BusinessChatDemo"
mkdir -p "$DEMO_ROOT"
chmod 700 "$DEMO_ROOT"
git -C /Users/sandra.ord/dev/odoo rev-parse HEAD
git -C /Users/sandra.ord/dev/odoo status --short
```

The commit is recorded in the generated index. If Git reports local changes, the index records that it includes working-tree changes; do not present it as an exact clean-commit snapshot. Keep the selected source unchanged while indexing.

Run:

```sh
dotnet run --project Chat.Indexer --no-build -- \
  --source /Users/sandra.ord/dev/odoo \
  --output "$DEMO_ROOT/source-indexes" \
  --Indexing:IncludePaths:0 addons/sale/models/sale_order.py \
  --Indexing:IncludePaths:1 addons/stock/models/stock_picking.py \
  --Indexing:IncludePaths:2 addons/sale_stock/models/sale_order.py \
  --version odoo-poc-subset
```

On success, the command prints `Index: /.../source-indexes/<generation>`, the commit and chunk counts. Copy the **complete generated directory**, not just `source-indexes`. Set it below, replacing the example placeholder:

```sh
export SOURCE_GENERATION='/paste/the/full/directory/printed/after/Index:'
ls "$SOURCE_GENERATION/manifest.json" "$SOURCE_GENERATION/chunks.jsonl"
```

Both files must exist. `odoo-poc-subset` is a demo compatibility label supplied by you, not an inferred Odoo release. Indexing reads tracked Python files as data and needs no Copilot authentication.

## 4. Build the searchable generation

With Ollama and Qdrant still running, run in Terminal A:

```sh
dotnet run --project Chat.Indexer --no-build -- build-search \
  --artifact "$SOURCE_GENERATION" \
  --search-output "$DEMO_ROOT/search-indexes"
```

Wait for completion. This builds SQLite keyword search and Ollama embeddings, stores vectors in a new Qdrant collection, and prints `Search index: /.../search-indexes/<generation>`.

Copy that **complete search-generation directory** into the following variable:

```sh
export Retrieval__IndexPath='/paste/the/full/directory/printed/after/Search index:'
export KnowledgeChat__ExpectedApplicationVersion=odoo-poc-subset
ls "$Retrieval__IndexPath/search-manifest.json" "$Retrieval__IndexPath/knowledge.sqlite"
```

Both files must exist. The web app must point to this search generation, not the source generation. Keep its Qdrant collection and the same embedding model available. Changing the embedding model's digest requires restoring the original model or rebuilding the search generation.

## 5. Check retrieval before presenting

In Terminal A:

```sh
dotnet run --project Chat.Indexer --no-build -- search \
  --index "$Retrieval__IndexPath" \
  --expected-version "$KnowledgeChat__ExpectedApplicationVersion" \
  --question "Can I cancel a locked sales order?" \
  --module sale --limit 5
```

Confirm that it succeeds, reports the expected commit/version, and returns relevant sales cancellation evidence. Exact rankings can vary with the source and model. This diagnostic verifies retrieval, not Copilot authentication or answer quality. Its paths and symbols are for presenter preparation; the business UI should not display them.

## 6. Start the web app

From **the same Terminal A**, where authentication and index variables are configured:

```sh
dotnet run --project Chat.Web --no-build --launch-profile http
```

Wait for the listening message, then open [http://localhost:5112](http://localhost:5112). Leave the server running. The browser has no separate login in this local PoC; Copilot credentials remain on the server. Chat uses the prepared index and does not need the Odoo checkout at runtime.

If you open a new terminal to launch the app, first repeat the exports for `Copilot__BaseDirectory` (if customized), `Retrieval__IndexPath`, `KnowledgeChat__ExpectedApplicationVersion`, and any token you chose to use. Environment changes do not affect an already running app: restart it after changing settings.

## 7. Ask the demo questions

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

## Repeat the demo or stop

For later runs, reuse the completed search generation and its Qdrant storage. Start Ollama and the same Qdrant instance, restore the Terminal A environment variables, then run the web app. Repeat source indexing and search building when the source/scope changes; select the new search generation explicitly. Authentication normally only needs repeating when credentials expire or access changes.

Stop the web app with Ctrl+C in Terminal A. To stop Docker Qdrant without removing its data:

```sh
docker compose stop qdrant
```

If you started `ollama serve` in Terminal B, stop it with Ctrl+C. Keep the generated indexes private; they contain source evidence.

## If a step fails

| Symptom | Check |
| --- | --- |
| Copilot binary missing | Complete the solution build and check the platform directory under `Chat.Web/bin/Debug/net10.0/runtimes`. |
| Docker cannot connect / port 6333 occupied | Start Docker Desktop, or reuse the existing native Qdrant instance. Run one instance on that port. |
| Embedding connection/model error | Check Ollama is running and the exact model from step 2 is installed. |
| Missing index or version mismatch | Set the full search-generation directory and the same version label supplied to source indexing; restart the app. |
| Collection missing or model/database validation fails | Use the original matching Qdrant storage/model/artifacts, or rebuild the search generation and update the selected path. |
| Retrieval diagnostic works but chat fails | Check Copilot authentication, unintended token overrides, account/model access and usage limits. Restart after changing the environment. |
| Model unavailable | Choose an account-supported ID with the CLI `/model` selector, export `Copilot__Model` to that ID, then restart the app. |
| Partial answer or insufficient evidence | Check the selected source scope and rehearse the focused question in step 7. The app does not resolve every Odoo workflow. |

This runbook was checked against the current command parsing, configuration, service registration, launch profile and existing acceptance documentation. Creating this guide did not perform a new authenticated end-to-end demo.
