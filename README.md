# OAuth Card POC — Multi-tenant ISV variant

> ⚠️ This is the **multi-tenant** branch. For the single-tenant baseline that
> proves Generic OAuth 2 doesn't inherently produce a magic code, see
> [README.singletenant.md](README.singletenant.md) (or branch `master`).

## What this branch proves

The single-tenant POC answered the question *"is the magic-code prompt
inherent to Generic OAuth 2?"* — **No.**

This branch answers the next, more business-critical question:

> **"Can an ISV publish a Custom Engine Agent to Teams, have a customer in a
> completely different tenant install it, and have customer-tenant users sign
> in via OAuth — with the ISV backend reliably knowing which customer org each
> user belongs to?"**

✅ **Yes**, with the right configuration. This branch demonstrates the full
pattern end-to-end:

- The bot lives in the **ISV's tenant** (e.g., `0fbe7234`, playing the role of
  Veeam / Paychex Inc.)
- The customer **side-loads the same Teams app package** into their own tenant
  (e.g., `microsoft.com`, playing the role of any customer org). This is
  identical to what Teams Marketplace install does — just without the
  publication step.
- A native customer-tenant user (e.g., `someone@microsoft.com`) chats with the
  bot and clicks Sign In.
- They authenticate against **their own home tenant's** Entra (not the ISV's
  tenant). On first use they see a one-time consent prompt (auto-creates the
  app's service principal in their tenant — exactly what marketplace install
  does).
- The bot receives a token whose `tid`/`upn`/`iss` claims **prove the user's
  home org** to the ISV backend, cryptographically.

## How this differs from the single-tenant POC

Four targeted changes; everything else is identical:

| # | Setting | Single-tenant (master) | Multi-tenant (this branch) |
|---|---|---|---|
| 1 | AAD app `signInAudience` | `AzureADMyOrg` | **`AzureADMultipleOrgs`** |
| 2 | OAuth Connection authorize/token URL | `/{tenant}/oauth2/v2.0/...` | **`/common/oauth2/v2.0/...`** |
| 3 | Where the Teams app is sideloaded | ISV tenant | **Customer tenant** |
| 4 | Who signs in | User in ISV tenant | **User native to customer tenant** |

The Bot Service itself stays SingleTenant (in the ISV tenant). That's
internal to how Bot Service authenticates *to your bot endpoint* using your
bot's app credentials; it's unrelated to user authentication. (Microsoft
deprecated `--app-type MultiTenant` for new bots; the supported pattern today
is SingleTenant Bot + multi-tenant AAD app, exactly what we use here.)

## Architecture

```
ISV TENANT (0fbe7234)                     CUSTOMER TENANT (microsoft.com)
──────────────────────                    ──────────────────────────────
                                          ┌────────────────────────────┐
┌────────────────────┐                    │  Teams desktop / web       │
│  Container App     │                    │  signed in as someone@…    │
│  (bot/app.py)      │ ◀──── 1. activity ─┤  microsoft.com             │
│  uses OAuthPrompt  │   from msteams     └─────────────┬──────────────┘
│                    │                                  │
│  Bot AAD app:      │                                  │ user types
│  multi-tenant      │                                  │ "hello"
│  signInAudience:   │                                  ▼
│  AzureADMultiple…  │                       ┌─────────────────────────┐
└────────┬───────────┘                       │  Bot Framework channel  │
         │                                   │  (token.botframework)   │
         │ 2. GetSignInResource              └─────────────┬───────────┘
         ▼                                                 │
┌─────────────────────┐                                    │
│  Bot Service OAuth  │ ───── 3. signed sign-in URL ──────┤
│  Connection         │                                    │
│  authorize URL =    │                                    ▼
│  …/COMMON/oauth2/v2 │                       ┌────────────────────────┐
└─────────────────────┘                       │  user clicks "Sign In" │
                                              │  popup opens to        │
                                              │  login.microsoftonline │
                                              │  .com/COMMON/...       │
                                              └────────────┬───────────┘
                                                           │ 4. Entra detects
                                                           │ user's home tenant
                                                           ▼
                                              ┌────────────────────────┐
                                              │  Sign-in at user's     │
                                              │  HOME tenant           │
                                              │  (microsoft.com)       │
                                              │                        │
                                              │  First time: consent   │
                                              │  prompt → SP created   │
                                              │  in customer tenant    │
                                              └────────────┬───────────┘
                                                           │ 5. token
                                                           │ tid: 72f988bf-…
                                                           │ upn: someone@microsoft.com
                                                           ▼
                                              ┌────────────────────────┐
ISV TENANT                                    │  signin/tokenExchange  │
                                              │  invoke back to bot    │
┌────────────────────┐                        └────────────┬───────────┘
│  bot decodes JWT   │ ◀─────── 6. token ─────────────────┘
│  sees user's       │
│  customer-tenant   │
│  identity claims   │
└────────────────────┘
```

