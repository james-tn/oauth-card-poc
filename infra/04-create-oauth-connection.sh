#!/usr/bin/env bash
# Create the Bot Service "Generic OAuth 2" connection.
#
# Defaults to GitHub as the IdP because GitHub gives us the cleanest
# end-to-end demo of generic OAuth 2 (non-Microsoft IdP, opaque token,
# standard authorization-code flow). To use a different IdP, set the
# IDP_* variables in your .env (see .env.example for the shape).
#
# Why this script uses ARM REST instead of `az bot connection create`:
#   - The CLI silently rewrites template parameter casing and drops keys
#     it does not recognise.
#   - The `oauth2generic` service provider needs ALL ELEVEN parameters
#     below; if any are missing, ARM PUT returns 201 but Bot Service
#     rejects sign-in attempts with "An error occured while retrieving
#     the signin link".

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../.env"

# ---------- IdP defaults (GitHub) ---------------------------------------
IDP_AUTHORIZE_URL="${IDP_AUTHORIZE_URL:-https://github.com/login/oauth/authorize}"
IDP_TOKEN_URL="${IDP_TOKEN_URL:-https://github.com/login/oauth/access_token}"
IDP_REFRESH_URL="${IDP_REFRESH_URL:-${IDP_TOKEN_URL}}"
IDP_SCOPES="${IDP_SCOPES:-read:user}"
IDP_CLIENT_ID="${IDP_CLIENT_ID:?IDP_CLIENT_ID must be set in .env (e.g. your GitHub OAuth App Client ID)}"
IDP_CLIENT_SECRET="${IDP_CLIENT_SECRET:?IDP_CLIENT_SECRET must be set in .env (your IdP client secret)}"

# ---------- Constants ----------------------------------------------------
PROVIDER_ID="8379c6d2-b262-4d4f-b89b-68dc5b5f5482"  # oauth2generic
SUB="$(az account show --query id -o tsv)"
TOKEN="$(az account get-access-token --query accessToken -o tsv)"
URI="https://management.azure.com/subscriptions/${SUB}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.BotService/botServices/${BOT_NAME}/connections/${OAUTH_CONNECTION_NAME}?api-version=2022-09-15"

echo "==> Configuring OAuth connection '${OAUTH_CONNECTION_NAME}'"
echo "    provider:      Generic OAuth 2 (oauth2generic)"
echo "    authorize URL: ${IDP_AUTHORIZE_URL}"
echo "    token URL:     ${IDP_TOKEN_URL}"
echo "    scopes:        ${IDP_SCOPES}"

# Idempotent: delete first if it already exists, ignore 404
echo "    (deleting any pre-existing connection of the same name)"
curl -s -X DELETE -H "Authorization: Bearer ${TOKEN}" "${URI}" -o /dev/null
sleep 2

IDP_AUTHORIZE_URL="${IDP_AUTHORIZE_URL}" \
IDP_TOKEN_URL="${IDP_TOKEN_URL}" \
IDP_REFRESH_URL="${IDP_REFRESH_URL}" \
IDP_SCOPES="${IDP_SCOPES}" \
IDP_CLIENT_ID="${IDP_CLIENT_ID}" \
IDP_CLIENT_SECRET="${IDP_CLIENT_SECRET}" \
PROVIDER_ID="${PROVIDER_ID}" \
python3 - <<'PY' > /tmp/oauth-conn.json
import json, os
body = {
  "location": "global",
  "properties": {
    "scopes": os.environ["IDP_SCOPES"],
    "serviceProviderId": os.environ["PROVIDER_ID"],
    "parameters": [
      {"key": "ClientId",                            "value": os.environ["IDP_CLIENT_ID"]},
      {"key": "ClientSecret",                        "value": os.environ["IDP_CLIENT_SECRET"]},
      {"key": "ScopeListDelimiter",                  "value": " "},
      {"key": "AuthorizationUrlTemplate",            "value": os.environ["IDP_AUTHORIZE_URL"]},
      {"key": "AuthorizationUrlQueryStringTemplate", "value": "?client_id={ClientId}&response_type=code&redirect_uri={RedirectUrl}&scope={Scopes}&state={State}"},
      {"key": "TokenUrlTemplate",                    "value": os.environ["IDP_TOKEN_URL"]},
      {"key": "TokenUrlQueryStringTemplate",         "value": ""},
      {"key": "TokenBodyTemplate",                   "value": "code={Code}&grant_type=authorization_code&redirect_uri={RedirectUrl}&client_id={ClientId}&client_secret={ClientSecret}"},
      {"key": "RefreshUrlTemplate",                  "value": os.environ["IDP_REFRESH_URL"]},
      {"key": "RefreshUrlQueryStringTemplate",       "value": ""},
      {"key": "RefreshBodyTemplate",                 "value": "refresh_token={RefreshToken}&grant_type=refresh_token&client_id={ClientId}&client_secret={ClientSecret}"},
      {"key": "Scopes",                              "value": os.environ["IDP_SCOPES"]}
    ]
  }
}
print(json.dumps(body))
PY

echo "==> PUT ${URI##*/connections/}"
HTTP=$(curl -s -o /tmp/oauth-conn-resp.json -w "%{http_code}" -X PUT "${URI}" \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "Content-Type: application/json" \
    --data @/tmp/oauth-conn.json)

if [[ "${HTTP}" != "200" && "${HTTP}" != "201" ]]; then
    echo "ERROR: ARM PUT returned HTTP ${HTTP}" >&2
    cat /tmp/oauth-conn-resp.json >&2
    exit 1
fi

echo "    HTTP ${HTTP} OK"
python3 -c "
import json
d = json.load(open('/tmp/oauth-conn-resp.json'))
p = d.get('properties', {})
print(f\"    provisioningState: {p.get('provisioningState')}\")
print(f\"    serviceProvider:   {p.get('serviceProviderDisplayName')}\")
print(f\"    settingId:         {p.get('settingId')}\")
"

# Cleanup the secret-bearing temp file
rm -f /tmp/oauth-conn.json /tmp/oauth-conn-resp.json
echo "==> Done"
