# AgentChat

A Blazor Server app where several AI agents and you share one chat room. Each agent runs on its own
model (local models in Docker by default, online models when you configure them), and you tune each
agent from the sidebar.

## Run it

```powershell
docker compose up -d            # Ollama on http://localhost:11435
./scripts/pull-models.ps1       # llama3.2:3b, qwen2.5:3b, gemma3:1b
dotnet run --project src/AgentChat
```

Open the URL printed by `dotnet run` (http://localhost:5165 by default).

## How a conversation runs

- Agents take turns. Each turn is one **loop**: **read** the chat → **decide** (ask the model) →
  **comment** (post its reply) → **act** (run any tool calls) → **report** (post what it found).
  The UI shows the active agent's current step.
- An agent can reply `[PASS]` when it has nothing to add. If every enabled agent passes in a row, the
  room waits for you.
- `@Name` in a message hands the next turn to that agent. Agents can do this to each other.
  Otherwise turns go round robin, and an agent never answers itself straight away.
- The **throttle** sets how many loops the agents may run before they wait for your feedback. Sending
  a message resets the count. **Continue** lets them run another batch without a message, and
  **Stop** cancels the current loop.

## Agent settings (sidebar)

| Setting | What it does |
| --- | --- |
| Provider / Model | Which endpoint and model the agent uses. Models are listed from the provider. |
| System prompt | The agent's persona and instructions. Group-chat rules are added automatically. |
| Memory | Long-term notes that are always in the agent's context. |
| Context messages | How many recent chat messages the agent reads each loop. |
| Context tokens | Approximate prompt budget. The oldest messages are dropped to fit it. |
| Max reply tokens, temperature | Generation settings. |
| Tools | Which tools the agent may call. Agents have no tools until you enable some. |
| Enabled | Disabled agents keep their settings but skip their turns. |

Settings are saved to `src/AgentChat/App_Data/agents.json`. Delete that file to go back to the default agents.

## Models and providers

Every provider implements `ILlmProvider` (`src/AgentChat/Llm`). Providers are listed under
`Providers` in `appsettings.json`. The `OpenAICompatible` kind works with Ollama, LM Studio, vLLM,
llama.cpp server, OpenAI, OpenRouter, Groq and others. To use an online model, add or fill in a
provider entry and put its key in user secrets, not in appsettings:

```powershell
dotnet user-secrets --project src/AgentChat init
dotnet user-secrets --project src/AgentChat set "Providers:2:ApiKey" "sk-..."
```

(`2` is the provider's position in the `Providers` list, counting from 0.)

Then select that provider for an agent in the sidebar. For an API that isn't OpenAI-compatible (for
example Anthropic's native Messages API), implement `ILlmProvider` and add its `Kind` to
`LlmProviderRegistry`.

### Fast deterministic agent (no LLM)

The `random` provider (`RandomWordsProvider`) doesn't call a model. It replies instantly with random
runs of words taken from the chat, sometimes passes, and on about a third of its turns calls one of its
enabled tools with random arguments. If a tool argument looks like it wants a participant (`agent`,
`name`), it uses one of the names seen in the chat, so `mute_agent` can really mute someone.
Its replies are deterministic: the random seed comes from the conversation, so the same chat always
gets the same reply. The models `short`, `medium` and `long` set the reply length. The default agent
"Rando" uses it.

## Tools and MCP

Tools implement `ITool` and come from `IToolSource`s (`src/AgentChat/Tools`):

- `BuiltInToolSource`: tools implemented in the app (currently `current_time`).
- `ChatToolSource`: tools that act on the chat room. `mute_agent` silences another agent for the
  rest of the chat session. An agent can't mute itself. Mutes aren't saved: clearing the chat or
  restarting the app removes them, and you can unmute from the agent's card.
  `sack` gives every agent a sack that starts with 3 different random items from
  `sword, axe, shield, wand, hat, shoes`, filled the first time it is used. `look` lists your items;
  `give` moves one to another agent or to `User` (muted and disabled agents can still receive). A sack
  that hasn't been used yet is filled with its 3 items before the gift is added. You have a sack too:
  the "Your sack" panel in the sidebar shows it and lets you give items to agents. Your gifts appear in
  the chat, so agents see them. A call looks like
  `{"action": "give", "my_name": "Ada", "item": "sword", "give_to": "Quinn"}`. The agent puts its own
  name in `my_name`, but the tool checks it against the real caller, which the room sets, not the model.
  A call naming anyone else is refused, so no agent can touch another agent's sack. Every refusal
  includes a corrected call built from the agent's real name and items, which helps small models
  recover. Sacks are shown on the agent cards and emptied when the chat is cleared.
- `McpToolSource`: connects to an MCP server (stdio or HTTP) with the official
  `ModelContextProtocol` SDK and exposes each server tool as `<server>__<tool>`.

To add MCP servers, list them under `McpServers` in `appsettings.json`:

```json
"McpServers": [
  { "Name": "fs", "Transport": "stdio", "Command": "npx",
    "Arguments": [ "-y", "@modelcontextprotocol/server-filesystem", "C:/temp" ] }
]
```

Tools are loaded at startup. Servers that fail to load are shown in each agent's Tools section.
An agent is offered only the tools enabled for it, and calls to other tools are refused.
`Max tool calls per loop` limits how many calls an agent can make within one loop.
Not every model supports tool calling. If the model rejects tools (for example `gemma3` on Ollama),
the chat says so and the agent replies without them. Small models that write the tool call as JSON
text instead of a proper tool call (`llama3.2:3b` sometimes does) are handled too, as long as the JSON
is valid. `qwen2.5:3b` makes proper tool calls most reliably.

Ollama runs the models on CPU and keeps up to three loaded. That needs about 6 GB of the Docker VM's
memory. If turns are very slow, or Ollama's log shows `signal: killed`, give WSL more memory (`memory=`
in `%UserProfile%\.wslconfig`) or have the agents share fewer models.

## Layout

```
src/AgentChat
  Chat/        ChatRoom (turns, throttle), AgentLoop (one read→report loop), PromptBuilder, AgentStore
  Llm/         ILlmProvider, OpenAiCompatibleProvider, LlmProviderRegistry
  Tools/       ITool, ToolRegistry, BuiltInToolSource, McpToolSource
  Components/  Home.razor (chat), AgentEditor.razor (agent settings)
tests/AgentChat.Tests
```
