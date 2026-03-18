using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class ListSymbolsInFileTool : IMcpTool
    {
        private readonly ISolution _solution;

        public ListSymbolsInFileTool(ISolution solution) => _solution = solution;

        public string Name => "list_symbols_in_file";

        public string Description =>
            "List all symbols declared in a file: types, methods, properties, fields, events. " +
            "Provides a structural overview of a file without reading the full source.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new { type = "string", description = "Absolute path to the file" },
                kinds = new { type = "string", description = "Comma-separated filter: 'type', 'method', 'property', 'field', 'event'. Default: all (excluding locals/parameters)." },
                includeLocals = new { type = "boolean", description = "Include local variables and parameters. Default: false." }
            },
            required = new[] { "filePath" }
        };

        public object Execute(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            var kindsFilter = arguments["kinds"]?.ToString();
            var includeLocals = arguments["includeLocals"]?.Value<bool>() ?? false;

            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return new { error = resolved.Error };
            var sourceFile = resolved.SourceFile;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return new { error = "Could not get PSI tree for file" };

            var kindSet = !string.IsNullOrEmpty(kindsFilter)
                ? new HashSet<string>(kindsFilter.Split(',').Select(k => k.Trim().ToLowerInvariant()))
                : null;

            // Collect property/event names to filter compiler-generated accessors
            var propertyNames = new HashSet<string>();
            var eventNames = new HashSet<string>();
            foreach (var node in psiFile.Descendants().OfType<IDeclaration>())
            {
                var el = node.DeclaredElement;
                if (el is IProperty p) propertyNames.Add(p.ShortName);
                if (el is IEvent e) eventNames.Add(e.ShortName);
            }

            var symbols = new List<SymbolEntry>();
            var seen = new HashSet<string>();

            foreach (var node in psiFile.Descendants().OfType<IDeclaration>())
            {
                var element = node.DeclaredElement;
                if (element == null) continue;
                if (element is INamespace) continue;

                // Exclude local variables and parameters by default
                if (!includeLocals && (element is ILocalVariable || element is IParameter))
                    continue;

                // Skip compiler-generated accessors (get_X/set_X, add_X/remove_X)
                if (element is IMethod accessorMethod)
                {
                    var name = accessorMethod.ShortName;
                    if ((name.StartsWith("get_") || name.StartsWith("set_")) &&
                        propertyNames.Contains(name.Substring(4)))
                        continue;
                    if ((name.StartsWith("add_") || name.StartsWith("remove_")) &&
                        eventNames.Contains(name.Substring(name.IndexOf('_') + 1)))
                        continue;
                }

                if (kindSet != null && !MatchesKindFilter(element, kindSet))
                    continue;

                var range = TreeNodeExtensions.GetDocumentRange(node);
                if (!range.IsValid()) continue;

                var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);

                // Deduplicate
                var key = $"{line}";
                if (!seen.Add(key)) continue;

                string containingType = null;
                if (element is IClrDeclaredElement clr)
                {
                    var ct = clr.GetContainingType();
                    if (ct != null)
                        containingType = ct.ShortName;
                }

                symbols.Add(new SymbolEntry
                {
                    Element = element,
                    ContainingType = containingType,
                    Line = line,
                    IsType = element is ITypeElement
                });
            }

            // Format compact output with indentation by containing type
            var sb = new StringBuilder();
            sb.Append(filePath).Append(" — ").Append(symbols.Count).AppendLine(" symbols");

            string lastContainingType = null;
            foreach (var sym in symbols)
            {
                if (sym.IsType)
                {
                    // Type declaration — top-level (no indent)
                    sb.AppendLine();
                    sb.Append(PsiHelpers.FormatSignature(sym.Element));
                    sb.Append(" :").AppendLine(sym.Line.ToString());
                    lastContainingType = sym.Element.ShortName;
                }
                else if (sym.ContainingType != null)
                {
                    // Member — indent under its containing type
                    if (sym.ContainingType != lastContainingType)
                    {
                        // New containing type we haven't seen as a declaration
                        sb.AppendLine();
                        sb.Append("(").Append(sym.ContainingType).AppendLine(")");
                        lastContainingType = sym.ContainingType;
                    }
                    sb.Append("  ").Append(PsiHelpers.FormatSignature(sym.Element));
                    sb.Append(" :").AppendLine(sym.Line.ToString());
                }
                else
                {
                    // Top-level non-type (rare: delegates, etc.)
                    sb.Append(PsiHelpers.FormatSignature(sym.Element));
                    sb.Append(" :").AppendLine(sym.Line.ToString());
                    lastContainingType = null;
                }
            }

            return sb.ToString().TrimEnd();
        }

        private class SymbolEntry
        {
            public IDeclaredElement Element;
            public string ContainingType;
            public int Line;
            public bool IsType;
        }

        private static bool MatchesKindFilter(IDeclaredElement element, HashSet<string> kindSet)
        {
            if (element is ITypeElement && kindSet.Contains("type")) return true;
            if (element is IMethod && kindSet.Contains("method")) return true;
            if (element is IProperty && kindSet.Contains("property")) return true;
            if (element is IField && kindSet.Contains("field")) return true;
            if (element is IEvent && kindSet.Contains("event")) return true;
            return false;
        }
    }
}
