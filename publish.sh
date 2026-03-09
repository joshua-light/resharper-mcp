#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Load .env if present
if [ -f "$SCRIPT_DIR/.env" ]; then
    set -a
    source "$SCRIPT_DIR/.env"
    set +a
fi

if [ -z "${JB_MARKETPLACE_PAT:-}" ]; then
    echo "Error: JB_MARKETPLACE_PAT environment variable is not set."
    echo "Create a token at: https://plugins.jetbrains.com/author/me/tokens"
    exit 1
fi

ZIP_FILE="$SCRIPT_DIR/ReSharperMcp.zip"

# Build the plugin ZIP
"$SCRIPT_DIR/build-plugin.sh"

echo "Uploading to JetBrains Marketplace..."
RESPONSE=$(curl -s -w "\n%{http_code}" \
    --header "Authorization: Bearer $JB_MARKETPLACE_PAT" \
    -F xmlId=com.j-light.resharper-mcp \
    -F file=@"$ZIP_FILE" \
    https://plugins.jetbrains.com/api/updates/upload)

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

if [ "$HTTP_CODE" -ge 200 ] && [ "$HTTP_CODE" -lt 300 ]; then
    echo "Published successfully!"
    echo "$BODY"
else
    echo "Upload failed (HTTP $HTTP_CODE):"
    echo "$BODY"
    exit 1
fi
