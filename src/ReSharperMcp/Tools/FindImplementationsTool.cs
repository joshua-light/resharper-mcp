using System.Collections.Generic;
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
            "Returns the locations of all concrete implementations in the solution. " +
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

            var implementations = new List<object>();

            // For type elements (interfaces, abstract classes) — find inheritors
            if (declaredElement is ITypeElement typeElement)
            {
                var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);
                var psiServices = _solution.GetPsiServices();

                psiServices.Finder.FindInheritors(
                    typeElement,
                    searchDomain,
                    new FindResultConsumer(findResult =>
                    {
                        if (findResult is FindResultInheritedElement inherited)
                            AddImplementationLocation(implementations, inherited.DeclaredElement);
                        return FindExecution.Continue;
                    }),
                    NullProgressIndicator.Create());
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
                            AddImplementationLocation(implementations, overrideResult.OverridableMember);
                        return FindExecution.Continue;
                    }),
                    true,
                    NullProgressIndicator.Create());
            }

            return new
            {
                symbol = declaredElement.ShortName,
                kind = declaredElement.GetElementType().PresentableName,
                implementationsCount = implementations.Count,
                implementations
            };
        }

        private static void AddImplementationLocation(List<object> list, IDeclaredElement element)
        {
            if (element == null) return;
            var declarations = element.GetDeclarations();
            if (declarations.Count == 0) return;

            var decl = declarations[0];
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid()) return;

            var sourceFile = decl.GetSourceFile();
            if (sourceFile == null) return;

            var (implLine, implCol) = PsiHelpers.GetLineColumn(range.StartOffset);

            list.Add(new
            {
                name = element.ShortName,
                kind = element.GetElementType().PresentableName,
                file = sourceFile.GetLocation().FullPath,
                line = implLine,
                column = implCol,
                text = PsiHelpers.TruncateSnippet(decl.GetText())
            });
        }
    }
}
