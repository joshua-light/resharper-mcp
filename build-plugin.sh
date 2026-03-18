#!/bin/bash
set -euo pipefail

PLUGIN_NAME="ReSharperMcp"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING_DIR="$SCRIPT_DIR/.build-staging"

# Ensure JAVA_HOME is set for Gradle. If not already set, try to find Rider's bundled JBR.
if [ -z "${JAVA_HOME:-}" ]; then
    if [ -d "/Applications/Rider.app/Contents/jbr/Contents/Home" ]; then
        export JAVA_HOME="/Applications/Rider.app/Contents/jbr/Contents/Home"
    fi
fi

echo "Building backend..."
dotnet build "$SCRIPT_DIR/src/ReSharperMcp/ReSharperMcp.csproj" -c Release -v quiet

echo "Building frontend..."
(cd "$SCRIPT_DIR/rider-plugin" && ./gradlew jar --quiet)

echo "Assembling plugin ZIP..."
rm -rf "$STAGING_DIR"
mkdir -p "$STAGING_DIR/$PLUGIN_NAME/lib"
mkdir -p "$STAGING_DIR/$PLUGIN_NAME/dotnet"
cp "$SCRIPT_DIR/rider-plugin/build/libs/$PLUGIN_NAME.jar" "$STAGING_DIR/$PLUGIN_NAME/lib/"
cp "$SCRIPT_DIR/src/ReSharperMcp/bin/Release/net472/$PLUGIN_NAME.dll" "$STAGING_DIR/$PLUGIN_NAME/dotnet/"

cd "$STAGING_DIR"
rm -f "$SCRIPT_DIR/$PLUGIN_NAME.zip"
zip -r "$SCRIPT_DIR/$PLUGIN_NAME.zip" "$PLUGIN_NAME/" -q

# Cleanup
rm -rf "$STAGING_DIR"

echo ""
echo "Done! Plugin ZIP created: $SCRIPT_DIR/$PLUGIN_NAME.zip"
echo "Upload it at: https://plugins.jetbrains.com/plugin/add"
