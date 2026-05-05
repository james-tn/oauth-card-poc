# OAuth Card POC — Generic OAuth 2 vs. the magic-code prompt

A minimal, end-to-end reproduction that proves whether **Generic OAuth 2** for a
Bot Framework Custom Engine Agent in Microsoft Teams produces the dreaded
**6-digit magic-code prompt** during sign-in, or completes silently.

## TL;DR — Result

> ✅ **Generic OAuth 2 does NOT inherently produce a magic code in Teams.**
> Silent token-exchange (`signin/tokenExchange` invoke) completes cleanly when
> the bot, the OAuth Connection, the Teams manifest, and the IdP app
> registration are configured correctly.

If your users see a magic-code prompt, it is a **misconfiguration symptom**, not
a Generic OAuth 2 limitation. Jump to [Diagnosis checklist](#diagnosis-checklist-for-magic-code-symptoms)
to figure out which part of your configuration is broken.

## Why this POC exists

A real customer scenario:
- Their Teams custom-engine agent's bot uses a "Generic OAuth 2" Bot Service
  connection pointing at the customer's own IdP (not Entra).
- Users were prompted for a 6-digit magic code on sign-in.
- The Microsoft team suspected misconfiguration; the customer suspected the
  Generic OAuth 2 connection type.

This POC settles the question by **reproducing the exact pattern in your own
tenant**, but pointing the Generic OAuth 2 connection at Entra so you don't
need a separate IdP. The result is a green-path silent sign-in — proof that
the protocol path itself works.

## Architecture

```
┌─────────────┐   1. user types        ┌──────────────────┐
│  Teams      │──────────────────────▶ │  Bot Framework   │
│  desktop    │   "hello"               │  channel         │
└─────────────┘                        └────────┬─────────┘
       ▲                                        │ 2. forward to
       │                                        ▼
       │                              ┌──────────────────┐
       │                              │  Container App   │  bot/app.py
       │  6. card displayed           │  (Python)        │
       │                              │                  │
       │                              │  uses OAuthPrompt│
       │                              │  with conn name  │
       │                              │  "entra-as-…"    │
       │                              └────────┬─────────┘
       │                                        │ 3. GetSignInResource
       │                                        ▼
       │                          ┌────────────────────────────┐
       │                          │  api.botframework.com      │
       │                          │  Bot Service OAuth         │
       │                          │  Connection                │
       │                          │  (Generic OAuth 2 →        │
       │                          │   Entra v2 endpoint)       │
       │                          └────────────┬───────────────┘
       │                                        │ 4. signed sign-in URL
       │                                        ▼
       │                          ┌────────────────────────────┐
       │  5. user authenticates   │  login.microsoftonline.com │
       └──────────────────────────│  (acting as the IdP)        │
              in popup            └─────────────┬──────────────┘
                                                 │
                                                 ▼
                                  signin/tokenExchange invoke
                                  back to the bot → token →
                                  bot replies with claims
```

## Repo layout

```
oauth-card-poc/
├── README.md                    ← you are here
├── .env.example                 ← copy to .env and fill in the inputs
├── .gitignore
├── bot/
│   ├── app.py                   ← single-file aiohttp bot, OAuthPrompt
│   ├── pyproject.toml
│   └── Dockerfile
├── infra/
│   ├── 01-create-aad-app.sh     ← single-tenant AAD app + secret + identifierUri
│   ├── 02-create-test-user.sh   ← non-admin user in your tenant for sign-in
│   ├── 03-create-bot-service.sh ← Azure Bot (F0) + Teams channel
│   ├── 04-create-oauth-connection.sh ← Generic OAuth 2 connection (the tricky one)
│   ├── 05-deploy-container-app.sh    ← ACR build + Container Apps deploy
│   └── 06-build-manifest.sh     ← Teams app manifest + zip
└── manifest/
    ├── manifest.json            ← generated
    ├── color.png  outline.png   ← placeholder icons
    └── oauth-poc-app.zip        ← generated, side-load this in Teams
```

## Prerequisites

- Azure tenant + subscription where you have the Application Administrator
  and Owner roles (the POC creates an AAD app, a test user, an Azure Bot
  resource, and a Container App).
- A Microsoft 365 tenant where you can side-load custom Teams apps. You can
  use the same tenant as Azure or a different one (the user signing in does
  NOT need to be in the bot's tenant — that is the entire point of the POC).
- Tools installed locally:
  ```bash
  az --version           # Azure CLI 2.60+
  gh --version           # GitHub CLI (only if you fork/push)
  docker --version       # not strictly needed - we use ACR Tasks
  python3 --version      # 3.11+
  zip
  ```

## Step-by-step setup

### 1. Clone & configure inputs

```bash
git clone https://github.com/james-tn/oauth-card-poc.git
cd oauth-card-poc
cp .env.example .env
```

Edit `.env` and fill in **only the input fields** at the top (everything below
`# ---- Filled in by infra/01..05 scripts ----` is populated by the scripts):

```bash
TENANT_ID=<your-azure-tenant-guid>
SUBSCRIPTION_ID=<your-azure-subscription-guid>
TENANT_DOMAIN=<your-tenant>.onmicrosoft.com
LOCATION=eastus2
RESOURCE_GROUP=oauth-card-poc-rg
BOT_DISPLAY_NAME="OAuth POC"
BOT_AAD_APP_NAME=oauth-poc-bot-app
BOT_HANDLE=oauthpocbot01           # 4-42 chars, [a-zA-Z0-9_-], unique in your sub
OAUTH_CONNECTION_NAME=entra-as-generic
TEST_USER_DISPLAY_NAME="OAuth POC Test User"
TEST_USER_UPN_PREFIX=oauthpoctest  # final UPN: oauthpoctest@<tenant-domain>
BOT_PORT=8080
CAE_ENV_NAME=oauth-poc-cae-env
CAE_APP_NAME=oauthpocbotapp        # 2-32 chars, lowercase + hyphen
CAE_LOCATION=eastus2
```

### 2. Sign in to Azure

```bash
az login --tenant <TENANT_ID>
az account set --subscription <SUBSCRIPTION_ID>
```

If your tenant enforces Conditional Access with MFA on the management plane,
you may need to satisfy the claims challenge:

```bash
az login --tenant <TENANT_ID> \
    --scope "https://management.core.windows.net//.default" \
    --claims-challenge "eyJhY2Nlc3NfdG9rZW4iOnsiYWNycyI6eyJlc3NlbnRpYWwiOnRydWUsInZhbHVlcyI6WyJwMSJdfX19" \
    --use-device-code
```

### 3. Run the provisioning scripts in order

Each script is idempotent-ish (safe to re-run, but the first failure usually
needs you to clean up and re-run from that step).

```bash
cd infra
bash 01-create-aad-app.sh           # AAD app + client secret + api://botid-… URI + redirect URI
bash 02-create-test-user.sh         # Non-admin user in your tenant
bash 03-create-bot-service.sh       # Azure Bot resource + Teams channel
bash 04-create-oauth-connection.sh  # ⚠ THE FRAGILE STEP — see "Pitfalls" below
bash 05-deploy-container-app.sh     # ACR build + Container Apps deploy
bash 06-build-manifest.sh           # Teams manifest + oauth-poc-app.zip
```

After each script, look at `.env` — values like `BOT_APP_ID`, `BOT_APP_SECRET`,
`TEST_USER_PASSWORD`, `BOT_URL` will be appended.

### 4. Side-load the app into Teams

1. In Teams, open **Apps → Manage your apps → Upload an app → Upload a custom app**.
2. Pick `manifest/oauth-poc-app.zip`.
3. Add the bot to a personal chat.

### 5. Run the test

In the chat, type:

```
hello
```

You should get an OAuth Card titled **"Please sign in"** with an active
**Sign In** button.

Click **Sign In**. A popup window should appear at
`login.microsoftonline.com`. Sign in as the test user (or any user in your
bot's tenant — credentials are in your `.env` after step 02). The popup
should close on its own and the bot should respond with the decoded JWT
claims:

```json
{
  "aud": "00000003-0000-0000-c000-000000000000",
  "iss": "https://sts.windows.net/<tenant-id>/",
  "name": "...",
  "upn": "...",
  "tid": "...",
  "scp": "openid profile User.Read email",
  "appid": "<your-bot-app-id>"
}
```

If you got the claims back **without typing a magic code**, the silent
token-exchange flow worked — same conclusion as the POC.

### 6. Reset / iterate

The bot supports two utility commands inside the chat:

- `/reset` — cancels any in-flight OAuth dialog (use after a failed click)
- `/logout` — signs the user out via `UserTokenClient.sign_out_user`

To redeploy the bot after editing `bot/app.py`, re-run only step 5:

```bash
bash infra/05-deploy-container-app.sh
```

## Pitfalls — exactly what bit us, and how to avoid them

### 1. The Generic OAuth 2 connection silently accepts an incomplete config

`oauth2generic` (Bot Service service-provider GUID
`8379c6d2-b262-4d4f-b89b-68dc5b5f5482`) requires **eleven** parameters, not
three. Most "how to set up a Generic OAuth 2 connection" examples online show
only `AuthorizationUrl` / `TokenUrl` / `RefreshUrl`, which are actually keys
for the simpler `oauth2` provider.

If you only supply the three URL params:
- ARM PUT returns **201**.
- The connection appears in the Azure portal as healthy.
- `provisioningState` is `"Succeeded"`.
- But when the user clicks **Sign In** in Teams, Bot Service responds with
  `{"error":{"code":"ServiceError","message":"An error occured while
  retrieving the signin link"}}` because it cannot construct the authorize
  URL from the missing query-string template.

Required parameter set (see `infra/04-create-oauth-connection.sh`):

| Key | Notes |
|---|---|
| `ClientId` / `ClientSecret` | Your IdP app registration's client id + secret |
| `ScopeListDelimiter` | Almost always `" "` (space) |
| `AuthorizationUrlTemplate` | e.g. `https://login.acme.com/oauth2/authorize` |
| `AuthorizationUrlQueryStringTemplate` | `?client_id={ClientId}&response_type=code&redirect_uri={RedirectUrl}&scope={Scopes}&state={State}` |
| `TokenUrlTemplate` | e.g. `https://login.acme.com/oauth2/token` |
| `TokenUrlQueryStringTemplate` | `""` (empty) for most IdPs |
| `TokenBodyTemplate` | `code={Code}&grant_type=authorization_code&redirect_uri={RedirectUrl}&client_id={ClientId}&client_secret={ClientSecret}` |
| `RefreshUrlTemplate` | usually same as TokenUrl |
| `RefreshUrlQueryStringTemplate` | `""` |
| `RefreshBodyTemplate` | `refresh_token={RefreshToken}&grant_type=refresh_token&client_id={ClientId}&client_secret={ClientSecret}` |

**`az bot connection create --service Oauth2`** does NOT work cleanly here.
The script uses a direct ARM REST PUT to bypass the CLI normalization
that strips/re-cases parameters.

### 2. The IdP's redirect URI must be exactly Bot Service's callback

Add this to your IdP app registration:

```
https://token.botframework.com/.auth/web/redirect
```

Anything else — even a different scheme or trailing slash — and the silent
token-exchange callback fails, falling back to the magic-code prompt.

### 3. `validDomains` in the Teams manifest must list every host in the auth chain

At minimum:

```json
"validDomains": [
  "<your IdP login host>",
  "token.botframework.com",
  "login.microsoftonline.com"   // include if your IdP federates with Entra
]
```

Missing entries are textbook causes of the magic-code fallback.

### 4. After `/logout`, the OAuthPrompt dialog stays in conversation state

A subtle bot-code bug we hit: if you only call `sign_out_user` on logout
without also `cancel_all_dialogs`, the next `hello` from the user is consumed
by the still-pending OAuthPrompt as a failed magic-code attempt — silently —
and no new card is sent. Always reset:

```python
async def _sign_out(self, turn_context):
    dc = await self._dialogs.create_context(turn_context)
    await dc.cancel_all_dialogs()                        # ← critical
    await token_client.sign_out_user(...)
    await turn_context.send_activity("Signed out.")
```

### 5. Use `OAuthPrompt` (or equivalent) from a current SDK

Hand-rolled OAuth handling, or older botbuilder versions that do not process
the `signin/tokenExchange` invoke activity, **always** fall back to magic
code. There is no way around it without an SDK that handles the silent
exchange invoke.

### 6. `webApplicationInfo` is NOT required for the OAuth Card flow

A red herring this POC initially chased: a greyed-out **Sign In** button does
NOT mean the manifest is missing `webApplicationInfo`. The Teams client
greys out the button while the popup launch is in flight (state="loading").
Our successful run was on a manifest **without** `webApplicationInfo`.

`webApplicationInfo` IS required for **SSO token exchange** (`getAuthToken`
silent flow inside Teams) — different feature, different code path. Add it if
you do SSO; not required for the OAuth Card click-to-popup flow.

## Diagnosis checklist for magic-code symptoms

If you are seeing magic-code prompts in production, check, in this order:

1. **Is the IdP app registration's redirect URI exactly
   `https://token.botframework.com/.auth/web/redirect`?**
2. **Does the Teams manifest's `validDomains` include every host that the
   redirect chain touches** (including any auth/redirect domains the IdP
   itself uses)?
3. **Is the bot using `OAuthPrompt` from a current SDK that handles
   `signin/tokenExchange`?** (Hand-rolled OAuth always falls back.)
4. **If your connection is `oauth2generic`, are all 11 template parameters
   populated** — especially the body templates that govern how Bot Service
   POSTs to the IdP's token endpoint?
5. **Capture a network trace** of `/api/oauth/PostSignInCallback` and the
   subsequent `signin/tokenExchange` invoke — that pinpoints exactly which
   step is failing.

## Cleanup

```bash
az group delete --name oauth-card-poc-rg --yes --no-wait
az ad app delete --id "$BOT_APP_ID"
az ad user delete --id "$TEST_USER_UPN"
```

## License

POC code, MIT.
