# ReSharper MCP Server

An MCP (Model Context Protocol) server that runs inside the ReSharper/Rider backend process, exposing code intelligence features to AI assistants via HTTP.

Supports C#, F#, VB, and any language with a ReSharper PSI implementation.

## Tools

| Tool | Description |
|------|-------------|
| `find_usages` | Find all references to a symbol |
| `get_symbol_info` | Detailed symbol info: kind, type, params, docs, base types, members |
| `find_implementations` | Find implementations of interfaces/abstract classes and overrides |
| `get_file_errors` | Get compile errors and unresolved references |
| `search_symbol` | Search symbols by name (substring match) across the solution |
| `go_to_definition` | Navigate to a symbol's declaration with source text |
| `get_solution_structure` | List projects, target frameworks, and project references |
| `browse_namespace` | Browse namespace hierarchy: child namespaces and types |
| `list_symbols_in_file` | List all declarations in a file |
| `list_solutions` | List all open solutions across Rider instances |
| `fix_usings` | Fix missing using directives in C# files |
| `format_file` | Format, clean up, or apply code style to a file |

### Symbol resolution

Tools that operate on a symbol accept two modes:
- **By position** — `filePath` + `line` + `column` (1-based)
- **By name** — `symbolName` (e.g. `"MyClass"`, `"Namespace.MyClass"`, `"MyClass.MyMethod"`)

An optional `kind` filter (`"type"`, `"method"`, `"property"`, `"field"`, `"event"`) helps disambiguate. When multiple symbols match, tools return an ambiguity error listing all candidates with their qualified names, kinds, and locations.

## Installation

### From JetBrains Marketplace

Install the plugin from Rider: **Settings → Plugins → Marketplace** → search for "MCP Server for Code Intelligence".

### From source

```bash
./install-rider.sh
# Restart Rider
```

The script builds the plugin and copies it to your local Rider plugin directory.

## MCP client configuration

Add to your MCP client config (e.g. Claude Code `settings.json`):

```json
{
  "mcpServers": {
    "resharper": {
      "type": "http",
      "url": "http://127.0.0.1:23741/"
    }
  }
}
```

The server starts automatically when you open a solution in Rider.

Set `RESHARPER_MCP_PORT` environment variable to override the default port.

## Building

```bash
# Build the .NET backend
dotnet build src/ReSharperMcp/ReSharperMcp.csproj -c Release

# Build a distributable plugin ZIP
./build-plugin.sh
```

## Architecture

- Runs as a ReSharper `SolutionComponent` (activated when a solution opens, stopped when it closes)
- Hosts an HTTP server on `127.0.0.1:23741` implementing MCP over JSON-RPC 2.0
- Uses ReSharper's PSI (Program Structure Interface) APIs for code analysis
- Two-part Rider plugin: minimal JVM JAR (plugin descriptor) + .NET backend DLL
- Targets `net472` (required by the ReSharper host process)
