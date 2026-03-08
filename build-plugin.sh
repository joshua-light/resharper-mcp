#!/bin/bash
set -euo pipefail

PLUGIN_NAME="ReSharperMcp"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING_DIR="$SCRIPT_DIR/.build-staging"

echo "Building plugin..."
dotnet build "$SCRIPT_DIR/src/ReSharperMcp/ReSharperMcp.csproj" -c Release -v quiet

echo "Building frontend JAR..."
(cd "$SCRIPT_DIR/rider-plugin" && jar cf "$SCRIPT_DIR/$PLUGIN_NAME.jar" META-INF/ 2>/dev/null) || \
(cd "$SCRIPT_DIR/rider-plugin" && zip -r "$SCRIPT_DIR/$PLUGIN_NAME.jar" META-INF/ -q)

echo "Assembling plugin ZIP..."
rm -rf "$STAGING_DIR"
mkdir -p "$STAGING_DIR/$PLUGIN_NAME/lib"
mkdir -p "$STAGING_DIR/$PLUGIN_NAME/dotnet"
cp "$SCRIPT_DIR/$PLUGIN_NAME.jar" "$STAGING_DIR/$PLUGIN_NAME/lib/"
cp "$SCRIPT_DIR/src/ReSharperMcp/bin/Release/net472/$PLUGIN_NAME.dll" "$STAGING_DIR/$PLUGIN_NAME/dotnet/"

cd "$STAGING_DIR"
rm -f "$SCRIPT_DIR/$PLUGIN_NAME.zip"
zip -r "$SCRIPT_DIR/$PLUGIN_NAME.zip" "$PLUGIN_NAME/" -q

# Cleanup
rm -rf "$STAGING_DIR"
rm -f "$SCRIPT_DIR/$PLUGIN_NAME.jar"

echo ""
echo "Done! Plugin ZIP created: $SCRIPT_DIR/$PLUGIN_NAME.zip"
echo "Upload it at: https://plugins.jetbrains.com/plugin/add"
