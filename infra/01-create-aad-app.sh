#!/usr/bin/env bash
# Creates the MULTI-TENANT AAD app that plays both bot identity and OAuth client roles.
# Multi-tenant variant: lets users from ANY Entra tenant sign in (mirrors how
# an ISV-published app accepts customer-tenant users).
# Idempotent: re-run is safe.
set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ROOT_DIR="$( cd "${SCRIPT_DIR}/.." && pwd )"
ENV_FILE="${ROOT_DIR}/.env"

# shellcheck disable=SC1090
source "${ENV_FILE}"

echo "==> Looking for existing AAD app: ${BOT_AAD_APP_NAME}"
APP_ID="$(az ad app list --display-name "${BOT_AAD_APP_NAME}" --query '[0].appId' -o tsv 2>/dev/null || true)"

if [[ -z "${APP_ID}" || "${APP_ID}" == "null" ]]; then
    echo "==> Creating MULTI-TENANT AAD app: ${BOT_AAD_APP_NAME}"
    APP_ID="$(az ad app create \
        --display-name "${BOT_AAD_APP_NAME}" \
        --sign-in-audience AzureADMultipleOrgs \
        --web-redirect-uris "https://token.botframework.com/.auth/web/redirect" \
        --query appId -o tsv)"
    echo "    Created app id: ${APP_ID}"
    # Wait briefly for AD propagation
    sleep 10
else
    echo "    Found existing app: ${APP_ID}"
    # Make sure redirect URI is set + audience is multi-tenant
    az ad app update --id "${APP_ID}" \
        --web-redirect-uris "https://token.botframework.com/.auth/web/redirect" \
        --sign-in-audience AzureADMultipleOrgs >/dev/null
fi

OBJECT_ID="$(az ad app show --id "${APP_ID}" --query id -o tsv)"
echo "==> App object id: ${OBJECT_ID}"

# Ensure a service principal exists in this (home) tenant (required for OAuth flows + bot identity).
# Foreign tenants will get their own SP auto-created on first user consent.
SP_ID="$(az ad sp list --filter "appId eq '${APP_ID}'" --query '[0].id' -o tsv 2>/dev/null || true)"
if [[ -z "${SP_ID}" || "${SP_ID}" == "null" ]]; then
    echo "==> Creating service principal for app in home tenant"
    az ad sp create --id "${APP_ID}" >/dev/null
fi

# Create or rotate client secret
echo "==> Creating client secret (1 year)"
SECRET="$(az ad app credential reset --id "${APP_ID}" --display-name oauth-poc-secret --years 1 --query password -o tsv)"

# Persist back to .env
sed -i "s|^BOT_APP_ID=.*|BOT_APP_ID=${APP_ID}|" "${ENV_FILE}"
sed -i "s|^BOT_APP_SECRET=.*|BOT_APP_SECRET=${SECRET}|" "${ENV_FILE}"
sed -i "s|^BOT_APP_OBJECT_ID=.*|BOT_APP_OBJECT_ID=${OBJECT_ID}|" "${ENV_FILE}"

echo
echo "==> Done"
echo "    BOT_APP_ID=${APP_ID}"
echo "    BOT_APP_OBJECT_ID=${OBJECT_ID}"
echo "    signInAudience=AzureADMultipleOrgs"
echo "    Client secret stored in ${ENV_FILE}"
