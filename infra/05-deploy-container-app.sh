#!/usr/bin/env bash
# Builds the bot Docker image and deploys it to Azure Container Apps.
# Idempotent: re-run updates the existing container app with a new revision.
#
# This is the devtunnel-replacement: we get a stable HTTPS URL we can use as
# both the Bot Service messaging endpoint and the Teams manifest validDomain.
set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ROOT_DIR="$( cd "${SCRIPT_DIR}/.." && pwd )"
ENV_FILE="${ROOT_DIR}/.env"

# shellcheck disable=SC1090
source "${ENV_FILE}"

CAE_ENV_NAME="${CAE_ENV_NAME:-oauth-poc-cae-env}"
CAE_APP_NAME="${CAE_APP_NAME:-oauth-poc-bot-app}"
CAE_LOCATION="${CAE_LOCATION:-eastus2}"

if [[ -z "${BOT_APP_ID}" || -z "${BOT_APP_SECRET}" ]]; then
    echo "ERROR: BOT_APP_ID/BOT_APP_SECRET not set." >&2
    exit 1
fi

echo "==> Ensuring Container Apps environment ${CAE_ENV_NAME}"
if ! az containerapp env show -g "${RESOURCE_GROUP}" -n "${CAE_ENV_NAME}" --output none 2>/dev/null; then
    echo "    Creating environment in ${CAE_LOCATION}..."
    az containerapp env create \
        -g "${RESOURCE_GROUP}" \
        -n "${CAE_ENV_NAME}" \
        -l "${CAE_LOCATION}" \
        --logs-destination none \
        --output none
else
    echo "    Already exists"
fi

echo
echo "==> Deploying container app ${CAE_APP_NAME} from ${ROOT_DIR}/bot"
echo "    (this builds the Dockerfile via ACR Tasks and creates/updates the app)"

# az containerapp up handles: ACR creation, source build, image push, app create/update.
# Note: pass secret as plain env var on initial create (chicken-and-egg with secretref).
# We promote it to a Container App secret immediately after.
az containerapp up \
    --name "${CAE_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --location "${CAE_LOCATION}" \
    --environment "${CAE_ENV_NAME}" \
    --source "${ROOT_DIR}/bot" \
    --target-port 8080 \
    --ingress external \
    --env-vars \
        "BOT_APP_ID=${BOT_APP_ID}" \
        "BOT_APP_SECRET=${BOT_APP_SECRET}" \
        "TENANT_ID=${TENANT_ID}" \
        "OAUTH_CONNECTION_NAME=${OAUTH_CONNECTION_NAME}" \
        "BOT_PORT=8080" \
        "BOT_DEBUG=1"

# Promote secret out of plain env var
echo "==> Promoting BOT_APP_SECRET to a Container App secret"
az containerapp secret set \
    -g "${RESOURCE_GROUP}" \
    -n "${CAE_APP_NAME}" \
    --secrets "bot-app-secret=${BOT_APP_SECRET}" \
    --output none

az containerapp update \
    -g "${RESOURCE_GROUP}" \
    -n "${CAE_APP_NAME}" \
    --set-env-vars \
        "BOT_APP_SECRET=secretref:bot-app-secret" \
    --output none

FQDN="$(az containerapp show -g "${RESOURCE_GROUP}" -n "${CAE_APP_NAME}" --query properties.configuration.ingress.fqdn -o tsv)"
BOT_URL="https://${FQDN}"

echo
echo "==> Container App deployed"
echo "    URL: ${BOT_URL}"
echo "    Messaging endpoint: ${BOT_URL}/api/messages"

# Persist back
sed -i "s|^BOT_URL=.*|BOT_URL=${BOT_URL}|" "${ENV_FILE}"

# Update Bot Service messaging endpoint
echo
echo "==> Updating Bot Service messaging endpoint"
az bot update \
    -g "${RESOURCE_GROUP}" \
    -n "${BOT_HANDLE}" \
    --endpoint "${BOT_URL}/api/messages" \
    --output none

# Sanity-check
echo
echo "==> Health check"
sleep 5
curl -sf "${BOT_URL}/health" | python3 -m json.tool || echo "(health not ready yet — check logs with: az containerapp logs show -g ${RESOURCE_GROUP} -n ${CAE_APP_NAME} --follow)"

echo
echo "==> Done"
