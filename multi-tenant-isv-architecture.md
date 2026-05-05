# Multi-tenant ISV Custom Engine Agent — Architecture Guide

> **Scope.** This document is a vendor-neutral guide to building a Microsoft
> Teams Custom Engine Agent (a chat bot surfaced inside Teams / M365 Copilot)
> that you publish as an ISV and your customers install into their own M365
> tenants. It explains the identity flows, the Azure resources you provision,
> the customer's installation experience, and the design decisions you have to
> make.
>
> The POC in this repo is a concrete, runnable proof of the architecture below
> — see [README.md](README.md) for the step-by-step setup that produces a
> working multi-tenant bot in ~30 minutes.

---

## 1. The scenario

You are an ISV. You want to ship a Teams bot (Custom Engine Agent) that:

- Lives in **your** Azure tenant (your code, your secrets, your billing).
- Is installed by **customers** into **their** M365 tenants — via Teams
  Marketplace, side-load, or direct Teams app catalog.
- Lets each customer's end users sign in to **a downstream service** (yours,
  the customer's own systems, or both) so the bot can do work on their behalf.
- Always knows, on the server side, **which customer organization** a request
  is coming from — without that being something the user can spoof.

This is the same shape as Microsoft Graph apps, GitHub apps installed in
multiple orgs, or any SaaS that uses OAuth.

---

## 2. The two identity flows you have to design

A multi-tenant Teams bot ALWAYS deals with two distinct identity domains.
Confusing them is the root cause of most multi-tenant ISV bot failures.

### Flow A — "Who is the user / which customer tenant?"

The user is in their own M365 tenant. Teams sends activities to your bot
through Bot Framework, including `from.aadObjectId`, `channelData.tenant.id`,
and a service URL scoped to the user's tenant region. **You can't trust those
fields by themselves** — they're transport metadata, not authentication.

To get a *trustworthy* customer-tenant identity, you ask the user to sign in
inside the bot conversation. The OAuth Card flow runs and the bot receives a
JWT whose `iss`, `tid`, and `upn`/`oid` claims are signed by the user's home
tenant. Those claims are the only ground truth.

### Flow B — "Who is the bot itself / how does it authenticate to Bot Framework and downstream APIs?"

This is the bot's own service identity. The bot has an Entra app registration
in **your** tenant with a client secret or certificate. Bot Framework uses
those credentials to authenticate the bot when delivering activities and when
the bot replies. Downstream (e.g., Azure OpenAI, your data services, Graph
with app-only permissions) the bot can use either its own credentials or
on-behalf-of (OBO) tokens minted from the user's sign-in.

These two flows use **different** Entra app registrations in many designs, or
the **same** app with different audiences. In the simplest design they're
the same app — but the `signInAudience` setting and the OAuth Connection
configuration must be set correctly for each role.

---

## 3. The architecture (single-AAD-app variant)

```
┌───────────────────────────────────────┐    ┌────────────────────────────────────┐
│      ISV TENANT  (your Azure)         │    │   CUSTOMER TENANT  (their M365)    │
│                                       │    │                                    │
│  ┌─────────────────────────────────┐  │    │  ┌──────────────────────────────┐  │
│  │ AAD app  "BotApp"               │  │    │  │ Teams client                 │  │
│  │   signInAudience =              │  │    │  │   user signed in as          │  │
│  │   AzureADMultipleOrgs           │  │    │  │   user@customer.example      │  │
│  │   client_id, client_secret      │  │    │  └─────────────┬────────────────┘  │
│  └────┬────────────────────────────┘  │    │                │                   │
│       │                               │    │                │ types message     │
│  ┌────▼────────────────────────────┐  │    │                ▼                   │
│  │ Bot Service  "Bot01"            │  │    │  ┌──────────────────────────────┐  │
│  │   appType = SingleTenant        │  │    │  │ Bot Framework channel        │  │
│  │   messaging endpoint = bot host │ ◀────────┤ token.botframework.com       │  │
│  │   OAuth connection "Entra"      │  │    │  │ smba.trafficmanager.net      │  │
│  │     authorize_url =             │  │    │  └─────────────┬────────────────┘  │
│  │       /common/oauth2/v2.0/auth  │  │    │                │                   │
│  │     token_url =                 │  │    │                │ Sign In click     │
│  │       /common/oauth2/v2.0/token │  │    │                ▼                   │
│  │     scopes = openid profile     │  │    │  ┌──────────────────────────────┐  │
│  │              User.Read offline  │  │    │  │ popup → /common/ Entra       │  │
│  └────┬────────────────────────────┘  │    │  │   first time: consent prompt │  │
│       │                               │    │  │   auto-creates SP for BotApp │  │
│  ┌────▼────────────────────────────┐  │    │  │   in customer tenant         │  │
│  │ Container App / Functions / VM  │  │    │  └─────────────┬────────────────┘  │
│  │   bot code (Bot Framework SDK)  │  │    │                │ token signed by   │
│  │   OAuthPrompt → JWT validation  │  │    │                │ customer's Entra  │
│  │   downstream calls (OBO / app)  │  │    │                ▼                   │
│  └─────────────────────────────────┘  │    │   tid = customer-tenant-id         │
│                                       │    │   iss = customer-Entra             │
└───────────────────────────────────────┘    │   upn = user@customer.example      │
                                             └────────────────────────────────────┘
```

