using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Impl;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class FixUsingsTool : IMcpWriteTool
    {
        private readonly ISolution _solution;

        public FixUsingsTool(ISolution solution) => _solution = solution;

        public string Name => "fix_usings";

        public string Description =>
            "Fix missing using directives in a C# file. " +
            "Finds unresolved type references, searches the solution and referenced assemblies for matching types, " +
            "and adds using directives for unambiguous matches (exactly one candidate namespace). " +
            "Reports ambiguous matches with their candidate namespaces. " +
            "To resolve ambiguous types, call again with the 'resolutions' parameter specifying which namespace to use for each type. " +
            "Pass multiple files via the 'filePaths' array to fix usings in several files in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new
                {
                    type = "string",
                    description = "Absolute path to the C# file to fix usings for"
                },
                filePaths = new
                {
                    type = "array",
                    description = "Array of absolute file paths to fix usings for in batch. Results are concatenated with separators. Alternative to single 'filePath' parameter.",
                    items = new { type = "string" }
                },
                resolutions = new
                {
                    type = "object",
                    description = "Optional map of type name to namespace for resolving ambiguous imports. " +
                                  "Example: {\"ILogger\": \"Microsoft.Extensions.Logging\", \"Position\": \"UnityEngine\"}",
                    additionalProperties = new { type = "string" }
                }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var filePathsToken = arguments["filePaths"] as JArray;
            if (filePathsToken != null && filePathsToken.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < filePathsToken.Count; i++)
                {
                    if (i > 0) sb.AppendLine().AppendLine();
                    var itemArgs = new JObject();
                    itemArgs["filePath"] = filePathsToken[i]?.ToString();
                    CopyIfPresent(arguments, itemArgs, "resolutions");

                    sb.Append("=== [").Append(i + 1).Append('/').Append(filePathsToken.Count)
                      .Append("] ").Append(filePathsToken[i]).Append(" ===").AppendLine();
                    sb.Append(ResultToString(ExecuteSingle(itemArgs)));
                }
                return sb.ToString().TrimEnd();
            }

            return ExecuteSingle(arguments);
        }

        private object ExecuteSingle(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return new { error = "filePath is required" };

            // Parse explicit resolutions for ambiguous types
            var resolutions = new Dictionary<string, string>();
            var resolutionsToken = arguments["resolutions"] as JObject;
            if (resolutionsToken != null)
            {
                foreach (var prop in resolutionsToken.Properties())
                {
                    var val = prop.Value?.ToString();
                    if (!string.IsNullOrEmpty(val))
                        resolutions[prop.Name] = val;
                }
            }

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return new { error = resolved.Error };
            var sourceFile = resolved.SourceFile;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return new { error = "Could not get PSI tree for file" };

            var csharpFile = psiFile as ICSharpFile;
            if (csharpFile == null)
                return new { error = "fix_usings only supports C# files" };

            // 1. Collect existing using namespaces to avoid duplicates
            var existingNamespaces = new HashSet<string>();
            foreach (var import in csharpFile.ImportsEnumerable)
            {
                var nsDir = import as IUsingSymbolDirective;
                if (nsDir?.ImportedSymbolName != null)
                {
                    var ns = nsDir.ImportedSymbolName.QualifiedName;
                    if (!string.IsNullOrEmpty(ns))
                        existingNamespaces.Add(ns);
                }
            }

            // 2. Walk PSI tree to find unresolved type references.
            // Only consider IReferenceName nodes (type positions like declarations,
            // base types, generic args, attributes), NOT IReferenceExpression nodes
            // (method calls, property access) — those aren't fixable with usings.
            var unresolvedByName = new Dictionary<string, List<string>>();
            foreach (var node in csharpFile.Descendants())
            {
                if (!(node is IReferenceName))
                    continue;

                foreach (var reference in node.GetReferences())
                {
                    var resolveResult = reference.Resolve();
                    if (resolveResult.ResolveErrorType == ResolveErrorType.OK ||
                        resolveResult.ResolveErrorType == ResolveErrorType.IGNORABLE)
                        continue;

                    var name = reference.GetName();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (!unresolvedByName.ContainsKey(name))
                        unresolvedByName[name] = new List<string>();

                    var range = TreeNodeExtensions.GetDocumentRange(node);
                    if (range.IsValid())
                    {
                        var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                        var loc = $"{line}:{col}";
                        if (!unresolvedByName[name].Contains(loc))
                            unresolvedByName[name].Add(loc);
                    }
                }
            }

            if (unresolvedByName.Count == 0)
                return $"{filePath} — no unresolved type references found";

            // 3. Search symbol cache for candidates (FULL scope includes framework/NuGet types)
            var psiServices = _solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true);

            // namespace -> list of type names that triggered it
            var namespacesToAdd = new Dictionary<string, List<string>>();
            var ambiguous = new List<(string typeName, List<string> namespaces)>();
            var unresolved = new List<string>();
            var invalidResolutions = new List<(string typeName, string requestedNs)>();

            foreach (var kvp in unresolvedByName)
            {
                var typeName = kvp.Key;

                var candidateNamespaces = new HashSet<string>();
                foreach (var element in symbolScope.GetElementsByShortName(typeName))
                {
                    var typeElement = element as ITypeElement;
                    if (typeElement == null) continue;

                    var ns = typeElement.GetContainingNamespace();
                    if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                        candidateNamespaces.Add(ns.QualifiedName);
                }

                // Remove namespaces that are already imported
                candidateNamespaces.ExceptWith(existingNamespaces);

                if (candidateNamespaces.Count == 0)
                {
                    // Check if there were candidates at all (all already imported = different problem)
                    var anyCandidates = symbolScope.GetElementsByShortName(typeName)
                        .Any(e => e is ITypeElement);
                    if (!anyCandidates)
                        unresolved.Add(typeName);
                    // else: namespace imported but still unresolved — skip silently
                }
                else if (candidateNamespaces.Count == 1)
                {
                    // Unambiguous — auto-fix
                    var ns = candidateNamespaces.First();
                    if (!namespacesToAdd.ContainsKey(ns))
                        namespacesToAdd[ns] = new List<string>();
                    namespacesToAdd[ns].Add(typeName);
                }
                else if (resolutions.TryGetValue(typeName, out var chosenNs))
                {
                    // Caller provided an explicit resolution for this ambiguous type
                    if (candidateNamespaces.Contains(chosenNs))
                    {
                        if (!namespacesToAdd.ContainsKey(chosenNs))
                            namespacesToAdd[chosenNs] = new List<string>();
                        namespacesToAdd[chosenNs].Add(typeName);
                    }
                    else
                    {
                        invalidResolutions.Add((typeName, chosenNs));
                    }
                }
                else
                {
                    // Ambiguous, no resolution provided — report candidates
                    ambiguous.Add((typeName, candidateNamespaces.OrderBy(n => n).ToList()));
                }
            }

            // 4. Add using directives via CSharpElementFactory + UsingUtil
            var added = new List<(string ns, List<string> types)>();
            if (namespacesToAdd.Count > 0)
            {
                var factory = CSharpElementFactory.GetInstance(csharpFile);
                foreach (var ns in namespacesToAdd.Keys.OrderBy(n => n))
                {
                    var directive = factory.CreateUsingDirective(ns);
                    UsingUtil.AddImportTo(csharpFile, directive);
                    existingNamespaces.Add(ns);
                    added.Add((ns, namespacesToAdd[ns]));
                }
            }

            // 5. Format compact output
            var sb = new StringBuilder();
            sb.Append(filePath).AppendLine(" — fix_usings results");

            if (added.Count > 0)
            {
                sb.AppendLine();
                sb.Append("added ").Append(added.Count).AppendLine(" usings:");
                foreach (var (ns, types) in added)
                    sb.Append("  using ").Append(ns).Append("; (for: ")
                      .Append(string.Join(", ", types)).AppendLine(")");
            }

            if (ambiguous.Count > 0)
            {
                sb.AppendLine();
                sb.Append("ambiguous ").Append(ambiguous.Count)
                  .AppendLine(" references (call again with 'resolutions' to fix):");
                foreach (var (typeName, namespaces) in ambiguous)
                    sb.Append("  ").Append(typeName).Append(" — candidates: ")
                      .AppendLine(string.Join(", ", namespaces));
            }

            if (invalidResolutions.Count > 0)
            {
                sb.AppendLine();
                sb.Append("invalid ").Append(invalidResolutions.Count).AppendLine(" resolutions:");
                foreach (var (typeName, requestedNs) in invalidResolutions)
                    sb.Append("  ").Append(typeName).Append(" — '").Append(requestedNs)
                      .AppendLine("' is not a valid candidate");
            }

            if (unresolved.Count > 0)
            {
                sb.AppendLine();
                sb.Append("unresolved ").Append(unresolved.Count).AppendLine(" references:");
                foreach (var name in unresolved)
                    sb.Append("  ").Append(name).AppendLine(" — no matching type found");
            }

            if (added.Count == 0 && ambiguous.Count == 0 && unresolved.Count == 0 && invalidResolutions.Count == 0)
                sb.AppendLine("\nno fixable unresolved type references found");

            return sb.ToString().TrimEnd();
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
    }
}
