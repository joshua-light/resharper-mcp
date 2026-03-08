# ReSharper MCP — Project Guide

## What This Is

An MCP (Model Context Protocol) server that runs **inside** the ReSharper/Rider backend process,
exposing code intelligence features to AI assistants via HTTP.

## Architecture

### Two-part Rider plugin

Rider plugins require both a JVM frontend and a .NET backend:

- `rider-plugin/META-INF/plugin.xml` — minimal IntelliJ plugin descriptor (no Java/Kotlin code needed for backend-only plugins). Without this JAR, Rider silently ignores the `dotnet/` folder.
- `src/ReSharperMcp/` — the .NET backend that runs inside the ReSharper host process.

### Installed plugin layout (in `~/.local/share/JetBrains/Rider2025.3/ReSharperMcp/`)

```
lib/ReSharperMcp.jar       # plugin.xml descriptor (tells Rider this plugin exists)
dotnet/ReSharperMcp.dll    # backend component (loaded into ReSharper host)
```

### MCP transport

The official C# MCP SDK (`ModelContextProtocol` NuGet) requires .NET 8+ for HTTP transport,
but ReSharper plugins target **net472**. So we implement a lightweight MCP server manually:

- `McpHttpServer.cs` — `HttpListener`-based HTTP server on `127.0.0.1:23741`
- Implements JSON-RPC 2.0 directly (handles `initialize`, `tools/list`, `tools/call`)
- Protocol types in `Protocol/JsonRpc.cs` and `Protocol/McpTypes.cs`
- Uses `Newtonsoft.Json` (bundled with Rider's ReSharper host, no need to ship it)

### ReSharper integration

- `McpServerComponent.cs` — `[SolutionComponent]` that starts the HTTP server on solution open, stops on close via `Lifetime`. Registers all tools and handles threading dispatch.
- `PsiHelpers.cs` — shared helpers: file lookup, position-to-node resolution, element resolution, snippet truncation.
- `Tools/IMcpTool.cs` — common interface for all tools. Each tool provides its name, description, JSON schema, and execute method.

## Tools

| Tool | Description |
|------|-------------|
| `find_usages` | Find all references to a symbol (uses `IFinder.FindReferences()`) |
| `get_symbol_info` | Get detailed symbol info: kind, type, params, docs, base types, declaration location |
| `find_implementations` | Find implementations of interfaces/abstract classes and overrides of virtual members |
| `get_file_errors` | Get compile errors and unresolved references by walking the PSI tree |
| `search_symbol` | Search symbols by name (substring match) across the solution |
| `go_to_definition` | Navigate from a usage site or symbol name to the declaration location |
| `get_solution_structure` | List all projects, target frameworks, and project-to-project references |
| `browse_namespace` | Browse namespace hierarchy: child namespaces and types in a namespace |
| `list_symbols_in_file` | List all declarations in a file (types, methods, properties, etc.) |

### Symbol resolution

All tools that take a symbol accept **two modes**:
- `filePath` + `line` + `column` (1-based) — resolve from a specific code position
- `symbolName` — resolve by name (e.g. `"MyClass"`, `"Namespace.MyClass"`, `"MyClass.MyMethod"`)
- Optional `kind` filter (`"type"`, `"method"`, `"property"`, `"field"`, `"event"`) to disambiguate

When multiple symbols match a name, tools return an **ambiguity error** listing all candidates with their qualified names, kinds, and locations — so the caller can pick the right one.

`search_symbol`, `get_file_errors`, `get_solution_structure`, `browse_namespace`, and `list_symbols_in_file` use their own input formats.

`search_symbol` excludes namespace declarations by default to reduce noise. Pass `includeNamespaces: true` or add `"namespace"` to the `kinds` filter to include them.

### Language support

PSI file resolution is **language-agnostic** (`GetPsiFiles<KnownLanguage>()`), supporting C#, F#, VB, and any language with a ReSharper PSI implementation.

## Key Findings & Gotchas

### ReSharper SDK on Linux
- `JetBrains.ReSharper.SDK` 2025.3.3 **builds fine on Linux** with `dotnet build` targeting `net472`. The SDK provides reference assemblies.
- Rider 2025.3 loads `net472` plugins (confirmed by inspecting `ReSharperPlugin.HeapView.dll`).

### SolutionComponent attribute (2025.3 SDK)
The parameterless `[SolutionComponent]` constructor is **obsolete** in 2025.3. Must use:
```csharp
[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
```
The `Instantiation` enum lives in `JetBrains.Application.Parts`.

### Threading model
PSI operations **cannot** run on thread pool threads. The error:
> "This action cannot be executed on the .NET TP Worker thread"

Solution: inject `IShellLocks` and use `ExecuteOrQueueReadLock` to dispatch to the correct thread:
```csharp
shellLocks.ExecuteOrQueueReadLock("ReSharperMcp.FindUsages", () => { ... });
```
For synchronous HTTP responses, block the HTTP thread with `ManualResetEventSlim` until the R# thread completes (30s timeout).

### Getting PSI files
`IPsiSourceFile.GetPrimaryPsiFile()` does not exist. For language-agnostic access:
```csharp
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Files;
sourceFile.GetPsiFiles<KnownLanguage>(); // returns all PSI files for any language
```
For a specific language: `sourceFile.GetDominantPsiFile<CSharpLanguage>()` (requires `T : PsiLanguageType`).

### Document coordinates
`DocumentCoords` takes typed intrinsics, not plain ints:
```csharp
var docLine = (Int32<DocLine>)(line - 1);    // 0-based
var docColumn = (Int32<DocColumn>)(column - 1);
var coords = new DocumentCoords(docLine, docColumn);
```

### Document range from tree nodes
`GetDocumentRange()` is an extension in `JetBrains.ReSharper.Psi.Tree.TreeNodeExtensions`. If overload resolution fails, call it explicitly:
```csharp
var range = TreeNodeExtensions.GetDocumentRange(node);
```

### Document offset to coordinates
`IDocument.GetCoordsByOffset()` is obsolete. Use:
```csharp
var coords = documentOffset.ToDocumentCoords();
```

### Type presentation
`PresentationLanguageForTests` does not exist. Use `CSharpLanguage.Instance` as fallback:
```csharp
var lang = declaredElement.PresentationLanguage ?? CSharpLanguage.Instance;
type.GetPresentableName(lang);
```

### Abstract check on types
`ITypeElement.IsAbstract()` doesn't exist. Cast to `IModifiersOwner`:
```csharp
if (typeElement is IModifiersOwner m) result["isAbstract"] = m.IsAbstract;
```

### Namespace access
`GetContainingNamespace()` is on `ITypeElement`, not `IClrDeclaredElement`. For non-type elements, go via `GetContainingType().GetContainingNamespace()`.

### Symbol scope for namespace browsing
`LibrarySymbolScope` is in `JetBrains.ReSharper.Psi.Caches`:
```csharp
using JetBrains.ReSharper.Psi.Caches;
var symbolScope = psiServices.Symbols.GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);
var globalNs = symbolScope.GlobalNamespace;
globalNs.GetNestedNamespaces(symbolScope);
globalNs.GetNestedTypeElements(symbolScope);
```

### Project references (2025.3 SDK)
`GetProjectReferences` requires a `TargetFrameworkId`:
```csharp
using JetBrains.ProjectModel.Impl;
foreach (var tfm in project.TargetFrameworkIds)
    foreach (var reference in project.GetProjectReferences(tfm))
        var name = ProjectReferenceExtension.GetReferencedName(reference);
```
`IProjectToProjectReference` does **not** have `ResolveReferencedProject()` — use `GetReferencedName()`.

### Daemon API
`IDaemon` has no public "get current highlightings" API. For file errors, walk the PSI tree:
- `IErrorElement` nodes for syntax errors
- `reference.Resolve().ResolveErrorType` for unresolved references
`ResolveErrorType` is in `JetBrains.ReSharper.Psi.Resolve`.

## File Map

```
CLAUDE.md                              # this file
README.md                              # user-facing docs
install-rider.sh                       # build + install script (auto-detects Rider version)
ReSharperMcp.sln                       # solution file
rider-plugin/META-INF/plugin.xml       # IntelliJ plugin descriptor
src/ReSharperMcp/
  ReSharperMcp.csproj                  # targets net472, refs JetBrains.ReSharper.SDK 2025.3.3
  McpServerComponent.cs                # SolutionComponent — starts/stops server, registers tools
  McpHttpServer.cs                     # HttpListener-based MCP server (JSON-RPC 2.0)
  PsiHelpers.cs                        # Shared: file lookup, position resolution, snippets
  Protocol/
    JsonRpc.cs                         # JSON-RPC request/response/error types
    McpTypes.cs                        # MCP types: InitializeResult, ToolDefinition, etc.
  Tools/
    IMcpTool.cs                        # Tool interface: Name, Description, InputSchema, Execute
    FindUsagesTool.cs                  # find_usages — IFinder.FindReferences()
    GetSymbolInfoTool.cs               # get_symbol_info — detailed symbol metadata
    FindImplementationsTool.cs         # find_implementations — FindInheritors/FindImplementingMembers
    GetFileErrorsTool.cs               # get_file_errors — PSI tree walk for errors
    SearchSymbolTool.cs                # search_symbol — substring search across declarations
    GoToDefinitionTool.cs              # go_to_definition — navigate to symbol declaration
    GetSolutionStructureTool.cs        # get_solution_structure — projects, TFMs, references
    BrowseNamespaceTool.cs             # browse_namespace — namespace hierarchy exploration
    ListSymbolsInFileTool.cs           # list_symbols_in_file — all declarations in a file
```

## Building & Installing

```bash
./install-rider.sh              # builds Release, creates JAR, copies to Rider plugin dir
# then restart Rider
```

Override port: `RESHARPER_MCP_PORT=9999`

## Testing

```bash
# Initialize handshake
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}'

# List tools
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

# Find usages
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find_usages","arguments":{"filePath":"/path/to/File.cs","line":10,"column":18}}}'

# Get symbol info
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_symbol_info","arguments":{"filePath":"/path/to/File.cs","line":10,"column":18}}}'

# Find implementations
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"find_implementations","arguments":{"filePath":"/path/to/IFoo.cs","line":5,"column":18}}}'

# Get file errors
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_file_errors","arguments":{"filePath":"/path/to/File.cs"}}}'

# Search symbols by name
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_symbol","arguments":{"query":"Player","maxResults":10}}}'

# Go to definition (by name)
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"go_to_definition","arguments":{"symbolName":"MyClass"}}}'

# Find usages by symbol name (no need for file:line:col)
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"find_usages","arguments":{"symbolName":"IConnection"}}}'

# Find implementations by symbol name
curl -s http://127.0.0.1:23741/ -X POST -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"find_implementations","arguments":{"symbolName":"IConnection"}}}'
```

## Next Steps / Ideas

- SSE support for streaming results on large solutions
- NuGet packaging / JetBrains Marketplace distribution
- GitHub CI with release artifacts
- Proper Rider plugin with settings UI (port config, enable/disable)
### Symbol name resolution (`PsiHelpers.ResolveSymbolByName`)
Walks all PSI files looking for declarations matching the given name. Supports:
- Short names: `"MyClass"` → first match
- Qualified names: `"Namespace.MyClass"` → exact FQN match, falls back to short name match
- Nested types: `"MyClass.InnerType"`
- Skips `INamespace` elements to avoid noise
