using System.Collections.Generic;
using System.Linq;
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
            "Provide either a symbolName or a file path with position.";

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
                column = new { type = "integer", description = "1-based column number of the symbol" }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
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

            var result = new Dictionary<string, object>
            {
                ["name"] = declaredElement.ShortName,
                ["kind"] = declaredElement.GetElementType().PresentableName,
            };

            // Containing namespace/type
            if (declaredElement is IClrDeclaredElement clrElement)
            {
                var containingType = clrElement.GetContainingType();
                if (containingType != null)
                {
                    result["containingType"] = containingType.GetClrName().FullName;
                    var ns = containingType.GetContainingNamespace();
                    if (ns != null)
                        result["namespace"] = ns.QualifiedName;
                }

                if (declaredElement is ITypeElement typeElem)
                {
                    var ns = typeElem.GetContainingNamespace();
                    if (ns != null)
                        result["namespace"] = ns.QualifiedName;
                }
            }

            // Type information for typed members
            if (declaredElement is ITypeOwner typeOwner)
                result["type"] = typeOwner.Type.GetPresentableName(lang);

            // Method-specific info
            if (declaredElement is IParametersOwner parametersOwner)
            {
                var parameters = parametersOwner.Parameters.Select(p => new
                {
                    name = p.ShortName,
                    type = p.Type.GetPresentableName(lang),
                    isOptional = p.IsOptional
                }).ToList();

                result["parameters"] = parameters;

                if (declaredElement is IMethod method)
                {
                    result["returnType"] = method.ReturnType.GetPresentableName(lang);
                    result["isStatic"] = method.IsStatic;
                    result["isAbstract"] = method.IsAbstract;
                    result["isVirtual"] = method.IsVirtual;
                    result["isOverride"] = method.IsOverride;
                }
            }

            // Type-specific info
            if (declaredElement is ITypeElement typeElement)
            {
                if (typeElement is IModifiersOwner modOwner)
                    result["isAbstract"] = modOwner.IsAbstract;

                var superTypes = typeElement.GetSuperTypes()
                    .Select(t => t.GetPresentableName(lang))
                    .ToList();
                if (superTypes.Any())
                    result["baseTypes"] = superTypes;
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

                var members = new List<object>();
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

                    var memberInfo = new Dictionary<string, object>
                    {
                        ["name"] = member.ShortName,
                        ["kind"] = member.GetElementType().PresentableName
                    };

                    if (member is ITypeOwner memberTyped)
                        memberInfo["type"] = memberTyped.Type.GetPresentableName(lang);

                    if (member is IParametersOwner memberParams)
                    {
                        memberInfo["parameters"] = memberParams.Parameters.Select(p => new
                        {
                            name = p.ShortName,
                            type = p.Type.GetPresentableName(lang)
                        }).ToList();

                        if (member is IMethod memberMethod)
                            memberInfo["returnType"] = memberMethod.ReturnType.GetPresentableName(lang);
                    }

                    if (member is IModifiersOwner memberMod)
                    {
                        if (memberMod.IsStatic) memberInfo["isStatic"] = true;
                        if (memberMod.IsAbstract) memberInfo["isAbstract"] = true;
                    }

                    members.Add(memberInfo);
                }
                result["members"] = members;
            }

            // XML documentation
            var xmlDoc = declaredElement.GetXMLDoc(true);
            if (xmlDoc != null)
            {
                var summary = xmlDoc.SelectSingleNode("//summary")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(summary))
                    result["documentation"] = summary;

                var remarks = xmlDoc.SelectSingleNode("//remarks")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(remarks))
                    result["remarks"] = remarks;
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
                    result["declarationFile"] = decl.GetSourceFile()?.GetLocation().FullPath;
                    result["declarationLine"] = declLine;
                    result["declarationColumn"] = declCol;
                }
            }

            return result;
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
