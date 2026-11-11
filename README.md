# Concierge Agent

A virtual concierge for Hotel ZGY, a fictional hotel in Wynwood, Miami. Built
with .NET 10, ASP.NET Core, Aspire, Temporal, and Deepgram’s Voice Agent API.
Guests can use text or browser voice to ask about the hotel, check weather,
find nearby places and more

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/products/)
- [just](https://just.systems)
- [Aspire CLI](https://aspire.dev)
- [Dev Tunnels CLI](https://github.com/microsoft/dev-tunnels)
- [Ollama](https://ollama.com) for local models, 
- [Microsoft Foundry](https://azure.microsoft.com/products/ai-foundry/)
- [Geoapify](https://www.geoapify.com/) account and API key 
- [Deepgram](https://deepgram.com/) account and API key with Voice Agent API access 

Run all commands below from the repository root.

## Set up with an AI agent

The repo includes a `concierge-setup` [agent skill](.agents/skills/concierge-setup/SKILL.md)
that checks prerequisites, walks you through each account and key, and starts and
verifies the app. Open the repo in your agent and ask it to set up the project,
or invoke the skill directly:

| Agent | Invoke |
| --- | --- |
| Claude Code | `/concierge-setup` |
| Codex | `$concierge-setup`, or pick it from `/skills` |
| GitHub Copilot | Ask to "use the concierge-setup skill"; Copilot selects skills from your prompt |

The agent never handles your keys. When it asks for one, copy the command it
gives you, replace `<your-key>` with your key, and run it in your own terminal:

```bash
dotnet user-secrets set "Parameters:<parameter-name>" "<your-key>" --project src/Concierge.AppHost
```

Without an agent, run the setup check to see what's missing:

```bash
dotnet run --file .agents/skills/concierge-setup/scripts/check-setup.cs
```

Then follow the [account steps](.agents/skills/concierge-setup/references/accounts.md)
and the sections below.

## Run locally

### 1. Prepare the models

Development defaults to Ollama for both chat and embeddings.

```bash
ollama pull qwen3.8:27b-mlx
ollama pull embeddinggemma-2:740m
ollama create concierge-chat -f models/ollama/Modelfile.concierge-chat
```

The [Modelfile](models/ollama/Modelfile.concierge-chat) sets a 32,768-token
context. Load the chat model before trying voice to avoid its initial load delay.

To use Foundry instead, configure it [below](#use-foundry) and skip the local models
for whichever capability you switch.

### 2. Set the service keys

Geoapify for mapping features and is **required at startup**:

```bash
dotnet user-secrets set "Parameters:geoapify-api-key" "<your-key>" --project src/Concierge.AppHost
```

Deepgram key is required for voice features:

```bash
dotnet user-secrets set "Parameters:deepgram-api-key" "<your-key>" --project src/Concierge.AppHost
```

Keep keys in the AppHost user-secrets store, not checked-in settings.
`aspire secret set "<key>" "<value>" --apphost src/Concierge.AppHost` 

### 3. Prepare HTTPS and the tunnel

Once per machine:

```bash
dotnet dev-certs https --trust
devtunnel user login
```

Aspire creates the public voice callback tunnel and supplies its URL automatically.
The web UI uses a separate local HTTPS endpoint.

### 4. Start and sign in

```bash
aspire run --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

For an Aspire-managed background run:

```bash
aspire start --non-interactive --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
aspire wait web --status healthy --timeout 180 --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
aspire describe --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

Stop only this AppHost without resetting its persistent data:

```bash
aspire stop --non-interactive --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```

1. Open the Aspire dashboard and wait for the services to become healthy.
2. Open the **web** endpoint.
3. Check in using a demo guest's last name and room number from the table below.

Startup prepares the database, demo stays, and Markdown knowledge base.

#### Demo guest check-in information

These are seeded demo stays. Use any row's last name and room number to verify
the stay and start a concierge chat.

| Guest last name | Room number |
| --- | --- |
| Rivera | 412 |
| Morgan | 812 |
| Phillip | 112 |

## Try the concierge

- Ask a hotel question: “What time is checkout?”
- Check the weather: “What's the weather tomorrow?”
- Ask for nearby cafes, then request a ride to “that coffee shop.”
- Use the voice control for a spoken conversation and allow microphone access when prompted.

## Configuration

Development defaults use local Ollama models. Settings, defaults, and validation
rules for every service are in the [configuration reference](docs/configuration.md).
Secrets come from the AppHost user-secrets store.

Commonly changed settings:

| Key | Default | Purpose |
| --- | --- | --- |
| `AI:Chat:Provider` / `AI:Embeddings:Provider` | `Ollama` | `Ollama` or `Foundry`. |
| `Hotel:Address` | `2217 NW Miami Court, Miami, FL 33127` | Anchor for weather, routes, and rides. |
| `Deepgram:ListenModel` | `flux-general-en` | Speech-to-text model. |
| `Deepgram:SpeakModel` | `flux-hannah-en` | Text-to-speech voice. |
| `Deepgram:Speed` | `1.0` | Speech rate. |

### Use Foundry

For chat, set the service endpoint and **deployment name**:

```bash
dotnet user-secrets set "AI:Chat:Provider" "Foundry" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Chat:Endpoint" "https://<resource>.openai.azure.com/" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Chat:Model" "<chat-deployment>" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Chat:RequestTimeoutSeconds" "15" --project src/Concierge.AppHost
dotnet user-secrets set "Parameters:foundry-chat-api-key" "<your-key>" --project src/Concierge.AppHost
```

To switch embeddings too:

```bash
dotnet user-secrets set "AI:Embeddings:Provider" "Foundry" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Embeddings:Endpoint" "https://<resource>.openai.azure.com/" --project src/Concierge.AppHost
dotnet user-secrets set "AI:Embeddings:Model" "<embedding-deployment>" --project src/Concierge.AppHost
dotnet user-secrets set "Parameters:foundry-embeddings-api-key" "<your-key>" --project src/Concierge.AppHost
```

The embedding deployment must support **768 dimensions**. Without an API key,
the app uses `DefaultAzureCredential`.

## Tests and evaluation

Docker must be running for the integration tests. Run `just` to list all recipes.

```bash
just test
just live-place-weather      # Geoapify/Open-Meteo check; uses the AppHost user-secret key
just live-concierge-actions  # action checks against the running app
just eval-validate
just eval-run                # saves a new result; optional run ID: just eval-run my-run
```

Run a single live-check scenario against the running app, or `selftest` to check
the harness offline:

```bash
dotnet run --file tests/Concierge.LiveChecks/concierge-actions-smoke.cs -- <scenario>
```

Scenarios: `nearby`, `nearest`, `weather-now`, `ride-conversation`, `selftest`.
Live checks keep the fictional guest, stay, and ride rows they create.

Browser audio worklet tests require Node.js:
`node --test tests/voice-play-worklet.test.mjs`.

## Troubleshooting and reset

| Problem | Action |
| --- | --- |
| Tunnel authentication requested | Run `devtunnel user login`, then retry startup. |
| HTTPS certificate not trusted | Run `dotnet dev-certs https --trust`. |
| Hotel location fails at startup | Check the Geoapify key and `Hotel:Address`; inspect WorkflowServer logs in Aspire. |
| Missing AI endpoint/model | Set both values for the selected provider; Foundry uses deployment names. |
| Timeout setting rejected | Keep chat at 18 seconds or less and retrieval embeddings at 8 or less. |
| Demo stays expired or a schema/workflow change requires fresh data | Reset local data using the command below. |

**Reset deletes local Postgres data and Temporal history.** Use it when you want
a fresh demo, then start the app again:

```bash
just reset
aspire run --apphost src/Concierge.AppHost/Concierge.AppHost.csproj
```
