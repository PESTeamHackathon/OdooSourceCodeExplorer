# Business Knowledge Chat — Milestones 1–3

This extends the existing `OdooSourceCodeExplorer.sln` and its `Chat.*` projects. It implements local chat through the official GitHub Copilot SDK and independent Python source indexing. The chat does not yet consume the index. Application-specific questions should receive an explicit insufficient-evidence answer. See [Milestone 2 indexing instructions](INDEXING.md) for configuring a separate Odoo checkout, running the indexer, and inspecting its artifacts.

[Milestone 3 retrieval instructions](RETRIEVAL.md) cover local Ollama embeddings, Qdrant, SQLite FTS5, building a searchable generation, and testing semantic/keyword/hybrid retrieval through the console. Retrieval is not connected to chat yet.

## Architecture and minimum implementation

```text
Blazor Interactive Server UI
    -> IChatService / ChatService (Chat.App)
    -> IAnswerGenerator (Chat.App)
    -> CopilotAnswerGenerator (Chat.Infrastructure)
    -> GitHub Copilot SDK / managed Copilot runtime
```

Project references:

- `Chat.Web` -> `Chat.App`, `Chat.Infrastructure` (the composition root registers dependencies).
- `Chat.Infrastructure` -> `Chat.App`.
- `Chat.App` has no external package or UI/provider dependencies.
- `Chat.Indexer` -> `Chat.App` for chunk/retrieval contracts and `Chat.Infrastructure` for search integrations. It runs independently of the web runtime and never uses Copilot during indexing or retrieval.
- `Chat.App.Tests` -> `Chat.App`, `Chat.Indexer`.

`ChatMessage` and `ChatRole` describe the transcript. `IChatService` exposes history, sending and reset; `ChatService` validates questions and commits complete turns. `IAnswerGenerator` is the replaceable provider boundary. `AnswerGenerationException` carries safe errors. Infrastructure contains the SDK adapter, typed options and DI registration.

There is no conversation store abstraction yet: a scoped `ChatService` belongs to one Blazor circuit. Reloading or losing the circuit loses the UI history. History is limited to 20 turns, questions to 4,000 characters. Failed/canceled turns do not change history; overlapping sends and resets are rejected. A new SDK session receives the bounded transcript for each turn and is disposed afterward. The shared SDK client is disposed asynchronously by DI at shutdown.

REST is deferred: scoped Blazor conversations have no authenticated API ownership model yet. A future host can use the same application/provider interfaces, adding conversation ownership and storage for API calls without a Blazor dependency. Do not register `ChatService` as a singleton.

## Packages and SDK verification

Runtime package references:

- `GitHub.Copilot.SDK` **1.0.22**, retained from the existing Infrastructure project.
- `Microsoft.Extensions.Options.ConfigurationExtensions` **10.0.0**, for configuration binding and validated options.
- `Microsoft.Data.Sqlite` **10.0.13**, for the independently built knowledge/FTS5 store. Ollama and Qdrant integrations use their HTTP APIs without extra client frameworks.

