# Building agentic UI with .NET

This sample demonstrates how to build rich agentic user experiences with .NET. A **Blazor** chat experience provides a flexible conversational foundation, and the scenarios extend it with structured rich text, tool-driven UI, human approval, shared and predictive state, generative UI, and visible reasoning. The backend hosts agents built with the **Microsoft Agent Framework (MAF)**, while the **[AG-UI](https://docs.ag-ui.com) C# SDK** carries messages, actions, and state between the agents and the preview [Blazor AI components](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-11#experimental-blazor-ai-components-for-agentic-user-interfaces). [Aspire](https://learn.microsoft.com/dotnet/aspire/) wires the application together, and the agents use **[Foundry Local](https://learn.microsoft.com/azure/ai-foundry/foundry-local/)** by default, with optional **[Microsoft Foundry on Azure](https://learn.microsoft.com/azure/ai-foundry/)** inference.

![AgenticUI demonstrating frontend tools, human approval, and generative UI](docs/images/agentic-ui.gif)

## What it demonstrates

| Scenario | AG-UI feature | Endpoint |
| --- | --- | --- |
| **Agentic chat** | Streaming, multi-turn chat (`TEXT_MESSAGE_*`) rendered as structured rich text | `/agentic_chat` |
| **Backend tools** | Server-side tool calls (`TOOL_CALL_*`) mapped to a generated typed block and custom card | `/backend_tool_rendering` |
| **Frontend tools** | Application-local tool invoked through the AG-UI client tool pipeline and rendered as a typed block | `/tool_based_generative_ui` |
| **Human in the loop** | Tool approval interrupt → Approve / Reject → resume | `/human_in_the_loop` |
| **Shared state** | Bidirectional typed state via request state and `STATE_SNAPSHOT` | `/shared_state` |
| **Predictive state** | Proposed document edits with accept/reject and rollback | `/predictive_state` |
| **Agentic generative UI** | Live plan via `STATE_SNAPSHOT` + `STATE_DELTA` (JSON Patch) | `/agentic_generative_ui` |
| **Reasoning** | Reasoning summaries via `REASONING_*` events and a custom activity block | `/reasoning` |

## Architecture

![Architecture of the AgenticUI sample from Blazor through AG-UI and ASP.NET Core to Microsoft Foundry](docs/images/blazor-agentic-ui-architecture.svg)

- **`AgenticUI.AgentServer`** — ASP.NET Core app. Uses `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` (`AddAGUIServer()` + `MapAGUIServer("/route", agent)`) to expose one AG-UI endpoint per scenario. Agents are MAF `AIAgent`s backed by Foundry Local or Azure via `Microsoft.Agents.AI.OpenAI`.
- **`AgenticUI.Web`** — Blazor Web App (Interactive Server). Each scenario builds a `UIAgent` over an `AGUIChatClient` (from the AG-UI C# SDK's `AGUI.Client`), which turns an AG-UI endpoint into a standard `IChatClient`. UI is rendered with the Blazor AI components (`ChatPage`, `MessageList`, `BlockRenderer`, `UIAgent<TState>`, …).
- **`AgenticUI.AppHost` / `AgenticUI.ServiceDefaults`** — Aspire orchestration and service discovery.

### Packages used

- `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI` (1.15.0)
- `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` (1.15.0-preview — the AG-UI hosting glue is still preview)
- `AGUI.Client`, `AGUI.Abstractions`, `AGUI.Formatting`, `AGUI.Server` (1.0.0 — the stable AG-UI .NET SDK)
- `Azure.AI.OpenAI` (2.9.0-beta.1)
- `Microsoft.AspNetCore.Components.AI` (0.1.0-preview.1.26459.102)
- `Aspire` (13.5.3)

## Running it

### Prerequisites

- [.NET 11 RC1 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)
- [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/)
- [Foundry Local CLI](https://learn.microsoft.com/azure/ai-foundry/foundry-local/get-started), available as `foundry` on `PATH`.
- For optional Azure inference: Azure CLI and a Microsoft Foundry resource with a `gpt-5-mini` deployment.

### Clone and build

```bash
git clone https://github.com/danroth27/AgenticUI.git
cd AgenticUI
dotnet restore
dotnet build
```

### Use Foundry Local (default)

Run `aspire run`. Aspire starts Foundry Local, resolves the hardware-specific model ID, loads
the `qwen3.5-2b` model (downloading it if needed), and waits for it before starting the agent server.
Azure sign-in and an Azure endpoint are unnecessary in local mode. The model runs on your machine;
the initial model download requires internet access and disk space.

Choose another tool-capable model from `foundry model list` by setting:

```bash
dotnet user-secrets set "Foundry:LocalModel" "<model-alias>" --project src/AgenticUI.AppHost
```

Restart the AppHost after changing models so Aspire loads the selected model and supplies its
resolved model ID to AgentServer. Model selection does not require code changes. Chat models
without tool support can serve ordinary chat; the tool-card scenarios require a tool-capable model.

Local mode uses the OpenAI-compatible chat completions API for all scenarios, including reasoning.
The reasoning demo returns an answer, but Azure Responses API reasoning summaries are unavailable
with the default local model. Tool and structured-output quality varies by model.

For local requests with tools, the agent server uses complete chat responses and converts Qwen's
XML-like `<tool_call><function=...>` output or JSON-in-tag
`<tool_call>{"name":"...","arguments":{...}}</tool_call>` output into framework function-call content when Foundry Local
returns it as text. This lets the existing callbacks, approval flow, and AG-UI tool cards work.
The exact bare JSON `{"name":"...","arguments":{...}}` envelope is also recognized when it names
an available tool, covering local templates that remove the delimiters. Other JSON remains text.
Native structured tool calls pass through unchanged. Local responses with tools arrive once each
model turn completes; chat without tools continues to stream tokens. Azure streaming is unchanged.

If your local model/runtime reliably returns native structured tool calls while streaming, bypass
the compatibility adapter to retain token streaming in tool scenarios:

```bash
dotnet user-secrets set "Foundry:LocalToolCallCompatibility" "false" --project src/AgenticUI.AppHost
```

The default is `true`. For AgentServer running independently, set the same option in its `Foundry`
configuration section or use the `Foundry__LocalToolCallCompatibility` environment variable.
The compatibility layer handles returned tool calls; it cannot make a model request a tool.

### Configure Microsoft Foundry on Azure (optional)

Select Azure inference explicitly; the existing endpoint and deployment secrets remain supported:

```bash
dotnet user-secrets set "Foundry:Provider" "Azure" --project src/AgenticUI.AppHost
```

Set `Foundry:Provider` to `Local` to return to local inference.

Sign in to Azure with the identity that has access to the Foundry resource:

```bash
az login
```

The identity must have the **Cognitive Services OpenAI User** role on the Foundry resource. Ask the resource owner or administrator to assign the role if you don't have permission to do so.

Set the existing Foundry account endpoint as an AppHost user-secret:

```bash
dotnet user-secrets set "Parameters:foundry-endpoint" "https://<resource>.services.ai.azure.com/" --project src/AgenticUI.AppHost
```

Use the Foundry resource endpoint, such as `https://<resource>.services.ai.azure.com/`. The AppHost models Foundry as an externally managed HTTPS dependency, so it won't provision or modify the Foundry account. The app authenticates with Microsoft Entra ID through `DefaultAzureCredential`; a deployed AgentServer's managed identity needs the same role as the local developer.

Both deployment names default to `gpt-5-mini`. Override them when your deployment names differ:

```bash
dotnet user-secrets set "Parameters:foundry-model" "<deployment-name>" --project src/AgenticUI.AppHost
dotnet user-secrets set "Parameters:foundry-reasoning-model" "<reasoning-deployment-name>" --project src/AgenticUI.AppHost
```

> **Why a separate reasoning path?** Reasoning models only return their reasoning summaries through
> the OpenAI **Responses** API — chat completions spend the same reasoning tokens but return no
> reasoning text. So the reasoning scenario builds its client with `GetResponsesClient()` and opts in
> via the provider-neutral `ChatOptions.Reasoning`
> (`new ReasoningOptions { Output = ReasoningOutput.Full }`). `Microsoft.Extensions.AI` maps that to
> the Responses API's reasoning summary setting and surfaces the summaries as `TextReasoningContent`,
> which the MAF AG-UI adapter emits as `REASONING_*` events.

### Run

```bash
aspire run
```

Open the Aspire dashboard, then open the **web** resource and pick a scenario from the nav.

### Troubleshooting

- **Foundry Local fails to start:** Verify `foundry --help` works and inspect the `foundry-local` resource logs in Aspire.
- **Local model fails to load:** Check `foundry model list`, available memory, and disk space; choose a smaller tool-capable model with `Foundry:LocalModel`.
- **XML appears instead of a tool card:** Rebuild the agent server and enable `Foundry:LocalToolCallCompatibility`. The adapter supports function/parameter XML and JSON-in-tag calls and rejects malformed calls or functions and arguments absent from the supplied tools.
- **Azure endpoint missing:** Set the `Parameters:foundry-endpoint` AppHost user-secret shown above.
- **Azure authentication failures:** Run `az login` again and verify that the selected identity has the **Cognitive Services OpenAI User** role.
- **Azure deployment not found:** Set `Parameters:foundry-model` and `Parameters:foundry-reasoning-model` to the deployment names configured in your Foundry account.

## Repository layout

```text
src/
  AgenticUI.AppHost/          Aspire orchestration
  AgenticUI.ServiceDefaults/  Shared service defaults
  AgenticUI.AgentServer/      AG-UI backend (MAF + AG-UI C# SDK)
  AgenticUI.Web/              Blazor front end (Blazor AI components)
docs/
  findings.md                Current implementation findings and limitations
```

## Notes & findings

Run the Foundry Local tool-call regression checks with:

```bash
dotnet run --project tests/AgenticUI.AgentServer.Tests
```

See [`docs/findings.md`](docs/findings.md) for current implementation notes, design boundaries, and remaining limitations.
