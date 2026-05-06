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

**Observed symptom:** _(to be filled in after testing)_

---

### Test #4 — Missing `validDomains` in Teams manifest

**Hypothesis** (per Pitfall 3): the manifest's `validDomains` array must list
every host the auth chain touches. Missing entries are "textbook causes of
the magic-code fallback."

**Apply the misconfig:** edit `infra/06-build-manifest.sh` to remove
`token.botframework.com` and `login.microsoftonline.com` from `validDomains`,
then rebuild the zip and re-side-load.

**Revert:** restore the entries, rebuild, re-side-load.

**Predicted symptom:** popup blocked by Teams, OR popup opens but Teams
domain warning appears, OR magic-code fallback. Honestly unclear which —
that's why we're testing.

**Observed symptom:** _(to be filled in after testing)_

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

**Predicted symptom:** **Magic-code prompt in Teams** — popup completes
successfully, popup shows "Sign-in completed. Please enter the code 123456
in the bot chat", user types code, bot completes sign-in via the magic-code
path.

**Observed symptom:** _(to be filled in after testing)_

---

## Summary of findings

To be completed after running all three tests:

| Test | Hypothesis | Predicted symptom | Observed symptom | Real-world cause? |
|---|---|---|---|---|
| #3 wrong redirect URI | Pitfall 2 | Entra AADSTS50011 (not magic code) | _(tbd)_ | _(tbd)_ |
| #4 missing validDomains | Pitfall 3 | Popup blocked OR magic code | _(tbd)_ | _(tbd)_ |
| #2 drop {State} | (new) | **Magic-code prompt** | _(tbd)_ | _(tbd)_ |

After testing, update the relevant pitfall in `README.singletenant.md` with
the empirically-verified symptom and remove any speculation that turned out
not to match reality.
