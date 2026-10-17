# ReSharper MCP Server

An MCP (Model Context Protocol) server that runs inside the ReSharper/Rider backend process, exposing code intelligence features to AI assistants via HTTP.

Supports C#, F#, VB, and any language with a ReSharper PSI implementation.

## Tools

| Tool | Description |
|------|-------------|
| `find_usages` | Find all references to a symbol; rg-shaped `path` / `line: code` output, `mode: "files"`, `pathPrefix`, `excludeTests` |
| `get_symbol_info` | Detailed symbol info: kind, type, params, docs, base types, members |
| `find_implementations` | Find implementations of interfaces/abstract classes and overrides |
| `get_file_errors` | Fast compile check: syntax errors and unresolved references; `changedSince: "<git ref>"` checks every changed file |
| `search_symbol` | Search symbols by name (substring match) across the solution; `includeLibraries` adds referenced DLLs |
| `go_to_definition` | Navigate to a symbol's declaration with source text |
| `get_solution_structure` | List projects, target frameworks, and project references |
| `browse_namespace` | Browse namespace hierarchy: child namespaces and types |
| `list_symbols_in_file` | List all declarations in a file |
| `list_solutions` | List all open solutions across Rider instances |
| `fix_usings` | Fix missing using directives in C# files |
| `format_file` | Format, clean up, or apply code style to a file |
| `flow` | Describe control flow of a method or type: execution steps, branches, loops, error paths, inlined call targets |
| `get_symbol_source` | Get the full declaration source code of a symbol; decompiled source for symbols compiled into DLLs |
| `get_call_hierarchy` | Build an incoming (callers) or outgoing (callees) call hierarchy tree for a method |
| `get_type_hierarchy` | Get the inheritance hierarchy of a type: supertypes (base/interfaces) or subtypes |
| `get_diagnostics` | Run daemon inspections on a file; reports severity, inspection id, message, location, and quick-fix availability |
| `list_quick_fixes` | List the ReSharper quick-fixes (bulb actions) available at a position |
| `complete_at` | Get code completion suggestions at a caret position |

Write tools (modify source — require a write lock):

| Tool | Description |
|------|-------------|
| `rename_symbol` | Semantic, solution-wide rename of a symbol and all its references (supports `dryRun`) |
| `generate_members` | Generate members on a type (constructors, overrides, equality members, etc.) |
| `apply_quick_fix` | Apply a ReSharper quick-fix (bulb action) at a position |
| `apply_suggestions` | Apply inspection quick-fixes file-wide by inspection id (e.g. convert explicit constructor → primary constructor); position-free, `dryRun`/`all` supported |

### Symbol resolution

Tools that operate on a symbol accept two modes:
- **By position** — `filePath` + `line` + `column` (1-based)
- **By name** — `symbolName` (e.g. `"MyClass"`, `"MyClass.MyMethod"`, `"Namespace.MyClass"`, `"Wait<M>"`, `"Execute(int)"`)

Names are looked up in sources first, then in referenced assemblies (types and `Type.Member`), then in sources ignoring case. An optional `kind` filter (`"type"`, `"method"`, `"property"`, `"field"`, `"event"`) helps disambiguate. When multiple symbols match, tools return an ambiguity error listing all candidates with their qualified names, kinds, and locations. When nothing matches, the error lists up to five similar names.

File paths may be absolute, or relative to the repository root or the solution's folder.

### Files edited outside Rider

Before every call the server checks the files the call names, and the files it saw change on disk, against Rider's copy. A stale copy is reconciled with disk the way Rider does when its own file watcher reports a change, and Rider is asked to rescan, so a new file joins its project. A file open in an editor tab reloads at Rider's pace; the call waits up to 10 s for it. A copy with unsaved edits in Rider is never touched; the result notes that it reflects Rider's copy.

Edit tools (`fix_usings`, `format_file`, `rename_symbol`, `generate_members`, `apply_quick_fix`, `apply_suggestions`) refuse to run while Rider's copy of a named or changed file still differs from disk, because the edit would be applied to the old text and saved over the new one. `dryRun: true` calls are never refused.

### Failures

- A type or member ReSharper cannot read is skipped, and the result ends with `skipped N elements ReSharper failed to read (...)`.
- In a batch, a failing item reports one error line under its header; the other items still answer.
- A crash is reported as one line (`ReSharper failed on <tool>: <ExceptionType>: <message>`). The stack trace goes to the Rider log.

### Batch mode

Most tools support batch mode — processing multiple inputs in a single call. This reduces round-trips when querying several symbols or files at once:

- **Symbol-based tools** (`find_usages`, `get_symbol_info`, `find_implementations`, `go_to_definition`) accept a `symbols` array of `{symbolName, kind, filePath, line, column}` objects.
- **File-based tools** (`get_file_errors`, `list_symbols_in_file`, `fix_usings`, `format_file`) accept a `filePaths` array of strings.
- **`search_symbol`** accepts a `queries` array of strings.
- **`browse_namespace`** accepts a `namespaceNames` array of strings.

Results are concatenated with `=== [N/total] label ===` separators. Shared options (e.g. `maxResults`, `kinds`, `mode`) apply to all items in the batch. Original single-input parameters remain for backward compatibility.

### Health check

`GET http://127.0.0.1:23741/health` answers plain JSON without an MCP session, e.g. for a session-start hook:

```json
{"solutions":[{"name":"TPS.All","path":"/…/TPS.All.sln","indexing":false,"pendingDiskChanges":0}],"indexing":false,"pendingDiskChanges":0}
```

While indexing is in progress, tool results end with `(index still building — results may be incomplete)`. The `initialize` response carries a short usage guide in `instructions`.

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

With several solutions open, pass `solutionName`, or a `filePath` that picks one: an absolute path picks the solution whose git work tree contains it; a relative path picks the one solution where it exists (in several, the call asks for `solutionName`).

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