## Prerequisites

In addition to the [single-tenant prereqs](README.singletenant.md#prerequisites):

- A **second tenant** to play the customer role. Most easily, use any other
  M365 tenant where you have permission to side-load custom apps. (If you
  don't have one, your personal `*.microsoft.com` Teams account works as the
  customer; the ISV bot is in `*.onmicrosoft.com`.)
- The **second-tenant user must NOT be a guest** in the ISV tenant — they
  should be a native member of the customer tenant. (If they're a guest, you
  end up with a same-tenant scenario and don't actually exercise the
  cross-tenant OAuth path.)

## Step-by-step setup

### A. ISV side — provision the bot (one-time)

If you haven't already provisioned from `master`, do these once in your **ISV
tenant**:

```bash
git clone https://github.com/james-tn/oauth-card-poc.git -b multitenant
cd oauth-card-poc
cp .env.example .env
# edit .env with your ISV tenant's TENANT_ID, SUBSCRIPTION_ID, etc.

az login --tenant <ISV_TENANT_ID>
az account set --subscription <ISV_SUBSCRIPTION_ID>

cd infra
bash 01-create-aad-app.sh           # multi-tenant AAD app
bash 02-create-test-user.sh         # optional — only used for the single-tenant test
bash 03-create-bot-service.sh
bash 04-create-oauth-connection.sh  # uses /common/ endpoints in this branch
bash 05-deploy-container-app.sh
bash 06-build-manifest.sh
```

If you already provisioned the single-tenant version, you only need to:

```bash
# Flip AAD app to multi-tenant
az ad app update --id "$BOT_APP_ID" --sign-in-audience AzureADMultipleOrgs

# Recreate the OAuth connection with /common/ endpoints
bash infra/04-create-oauth-connection.sh
```

That's literally the only ISV-side change. The bot code, Container App, and
manifest are identical.

### B. Customer side — install and use the app

Switch hats: now you are the customer. Get the Teams app package from the
ISV (the `manifest/oauth-poc-app.zip` you built in step A6).

> In production this happens via Teams Marketplace ("Get it now" → installs
> into the customer tenant). For the POC we simulate that with a side-load,
> which has identical downstream behavior — Teams, Bot Framework, and Entra
> can't tell the difference.

1. Sign in to Teams (web or desktop) **as a native user of your customer
   tenant** (e.g. `someone@microsoft.com` for the Microsoft tenant).
2. **Apps → Manage your apps → Upload an app → Upload a custom app** → pick
   `oauth-poc-app.zip`.
3. Open the bot in a personal chat.
4. Type `hello`.

### C. The first sign-in (the consent moment)

Click **Sign In**. A popup opens at `login.microsoftonline.com/common/...`.

Because your AAD app is multi-tenant and is being used in a tenant that has
never seen it before, Entra shows a **consent screen** the very first time:

> *"Permissions requested — OAuth POC by [your ISV name] would like to:
> Sign you in and read your profile. Read your basic profile."*

Click **Accept**. Behind the scenes Entra:

1. Creates an enterprise-app service principal for your AAD app in the
   customer tenant.
2. Grants the consented scopes.
3. Issues a token.
4. Redirects the popup back to Bot Service, which closes the popup and fires
   `signin/tokenExchange` to the bot.

In production, an ISV typically does this consent once via the *org-wide
admin-consent URL* on behalf of all users in the customer tenant, so end
users never see the prompt at all. For the POC, the per-user prompt is fine.

### D. Verify the token's claims

The bot replies with the decoded JWT. The critical fields:

```json
{
  "tid": "72f988bf-86f1-41af-91ab-2d7cd011db47",     ← CUSTOMER tenant id (microsoft.com)
  "iss": "https://sts.windows.net/72f988bf-…/",      ← Issued BY customer tenant
  "upn": "someone@microsoft.com",                     ← Customer-tenant UPN
  "appid": "43f717ae-…",                              ← ISV's bot AAD app id
  "scp": "openid profile User.Read email"
}
```

Note that `tid` (issuer) is the **customer's** tenant id, while `appid` (the
client) is the **ISV's** AAD app id. This is the proof that:

- ✅ The customer-tenant user signed into their own home tenant
- ✅ The token was minted by the customer's Entra
- ✅ The ISV's backend can identify which customer the user belongs to from
      `tid` alone — no shared secret, no provisioning step needed
