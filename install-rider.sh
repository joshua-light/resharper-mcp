#!/bin/bash
set -euo pipefail

PLUGIN_NAME="ReSharperMcp"
RIDER_VERSION="${1:-}"

# Auto-detect Rider version if not specified
if [ -z "$RIDER_VERSION" ]; then
    # Try macOS path first, then Linux
    RIDER_DIR=$(ls -d "$HOME/Library/Application Support/JetBrains/Rider"* 2>/dev/null | sort -V | tail -1)
    if [ -z "$RIDER_DIR" ]; then
        RIDER_DIR=$(ls -d ~/.local/share/JetBrains/Rider* 2>/dev/null | sort -V | tail -1)
    fi
    if [ -z "$RIDER_DIR" ]; then
        echo "Error: Could not find Rider plugin directory."
        echo "Usage: $0 [Rider2025.3]"
        exit 1
    fi
else
    # Check macOS path first, then Linux
    if [ -d "$HOME/Library/Application Support/JetBrains/$RIDER_VERSION" ]; then
        RIDER_DIR="$HOME/Library/Application Support/JetBrains/$RIDER_VERSION"
    else
        RIDER_DIR="$HOME/.local/share/JetBrains/$RIDER_VERSION"
    fi
fi

PLUGIN_DIR="$RIDER_DIR/plugins/$PLUGIN_NAME"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "Building plugin..."
dotnet build "$SCRIPT_DIR/src/ReSharperMcp/ReSharperMcp.csproj" -c Release -v quiet

echo "Building frontend JAR..."
(cd "$SCRIPT_DIR/rider-plugin" && jar cf "$SCRIPT_DIR/$PLUGIN_NAME.jar" META-INF/ 2>/dev/null) || \
(cd "$SCRIPT_DIR/rider-plugin" && zip -r "$SCRIPT_DIR/$PLUGIN_NAME.jar" META-INF/ -q)

echo "Installing to: $PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR/dotnet"
mkdir -p "$PLUGIN_DIR/lib"
cp "$SCRIPT_DIR/src/ReSharperMcp/bin/Release/net472/$PLUGIN_NAME.dll" "$PLUGIN_DIR/dotnet/"
cp "$SCRIPT_DIR/src/ReSharperMcp/bin/Release/net472/$PLUGIN_NAME.pdb" "$PLUGIN_DIR/dotnet/" 2>/dev/null || true
cp "$SCRIPT_DIR/$PLUGIN_NAME.jar" "$PLUGIN_DIR/lib/"
rm -f "$SCRIPT_DIR/$PLUGIN_NAME.jar"

echo ""
echo "Done! Plugin installed to $PLUGIN_DIR"
echo "  lib/  -> $PLUGIN_NAME.jar (frontend descriptor)"
echo "  dotnet/ -> $PLUGIN_NAME.dll (backend component)"
echo ""
echo "Restart Rider for the plugin to take effect."
echo "The MCP server will start on http://127.0.0.1:23741/ when you open a solution."
echo "Set RESHARPER_MCP_PORT env var to use a different port."
