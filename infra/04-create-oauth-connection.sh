#!/usr/bin/env bash
# Create the Bot Service OAuth Connection that turns Entra into a "Generic OAuth 2"
# IdP from the bot's perspective. Mirrors what Paycor configures against their
# own non-Entra IdP, just pointing at Entra so we don't need a separate provider.
#
# IMPORTANT FINDINGS:
#  1. Use the `oauth2generic` (id 8379c6d2-...) service provider. The simpler
#     `oauth2` provider listed in listAuthServiceProviders is silently rewritten
#     to oauth2generic on PUT.
#  2. The provider requires ALL ELEVEN template parameters below. If you only
#     supply AuthorizationUrl/TokenUrl/RefreshUrl, ARM accepts the PUT but
#     Bot Service returns "ServiceError: An error occured while retrieving the
#     signin link" when the user clicks Sign In.
#  3. ARM normalizes parameter keys to camelCase on storage; supply them in
#     PascalCase on PUT to match the schema.
#  4. `az bot authsetting create` does not support all providers cleanly;
#     using ARM REST is more reliable.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/../.env"

PROVIDER_ID="8379c6d2-b262-4d4f-b89b-68dc5b5f5482"  # oauth2generic
SUB="$(az account show --query id -o tsv)"

AUTHORIZE_URL="https://login.microsoftonline.com/common/oauth2/v2.0/authorize"
TOKEN_URL="https://login.microsoftonline.com/common/oauth2/v2.0/token"

TOKEN="$(az account get-access-token --query accessToken -o tsv)"

echo "Deleting any existing connection..."
curl -s -X DELETE -H "Authorization: Bearer $TOKEN" \
    "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.BotService/botServices/$BOT_NAME/connections/$OAUTH_CONNECTION_NAME?api-version=2022-09-15" \
    -w "  HTTP %{http_code}\n" > /dev/null
sleep 2

cat > /tmp/oauth-conn.json <<JSON
{
  "location": "global",
  "properties": {
    "serviceProviderId": "$PROVIDER_ID",
    "clientId": "$BOT_APP_ID",
    "clientSecret": "$BOT_APP_SECRET",
    "scopes": "openid profile offline_access User.Read",
    "parameters": [
      {"key": "ClientId",                            "value": "$BOT_APP_ID"},
      {"key": "ClientSecret",                        "value": "$BOT_APP_SECRET"},
      {"key": "ScopeListDelimiter",                  "value": " "},
      {"key": "AuthorizationUrlTemplate",            "value": "$AUTHORIZE_URL"},
      {"key": "AuthorizationUrlQueryStringTemplate", "value": "?client_id={ClientId}&response_type=code&redirect_uri={RedirectUrl}&scope={Scopes}&state={State}"},
      {"key": "TokenUrlTemplate",                    "value": "$TOKEN_URL"},
      {"key": "TokenUrlQueryStringTemplate",         "value": ""},
      {"key": "TokenBodyTemplate",                   "value": "code={Code}&grant_type=authorization_code&redirect_uri={RedirectUrl}&client_id={ClientId}&client_secret={ClientSecret}"},
      {"key": "RefreshUrlTemplate",                  "value": "$TOKEN_URL"},
      {"key": "RefreshUrlQueryStringTemplate",       "value": ""},
      {"key": "RefreshBodyTemplate",                 "value": "refresh_token={RefreshToken}&grant_type=refresh_token&client_id={ClientId}&client_secret={ClientSecret}"}
    ]
  }
}
JSON

echo "Creating OAuth connection '$OAUTH_CONNECTION_NAME'..."
RESP=$(curl -sf -X PUT \
    "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.BotService/botServices/$BOT_NAME/connections/$OAUTH_CONNECTION_NAME?api-version=2022-09-15" \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    --data @/tmp/oauth-conn.json)

echo "$RESP" | python3 -c "
import json, sys
d = json.load(sys.stdin)
props = d.get('properties', {})
print('  Provider:', props.get('serviceProviderDisplayName'))
print('  State:   ', props.get('provisioningState'))
print('  Scopes:  ', props.get('scopes'))
print(f\"  Parameters: {len(props.get('parameters', []))} stored\")
"
rm -f /tmp/oauth-conn.json