**Key design decisions baked into this picture:**

| Decision | Choice in this architecture | Why |
|---|---|---|
| Where does the AAD app live? | ISV tenant | You own the credentials and lifecycle |
| `signInAudience` of the AAD app | `AzureADMultipleOrgs` | Required so customer-tenant users can sign into it |
| Bot Service `appType` | `SingleTenant` | Bot Service auth is about your bot's own credentials, not user auth. `MultiTenant` is deprecated for new Bot Services. |
| OAuth Connection authorize/token URL | `/common/` | Lets each user sign into their own home tenant |
| Where does the bot run? | ISV-owned Azure compute (Container App, Functions, App Service…) | Your code, your data, your control |
| One AAD app or two? | One is fine for most designs | Use two only if you need separate identifier URIs / API surfaces for the bot vs. backend |

---

## 4. Customer onboarding — what the customer actually sees

There are two customer onboarding paths. They're functionally identical from
the bot's perspective.

### Path 1 — Teams Marketplace (production)

1. Customer's M365 admin browses Teams Marketplace, finds your app, clicks
   "Get it now."
2. Marketplace's installer adds your app to the customer's Teams app catalog.
3. The first user opens the app and clicks Sign In. Entra shows a per-user
   consent prompt for the scopes you requested (or, if the admin
   pre-consented org-wide, no prompt).
4. Behind the scenes, Entra creates a service principal for your AAD app in
   the customer's tenant. This is normal and expected — it's how multi-tenant
   apps work.

### Path 2 — Side-load (POC / pilot / early customer)

1. You hand the customer your `app.zip` (manifest + icons).
2. Their admin (or a user with side-load permission) uploads it via Teams →
   Apps → Manage your apps → Upload a custom app.
3. Same sign-in / consent flow as Path 1.

For end users, both paths produce identical UX. The only operational
difference is who keeps the zip up to date — Marketplace pulls updates
automatically; side-load means you push updates to each customer.

### Optional: org-wide admin consent (recommended for production)

If your scopes are user-consentable (`openid profile User.Read email`), each
user sees a consent prompt the first time. To eliminate that, the customer's
tenant admin can pre-consent on behalf of all users:

```
https://login.microsoftonline.com/{customer-tenant-id}/adminconsent
    ?client_id={your-bot-aad-app-id}
    &redirect_uri=https://your-acknowledgement-page
```

The admin clicks once, all subsequent users sign in silently.

---

## 5. The cryptographic guarantee

When a customer-tenant user signs in via the OAuth Card, the bot receives a
JWT. Decoding it (without verification) shows roughly:

```json
{
  "aud": "00000003-0000-0000-c000-000000000000",
  "iss": "https://sts.windows.net/{CUSTOMER-TENANT-ID}/",
  "tid": "{CUSTOMER-TENANT-ID}",
  "upn": "user@customer.example",
  "oid": "{user-object-id-in-customer-tenant}",
  "appid": "{your-bot-aad-app-id}",
  "scp": "openid profile User.Read email"
}
```

**Why you can trust it:** the token is signed by the customer tenant's Entra
keys. Your backend can fetch the customer tenant's JWKS at
`https://login.microsoftonline.com/{tid}/discovery/v2.0/keys` and verify the
signature. If verification passes:

- ✅ The user is genuinely in `tid` (the customer tenant).
- ✅ Their UPN/OID is what the customer tenant says it is.
- ✅ The token was minted for your `appid` (the customer consented your app).
- ✅ Nothing in this can be spoofed by the user or anyone else without
      possessing the customer tenant's signing key.

**This is the only piece of customer identity you should base authorization
on.** Don't trust `from.aadObjectId` from the Bot Framework activity, don't
trust `channelData.tenant.id`, don't trust anything the user types. The JWT
is the truth.

---

## 6. Common variations

### Variation A — ISV provides the IdP (this POC)

