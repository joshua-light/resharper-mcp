using System.Collections.Generic;
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
    public class FindImplementationsTool : IMcpTool
    {
        private readonly ISolution _solution;

        public FindImplementationsTool(ISolution solution) => _solution = solution;

        public string Name => "find_implementations";

        public string Description =>
            "Find all implementations of an interface, abstract class, or virtual/abstract member. " +
            "Returns the locations of all concrete implementations in the solution, " +
            "distinguishing direct implementations from indirect ones (via intermediate interfaces). " +
            "Provide either a symbolName or a file path with position.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                symbolName = new { type = "string", description = "Symbol name to find implementations of (e.g. 'IMyInterface', 'Namespace.IMyInterface'). Alternative to filePath+line+column." },
                kind = new { type = "string", description = "Filter by symbol kind when using symbolName: 'type', 'method', 'property', 'field', 'event'. Helps disambiguate when multiple symbols share a name." },
                filePath = new { type = "string", description = "Absolute path to the file containing the symbol" },
                line = new { type = "integer", description = "1-based line number of the symbol" },
                column = new { type = "integer", description = "1-based column number of the symbol" }
            },
            required = new string[0]
        };

        public object Execute(JObject arguments)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                arguments["symbolName"]?.ToString(),
                arguments["kind"]?.ToString(),
                arguments["filePath"]?.ToString(),
                arguments["line"]?.Value<int>() ?? 0,
                arguments["column"]?.Value<int>() ?? 0);

            if (error != null) return error;

            var implementations = new List<ImplInfo>();

            // For type elements (interfaces, abstract classes) — find inheritors with depth info
            if (declaredElement is ITypeElement typeElement)
            {
                var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);
                var psiServices = _solution.GetPsiServices();

                // Collect all inheritors
                var inheritors = new List<ITypeElement>();
                psiServices.Finder.FindInheritors(
                    typeElement,
                    searchDomain,
                    new FindResultConsumer(findResult =>
                    {
                        if (findResult is FindResultInheritedElement inherited &&
                            inherited.DeclaredElement is ITypeElement inheritedType)
                            inheritors.Add(inheritedType);
                        return FindExecution.Continue;
                    }),
                    NullProgressIndicator.Create());

                // Build a set of all inheritor FQNs for "via" resolution
                var inheritorSet = new HashSet<string>(
                    inheritors.Select(i => i.GetClrName().FullName));

                foreach (var inheritor in inheritors)
                {
                    var info = BuildImplInfo(inheritor);
                    if (info == null) continue;

                    var directlyImplements = IsDirectImplementor(inheritor, typeElement);
                    info.Direct = directlyImplements;

                    if (!directlyImplements)
                    {
                        var via = FindIntermediateTypes(inheritor, typeElement, inheritorSet);
                        if (via.Count > 0)
                            info.Via = via;
                    }

                    implementations.Add(info);
                }
            }

            // For overridable members (virtual/abstract methods, properties) — find overrides
            if (declaredElement is IOverridableMember overridable)
            {
                var psiServices = _solution.GetPsiServices();

                psiServices.Finder.FindImplementingMembers(
                    overridable,
                    overridable.GetSearchDomain(),
                    new FindResultConsumer(findResult =>
                    {
                        if (findResult is FindResultOverridableMember overrideResult)
                        {
                            var member = overrideResult.OverridableMember;
                            if (member == null) return FindExecution.Continue;

                            var info = BuildImplInfo(member);
                            if (info != null)
                            {
                                if (member is IClrDeclaredElement clr)
                                {
                                    var containingType = clr.GetContainingType();
                                    if (containingType != null && declaredElement is IClrDeclaredElement baseClr)
                                    {
                                        var baseContainingType = baseClr.GetContainingType();
                                        if (baseContainingType != null)
                                            info.Direct = IsDirectImplementor(containingType, baseContainingType);
                                    }
                                }
                                implementations.Add(info);
                            }
                        }
                        return FindExecution.Continue;
                    }),
                    true,
                    NullProgressIndicator.Create());
            }

            // Sort: direct first, then by name
            var sorted = implementations
                .OrderBy(i => i.Direct ? 0 : 1)
                .ThenBy(i => i.Name)
                .ToList();

            // Format compact output
            var sb = new StringBuilder();
            sb.Append(declaredElement.GetElementType().PresentableName).Append(' ');
            sb.Append(declaredElement.ShortName);
            sb.Append(" — ").Append(sorted.Count).AppendLine(" implementations");

            foreach (var impl in sorted)
            {
                sb.AppendLine();
                if (impl.Direct)
                    sb.Append("[direct] ");
                else if (impl.Via != null && impl.Via.Count > 0)
                    sb.Append("[via ").Append(string.Join(", ", impl.Via)).Append("] ");
                else
                    sb.Append("[indirect] ");

                sb.Append(impl.Kind).Append(' ').Append(impl.Name);
                sb.Append(" — ").Append(impl.File).Append(':').AppendLine(impl.Line.ToString());
                if (impl.Text != null)
                    sb.Append("  ").AppendLine(impl.Text);
            }

            return sb.ToString().TrimEnd();
        }

        private class ImplInfo
        {
            public string Name;
            public string Kind;
            public string File;
            public int Line;
            public string Text;
            public bool Direct;
            public List<string> Via;
        }

        private static ImplInfo BuildImplInfo(IDeclaredElement element)
        {
            if (element == null) return null;
            var declarations = element.GetDeclarations();
            if (declarations.Count == 0) return null;

            var decl = declarations[0];
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid()) return null;

            var sourceFile = decl.GetSourceFile();
            if (sourceFile == null) return null;

            var (implLine, _) = PsiHelpers.GetLineColumn(range.StartOffset);

            return new ImplInfo
            {
                Name = element.ShortName,
                Kind = element.GetElementType().PresentableName,
                File = sourceFile.GetLocation().FullPath,
                Line = implLine,
                Text = PsiHelpers.TruncateSnippet(decl.GetText())
            };
        }

        /// <summary>
        /// Checks if inheritor directly lists the target type in its super types (not via intermediate).
        /// </summary>
        private static bool IsDirectImplementor(ITypeElement inheritor, ITypeElement targetType)
        {
            var targetFqn = targetType.GetClrName().FullName;
            foreach (var superType in inheritor.GetSuperTypes())
            {
                var resolved = superType.GetTypeElement();
                if (resolved != null && resolved.GetClrName().FullName == targetFqn)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Finds intermediate types through which inheritor implements targetType.
        /// Returns the short names of those intermediate types.
        /// </summary>
        private static List<string> FindIntermediateTypes(
            ITypeElement inheritor, ITypeElement targetType, HashSet<string> allInheritorFqns)
        {
            var targetFqn = targetType.GetClrName().FullName;
            var via = new List<string>();

            foreach (var superType in inheritor.GetSuperTypes())
            {
                var resolved = superType.GetTypeElement();
                if (resolved == null) continue;

                var resolvedFqn = resolved.GetClrName().FullName;
                if (resolvedFqn == targetFqn) continue;

                if (allInheritorFqns.Contains(resolvedFqn) || ImplementsType(resolved, targetFqn))
                    via.Add(resolved.ShortName);
            }

            return via;
        }

        /// <summary>
        /// Recursively checks if a type implements/extends the target (by FQN).
        /// </summary>
        private static bool ImplementsType(ITypeElement type, string targetFqn)
        {
            foreach (var superType in type.GetSuperTypes())
            {
                var resolved = superType.GetTypeElement();
                if (resolved == null) continue;
                if (resolved.GetClrName().FullName == targetFqn) return true;
                if (ImplementsType(resolved, targetFqn)) return true;
            }
            return false;
        }
    }
}
