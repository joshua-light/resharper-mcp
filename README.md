# ReSharper MCP Server

An MCP (Model Context Protocol) server that runs inside the ReSharper process, exposing code intelligence features to AI assistants.

## Status

**MVP** — currently supports:
- `find_usages` — find all usages of a symbol by file path + line/column position

## Architecture

- Runs as a ReSharper `SolutionComponent` (activated when a solution opens)
- Hosts an HTTP server on `127.0.0.1:23741` implementing MCP over JSON-RPC 2.0
- Uses ReSharper's PSI (Program Structure Interface) APIs for code analysis

## Configuration

Set `RESHARPER_MCP_PORT` environment variable to override the default port (23741).

## MCP Client Configuration

Add to your MCP client config:

```json
{
  "mcpServers": {
    "resharper": {
      "url": "http://127.0.0.1:23741/"
    }
  }
}
```

## Building

```
dotnet build src/ReSharperMcp/ReSharperMcp.csproj
```

## Tools

### find_usages

Find all references to a code symbol in the solution.

**Parameters:**
- `filePath` (string) — absolute path to the file containing the symbol
- `line` (integer) — 1-based line number
- `column` (integer) — 1-based column number

**Example response:**
```json
{
  "symbol": "MyMethod",
  "kind": "Method",
  "usagesCount": 3,
  "usages": [
    { "file": "/path/to/Consumer.cs", "line": 42, "column": 12, "text": "obj.MyMethod()" }
  ]
}
```