- OAuth Connection points at Entra `/common/`
- Customer admin consents your AAD app once
- All users sign in via Microsoft credentials (the customer's M365 identities)
- Easiest to ship; works for any customer that's already on Microsoft 365

### Variation B — Customer brings their own non-Entra IdP

- OAuth Connection uses **Generic OAuth 2** provider, configured per-customer
  with the customer's IdP authorize/token endpoints (Okta, Auth0, a custom
  OIDC IdP, an HCM/CRM/etc.)
- One OAuth Connection name per customer, OR a single connection plus
  per-customer secrets stored in the bot
- Each customer sets up an OAuth client in their IdP and gives you the
  client_id / client_secret
- More flexible (works for non-M365 identities) but more onboarding friction

### Variation C — Hybrid (Entra for Teams identity, customer IdP for data access)

- Sign in flow A: OAuth Card with Entra `/common/` → identifies the user's
  M365 tenant
- Sign in flow B: a second OAuth Card, this time targeting the customer's
  data-system IdP, to get a token for the downstream data API
- The bot stores both tokens for the conversation
- Use this when your bot needs to act against systems the user has
  separately-managed credentials for

### Variation D — Bot uses OBO to call downstream APIs as the user

- After sign-in, the bot's backend exchanges the user's token (issued for
  your AAD app) for a token to a downstream API using OAuth 2.0 On-Behalf-Of
- The downstream API sees the user's identity, not the bot's
- Required when the downstream API enforces per-user authorization (e.g.,
  Microsoft Graph with delegated permissions, or a customer's data API that
  scopes by user)
- The Daily Account Planner project in the parent repo is a worked example

---

## 7. Pitfalls and how to avoid them

### 7.1 — Mixing up the two identity flows

The single most common bug. Two checklists:

**For Flow A (user identity):**
- AAD app `signInAudience` = `AzureADMultipleOrgs` (or
  `AzureADandPersonalMicrosoftAccount` if you want consumer accounts too)
- OAuth Connection URLs use `/common/` or `/organizations/`, NOT
  `/{your-tenant-id}/`
- Bot validates the JWT against the customer tenant's JWKS, not yours
- Authorization decisions in the bot key off `tid` from the JWT

