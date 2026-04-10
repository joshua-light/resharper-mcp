using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class FindUsagesTool : IMcpTool
    {
        private readonly ISolution _solution;

        public FindUsagesTool(ISolution solution) => _solution = solution;

        public string Name => "find_usages";

        public string Description =>
            "Find all usages/references of a code symbol (class, method, property, variable, etc.) " +
            "in the current solution. Provide either a symbolName (e.g. 'MyClass' or 'Namespace.MyClass') " +
            "or a file path with position (line/column). " +
            "Results are grouped by project and file for easy navigation. " +
            "Pass multiple symbols via the 'symbols' array to search for several in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to find usages of (e.g. 'MyClass', 'Namespace.MyClass', 'MyClass.MyMethod'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                excludeDeclarationFile = new { type = "boolean", description = "Exclude usages from the file(s) where the symbol is declared. Useful for finding consumers rather than definition-site references. Default: false." },
                maxResults = new { type = "integer", description = "Maximum number of usages to return. Default: unlimited. Use to cap results on widely-used symbols." },
                symbols = new
                {
                    type = "array",
                    description = "Array of symbols to find usages for in batch. Each item is an object with symbolName/kind or filePath/line/column. Results are concatenated with separators.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Symbol name" },
                            kind = new { type = "string", description = "Symbol kind filter" },
                            filePath = new { type = "string", description = "File path" },
                            line = new { type = "integer", description = "1-based line" },
                            column = new { type = "integer", description = "1-based column" }
                        }
                    }
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var symbolsToken = arguments["symbols"] as JArray;
            if (symbolsToken != null && symbolsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < symbolsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = CloneObject(symbolsToken[i] as JObject);
                    CopyIfPresent(arguments, itemArgs, "excludeDeclarationFile");
                    CopyIfPresent(arguments, itemArgs, "maxResults");

                    var label = itemArgs["symbolName"]?.ToString() ??
                                $"{itemArgs["filePath"]}:{itemArgs["line"]}:{itemArgs["column"]}";
                    sb.Append("=== [").Append(i + 1).Append('/').Append(symbolsToken.Count)
                      .Append("] ").Append(label).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            var excludeDeclFile = arguments["excludeDeclarationFile"]?.Value<bool>() ?? false;
            var maxResults = arguments["maxResults"]?.Value<int>() ?? 0;
            if (maxResults < 0) maxResults = 0;
            var hitLimit = false;

            // Collect declaration file paths for exclusion
            var declFilePaths = new HashSet<string>();
            if (excludeDeclFile)
            {
                foreach (var decl in declaredElement.GetDeclarations())
                {
                    var sf = decl.GetSourceFile();
                    if (sf != null)
                        declFilePaths.Add(sf.GetLocation().FullPath);
                }
            }

            // Collect raw usages
            var rawUsages = new List<RawUsage>();
            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            // Shared consumer logic for all FindReferences calls
            FindExecution HandleResult(FindResult findResult)
            {
                if (maxResults > 0 && rawUsages.Count >= maxResults)
                {
                    hitLimit = true;
                    return FindExecution.Stop;
                }
                if (findResult is FindResultReference reference)
                {
                    var refNode = reference.Reference.GetTreeNode();
                    var refSourceFile = refNode.GetSourceFile();
                    if (refSourceFile != null)
                    {
                        var filePath = refSourceFile.GetLocation().FullPath;
                        if (string.IsNullOrEmpty(filePath))
                            return FindExecution.Continue;

                        if (excludeDeclFile && declFilePaths.Contains(filePath))
                            return FindExecution.Continue;

                        var refRange = TreeNodeExtensions.GetDocumentRange(refNode);
                        if (refRange.IsValid())
                        {
                            var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                            var projectName = refSourceFile.GetProject()?.Name;
                            rawUsages.Add(new RawUsage
                            {
                                Project = projectName ?? "(unknown)",
                                File = filePath,
                                Line = refLine,
                                Column = refCol,
                                Text = PsiHelpers.TruncateSnippet(
                                    refNode.Parent?.GetText() ?? refNode.GetText())
                            });
                        }
                    }
                }
                return FindExecution.Continue;
            }

            // Find direct usages
            psiServices.Finder.FindReferences(
                declaredElement,
                searchDomain,
                new FindResultConsumer(HandleResult),
                NullProgressIndicator.Create());

            // Also search for usages of interface/base methods this element implements
            var superMembers = FindInterfaceMembers(declaredElement);
            foreach (var superMember in superMembers)
            {
                if (hitLimit) break;
                psiServices.Finder.FindReferences(
                    superMember,
                    searchDomain,
                    new FindResultConsumer(HandleResult),
                    NullProgressIndicator.Create());
            }

            // Deduplicate: keep only one usage per line per file
            var deduped = rawUsages
                .GroupBy(u => $"{u.File}:{u.Line}")
                .Select(g => g.First())
                .ToList();

            // Format compact output
            var sb = new StringBuilder();
            var fileCount = deduped.Select(u => u.File).Distinct().Count();
            var projectCount = deduped.Select(u => u.Project).Distinct().Count();

            sb.Append(declaredElement.GetElementType().PresentableName).Append(' ');
            sb.Append(declaredElement.ShortName);
            sb.Append(" — ").Append(deduped.Count);
            if (hitLimit) sb.Append('+');
            sb.Append(" usages in ");
            sb.Append(fileCount).Append(" files, ");
            sb.Append(projectCount).AppendLine(" projects");
            if (hitLimit)
                sb.AppendLine("(limit reached; increase maxResults to see more)");

            if (superMembers.Count > 0)
            {
                sb.Append("(includes usages via: ");
                sb.Append(string.Join(", ", superMembers.Select(m => PsiHelpers.GetQualifiedName(m))));
                sb.AppendLine(")");
            }

            // When the element is an implementation/override and we found few or no direct usages,
            // suggest searching the interface/base member directly for better results
            if (deduped.Count == 0 && superMembers.Count == 0)
            {
                var implNote = GetImplementationNote(declaredElement);
                if (implNote != null)
                    sb.AppendLine(implNote);
            }

            // Declaration location
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var declRange = TreeNodeExtensions.GetDocumentRange(decl);
                if (declRange.IsValid())
                {
                    var declSourceFile = decl.GetSourceFile();
                    if (declSourceFile != null)
                    {
                        var declPath = declSourceFile.GetLocation().FullPath;
                        if (string.IsNullOrEmpty(declPath))
                            declPath = "[no source]";
                        var (declLine, declCol) = PsiHelpers.GetLineColumn(declRange.StartOffset);
                        sb.Append("declared: ").Append(declPath)
                          .Append(':').Append(declLine).Append(':').AppendLine(declCol.ToString());
                    }
                }
            }

            // Group by project → file
            var grouped = deduped
                .GroupBy(u => u.Project)
                .OrderByDescending(g => g.Count());

            foreach (var projectGroup in grouped)
            {
                sb.AppendLine();
                sb.Append("--- ").Append(projectGroup.Key).Append(" (")
                  .Append(projectGroup.Count()).AppendLine(" usages) ---");

                foreach (var fileGroup in projectGroup.GroupBy(u => u.File).OrderByDescending(g => g.Count()))
                {
                    sb.AppendLine(Path.GetFileName(fileGroup.Key));
                    foreach (var u in fileGroup)
                    {
                        sb.Append("  :").Append(u.Line).Append(':').Append(u.Column)
                          .Append(" — ").AppendLine(u.Text);
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static JObject CloneObject(JObject source)
        {
            if (source == null) return new JObject();
            return (JObject)source.DeepClone();
        }

        private static void CopyIfPresent(JObject source, JObject target, string key)
        {
            var token = source[key];
            if (token != null) target[key] = token;
        }

        private static string ResultToString(object result)
        {
            if (result is string s) return s;
            var jo = JObject.FromObject(result);
            return "error: " + (jo["error"]?.ToString() ?? result.ToString());
        }

        /// <summary>
        /// Finds interface methods that the given element implements.
        /// Walks the full type hierarchy to find all transitive interface members.
        /// </summary>
        private static List<IDeclaredElement> FindInterfaceMembers(IDeclaredElement element)
        {
            var result = new List<IDeclaredElement>();
            if (!(element is IClrDeclaredElement clrElem)) return result;

            var containingType = clrElem.GetContainingType();
            if (containingType == null) return result;

            var memberName = element.ShortName;
            var paramCount = (element as IParametersOwner)?.Parameters.Count ?? -1;
            var visited = new HashSet<string>();

            CollectInterfaceMembers(containingType, memberName, paramCount, element, result, visited);
            return result;
        }

        private static void CollectInterfaceMembers(ITypeElement type, string memberName, int paramCount,
            IDeclaredElement originalMember, List<IDeclaredElement> result, HashSet<string> visited)
        {
            foreach (var superType in type.GetSuperTypes())
            {
                var superElement = superType.GetTypeElement();
                if (superElement == null) continue;

                var fqn = superElement.GetClrName().FullName;
                if (!visited.Add(fqn)) continue;

                // Only collect members from interfaces
                if (superElement is IInterface)
                {
                    foreach (var m in superElement.GetMembers())
                    {
                        if (m.ShortName != memberName) continue;
                        if (m.Equals(originalMember)) continue;
                        if (paramCount >= 0 && m is IParametersOwner po && po.Parameters.Count != paramCount)
                            continue;
                        result.Add(m);
                    }
                }

                // Recurse into all super types to find transitive interfaces
                CollectInterfaceMembers(superElement, memberName, paramCount, originalMember, result, visited);
            }
        }

        /// <summary>
        /// If the element is an implementation/override of an interface or base class member,
        /// returns a note suggesting the user search the base member instead.
        /// </summary>
        private static string GetImplementationNote(IDeclaredElement element)
        {
            if (!(element is IOverridableMember overridable)) return null;

            var superMembers = overridable.GetImmediateSuperMembers();
            if (superMembers == null) return null;

            var notes = new List<string>();
            foreach (var superMemberInstance in superMembers)
            {
                var superMember = superMemberInstance.Member;
                if (superMember == null) continue;
                notes.Add(PsiHelpers.GetQualifiedName(superMember));
            }

            if (notes.Count == 0) return null;

            return "Note: This is an implementation/override of " +
                   string.Join(", ", notes) +
                   ". Use find_usages on the interface/base member to find call sites.";
        }

        private class RawUsage
        {
            public string Project;
            public string File;
            public int Line;
            public int Column;
            public string Text;
        }
    }
}
