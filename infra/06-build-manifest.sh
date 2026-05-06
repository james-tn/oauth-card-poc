#!/usr/bin/env bash
# Builds the unified Microsoft 365 app manifest from template, generates
# placeholder icons, and zips the package for sideloading.
#
# The output package surfaces the same bot in BOTH:
#   - Teams chat            via `bots[]`
#   - M365 Copilot CEA      via `copilotAgents.customEngineAgents[]`
#
# Uses manifestVersion `devPreview` because `customEngineAgents` is currently
# in public preview and not in the GA 1.19/1.20 schemas.

set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
ROOT_DIR="$( cd "${SCRIPT_DIR}/.." && pwd )"
ENV_FILE="${ROOT_DIR}/.env"
MANIFEST_DIR="${ROOT_DIR}/manifest"

# shellcheck disable=SC1090
source "${ENV_FILE}"

if [[ -z "${BOT_URL}" || -z "${BOT_APP_ID}" ]]; then
    echo "ERROR: BOT_URL or BOT_APP_ID not set in .env" >&2
    exit 1
fi

HOST="$(echo "${BOT_URL}" | sed -e 's|https://||')"
MANIFEST_VERSION="${MANIFEST_VERSION:-1.0.0}"

# Stable manifest GUID derived from app id
MANIFEST_ID="${MANIFEST_ID:-$(python3 -c "import uuid; print(uuid.uuid5(uuid.NAMESPACE_DNS, 'oauth-card-poc:${BOT_APP_ID}'))")}"

echo "==> Building manifest"
echo "    bot id:           ${BOT_APP_ID}"
echo "    host:             ${HOST}"
echo "    manifest id:      ${MANIFEST_ID}"
echo "    manifest version: ${MANIFEST_VERSION}"

cat > "${MANIFEST_DIR}/manifest.json" <<JSON
{
  "\$schema": "https://developer.microsoft.com/json-schemas/teams/vDevPreview/MicrosoftTeams.schema.json",
  "manifestVersion": "devPreview",
  "version": "${MANIFEST_VERSION}",
  "id": "${MANIFEST_ID}",
  "developer": {
    "name": "OAuth POC",
    "websiteUrl": "https://example.com",
    "privacyUrl": "https://example.com/privacy",
    "termsOfUseUrl": "https://example.com/terms"
  },
  "name": {
    "short": "OAuth POC",
    "full": "OAuth Card POC Bot"
  },
  "description": {
    "short": "Generic OAuth 2 sign-in demo for Teams + M365 Copilot",
    "full": "Demonstrates OAuth Card sign-in via a Generic OAuth 2 Bot Service connection. Surfaces the same bot in Teams chat and Microsoft 365 Copilot Custom Engine Agents."
  },
  "icons": {
    "outline": "outline.png",
    "color": "color.png"
  },
  "accentColor": "#0078D4",
  "bots": [
    {
      "botId": "${BOT_APP_ID}",
      "scopes": ["personal"],
      "supportsFiles": false,
      "isNotificationOnly": false
    }
  ],
  "copilotAgents": {
    "customEngineAgents": [
      {
        "id": "${BOT_APP_ID}",
        "type": "bot"
      }
    ]
  },
  "permissions": ["identity"],
  "validDomains": [
    "${HOST}",
    "token.botframework.com",
    "login.microsoftonline.com"
  ],
  "defaultInstallScope": "personal"
}
JSON

# Generate trivial PNG icons if they don't exist
if [[ ! -f "${MANIFEST_DIR}/color.png" ]]; then
    echo "==> Generating placeholder icons"
    python3 - <<'PY'
import struct, zlib, os
def make_png(path, width, height, rgb):
    sig = b'\x89PNG\r\n\x1a\n'
    def chunk(typ, data):
        crc = zlib.crc32(typ + data)
        return struct.pack('>I', len(data)) + typ + data + struct.pack('>I', crc & 0xffffffff)
    ihdr = struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)  # 8-bit RGB
    raw = b''
    for _ in range(height):
        raw += b'\x00' + (bytes(rgb) * width)
    idat = zlib.compress(raw)
    png = sig + chunk(b'IHDR', ihdr) + chunk(b'IDAT', idat) + chunk(b'IEND', b'')
    with open(path, 'wb') as f:
        f.write(png)
md = os.environ['MANIFEST_DIR']
make_png(os.path.join(md, 'color.png'), 192, 192, (0, 120, 212))
make_png(os.path.join(md, 'outline.png'), 32, 32, (255, 255, 255))
PY
fi

echo "==> Zipping app package"
cd "${MANIFEST_DIR}"
rm -f oauth-poc-app.zip
zip -j oauth-poc-app.zip manifest.json color.png outline.png
ls -la oauth-poc-app.zip

echo
echo "==> Done"
echo "    Package: ${MANIFEST_DIR}/oauth-poc-app.zip"
echo "    Sideload via: Teams or M365 Copilot → Apps → Upload a custom app"