**For Flow B (bot's own identity):**
- Bot Service `appType` = `SingleTenant` (or `UserAssignedMSI`)
- Bot's messaging endpoint authenticates incoming requests using the bot's
  own AAD app credentials (handled by the SDK)
- Outbound calls to Bot Framework use the same credentials
- Downstream calls use the bot's app credentials (Variation A) OR OBO from
  the user's token (Variation D)

### 7.2 — `/common/` silently using a cached browser session

When the OAuth popup opens to `/common/`, Entra uses whatever Entra session
cookie the browser has. If the user previously signed into Entra as somebody
else, the popup may complete silently using that cached identity — and the
bot gets the wrong claims back, with no obvious error.

**Mitigations:**
- Document `/logout` (in the bot) and a clean Entra sign-out URL for testing
- For production, this is usually a feature, not a bug — users *want* SSO
  with whoever they're already signed in as
- If you need to force account picker, request `prompt=select_account` in
  the authorize URL

### 7.3 — Bot Framework token cache

After a successful sign-in, Bot Framework's token service caches the user's
token, keyed by `(channelId, userId, connectionName)`. OAuthPrompt will
return the cached token without prompting on the next sign-in attempt. If
you need to force a new sign-in (different account, different scopes), call
`UserTokenClient.SignOutUser` from the bot — that's what `/logout` should
do.

### 7.4 — Requesting admin-only scopes without admin consent

If your OAuth Connection requests scopes like `User.Read.All`, `Mail.Read`,
or any application-permission-only scope, **end users cannot self-consent**.
The first user in a new customer tenant will hit "Approval required" and be
unable to proceed.

Either:
- Stick to user-consentable scopes (`openid profile User.Read email`,
  `User.ReadBasic.All`), OR
- Document that the customer admin must run the org-wide admin-consent URL
  before users can sign in

### 7.5 — `/common/` vs `/organizations/` vs `/{tenant}/`

| Endpoint | Who can sign in | Use when |
|---|---|---|
| `/common/` | Any Entra org user OR Microsoft consumer account | Truly multi-tenant, accept anyone |
| `/organizations/` | Any Entra org user, NO consumer accounts | Multi-tenant business apps |
| `/{tenant-id}/` | Only users in that specific tenant | Single-tenant or per-customer connection |

For business ISVs, `/organizations/` is usually the right default — same as
`/common/` minus the chance of someone signing in with a personal Microsoft
account by accident.

### 7.6 — B2B guests look like multi-tenant but aren't

If you invite a `customer.example` user as a B2B guest into your ISV tenant
so you can test from their email address, you'll see sign-in succeed — but
the resulting JWT will have `tid = your-ISV-tenant-id`, not the customer
tenant. That's because as a guest, they're effectively a member of your
tenant for that interaction.

For real multi-tenant testing, the user must be a **native member of the
customer tenant** AND **never have accepted a B2B invite** into your tenant.

### 7.7 — Bot Service `--app-type MultiTenant` is deprecated

Don't try to make the Bot Service itself multi-tenant. The supported pattern
today is:

- Bot Service `appType = SingleTenant` (in your ISV tenant)
- AAD app `signInAudience = AzureADMultipleOrgs` (so users from any tenant
  can sign in via OAuth Card)

Bot Service tenancy is about how Bot Framework authenticates **to** your bot
endpoint using your bot's own credentials. It has nothing to do with which
end users can chat with the bot.

### 7.8 — Generic OAuth 2 provider has 11 mandatory parameters

If you use the Bot Service "Generic OAuth 2" provider (whether against Entra
or a third-party IdP), the provider requires all 11 template parameters
(authorize/token/refresh URL templates AND query-string AND body templates).
Supplying only the URLs makes ARM accept the PUT but Bot Service returns
"An error occurred while retrieving the signin link" when the user clicks
Sign In. See `infra/04-create-oauth-connection.sh` in this repo for a
working ARM REST PUT body.

---

## 8. Operational considerations

### Per-customer state

In the simplest design, the bot has a single OAuth Connection (`/common/`)
and serves all customers from a single deployment. Per-customer state lives
only in:

- The set of `tid` values the bot has seen sign-ins from
- Any per-customer config you store yourself (keyed by `tid`)
- The Entra service principals auto-created in each customer tenant on
  consent (visible to the customer admin, invisible to you operationally)

You don't have per-customer secrets, per-customer connection strings, per-
customer URL endpoints. That's the appeal of this architecture.

### Per-customer Connection (variation B)

If different customers use different IdPs, you'll have one OAuth Connection
per customer (or per IdP type). Then the bot has to pick the right
connection name at runtime, typically by looking up the customer's `tid` or
domain in your config store.

### Compliance and data residency

The bot runs in your ISV tenant's region(s). If a customer requires data to
stay in a specific region or sovereign cloud, you either:
- Replicate your stack into that cloud / region and route customers there
  via DNS / config
- Operate a separate "customer-managed" deployment

Bot Service supports US Gov and other sovereign clouds; the architecture
above works in all of them, but the hostnames and consent URLs differ.

### Rate limiting / cost attribution

Use the JWT's `tid` as the customer attribution key for:
- Rate limiting per customer
- Cost attribution (Azure OpenAI tokens, downstream API calls)
- Per-customer quotas / billing tiers

### Customer offboarding

When a customer wants to stop using your app:
1. Customer admin removes your app from Teams admin center → no more
   activities arrive
2. Customer admin deletes the service principal for your AAD app from their
   Enterprise Applications list → consent revoked, sign-ins fail
3. You delete any per-customer state you've stored, keyed off their `tid`

---

## 9. The POC in this repo

[README.md](README.md) walks through provisioning the architecture above in
your own Azure subscription, in about 30 minutes:

1. Provision the AAD app, Bot Service, OAuth Connection, and Container App
   in your ISV tenant (one-time).
2. Side-load the resulting Teams app package into a different M365 tenant
   playing the customer role.
3. Sign in as a native user of that customer tenant.
4. Bot replies with the decoded JWT, proving the user's home-tenant identity
   reached the ISV backend cryptographically.

Use the POC to:
- Show stakeholders the working pattern end-to-end
- Validate that your specific customer tenants work (consent UX, scope
  approval, user-tenant routing)
- Diff against your real bot's config to find drift from the reference
  architecture

The POC is intentionally minimal: a single Python file, no LLM, no
downstream APIs, no persistence. That's the point — it isolates the
identity flow so any failure is unambiguous.

---

## 10. Further reading

- [Microsoft Identity Platform — Multi-tenant apps](https://learn.microsoft.com/azure/active-directory/develop/single-and-multi-tenant-apps)
- [Bot Framework — User authentication](https://learn.microsoft.com/azure/bot-service/bot-builder-concept-authentication)
- [Bot Framework — Adding OAuth Card authentication](https://learn.microsoft.com/azure/bot-service/bot-builder-authentication)
- [Microsoft Graph — On-Behalf-Of flow](https://learn.microsoft.com/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow)
- [Teams — Custom Engine Agents](https://learn.microsoft.com/microsoftteams/platform/teams-ai-library/welcome)
- [POC README](README.md) and [single-tenant baseline POC](README.singletenant.md) in this repo
