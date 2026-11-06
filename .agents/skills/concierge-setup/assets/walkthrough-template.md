# Walkthrough template

Fill this in and send it to the user as **one message**, so they can work
through every step without waiting on you.

- Include only the sections for items `check-setup.cs` reported as missing or
  that the chosen providers need. Renumber them in order.
- Take account steps and links from `references/accounts.md` and copy them in
  full: every numbered step and every link. Don't shrink them into one-line
  comments or "see docs" pointers.
- Put every command the user runs in its own fenced `bash` block so it can be
  copied and pasted. Never put a user-run command in inline code.
- Take setting names from `check-setup.cs` output and `AppHost.cs`.
- Take model requirements from `docs/configuration.md` and
  `tests/Concierge.Evaluation/README.md`.
- Drop the `<!-- -->` notes when you fill it in.

---

<One line: what's already done (tools, Docker, cert, and so on) and what's left.>

All commands run from the repository root. For each key, copy the command,
replace `<your-key>` with your key, and run it in your own terminal. Don't paste
the filled-in command back here.

### <n>. <Service name> (<what it powers>; <required at startup | needed for voice | optional>)

1. <Account step from references/accounts.md, with its link>
2. <...every remaining step...>
3. Store it:

   ```bash
   dotnet user-secrets set "Parameters:<parameter-name>" "<your-key>" --project <AppHost dir>
   ```

Docs: <doc link from references/accounts.md>

<!-- Repeat for each missing service, in this order: Geoapify, Deepgram, Foundry or Ollama. -->

### <n>. Microsoft Foundry (chat and/or embeddings)

**Model requirements** (from the repo):

- Chat: <rule from docs/configuration.md and the evaluated deployment from the
  evaluation README, e.g. which model and reasoning effort were evaluated>
- Embeddings: <dimension rule>

<Resource, deployment, and endpoint/key steps from references/accounts.md, in full.>

Store the key(s):

```bash
dotnet user-secrets set "Parameters:<chat-key-parameter>" "<your-key>" --project <AppHost dir>
```

```bash
dotnet user-secrets set "Parameters:<embeddings-key-parameter>" "<your-key>" --project <AppHost dir>
```

Then send me the endpoint and the deployment names. I'll set the non-secret
settings (<list the setting names>). <Or, if the user already gave them: "I've
set these: ...", listing names and values.>

<Keyless alternative from references/accounts.md.>

### <n>. One-time machine setup

<Only the items that are missing: dev cert trust, devtunnel login. Each with its
command and one line on why it's needed.>

### <n>. Next

When you've finished the steps above, tell me. I'll re-run the setup check,
start the AppHost, and confirm every resource is healthy.
