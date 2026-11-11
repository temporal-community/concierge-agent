# Architecture overview

Top-level view of the Concierge Agent as orchestrated by the Aspire AppHost
([AppHost.cs](../src/Concierge.AppHost/AppHost.cs)). Every guest turn, from any client, becomes a
Temporal Update on one long-running `ConciergeStayWorkflow` per stay; that workflow is the single
source of truth for the conversation.

```mermaid
flowchart LR
    %% ---------- Clients ----------
    subgraph Clients
        Browser["Browser<br/>Blazor Server chat (AG-UI)<br/>+ mic/speaker worklets"]
        Device["Room device / remote client<br/>(ESP32 companion, ADR 0012)"]
    end

    %% ---------- External services ----------
    subgraph External["External services"]
        Deepgram["Deepgram<br/>Voice Agent API<br/>(STT + TTS)"]
        LLM["Chat + embedding models<br/>Ollama (local) or Microsoft Foundry"]
        Geo["Geoapify<br/>places, hours, routes, geocoding"]
        Meteo["Open-Meteo<br/>weather"]
    end

    Tunnel["Dev Tunnel<br/>think-tunnel (public HTTPS)"]

    %% ---------- Aspire-orchestrated app ----------
    subgraph Aspire["Aspire AppHost"]
        subgraph Web["Concierge.Web"]
            UI["Chat UI + AG-UI endpoint<br/>/agui/concierge"]
            API["Guest REST API<br/>/api/concierge/*"]
            Relay["Voice relay<br/>/api/concierge/voice (WebSocket)"]
            Think["Think endpoint<br/>/v1/chat/completions"]
            Svc["ConciergeService<br/>(Temporal client)"]
        end

        Temporal[("Temporal dev server")]

        subgraph WFS["Concierge.WorkflowServer (worker)"]
            Stay["ConciergeStayWorkflow<br/>one per stay: turns, memory,<br/>tool loop, ride feed stream"]
            Setup["HotelSetupWorkflow<br/>seed stays + index knowledge"]
            Acts["Activities<br/>LLM call (Durable AI), RAG retrieval,<br/>weather, places, routes, transcript"]
        end

        subgraph Rides["Concierge.RideService (worker + HTTP)"]
            RideAPI["Ride API<br/>/rides, /guests/{id}/rides"]
            RideWF["RideBookingWorkflow<br/>book, transition, cancel"]
        end

        subgraph PG["PostgreSQL + pgvector"]
            DBC[("concierge DB<br/>stays, transcript,<br/>knowledge embeddings")]
            DBR[("rides DB")]
        end
    end

    KB[/"knowledge/hotel-zgy/*.md"/]

    %% ---------- Text path ----------
    Browser -- "text turns (AG-UI SSE)" --> UI
    Device -- "sessions / turns (HTTPS)" --> Tunnel
    Tunnel -- "opt-in: RemoteAccess:PublicTunnel" --> API
    UI --> Svc
    API --> Svc

    %% ---------- Voice path ----------
    Browser <-- "PCM audio (WebSocket)" --> Relay
    Relay <-- "Voice Agent session" --> Deepgram
    Deepgram -- "think step (custom LLM)" --> Tunnel
    Tunnel --> Think
    Think --> Svc

    %% ---------- Temporal ----------
    Svc -- "Updates: SubmitTurn, History,<br/>Reset, End; ride stream subscribe" --> Temporal
    Temporal <--> Stay
    Temporal <--> Setup
    Stay --> Acts
    Setup --> Acts

    %% ---------- Activities to dependencies ----------
    Acts --> LLM
    Acts --> Geo
    Acts --> Meteo
    Acts --> DBC
    Setup -. "reads + embeds" .-> KB
    Acts -- "pg_notify partial answer text" --> DBC
    DBC -. "LISTEN (streaming tokens)" .-> Svc

    %% ---------- Rides ----------
    Acts -- "ride tools (HTTP)" --> RideAPI
    RideAPI -- "Update-with-start" --> Temporal
    Temporal <--> RideWF
    RideWF --> DBR
    RideWF -. "publish ride activity<br/>to stay's Workflow Stream" .-> Temporal
```

## How a turn flows

1. **Text.** The browser posts to the AG-UI endpoint. `StayWorkflowChatClient` forwards the latest
   user message through `ConciergeService` as a `SubmitTurn` Update on the stay workflow.
2. **Voice.** The browser streams audio to the Web voice relay, which owns the Deepgram Voice Agent
   session. Deepgram's think step calls back through the dev tunnel to the Think endpoint, which
   submits the same `SubmitTurn` Update. Deepgram speaks the answer back through the relay.
3. **Remote clients.** The room device calls `/api/concierge/sessions` and `/turns` over the tunnel
   (opt-in). Same Update, same stay.
4. **Inside the workflow.** The stay workflow retrieves hotel knowledge (pgvector), runs the model
   as a durable activity, and executes tool calls (weather, places, opening hours, routes, rides) as
   activities. It persists the transcript to Postgres.
5. **Streaming back.** The model activity publishes partial answer text with `pg_notify`; Web
   `LISTEN`s and streams it to the chat while the Update completes.
6. **Rides.** Ride tools call the ride service over HTTP. It runs a `RideBookingWorkflow` per ride
   and publishes status changes onto the stay workflow's Workflow Stream, which Web subscribes to
   for the ride activity panel.

## Startup

`HotelSetupWorkflow` seeds the demo stays and chunks and embeds `knowledge/hotel-zgy/*.md` into
pgvector. The workflow server's readiness check gates the Web start.

## See also

- [architecture.html](architecture.html): infographic version of this page
- [configuration.md](configuration.md): settings for every service
