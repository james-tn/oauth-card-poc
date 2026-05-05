#!/usr/bin/env bash
# Creates Azure Bot Service resource (F0 SKU, registration-only) and enables Teams channel.
# Idempotent: re-run is safe.
set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ROOT_DIR="$( cd "${SCRIPT_DIR}/.." && pwd )"
ENV_FILE="${ROOT_DIR}/.env"

# shellcheck disable=SC1090
source "${ENV_FILE}"

if [[ -z "${BOT_APP_ID}" ]]; then
    echo "ERROR: BOT_APP_ID not set. Run 01-create-aad-app.sh first." >&2
    exit 1
fi

echo "==> Ensuring resource group ${RESOURCE_GROUP} exists in ${LOCATION}"
az group create -n "${RESOURCE_GROUP}" -l "${LOCATION}" --output none

# Initial messaging endpoint placeholder; we'll patch it after devtunnel comes up.
PLACEHOLDER_ENDPOINT="https://example.com/api/messages"

echo "==> Looking for existing bot resource: ${BOT_HANDLE}"
EXISTING="$(az bot show -g "${RESOURCE_GROUP}" -n "${BOT_HANDLE}" --query name -o tsv 2>/dev/null || true)"

if [[ -z "${EXISTING}" ]]; then
    echo "==> Creating Bot Service (F0, SingleTenant, app id ${BOT_APP_ID})"
    az bot create \
        --resource-group "${RESOURCE_GROUP}" \
        --name "${BOT_HANDLE}" \
        --app-type SingleTenant \
        --appid "${BOT_APP_ID}" \
        --tenant-id "${TENANT_ID}" \
        --sku F0 \
        --location global \
        --display-name "${BOT_DISPLAY_NAME}" \
        --endpoint "${PLACEHOLDER_ENDPOINT}" \
        --output none
    echo "    Created bot: ${BOT_HANDLE}"
else
    echo "    Bot resource already exists"
fi

echo "==> Enabling Microsoft Teams channel"
az bot msteams create \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${BOT_HANDLE}" \
    --output none

echo
echo "==> Done"
echo "    BOT_HANDLE=${BOT_HANDLE}"
echo "    Endpoint placeholder: ${PLACEHOLDER_ENDPOINT}"
echo "    (will be updated to devtunnel URL by step 07)"
