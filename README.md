# OAuth Card POC — Generic OAuth 2 with Bot Framework + Microsoft 365

A complete, working reference for configuring **Generic OAuth 2** sign-in for
a Bot Framework bot that runs as both a **Teams chat bot** and a **Microsoft
365 Copilot Custom Engine Agent** (CEA), against any RFC 6749 — compliant
authorization-code provider.

The walkthrough uses **GitHub OAuth** as the identity provider because:

- It is the cleanest demonstration of *truly generic* OAuth 2 — no Microsoft
  identity glue, opaque (non-JWT) access tokens, vanilla
  authorization-code-with-PKCE-disabled flow.
- A GitHub OAuth App is free and takes 30 seconds to register.
- The same configuration pattern works unchanged for Auth0, Okta, Google,
  your own STS, or Entra ID in "generic" mode — just point the four IdP URLs
  at your provider.

## What this gives you

| Component | What it is |
|---|---|
| `bot/app.py` | A small Python bot using the standard Bot Framework `OAuthPrompt` to render the OAuth Card and complete sign-in. ~200 LoC, single file. |
| `infra/04-create-oauth-connection.sh` | Provisions the Bot Service `Generic OAuth 2` connection with all eleven required template parameters, idempotent, IdP-agnostic. |
| `infra/06-build-manifest.sh` | Builds the unified Microsoft 365 app package that surfaces the same bot in both Teams chat and M365 Copilot. |
| `infra/01..05*.sh` | One-shot scripts to provision the bot's Azure resources from a clean subscription. |

## Architecture

```
┌──────────────────────────────┐         ┌──────────────────────────────┐
│   Microsoft Teams chat       │         │   M365 Copilot (CEA)         │
│   manifest: bots[]           │         │   manifest:                  │
│                              │         │   copilotAgents              │
│                              │         │     .customEngineAgents[]    │
└─────────────┬────────────────┘         └────────────┬─────────────────┘
              │                                        │
              │   user activities                      │
              ▼                                        ▼
        ┌────────────────────────────────────────────────────┐
        │          Azure Bot Service (channel)               │
        │          api.botframework.com                      │
        └─────────────────────────┬──────────────────────────┘
                                  │
                                  ▼ activities + invokes
                        ┌──────────────────┐
                        │  Container App   │  bot/app.py
                        │  (Python)        │  uses OAuthPrompt
                        │                  │  with connection name
                        │                  │  "github-as-generic"
                        └──────┬───────────┘
                               │ 1. GetSignInResource
                               ▼
                ┌────────────────────────────────────┐
                │  Bot Service OAuth Connection      │
                │  Generic OAuth 2 (oauth2generic)   │
                │  → GitHub authorize / token URLs   │
                └────────────────┬───────────────────┘
                                 │ 2. signed sign-in URL
                                 ▼
                ┌────────────────────────────────────┐
                │  github.com/login/oauth/authorize  │
                │  user authenticates in popup       │
                └────────────────┬───────────────────┘
                                 │ 3. redirect with code
                                 ▼
                ┌────────────────────────────────────┐
                │  token.botframework.com            │
                │  /.auth/web/redirect               │
                │  → posts back to host channel      │
                └────────────────┬───────────────────┘
                                 │ 4. signin/verifyState invoke
                                 ▼ (carries the auth code)
                        ┌──────────────────┐
                        │  Bot exchanges   │
                        │  for access token│
                        │  via Bot Service │
                        │  UserTokenClient │
                        └──────────────────┘
```

## Prerequisites

- An Azure tenant + subscription where you have **Application Administrator**
  and **Owner** roles (the POC creates an AAD app for the bot's own identity,
  an Azure Bot resource, and a Container App).
- A Microsoft 365 tenant where you can sideload custom Teams apps. It can be
  the same tenant as Azure or a different one.
- A GitHub account.
- CLI tools:
  ```bash
  az --version           # Azure CLI 2.60+
  python3 --version      # 3.11+
  zip --version
  ```

## Step-by-step setup

### 1. Register a GitHub OAuth App

1. Go to <https://github.com/settings/developers> → **OAuth Apps** → **New OAuth App**
2. Fill in:
   | Field | Value |
   |---|---|
   | **Application name** | `oauth-card-poc-github` (or anything) |
   | **Homepage URL** | `https://example.com` (any valid URL) |
   | **Authorization callback URL** | `https://token.botframework.com/.auth/web/redirect` |
   | **Enable Device Flow** | unchecked |
3. Click **Register application**.
4. Note the **Client ID** shown on the next page.
5. Click **Generate a new client secret** and **copy the secret immediately** —
   GitHub only shows it once.