Tests use `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3 and `xunit.runner.visualstudio` 3.1.4. No extra agent framework is used.

The implementation was checked against the [official versioned .NET SDK documentation](https://github.com/github/copilot-sdk/blob/v1.0.22/dotnet/README.md) and the installed package's API documentation. It uses `CopilotClient`, `CreateSessionAsync`, `SendAndWaitAsync`, `AbortAsync`, and async disposal. This package acquires and verifies a pinned native runtime during the build; a separate CLI install is not required to run the web app. Initial builds need access to NuGet and GitHub release assets.

`CopilotClientMode.Empty` disables ambient coding-agent features; `AvailableTools = []` exposes no tools. All permission requests are rejected. The SDK marks the typed permission decision API experimental (`GHCP001`); suppression is limited to that handler inside Infrastructure. Provider upgrades must recheck this API.

## Prerequisites and authentication

Install the .NET 10 SDK. Have an eligible GitHub Copilot account and internet access. If your organization supplies Copilot, its CLI policy must allow access. Reasoning uses hosted Copilot models; this PoC is locally hosted, not offline.

Run commands from the solution directory:

```sh
dotnet build OdooSourceCodeExplorer.sln
```

The application deliberately uses its own Copilot data directory, defaulting to `~/.copilot-business-chat`. Signing in to an editor alone is not sufficient. Authenticate using the bundled CLI and the same data directory. Empty mode disables keychain credential storage, so use the matching environment setting during login.

macOS Apple Silicon (this workspace):

```sh
export COPILOT_HOME="$HOME/.copilot-business-chat"
export COPILOT_DISABLE_KEYTAR=1
./Chat.Web/bin/Debug/net10.0/runtimes/osx-arm64/native/copilot
```

Windows PowerShell:

```powershell
$env:COPILOT_HOME = Join-Path $HOME '.copilot-business-chat'
$env:COPILOT_DISABLE_KEYTAR = '1'
& .\Chat.Web\bin\Debug\net10.0\runtimes\win-x64\native\copilot.exe
```

Choose the runtime directory matching your build platform (`osx-x64`, `linux-x64`, `win-arm64`, etc.). In the interactive CLI, enter `/login`, finish the browser/device flow, then `/exit`. No model prompt is needed to log in. Do not commit the credential/session directory. Restrict its filesystem permissions to your account. It may contain credentials and SDK conversation data even after the UI conversation is cleared.

Alternatively, provide a fine-grained personal access token with the **Copilot Requests** account permission as `COPILOT_GITHUB_TOKEN` in the server process environment. Do not place tokens in appsettings or source control. The runtime also recognizes `GH_TOKEN` and `GITHUB_TOKEN`; existing environment tokens can override stored login. See GitHub's [authentication instructions](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli) and [installation/token prerequisites](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli).

No third-party model API key is required. SDK prompts consume Copilot allowance under the [Copilot SDK billing model](https://github.com/github/copilot-sdk/blob/v1.0.22/README.md#how-does-billing-work-for-sdk-usage). Monitor your account's limits and spending settings; this application does not enable extra paid usage or retry failed calls automatically. Each follow-up resends the current transcript and makes another model request.

## Run and configure

```sh
dotnet run --project Chat.Web --launch-profile http
```

Open http://localhost:5112. The HTTP launch profile is for local development. The HTTPS profile uses https://localhost:7003 and requires a trusted development certificate (`dotnet dev-certs https --trust`). Global Interactive Server rendering is configured on Routes and HeadOutlet.

Configuration uses standard .NET providers. `Chat.Web/appsettings.json` includes:

```json
{
  "Copilot": {
    "Model": null,
    "TimeoutSeconds": 120
  }
}
```

A null model uses the runtime's default available to your account. Set `Copilot__Model` to a model ID available in your CLI `/model` selector. `Copilot__TimeoutSeconds` accepts 10–600 seconds. `Copilot__BaseDirectory` overrides the absolute path used for credentials/session state; log in with that same path as `COPILOT_HOME`. Configuration changes require restarting the app. Credentials and SDK execution stay server-side.

## Verify

```sh
dotnet test OdooSourceCodeExplorer.sln
```

The offline xUnit tests cover follow-up context, attachment validation, isolation, reset, failed/empty responses, cancellation, size/turn limits and overlapping operations. They do not contact Copilot or consume allowance.

Milestone 1 browser verification confirmed input, submission through the service layers, and safe provider-unavailable errors while preserving the question. A successful authenticated model response was not verified in that check; complete the login steps above and the checks below before treating the live chat integration as operational. See [indexing verification](INDEXING.md#tests) for Milestone 2 results.

After authenticating, exercise the real integration:

1. Ask “What is a credit memo?” Expect a general business explanation.
2. Ask “Summarize that in one sentence.” Expect it to use the previous exchange.
3. Ask “What happens in our application when an order cannot be delivered?” Expect an explicit lack-of-evidence answer, without invented application rules.
4. Start a new conversation. Previous messages should disappear.
5. Send and cancel a request. Its question remains editable and the transcript is unchanged.
6. If authentication is missing, usage is exhausted, or the model is unavailable, expect a safe error and a retryable question, without raw provider errors.

Model instructions guide evidence handling and business language; they are not a formal guarantee against hallucination. Source-grounded evaluation belongs to later milestones. Responses are rendered from Markdown with raw HTML disabled, so answer content cannot inject markup.

## Attachments

Users can attach up to 5 files per message: images (PNG, JPG, GIF, WebP, max 5 MB) and UTF-8 text files (txt, md, csv, json, xml, code and similar, max 200 KB). The type is derived from the file extension and validated in `ChatService`; a conversation holds at most 20 MB of attachments. Images are sent to Copilot as inline base64 blobs; text file content is embedded in the transcript. Both are labeled as untrusted user data, tools stay disabled, and nothing is written to disk. Attachments remain in the conversation so follow-ups can refer to them, which means they are resent (and billed) on each turn. Whether images are understood depends on the selected model's vision support.

## Scope and future integration

The chat runtime has no repository access or retrieval integration. Milestone 2 adds an independent source indexer; Milestone 3 consumes those artifacts to build and search local embedding, vector and keyword indexes. Source indexing and retrieval remain separate from chat requests. CI/CD, production deployment, terminology mappings and relationship expansion remain outside the implemented scope.

This unauthenticated PoC is for local developer use. Production requires application authentication/authorization, conversation ownership, index access protection, source-sharing policy approval, and explicit validation of AI authentication, licensing and billing. A developer's personal Copilot subscription is not assumed to cover business users. Raw SDK errors and transcript content are not written to application logs; SDK-managed local data still requires protection.
