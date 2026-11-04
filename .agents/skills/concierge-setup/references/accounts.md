# External account steps

These steps describe third-party sites, which change their UI on their own
schedule. If a step doesn't match what the user sees, follow the linked doc.

Setting names don't appear here on purpose. Get them from `check-setup.cs`
output or `src/Concierge.AppHost/AppHost.cs`.

## Geoapify

Maps, places, routes, and hotel geocoding. The WorkflowServer won't start
without a working key.

1. Sign up or sign in at <https://myprojects.geoapify.com>.
2. Create a project. Geoapify generates an API key for it; copy it.
3. In the project's key settings, make sure no origin, referrer, or IP
   restriction blocks server-side calls from the user's machine. A blocked call
   returns HTTP 401.

Docs: <https://www.geoapify.com/get-started-with-maps-api/>

## Deepgram

Voice (Voice Agent API).

1. Sign up at <https://console.deepgram.com/signup>. A first project is created
   automatically.
2. Select the project from the top-left dropdown, then go to
   **Settings → API Keys → Create a New API Key**.
3. Enter a name, choose a role (Member is enough for API calls) and an
   expiration, then select **Create Key**.
4. Copy the key immediately; Deepgram doesn't show it again.

Docs: <https://developers.deepgram.com/docs/create-additional-api-keys>

## Microsoft Foundry (Azure OpenAI)

Chat and/or embeddings when the provider is `Foundry`.

1. **Subscription:** use an existing one, or create one at
   <https://azure.microsoft.com/pricing/purchase-options/azure-account>.
2. **Resource:** in <https://portal.azure.com>, select **Create a resource**,
   search for **Azure OpenAI**, and select **Create**. Choose a subscription,
   resource group, region, and name, with the **Standard** tier. Keep network
   access on **All networks** so the app can call it from the user's machine.
   Then **Review + submit → Create**.
3. **Deployments:** in <https://ai.azure.com>, open the resource, go to
   **Deployments** (or **Models + endpoints**), then
   **+ Deploy model → Deploy base model**. Deploy the chat and/or embedding
   models chosen in step 3 of the skill. The app calls them by *deployment
   name*, so record those names. If a model isn't offered in the region, try the
   **Global Standard** deployment type.
4. **Endpoint and key:** in the Azure portal, open the resource, then
   **Resource Management → Keys and Endpoint**. Copy the **Endpoint**
   (`https://<resource>.openai.azure.com/`) and **KEY 1**. One resource and key
   can serve both chat and embeddings.
5. **Keyless alternative:** skip the key and run `az login`. The app falls back
   to `DefaultAzureCredential`, and the signed-in identity needs an Azure OpenAI
   user role on the resource.

Docs: <https://learn.microsoft.com/azure/ai-foundry/openai/how-to/create-resource>

## Ollama

Local chat and/or embeddings when the provider is `Ollama`.

1. Install from <https://ollama.com/download> and make sure the server is
   running.
2. Run the model pull and create commands from the README's "Prepare the models"
   section.

## Dev Tunnels

Public callback URL for voice. No key to store.

1. Install the CLI: <https://learn.microsoft.com/azure/developer/dev-tunnels/get-started>
2. Run `devtunnel user login` (Microsoft account), or add `-g` to sign in with
   GitHub.