### 2. Clone & configure inputs

```bash
git clone https://github.com/james-tn/oauth-card-poc.git
cd oauth-card-poc
cp .env.example .env
```

Edit `.env` and fill in the inputs at the top, including the GitHub credentials
from step 1:

```bash
TENANT_ID=<your-azure-tenant-guid>
SUBSCRIPTION_ID=<your-azure-subscription-guid>
LOCATION=eastus2
RESOURCE_GROUP=oauth-card-poc-rg

BOT_DISPLAY_NAME="OAuth POC"
BOT_AAD_APP_NAME=oauth-poc-bot-app
BOT_HANDLE=oauthpocbot01
OAUTH_CONNECTION_NAME=github-as-generic

# IdP (GitHub by default)
IDP_CLIENT_ID=Ov23li...
IDP_CLIENT_SECRET=10a5eb...
IDP_AUTHORIZE_URL=https://github.com/login/oauth/authorize
IDP_TOKEN_URL=https://github.com/login/oauth/access_token
IDP_SCOPES=read:user
```

### 3. Sign in to Azure

```bash
az login --tenant <TENANT_ID>
az account set --subscription <SUBSCRIPTION_ID>
```

If your tenant requires a CAE/MFA claim challenge for management operations:

```bash
az login --tenant <TENANT_ID> \
    --scope "https://management.core.windows.net//.default" \
    --use-device-code
```

### 4. Provision Azure resources

Run the scripts in order. Each appends generated values (app id, secret, bot
URL, …) to your `.env`:

```bash
cd infra
bash 01-create-aad-app.sh           # AAD app for the BOT's own identity
bash 03-create-bot-service.sh       # Azure Bot resource + Teams channel
bash 04-create-oauth-connection.sh  # Generic OAuth 2 connection → GitHub
bash 05-deploy-container-app.sh     # ACR build + Container App deploy
bash 06-build-manifest.sh           # Builds manifest/oauth-poc-app.zip
```

> Script `02-create-test-user.sh` is **only needed if you point the IdP at
> Entra ID** instead of GitHub. Skip it for the GitHub path.

After step 4 you should see something like:

```
==> Configuring OAuth connection 'github-as-generic'
    provider:      Generic OAuth 2 (oauth2generic)
    authorize URL: https://github.com/login/oauth/authorize
    token URL:     https://github.com/login/oauth/access_token
    scopes:        read:user
==> PUT github-as-generic?api-version=2022-09-15
    HTTP 201 OK
    provisioningState: Succeeded
    serviceProvider:   Oauth 2 Generic Provider
```

### 5. Sideload the app

`manifest/oauth-poc-app.zip` from step 4 is a unified Microsoft 365 app
package. The same package works in both surfaces:

- **Teams**: Apps → Manage your apps → Upload an app → Upload a custom app
- **M365 Copilot**: <https://m365.cloud.microsoft> → Agents panel → upload

### 6. Run the test

In either Teams chat or the M365 Copilot agent, type anything:

```
hello
```

You should see an OAuth Card with a **Sign In** button. Click it. A popup
opens to `github.com/login/oauth/authorize`. Authenticate and authorize the
app. The popup closes and the bot replies:

```
✅ Sign-in completed. Claims from token:
(opaque access token, length=40 — IdP did not return a JWT)
```

The opaque-token message is expected for GitHub (its access tokens are
opaque `gho_*` strings, not JWTs). For an IdP that returns a JWT (Entra,
Auth0 in JWT mode, Google, …) the bot will pretty-print the standard claims.

### 7. Reset / iterate

The bot supports two utility commands inside the chat:

- `/reset` — cancels any in-flight OAuth dialog (use after a misclick)
- `/logout` — signs the user out via `UserTokenClient.sign_out_user`

To redeploy after editing `bot/app.py`, re-run only step 5 of provisioning:

```bash
bash infra/05-deploy-container-app.sh
```

## Configuration reference

### The eleven parameters of the `oauth2generic` connection

The Bot Service service provider `8379c6d2-b262-4d4f-b89b-68dc5b5f5482`
("Oauth 2 Generic Provider") requires every parameter below. The first time
you provision a connection that is missing any of them, ARM will accept the
PUT and Bot Service will report `provisioningState: Succeeded`, but the
first user sign-in click will fail with
`ServiceError: An error occured while retrieving the signin link`. Always
provide all eleven.