- ✅ The signature on the token is verifiable against the customer's tenant
      keys (so the ISV backend can trust these claims)

## What this maps to in the Paychex / Paycor scenario

| ISV (Veeam / Paychex Inc.) | This POC |
|---|---|
| Veeam's Azure tenant where the bot AAD app + Bot Service live | `0fbe7234` |
| Veeam's bot Container App | `oauthpocbotapp` |
| Customer org installing the app from marketplace (e.g., a Veeam customer) | side-load into `microsoft.com` |
| Customer-tenant end user signing in | `someone@microsoft.com` |
| ISV backend learning user's home org from token claims | `bot/app.py` decoding JWT and seeing `tid` / `upn` |

The exact same pattern works whether the IdP is:
- **Entra** (this POC, via `/common/`) — works for any Entra customer
- **A non-Entra IdP the customer brings** (e.g., Paycor's HCM IdP) — would
  use a per-customer OAuth Connection instead of `/common/`, but everything
  else stays identical

## Pitfalls specific to multi-tenant

In addition to the [single-tenant pitfalls](README.singletenant.md#pitfalls--exactly-what-bit-us-and-how-to-avoid-them):

### 1. The customer user must NOT be a B2B guest in the ISV tenant

If you invite the customer user as a guest in the ISV tenant (so they can chat
with a single-tenant bot via guest access), you'll observe sign-in working
fine — but the token's `tid` will be the **ISV** tenant, not the customer
tenant. That's because as a guest, they're effectively a member of the ISV
tenant for that interaction.

To prove cross-tenant OAuth, you need the user to be a native member of the
customer tenant **and never have accepted a B2B invite into the ISV tenant**.

### 2. First-time consent UX

The very first time a user from a new customer tenant signs in, they get the
Entra consent screen. This is normal for multi-tenant apps and **is not** the
magic code prompt — it's a one-time "Accept" click that creates the service
principal in their tenant. Users don't see it again on subsequent sign-ins.

If your real ISV scenario can't tolerate per-user consent, generate the
admin-consent URL once per customer:

```
https://login.microsoftonline.com/{customer-tenant-id}/adminconsent
    ?client_id={ISV-bot-app-id}
    &redirect_uri=https://your-ack-page
```

The customer's tenant admin clicks once, all subsequent users sign in
silently.

### 3. Don't request scopes that need admin consent without admin consent

Scopes like `User.Read.All`, `Mail.Read`, etc. require tenant-admin consent.
Requesting them in the OAuth Connection's scope list will block end-user
self-consent — they'll see "approval required" or get sent into an error
loop. Stick to user-consentable scopes (`openid profile User.Read email`)
unless you've negotiated admin-consent with the customer.

### 4. `/common/` vs `/organizations/` vs `/{customer-tenant-id}/`

| Endpoint | Who can sign in | Use when |
|---|---|---|
| `/common/` | Any Entra org user OR Microsoft personal account (MSA) | True multi-tenant, accept anyone |
| `/organizations/` | Any Entra org user, NO personal accounts | Multi-tenant but org-only (no consumer Microsoft accounts) |
| `/{tenant-id}/` | Only users in that specific tenant | Single-tenant or per-customer OAuth connection |

This POC uses `/common/`. For a real ISV that knows it only sells to
businesses, `/organizations/` is slightly safer (rejects MSA users).

## Diagnosis checklist for ISV multi-tenant issues

If a customer reports the app doesn't work after install:

1. **Did they consent?** Check Entra admin center → Enterprise applications
   in their tenant → search for your AAD app's name. If it's not there, the
   user closed the consent prompt. They need to retry sign-in and click Accept.
2. **Are they actually a native user of their tenant?** Check `upn` claim — if
   it has `#EXT#`, they're a B2B guest of some other tenant.
3. **Is your AAD app's `signInAudience` `AzureADMultipleOrgs` (or
   `AzureADandPersonalMicrosoftAccount`)?**
   ```bash
   az ad app show --id $BOT_APP_ID --query signInAudience -o tsv
   ```
4. **Is the OAuth Connection's authorize URL using `/common/` or
   `/organizations/`?** A `/{specific-tenant}/` URL will only work for that
   one tenant.
5. **Are the requested scopes user-consentable?** If you need admin-only
   scopes, the customer tenant admin must consent first.

## Cleanup

```bash
# In ISV tenant
az group delete --name oauth-card-poc-rg --yes --no-wait
az ad app delete --id "$BOT_APP_ID"

# In each customer tenant where the app was consented:
# Entra admin center → Enterprise applications → find the app → Delete
```
