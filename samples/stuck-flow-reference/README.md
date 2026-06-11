# Reference: fixing the stuck "You're almost signed in…" OAuth state (M365 Copilot CEA)

This is a minimal **reference implementation** for a Microsoft 365 Custom Engine Agent (CEA) built on
the **Microsoft 365 Agents SDK (.NET)** that fixes the stuck sign-in state where, after a page reload
or in a new chat, the agent shows a sign-in prompt **with no working button** and the user can't proceed.

Validated in Microsoft 365 Copilot (Web) and Teams: reload, slow reload, and new chat each present a
fresh, clickable Sign in card — no frozen state.

> The fix lives in **`src/SmartAuthHandler.cs`** (a custom `IUserAuthorization` wrapping the stock
> `AzureBotUserAuthorization`). The default behavior `SMARTAUTH_STRAY_BEHAVIOR=smart` is the relevant
> one; the other behaviors are kept only for A/B comparison.

## The problem

The SDK keeps OAuth state in **two layers, scoped differently**:

- **Outer** `SignInState`, banked by `UserAuthorization` at `oauth/{channel}/{userId}/userAuthorizationState`
  — scoped **per user + channel, NOT per conversation**. Once a flow is active (`ActiveHandler` set),
  *every* conversation and *every* reload for that user is treated as a continuation of the same flow.
- **Inner** `FlowState` at `oauth/{handler}/{channel}/{conversationId}/flowState` — scoped **per conversation**.

So after a reload / in a new chat, the outer state says "this user is mid-sign-in" (so auth runs **before
your routes** and the turn returns early), but the inner conversation has no rendered card. The user is
pinned in an active-but-cardless state and can't even reach a `/signout` route. Permanent "almost signed in."

## The fix (`src/SmartAuthHandler.cs`, `smart` behavior)

When a **non-code message** arrives during a **pending** (non-forced) flow:

1. **Auto-recover with a throttle (the actual fix — no new command).** Keep a tiny per-conversation
   "card issued at" timestamp. If a card was shown in *this* conversation within a short window
   (`SMARTAUTH_RECARD_THROTTLE_SECONDS`, default 8s) → send a gentle nudge (avoids card spam from burst
   typing). Otherwise — new chat, reload, or the card went stale — **reset the inner flow and present a
   fresh sign-in card right there.** The user simply types again and gets a working button; no retyping.

2. **Make `/signout` work while stuck (escape hatch).** Because auth returns before routes during a
   pending flow, your own `/signout` route is unreachable in this state. The handler intercepts it and
   fully clears the flow: resets the inner `FlowState` **and deletes the outer
   `oauth/{channel}/{userId}/userAuthorizationState` key** and ledgers, then confirms. The next message
   routes normally. (Deleting the outer key is what actually un-pins the user.)

SDK note (verified against `AzureBotUserAuthorization.SignInUserAsync`): `forceSignIn:true` does **not**
force a fresh card — `BeginFlow` vs `ContinueFlow` is decided solely by `FlowState.FlowStarted`. The
recard path calls `ResetStateAsync` first (sets `FlowStarted=false`) → `BeginFlowAsync` → fresh card.

Guardrails worth copying:
- A per-`{channel}/{userId}` `SemaphoreSlim` serializes these mutations (fine for single instance /
  `MemoryStorage`; **multi-instance needs storage ETags or a distributed lock**).
- The per-conversation card-state throttle lets reloads recover while rapid typing doesn't spawn a stack
  of cards.

## Wiring (the two things to diff if it "doesn't take effect")

`appsettings.json` — register `SmartAuthHandler` as the handler the routes use:

```jsonc
"AgentApplication": {
  "UserAuthorization": {
    "DefaultHandlerName": "auto",
    "AutoSignIn": true,
    "Handlers": {
      "auto": {
        "Assembly": "<YourAssembly>",
        "Type": "<YourNamespace>.SmartAuthHandler",
        "Settings": { "AzureBotOAuthConnectionName": "<your-oauth-connection-name>" }
      }
    }
  }
}
```

Routes that require auth must reference that **same handler name** (e.g. `autoSignInHandlers: ["auto"]`),
and `DefaultHandlerName` must match — otherwise the pending turn is dispatched to the stock handler, not
your wrapper. See `src/Program.cs` for the registration + middleware order.

## Confirming the handler is actually engaged (diagnostics)

Most "it didn't work" cases are registration, not logic.

1. **Prove it's constructed.** Add one line to the constructor:
   `_logger?.LogInformation("[SmartAuth] constructed: name={Name}", name);`
   If you never see `[SmartAuth] constructed` at startup/first turn, the SDK is still using the stock
   `AzureBotUserAuthorization` — your `Handlers:…:Type` registration isn't being picked up.

2. **See which branch fires on a frozen turn** (these are logged):
   - `[SmartAuth] smart: re-presenting sign-in card …` → recard (expect a fresh card **with** a button)
   - `[SmartAuth] smart: recent card …; nudging …` → throttle nudge
   - `[SmartAuth] escape …` → `/signout`
   If none appear on a plain-text message during pending sign-in, the stray branch isn't being entered —
   check your SDK version and the route wiring above.

## Run it

```bash
cd src
dotnet run
```

Configure your bot App ID / OAuth connection via environment variables or `appsettings` (the committed
`appsettings.json` intentionally ships with **empty** secrets). Key knobs:
`SMARTAUTH_STRAY_BEHAVIOR=smart`, `SMARTAUTH_RECARD_THROTTLE_SECONDS=8`.
