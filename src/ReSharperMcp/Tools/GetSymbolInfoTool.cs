using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    public class GetSymbolInfoTool : IMcpTool
    {
        private readonly ISolution _solution;

        public GetSymbolInfoTool(ISolution solution) => _solution = solution;

        public string Name => "get_symbol_info";

        public string Description =>
            "Get detailed information about a code symbol: " +
            "kind, full qualified name, type, documentation, containing type/namespace, and parameter info for methods. " +
            "Provide either a symbolName or a file path with position. " +
            "Pass multiple symbols via the 'symbols' array to query several in one call.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to get info about (e.g. 'MyClass', 'Namespace.MyClass'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                includeMembers = new { type = "boolean", description = "If true, include the type's members (methods, properties, fields, events) in the response. Only applies to type symbols. Default: false." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" },
                symbols = new
                {
                    type = "array",
                    description = "Array of symbols to get info for in batch. Each item is an object with symbolName/kind or filePath/line/column. Results are concatenated with separators.",
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
                    CopyIfPresent(arguments, itemArgs, "includeMembers");

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
            var (declaredElement, resolveError) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (resolveError != null) return resolveError;

            var includeMembers = arguments["includeMembers"]?.Value<bool>() ?? false;
            var lang = declaredElement.PresentationLanguage ?? CSharpLanguage.Instance;
            var sb = new StringBuilder();

            // Header: compact signature
            sb.AppendLine(PsiHelpers.FormatSignature(declaredElement));

            // Containing type / namespace
            if (declaredElement is IClrDeclaredElement clrElement)
            {
                var containingType = clrElement.GetContainingType();
                if (containingType != null)
                    sb.Append("containingType: ").AppendLine(containingType.GetClrName().FullName);

                if (declaredElement is ITypeElement typeElem)
                {
                    var ns = typeElem.GetContainingNamespace();
                    if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                        sb.Append("namespace: ").AppendLine(ns.QualifiedName);
                }
                else if (containingType != null)
                {
                    var ns = containingType.GetContainingNamespace();
                    if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                        sb.Append("namespace: ").AppendLine(ns.QualifiedName);
                }
            }

            // Base types for type elements
            if (declaredElement is ITypeElement typeElement)
            {
                var superTypes = typeElement.GetSuperTypes()
                    .Select(t => t.GetPresentableName(lang))
                    .ToList();
                if (superTypes.Any())
                    sb.Append("baseTypes: ").AppendLine(string.Join(", ", superTypes));
            }

            // Declaration location
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var range = TreeNodeExtensions.GetDocumentRange(decl);
                if (range.IsValid())
                {
                    var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                    var file = decl.GetSourceFile()?.GetLocation().FullPath;
                    if (file != null)
                        sb.Append("declared: ").Append(file).Append(':').Append(declLine).Append(':').AppendLine(declCol.ToString());
                }
            }

            // XML documentation
            var xmlDoc = declaredElement.GetXMLDoc(true);
            if (xmlDoc != null)
            {
                var summary = xmlDoc.SelectSingleNode("//summary")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(summary))
                    sb.Append("doc: ").AppendLine(summary);

                var remarks = xmlDoc.SelectSingleNode("//remarks")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(remarks))
                    sb.Append("remarks: ").AppendLine(remarks);
            }

            // Members (when requested for type symbols)
            if (includeMembers && declaredElement is ITypeElement membersType)
            {
                // Collect property/event names to filter out compiler-generated accessors
                var propertyNames = new HashSet<string>();
                var eventNames = new HashSet<string>();
                foreach (var m in membersType.GetMembers())
                {
                    if (m is IProperty prop) propertyNames.Add(prop.ShortName);
                    if (m is IEvent evt) eventNames.Add(evt.ShortName);
                }

                sb.AppendLine();
                sb.AppendLine("members:");

                foreach (var member in membersType.GetMembers())
                {
                    // Skip compiler-generated accessors (get_X/set_X for properties, add_X/remove_X for events)
                    if (member is IMethod accessorMethod)
                    {
                        var name = accessorMethod.ShortName;
                        if ((name.StartsWith("get_") || name.StartsWith("set_")) &&
                            propertyNames.Contains(name.Substring(4)))
                            continue;
                        if ((name.StartsWith("add_") || name.StartsWith("remove_")) &&
                            eventNames.Contains(name.Substring(name.IndexOf('_') + 1)))
                            continue;
                    }

                    // Skip compiler-generated record members
                    if (IsCompilerGeneratedMember(member, membersType))
                        continue;

                    sb.Append("  ").AppendLine(PsiHelpers.FormatSignature(member));
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
        /// Detects compiler-generated members typical of records: Equals, GetHashCode, ToString,
        /// PrintMembers, Deconstruct, op_Equality, op_Inequality, EqualityContract, and clone methods.
        /// </summary>
        private static bool IsCompilerGeneratedMember(ITypeMember member, ITypeElement containingType)
        {
            var name = member.ShortName;

            // Skip any $-prefixed members (compiler internals)
            if (name.StartsWith("$") || name.StartsWith("<"))
                return member.GetDeclarations().Count == 0;

            // Equality operators are always compiler-generated on records
            // (these may be IOperator, not IMethod, so check before the IMethod block)
            if (name == "op_Equality" || name == "op_Inequality")
                return true;

            // Parameterless constructor on a record struct (always compiler-generated)
            if (name == ".ctor" && member is IParametersOwner ctor && ctor.Parameters.Count == 0)
                return true;

            if (member is IMethod)
            {
                switch (name)
                {
                    case "Equals":
                    case "GetHashCode":
                    case "ToString":
                    case "PrintMembers":
                    case "Deconstruct":
                        return member.GetDeclarations().Count == 0;
                }
            }

            // EqualityContract property (record-generated)
            if (member is IProperty && name == "EqualityContract")
                return member.GetDeclarations().Count == 0;

            return false;
        }
    }
}
