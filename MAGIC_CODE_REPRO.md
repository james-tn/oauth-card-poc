# Magic-code reproduction test matrix

> **Goal.** Empirically verify which of the documented "magic-code" pitfalls
> actually produce the magic-code prompt in Teams, vs. which produce
> different failure modes (Entra error, silent failure, ServiceError, etc.).
>
> **Branch.** `magic-code-repro`, off `master`. Each misconfig is one commit;
> revert with `git revert HEAD` (and re-run any provisioning script the
> commit changed) before applying the next.
>
> **Live Azure state when these tests are run.** The resources currently in
> place are configured per the `multitenant` branch (multi-tenant AAD app,
> `/common/` OAuth Connection). The misconfigurations under test are
> independent of single-vs-multi-tenant, so they apply identically.
> Continue testing from your microsoft.com Teams (where the working baseline
> is already verified).

## Test protocol

For each misconfig:

1. Apply the change (run the script or `az` command in the test row).
2. In the bot in Teams, type `/logout` to clear the BF token cache.
3. Wait for "Signed out." reply.
4. Type `hello`. The bot should send a fresh OAuth Card.
5. Click **Sign In**.
6. Observe and record:
   - Does the popup open?
   - Does the popup show an error, or complete?
   - Does the bot reply with claims (success), or with a magic-code prompt
     ("Please enter the code sent to you"), or nothing?
7. Apply the revert command in the row.
8. Verify baseline restored: re-type `/logout`, `hello`, Sign In, expect
   success with claims.

## Test matrix

### Test #3 — Wrong IdP redirect URI

**Hypothesis** (per Pitfall 2 in `README.singletenant.md`): the IdP app
registration's redirect URI must be exactly
`https://token.botframework.com/.auth/web/redirect`. Anything else and
"the silent token-exchange callback fails, falling back to the magic-code
prompt."

**Apply the misconfig:**

```bash
source .env
# Replace the correct BF redirect URI with a wrong one
az ad app update --id "$BOT_APP_ID" \
    --web-redirect-uris "https://wrong-host.example.com/auth"
```

**Revert:**

```bash
source .env
az ad app update --id "$BOT_APP_ID" \
    --web-redirect-uris "https://token.botframework.com/.auth/web/redirect"
```