| Key | Purpose | Example (GitHub) |
|---|---|---|
| `ClientId` | OAuth app's client id at the IdP | `Ov23li…` |
| `ClientSecret` | OAuth app's client secret at the IdP | `10a5eb…` |
| `ScopeListDelimiter` | Separator between scope strings in the authorize URL | `" "` (space) |
| `AuthorizationUrlTemplate` | The IdP's authorize endpoint (no query string) | `https://github.com/login/oauth/authorize` |
| `AuthorizationUrlQueryStringTemplate` | Query string template; **must include `&state={State}`** for CSRF protection | `?client_id={ClientId}&response_type=code&redirect_uri={RedirectUrl}&scope={Scopes}&state={State}` |
| `TokenUrlTemplate` | The IdP's token endpoint | `https://github.com/login/oauth/access_token` |
| `TokenUrlQueryStringTemplate` | Usually empty — most IdPs put params in the body | `""` |
| `TokenBodyTemplate` | POST body for the code-for-token exchange | `code={Code}&grant_type=authorization_code&redirect_uri={RedirectUrl}&client_id={ClientId}&client_secret={ClientSecret}` |
| `RefreshUrlTemplate` | Refresh endpoint, often the same as `TokenUrlTemplate` | `https://github.com/login/oauth/access_token` |
| `RefreshUrlQueryStringTemplate` | Usually empty | `""` |
| `RefreshBodyTemplate` | Refresh POST body | `refresh_token={RefreshToken}&grant_type=refresh_token&client_id={ClientId}&client_secret={ClientSecret}` |

Why we use direct ARM REST instead of `az bot connection create`: the CLI
silently rewrites parameter casing and drops keys it doesn't recognise,
making it very hard to land an `oauth2generic` connection cleanly.

### Manifest essentials

The package built by `infra/06-build-manifest.sh` registers the same bot in
both hosts. The two relevant blocks are:

```jsonc
{
  "manifestVersion": "devPreview",   // required for customEngineAgents

  "bots": [
    { "botId": "<BOT_APP_ID>", "scopes": ["personal"] }
  ],

  "copilotAgents": {
    "customEngineAgents": [
      { "id": "<BOT_APP_ID>", "type": "bot" }
    ]
  },

  "validDomains": [
    "<your bot's container-apps host>",
    "token.botframework.com",
    "login.microsoftonline.com"
  ]
}
```

`copilotAgents.customEngineAgents` is in **public preview** at the time of
writing and is only present in the `devPreview` manifest schema; the GA
1.19 / 1.20 / 1.21 schemas reject it.

`validDomains` must include every host that any popup in the OAuth chain
loads content from. Always include `token.botframework.com` (the channel
callback) and your IdP's login host. Include `login.microsoftonline.com`
even with non-Microsoft IdPs — Teams sometimes navigates through it during
its own SSO bootstrap.

### Bot code

The bot uses the standard `OAuthPrompt` from `botbuilder-dialogs`. The full
sign-in flow is:

```python
async def prompt_step(self, step):
    return await step.prompt(
        OAuthPrompt.__name__,
        PromptOptions(prompt=MessageFactory.text("Please sign in.")),
    )

async def show_token_step(self, step):
    token_response = step.result
    token = token_response.token
    claims = _decode_jwt_claims(token)
    await step.context.send_activity(
        f"✅ Sign-in completed. Claims from token:\n```\n{claims}\n```"
    )
    return await step.end_dialog()
```

`OAuthPrompt` handles both the OAuth Card render and the `signin/*` invoke
activities that complete the flow. **Do not hand-roll OAuth in Bot
Framework** — the SDK's prompt is what subscribes to the silent-completion
invokes; without it, the user is stuck typing 6-digit codes.

## What you'll see in the logs

When a user signs in successfully, the bot logs an `on_invoke_activity`
entry with one of these names:

| Invoke name | When | Means |
|---|---|---|
| `signin/tokenExchange` | Teams chat, **Entra-IdP only**, when SSO trust is configured | True silent SSO. No code is ever generated. The invoke carries an Entra-issued SSO token assertion that the bot exchanges via OBO. |
| `signin/verifyState` (Teams chat) | Teams chat with non-Entra IdPs | OAuth Card flow. The popup auto-submits a one-time code via the channel; the user does not see it. |
| `signin/verifyState` (Copilot CEA) | Always | M365 Copilot's CEA host completes OAuth Card sign-in via this invoke. The popup auto-closes; the code is hidden from the user. |

All three paths produce the same `OAuthPrompt` outcome — a token in the
prompt result. The bot code does not need to distinguish between them.

## Cleanup

```bash
az group delete --name oauth-card-poc-rg --yes --no-wait
az ad app delete --id "$BOT_APP_ID"
```

If you set up GitHub: revoke the OAuth App at
<https://github.com/settings/developers>.

## License

POC code, MIT.
