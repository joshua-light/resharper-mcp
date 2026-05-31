using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Generates members on a type using ONLY low-level PSI tree mutation — no generator
    /// workflow framework at all. This is critical: the prior implementations drove
    /// ReSharper's generator workflow, which internally re-schedules work onto the R# main
    /// thread and waits for it. Because this is an <see cref="IMcpWriteTool"/>, the host
    /// (McpServerComponent) already dispatches Execute() onto the R# main thread under a write
    /// lock and an auto-commit PSI transaction — so the workflow's re-entrant main-thread wait
    /// dead-locked the host (Rider froze).
    ///
    /// This rewrite synthesizes member declarations with <see cref="CSharpElementFactory"/> and
    /// inserts them directly into the type's PSI tree via
    /// <c>IClassLikeDeclaration.AddClassMemberDeclaration</c>. That is purely synchronous tree
    /// mutation: no message pump, no main-thread re-entrancy, no workflow. The host's auto-commit
    /// cookie persists the edits.
    ///
    /// Kinds:
    ///   - 'constructor'       — FULLY implemented (low-level PSI).
    ///   - 'equality-members'  — FULLY implemented (low-level PSI).
    ///   - 'implement-interface' / 'override-members' — gracefully declined. Correctly mapping
    ///     arbitrary member signatures (generics, ref/out/params, accessor visibility, events,
    ///     indexers, substitution through the interface) with the raw factory is error-prone and
    ///     cannot be runtime-verified here; emitting subtly-wrong-but-compiling code is worse than
    ///     a clear "not supported" so these return a structured error instead.
    /// </summary>
    public class GenerateMembersTool : IMcpWriteTool
    {
        private readonly ISolution _solution;

        public GenerateMembersTool(ISolution solution) => _solution = solution;

        public string Name => "generate_members";

        public string Description =>
            "Generate members on a type (WRITE operation — modifies source files). " +
            "Resolve the target type via symbolName (e.g. 'MyClass', 'Namespace.MyClass') or filePath+line+column. " +
            "Choose 'kind': 'constructor' (generate a constructor initializing the type's fields/settable properties) or " +
            "'equality-members' (Equals(T)/Equals(object)/GetHashCode from the type's fields/properties). " +
            "('implement-interface' and 'override-members' are currently not supported and return an error.) " +
            "Optionally restrict to specific 'memberNames' (for 'constructor', the field/property names to initialize); " +
            "omit to use all applicable members. " +
            "Returns the names and locations of the generated members.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Target type name (e.g. 'MyClass', 'Namespace.MyClass'). Alternative to filePath+line+column." },
                kind = new
                {
                    type = "string",
                    description = "What to generate: 'constructor' or 'equality-members'. ('implement-interface' and 'override-members' are accepted but currently unsupported.)",
                    @enum = new[] { "implement-interface", "override-members", "constructor", "equality-members" }
                },
                memberNames = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Optional. For 'constructor'/'equality-members': short names of the fields/properties to include. Omit to use all applicable members."
                },
                filePath = new { type = "string", description = "Absolute path to the file containing the target type" },
                line = new { type = "integer", description = "1-based line number of the type" },
                column = new { type = "integer", description = "1-based column number of the type" }
            },
            required = new[] { "kind" }
        };

        public object Execute(JObject arguments)
        {
            var kindArg = arguments["kind"]?.ToString();
            if (string.IsNullOrEmpty(kindArg))
                return new { error = "Missing required argument 'kind'. Use one of: 'constructor', 'equality-members'." };

            var normalizedKind = kindArg.ToLowerInvariant();
            if (normalizedKind != "implement-interface" &&
                normalizedKind != "override-members" &&
                normalizedKind != "constructor" &&
                normalizedKind != "equality-members")
                return new { error = $"Unknown kind '{kindArg}'. Use one of: 'constructor', 'equality-members'." };

            // Resolve the target symbol (by name or by position).
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                // Force a 'type' kind filter for symbolName resolution so we land on the type.
                string.IsNullOrEmpty(arguments["symbolName"]?.ToString()) ? null : "type",
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            if (!(declaredElement is ITypeElement typeElement))
                return new { error = $"Target '{declaredElement.ShortName}' is not a type. generate_members requires a class/struct/record type." };

            var targetName = PsiHelpers.GetQualifiedName(typeElement);

            // Every failure is reported as a structured error so nothing can throw out of Execute()
            // and surface as an opaque JSON-RPC 500 — or worse, leave the write lock in a bad state.
            try
            {
                switch (normalizedKind)
                {
                    case "constructor":
                        return GenerateConstructor(typeElement, targetName, kindArg, arguments["memberNames"]);
                    case "equality-members":
                        return GenerateEqualityMembers(typeElement, targetName, kindArg, arguments["memberNames"]);
                    default:
                        // implement-interface / override-members: gracefully unsupported.
                        return new
                        {
                            error = $"'{kindArg}' is not yet supported. " +
                                    "generate_members currently supports 'constructor' and 'equality-members' " +
                                    "(implemented with low-level PSI). Signature mapping for interface/override " +
                                    "members is not implemented to avoid generating incorrect code.",
                            target = targetName,
                            kind = kindArg
                        };
                }
            }
            catch (Exception ex)
            {
                return new
                {
                    error = $"generate_members failed: {ex.Message}",
                    target = targetName,
                    kind = kindArg
                };
            }
        }

        // ---------------------------------------------------------------------------------------
        // Anchor resolution
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Finds the type's editable class-like source declaration to mutate, or returns a
        /// structured error if there is none (compiled/library type, partial-only-elsewhere, etc.).
        /// </summary>
        private static (IClassLikeDeclaration decl, object error) ResolveClassDeclaration(
            ITypeElement typeElement, string targetName, string kindArg)
        {
            var declarations = typeElement.GetDeclarations();
            if (declarations.Count == 0)
                return (null, new
                {
                    error = $"Type '{typeElement.ShortName}' has no source declaration to generate into (likely a compiled/library type).",
                    target = targetName,
                    kind = kindArg
                });

            foreach (var d in declarations)
            {
                if (d.GetSourceFile() == null) continue;
                if (d is IClassLikeDeclaration classLike)
                    return (classLike, null);
            }

            return (null, new
            {
                error = $"Type '{typeElement.ShortName}' has no editable class/struct/record declaration to generate into.",
                target = targetName,
                kind = kindArg
            });
        }

        // ---------------------------------------------------------------------------------------
        // Member collection — plain PSI queries, NO generator framework
        // ---------------------------------------------------------------------------------------

        private struct DataMember
        {
            public string Name;     // member short name (used for field assignment / equality)
            public IType Type;      // declared type (passed to the factory as a $N substitution arg)
        }

        /// <summary>
        /// Collects the type's settable data members: instance fields (incl. readonly, which a
        /// constructor may assign) and instance properties that have a setter. Excludes static,
        /// const, and (best-effort) compiler-generated backing fields.
        /// </summary>
        private static List<DataMember> CollectDataMembers(ITypeElement typeElement, HashSet<string> filter)
        {
            var result = new List<DataMember>();
            var seen = new HashSet<string>();

            foreach (var field in typeElement.Fields)
            {
                if (field.IsStatic || field.IsConstant) continue;
                var name = field.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                // Skip compiler-generated backing fields (e.g. "<Prop>k__BackingField").
                if (name.IndexOf('<') >= 0 || name.IndexOf('$') >= 0) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = field.Type });
            }

            foreach (var property in typeElement.Properties)
            {
                if (property.IsStatic) continue;
                if (!property.IsWritable) continue;          // needs a setter to initialize
                if (property.Parameters.Count > 0) continue; // skip indexers
                var name = property.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = property.Type });
            }

            return result;
        }

        // ---------------------------------------------------------------------------------------
        // constructor
        // ---------------------------------------------------------------------------------------

        private object GenerateConstructor(
            ITypeElement typeElement, string targetName, string kindArg, JToken memberNamesToken)
        {
            var (classDecl, declError) = ResolveClassDeclaration(typeElement, targetName, kindArg);
            if (declError != null) return declError;

            var filter = ParseMemberNames(memberNamesToken);
            var members = CollectDataMembers(typeElement, filter);

            if (members.Count == 0)
            {
                return new
                {
                    error = filter != null
                        ? $"None of the requested member(s) are assignable fields/properties on '{typeElement.ShortName}'."
                        : $"'{typeElement.ShortName}' has no instance fields or settable properties to initialize in a constructor.",
                    target = targetName,
                    kind = kindArg
                };
            }

            var factory = CSharpElementFactory.GetInstance(classDecl);

            // Build "public TypeName($0 p0, $1 p1, ...) { this.f0 = p0; ... }".
            // Names/identifiers are baked directly into the format string (they are plain C#
            // identifiers); only the parameter TYPES go through $N substitution so the factory
            // renders fully-qualified, correct type syntax for us.
            var args = new List<object>();
            var parms = new List<string>();
            var body = new StringBuilder();
            for (var i = 0; i < members.Count; i++)
            {
                var paramName = ToParameterName(members[i].Name, i);
                parms.Add("$" + i + " " + paramName);
                body.Append("this.").Append(members[i].Name).Append(" = ").Append(paramName).Append(";\n");
                args.Add(members[i].Type);
            }

            var ctorName = typeElement.ShortName;
            var text = "public " + ctorName + "(" + string.Join(", ", parms) + ") {\n" + body + "}";

            var member = factory.CreateTypeMemberDeclaration(text, args.ToArray());
            var inserted = classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member);

            return ShapeResult(targetName, kindArg, classDecl, new[] { (IClassMemberDeclaration)inserted });
        }

        // ---------------------------------------------------------------------------------------
        // equality-members
        // ---------------------------------------------------------------------------------------

        private object GenerateEqualityMembers(
            ITypeElement typeElement, string targetName, string kindArg, JToken memberNamesToken)
        {
            var (classDecl, declError) = ResolveClassDeclaration(typeElement, targetName, kindArg);
            if (declError != null) return declError;

            var filter = ParseMemberNames(memberNamesToken);
            var members = CollectDataMembers(typeElement, filter);
            // For equality we also want readonly/get-only properties, but to stay simple and
            // correct we reuse the settable-member set; if it is empty, fall back to all readable
            // instance fields/properties so Equals/GetHashCode still have something to compare.
            if (members.Count == 0)
                members = CollectReadableMembers(typeElement, filter);

            var factory = CSharpElementFactory.GetInstance(classDecl);
            var selfType = TypeFactory.CreateType(typeElement); // $0 in the templates below
            var typeName = typeElement.ShortName;

            var inserted = new List<IClassMemberDeclaration>();

            // bool Equals(TypeName other) — strongly-typed equality.
            {
                var sb = new StringBuilder();
                sb.Append("public bool Equals($0 other) {\n");
                if (members.Count == 0)
                {
                    sb.Append("return !ReferenceEquals(null, other);\n");
                }
                else
                {
                    sb.Append("if (ReferenceEquals(null, other)) return false;\n");
                    sb.Append("if (ReferenceEquals(this, other)) return true;\n");
                    sb.Append("return ");
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (i > 0) sb.Append(" && ");
                        var n = members[i].Name;
                        sb.Append("System.Collections.Generic.EqualityComparer<$")
                          .Append(i + 1)
                          .Append(">.Default.Equals(this.").Append(n).Append(", other.").Append(n).Append(")");
                    }
                    sb.Append(";\n");
                }
                sb.Append("}");

                var args = new List<object> { selfType };
                foreach (var m in members) args.Add(m.Type);

                var member = factory.CreateTypeMemberDeclaration(sb.ToString(), args.ToArray());
                inserted.Add((IClassMemberDeclaration)classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member));
            }

            // bool Equals(object obj) — delegates to the typed overload.
            {
                var member = factory.CreateTypeMemberDeclaration(
                    "public override bool Equals(object obj) {\n" +
                    "if (ReferenceEquals(null, obj)) return false;\n" +
                    "if (ReferenceEquals(this, obj)) return true;\n" +
                    "return obj is $0 other && Equals(other);\n" +
                    "}",
                    selfType);
                inserted.Add((IClassMemberDeclaration)classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member));
            }

            // int GetHashCode() — combines the members' hash codes.
            {
                var sb = new StringBuilder();
                sb.Append("public override int GetHashCode() {\n");
                if (members.Count == 0)
                {
                    sb.Append("return 0;\n");
                }
                else
                {
                    sb.Append("unchecked {\n");
                    sb.Append("int hashCode = 17;\n");
                    foreach (var m in members)
                    {
                        // null-safe: value types box harmlessly; reference types guard against null.
                        sb.Append("hashCode = (hashCode * 397) ^ (this.").Append(m.Name)
                          .Append(" != null ? this.").Append(m.Name).Append(".GetHashCode() : 0);\n");
                    }
                    sb.Append("return hashCode;\n");
                    sb.Append("}\n");
                }
                sb.Append("}");

                var member = factory.CreateTypeMemberDeclaration(sb.ToString());
                inserted.Add((IClassMemberDeclaration)classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member));
            }

            return ShapeResult(targetName, kindArg, classDecl, inserted);
        }

        /// <summary>
        /// Readable instance members (fields + readable properties) for equality fallback when
        /// the type has no settable members. Excludes static/const/backing fields and indexers.
        /// </summary>
        private static List<DataMember> CollectReadableMembers(ITypeElement typeElement, HashSet<string> filter)
        {
            var result = new List<DataMember>();
            var seen = new HashSet<string>();

            foreach (var field in typeElement.Fields)
            {
                if (field.IsStatic || field.IsConstant) continue;
                var name = field.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf('<') >= 0 || name.IndexOf('$') >= 0) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = field.Type });
            }

            foreach (var property in typeElement.Properties)
            {
                if (property.IsStatic) continue;
                if (!property.IsReadable) continue;
                if (property.Parameters.Count > 0) continue;
                var name = property.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = property.Type });
            }

            return result;
        }

        // ---------------------------------------------------------------------------------------
        // Output shaping
        // ---------------------------------------------------------------------------------------

        private object ShapeResult(
            string targetName, string kindArg,
            IClassLikeDeclaration classDecl, IReadOnlyList<IClassMemberDeclaration> inserted)
        {
            var sourceFile = classDecl.GetSourceFile();
            var filePath = sourceFile?.GetLocation().FullPath;

            var generated = new List<object>();
            foreach (var memberDecl in inserted)
            {
                if (memberDecl == null) continue;

                var declaredEl = memberDecl.DeclaredElement;
                var entry = new Dictionary<string, object>
                {
                    ["name"] = declaredEl?.ShortName ?? "(member)",
                    ["kind"] = declaredEl?.GetElementType()?.PresentableName ?? kindArg
                };

                var range = TreeNodeExtensions.GetDocumentRange(memberDecl);
                if (range.IsValid())
                {
                    var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                    entry["line"] = line;
                    entry["column"] = col;
                }

                generated.Add(entry);
            }

            return new
            {
                target = targetName,
                kind = kindArg,
                generatedCount = generated.Count,
                file = filePath,
                members = generated
            };
        }

        // ---------------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Derives a camelCase parameter name from a member name (strips a leading underscore /
        /// "m_" prefix, lowercases the first letter). Falls back to "p{index}" if it can't.
        /// </summary>
        private static string ToParameterName(string memberName, int index)
        {
            if (string.IsNullOrEmpty(memberName)) return "p" + index;

            var name = memberName;
            if (name.StartsWith("m_") && name.Length > 2) name = name.Substring(2);
            name = name.TrimStart('_');
            if (name.Length == 0) return "p" + index;

            var first = char.ToLowerInvariant(name[0]);
            var candidate = first + name.Substring(1);

            // Avoid C# keyword collisions for the most common cases by prefixing '@'.
            if (IsCSharpKeyword(candidate)) candidate = "@" + candidate;
            return candidate;
        }

        private static bool IsCSharpKeyword(string s)
        {
            switch (s)
            {
                case "value": case "object": case "string": case "int": case "bool":
                case "byte": case "char": case "double": case "float": case "long":
                case "short": case "decimal": case "void": case "class": case "struct":
                case "this": case "base": case "null": case "true": case "false":
                case "new": case "return": case "params": case "ref": case "out":
                case "in": case "event": case "delegate": case "namespace": case "using":
                    return true;
                default:
                    return false;
            }
        }

        private static HashSet<string> ParseMemberNames(JToken token)
        {
            if (token == null || token.Type != JTokenType.Array)
                return null;

            var names = new HashSet<string>();
            foreach (var t in token)
            {
                var s = t?.ToString();
                if (!string.IsNullOrEmpty(s))
                    names.Add(s);
            }

            return names.Count > 0 ? names : null;
        }
    }
}