**Predicted symptom:** Popup opens but Entra immediately rejects with
`AADSTS50011: redirect URI in the request does not match the redirect URIs
configured for the application` — *not* magic code. (If this prediction is
wrong and we DO see magic code, that's a finding.)

**Observed symptom:** ✅ **CONFIRMED prediction.** Popup opened (showed Teams
account picker). After selecting the test account, Entra immediately returned:

```
Sorry, but we're having trouble signing you in.
AADSTS50011: The redirect URI
'https://token.botframework.com/.auth/web/redirect' specified in the
request does not match the redirect URIs configured for the application
'43f717ae-23c2-4dfa-803d-260257abe189'.
```

Sign-in cannot complete. **Bot is never invoked. Magic-code prompt does NOT
appear.**

**Implication:** Pitfall 2 in `README.singletenant.md` — which says wrong
redirect URI causes "fallback to the magic-code prompt" — is **incorrect**.
The actual symptom is a hard `AADSTS50011` error in the popup. Magic-code
fallback would require the OAuth round-trip to *succeed* but fail to
correlate back to BF, which doesn't happen here because Entra rejects the
request at the authorize step.

**Hypothesis** (per Pitfall 3): the manifest's `validDomains` array must list
every host the auth chain touches. Missing entries are "textbook causes of
the magic-code fallback."

**Apply the misconfig:** edit `infra/06-build-manifest.sh` to remove
`token.botframework.com` and `login.microsoftonline.com` from `validDomains`,
then rebuild the zip and re-side-load.

**Revert:** restore the entries, rebuild, re-side-load.

**Observed symptom:** ✅ Teams blocked the popup entirely. Clicking Sign In
produced no popup; instead a red error appeared **inline below the Sign In
button** in the chat:

```
⚠ Something went wrong. Please try again.
```

The bot's `OAuth Card` was rendered correctly with the Sign In button, but
Teams' client-side `validDomains` check refused to launch the OAuth popup
because `token.botframework.com` was not in the manifest's allowlist.

**No popup. No magic code. No request reaches Bot Service. No request
reaches the IdP. No request reaches the bot.**

**Implication:** Pitfall 3 in `README.singletenant.md` — which says missing
`validDomains` are "textbook causes of the magic-code fallback" — is
**incorrect**. The actual symptom is a Teams client-side block with a
generic "Something went wrong" toast. The user can't even start the OAuth
flow, so there's nothing for BF to fall back from.

---

### Test #2 — Drop `{State}` from `AuthorizationUrlQueryStringTemplate`

**Hypothesis** (added during this exercise): if the OAuth Connection's
`AuthorizationUrlQueryStringTemplate` doesn't include `state={State}`, then
Bot Service won't insert its session-correlation token into the authorize
URL. The IdP can't round-trip a state value that wasn't sent. The redirect
arrives at `token.botframework.com/.auth/web/redirect` without state, BF
can't correlate to the OAuthPrompt session, and falls back to the
6-digit-code page in the popup.

**Apply the misconfig:** edit `infra/04-create-oauth-connection.sh` to
remove `&state={State}` from `AUTHZ_QUERY_TEMPLATE`, then rerun the script
to recreate the connection.

**Revert:** restore the template, rerun the script.

**Observed symptom:** ✅ Popup opened correctly. After selecting an account
and authenticating against Entra, Entra redirected back to
`https://token.botframework.com/.auth/web/redirect?code=...&session_state=...`
**without** a `state=` query param. BF's redirect handler then returned a
hard error page in the popup:

```
{
  "error": {
    "code": "ServiceError",
    "message": "Missing required query string parameter: state.
                Url = https://token.botframework.com/.auth/web/redirect?code=...&session_state=..."
  }
}
```

**No magic-code prompt. No silent fallback. BF refuses the callback.**

**Implication:** BF strictly enforces the OAuth `state` parameter as
RFC 6749 §10.12 CSRF protection — a missing state is treated as a
potentially malicious cross-site callback, not as a recoverable condition.
The hypothesis that dropping `{State}` would trigger magic-code fallback
is **wrong**. It triggers a hard ServiceError instead.

This is the most security-relevant finding of the three: BF is doing the
right thing here (refusing to process callbacks without CSRF protection),
but it is not the silent fallback to magic code that we hypothesized.

---

## Summary of findings

All three documented "magic-code fallback" hypotheses were **wrong**. None
of the misconfigurations under test produced the magic-code prompt in
Teams. Each produced a different, more diagnostically obvious failure:

| Test | Hypothesis | Predicted symptom | Observed symptom | Real-world cause? |
|---|---|---|---|---|
| #3 wrong redirect URI | Pitfall 2 → magic code | Entra AADSTS50011 (already corrected the prediction before testing) | ✅ AADSTS50011 in popup. Sign-in cannot complete; bot never invoked. | Hard error, not magic code. |
| #4 missing validDomains | Pitfall 3 → magic code | Popup blocked OR magic code | ✅ Teams refused to launch popup. "Something went wrong. Please try again." appeared inline in chat. No popup, no Bot Service request, no IdP request. | Hard error in Teams client, not magic code. |
| #2 drop {State} | (new) → magic code | Magic-code prompt | ✅ Popup opened, Entra accepted login, redirected back to BF — but BF returned `ServiceError: Missing required query string parameter: state` and refused the callback. | Hard error, not magic code. BF enforces state per RFC 6749 §10.12 (CSRF protection). |

### What this means for the README

`README.singletenant.md`'s pitfall list contains three claims that the
named misconfiguration "falls back to the magic-code prompt." All three
are empirically wrong. Each misconfiguration produces a hard error at a
different layer (Entra, Teams client, BF token service) that is more
diagnosable than a silent magic-code fallback would be.

The pitfalls are still **good defensive practices** — you still want the
correct redirect URI, correct `validDomains`, and correct OAuth template
parameters — but the symptom they prevent is a hard error, not a
magic-code prompt. The README should be corrected to reflect this.

### What actually causes the magic-code prompt?

Based on these results, the magic-code prompt does NOT appear when the
OAuth round-trip is broken — those produce hard errors. The magic-code
prompt is shown when the OAuth round-trip **succeeds** but the channel
or the bot can't process the silent-completion `signin/verifyState` /
`signin/tokenExchange` invoke. Plausible causes worth testing in a
follow-up exercise:

- **Channel that doesn't deliver invokes** — Web Chat in older
  configurations, BF Emulator, Direct Line without enhanced OAuth setup.
  These channels intentionally use the magic-code UX as the default
  because they have no equivalent of Teams' silent-completion plumbing.
- **Hand-rolled OAuth handler** — bot code that doesn't subscribe to
  `signin/tokenExchange` or `signin/verifyState` invoke activities will
  see the user paste the magic code as an ordinary chat message and have
  to handle it manually. Older botbuilder versions before the modern
  OAuthPrompt are the canonical example.
- **Bot's own credentials are stale or rejected** — if BF can't deliver
  the invoke to the bot (auth failure on the channel-to-bot leg), it
  may fall back to showing the user the code in the popup and waiting
  for them to paste it. This is the most plausible cause of "we used to
  not see magic codes and now we do" in production.
- **Stale OAuth Connection in the bot's `OAuthPromptSettings`** — if
  `connectionName` doesn't match what's on Bot Service, the silent
  exchange goes to the wrong slot, no token comes back, and BF may
  fall back to the magic-code path while waiting for the user.

The first one (wrong channel) is trivial to test by pointing BF Emulator
at the same Container App. The others require more invasive bot-code
changes. Out of scope for this matrix.

### Action items for `README.singletenant.md`

1. Rewrite Pitfall 2: change "falling back to the magic-code prompt" to
   "produces an `AADSTS50011` error in the popup; sign-in cannot complete."
2. Rewrite Pitfall 3: change "textbook causes of the magic-code fallback"
   to "Teams refuses to launch the OAuth popup and shows a generic
   'Something went wrong' inline error."
3. Rewrite Pitfall 1's symptom note to clarify it produces a `ServiceError`
   on the Sign In click — which we already had right.
4. Rewrite the diagnosis checklist to note that magic-code symptoms
   indicate a working OAuth round-trip with broken silent-completion
   (channel/SDK/bot-credential issues), not a broken authorize/redirect
   chain.
