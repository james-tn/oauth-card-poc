#!/usr/bin/env bash
# Creates the test user in 0fbe7234 tenant for OAuth Card sign-in.
# Idempotent: re-run is safe.
set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ROOT_DIR="$( cd "${SCRIPT_DIR}/.." && pwd )"
ENV_FILE="${ROOT_DIR}/.env"

# shellcheck disable=SC1090
source "${ENV_FILE}"

UPN="${TEST_USER_UPN_PREFIX}@${TENANT_DOMAIN}"
echo "==> Looking for existing test user: ${UPN}"

USER_ID="$(az ad user list --filter "userPrincipalName eq '${UPN}'" --query '[0].id' -o tsv 2>/dev/null || true)"

# Generate a strong password regardless (we'll reset if user exists)
PASSWORD="OauthPoc!$(openssl rand -hex 8)"

if [[ -z "${USER_ID}" || "${USER_ID}" == "null" ]]; then
    echo "==> Creating user ${UPN}"
    USER_ID="$(az ad user create \
        --display-name "${TEST_USER_DISPLAY_NAME}" \
        --user-principal-name "${UPN}" \
        --password "${PASSWORD}" \
        --force-change-password-next-sign-in false \
        --query id -o tsv)"
    echo "    Created user id: ${USER_ID}"
else
    echo "    Found existing user: ${USER_ID}"
    echo "==> Resetting password"
    az ad user update --id "${USER_ID}" --password "${PASSWORD}" --force-change-password-next-sign-in false >/dev/null
fi

# Persist back to .env
sed -i "s|^TEST_USER_ID=.*|TEST_USER_ID=${USER_ID}|" "${ENV_FILE}"
sed -i "s|^TEST_USER_UPN=.*|TEST_USER_UPN=${UPN}|" "${ENV_FILE}"
sed -i "s|^TEST_USER_PASSWORD=.*|TEST_USER_PASSWORD=${PASSWORD}|" "${ENV_FILE}"

echo
echo "==> Done"
echo "    TEST_USER_UPN=${UPN}"
echo "    TEST_USER_PASSWORD stored in ${ENV_FILE}"
