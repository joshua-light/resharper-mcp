using System.Collections.Generic;
using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util.dataStructures.TypedIntrinsics;

namespace ReSharperMcp
{
    public static class PsiHelpers
    {
        public const int MaxSnippetLength = 2000;

        public static IPsiSourceFile GetSourceFile(ISolution solution, string filePath)
        {
            var projectFile = solution.GetAllProjects()
                .SelectMany(p => p.GetAllProjectFiles())
                .FirstOrDefault(f => f.Location.FullPath == filePath);

            return projectFile?.ToSourceFiles().FirstOrDefault();
        }

        /// <summary>
        /// Result of resolving a symbol by name. Either a single element, an ambiguity error, or not found.
        /// </summary>
        public class SymbolResolveResult
        {
            public IDeclaredElement Element { get; set; }
            public List<SymbolCandidate> Candidates { get; set; }
            public bool IsAmbiguous => Candidates != null && Candidates.Count > 1;
            public bool IsFound => Element != null;
        }

        public class SymbolCandidate
        {
            public string Name { get; set; }
            public string QualifiedName { get; set; }
            public string Kind { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
        }

        /// <summary>
        /// Resolves a declared element by name across the solution.
        /// Supports short names ("MyClass") and qualified names ("Namespace.MyClass").
        /// Optionally filters by kind ("type", "method", "property", "field", "event").
        /// Returns ambiguity info when multiple distinct symbols match.
        /// Uses R#'s symbol cache for fast indexed lookup instead of walking PSI trees.
        /// </summary>
        public static SymbolResolveResult ResolveSymbolByName(ISolution solution, string symbolName, string kind = null)
        {
            if (string.IsNullOrEmpty(symbolName))
                return new SymbolResolveResult();

            var nameParts = symbolName.Split('.');
            var shortName = nameParts[nameParts.Length - 1];
            var isQualified = nameParts.Length > 1;

            var psiServices = solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);

            // Collect all candidates
            var candidates = new List<(IDeclaredElement element, string fqn)>();
            var seenFqns = new HashSet<string>();

            // First: search types/namespaces from the symbol cache (fast indexed lookup)
            foreach (var element in symbolScope.GetElementsByShortName(shortName))
            {
                if (element is INamespace) continue;

                if (kind != null && !MatchesKind(element, kind)) continue;

                var fqn = GetQualifiedName(element);

                if (isQualified && fqn != symbolName) continue;

                if (!seenFqns.Add(fqn)) continue;

                candidates.Add((element, fqn));
            }

            // Second: search type members (methods, properties, fields, events)
            // Only if we're looking for a member kind, or no kind filter was specified
            if (candidates.Count == 0 && (kind == null || kind == "method" || kind == "property" || kind == "field" || kind == "event"))
            {
                // For qualified names like "MyClass.MyMethod", search members of the containing type
                if (isQualified && nameParts.Length >= 2)
                {
                    var containingTypeName = nameParts[nameParts.Length - 2];
                    foreach (var typeElement in symbolScope.GetElementsByShortName(containingTypeName))
                    {
                        if (!(typeElement is ITypeElement te)) continue;

                        foreach (var member in te.GetMembers())
                        {
                            if (member.ShortName != shortName) continue;
                            if (kind != null && !MatchesKind(member, kind)) continue;

                            var fqn = GetQualifiedName(member);
                            if (isQualified && fqn != symbolName) continue;

                            if (!seenFqns.Add(fqn)) continue;
                            candidates.Add((member, fqn));
                        }
                    }
                }
                else
                {
                    // Unqualified member search — scan all types for matching members
                    foreach (var typeName in symbolScope.GetAllShortNames())
                    {
                        foreach (var element in symbolScope.GetElementsByShortName(typeName))
                        {
                            if (!(element is ITypeElement te)) continue;

                            foreach (var member in te.GetMembers())
                            {
                                if (member.ShortName != shortName) continue;
                                if (kind != null && !MatchesKind(member, kind)) continue;

                                var fqn = GetQualifiedName(member);
                                if (!seenFqns.Add(fqn)) continue;
                                candidates.Add((member, fqn));
                            }
                        }

                        // Stop if we found some candidates (avoid scanning everything for common names)
                        if (candidates.Count > 0 && candidates.Count >= 10) break;
                    }
                }
            }

            if (candidates.Count == 0)
                return new SymbolResolveResult();

            if (candidates.Count == 1)
                return new SymbolResolveResult { Element = candidates[0].element };

            // Multiple matches — build candidate list for the error message
            var candidateInfos = new List<SymbolCandidate>();
            foreach (var (element, fqn) in candidates)
            {
                var declarations = element.GetDeclarations();
                var decl = declarations.Count > 0 ? declarations[0] : null;
                var sf = decl?.GetSourceFile();
                var line = 0;
                if (decl != null)
                {
                    var range = TreeNodeExtensions.GetDocumentRange(decl);
                    if (range.IsValid())
                    {
                        var (l, _) = GetLineColumn(range.StartOffset);
                        line = l;
                    }
                }

                candidateInfos.Add(new SymbolCandidate
                {
                    Name = element.ShortName,
                    QualifiedName = fqn,
                    Kind = element.GetElementType().PresentableName,
                    File = sf?.GetLocation().FullPath,
                    Line = line
                });
            }

            return new SymbolResolveResult { Candidates = candidateInfos };
        }

        private static bool MatchesKind(IDeclaredElement element, string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "type": return element is ITypeElement;
                case "method": return element is IMethod;
                case "property": return element is IProperty;
                case "field": return element is IField;
                case "event": return element is IEvent;
                default: return false;
            }
        }

        public static string GetQualifiedName(IDeclaredElement element)
        {
            var parts = new List<string> { element.ShortName };

            if (element is IClrDeclaredElement clr)
            {
                var containingType = clr.GetContainingType();
                while (containingType != null)
                {
                    parts.Insert(0, containingType.ShortName);
                    containingType = containingType.GetContainingType();
                }

                var ns = clr is ITypeElement te
                    ? te.GetContainingNamespace()
                    : clr.GetContainingType()?.GetContainingNamespace();

                if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                    parts.Insert(0, ns.QualifiedName);
            }

            return string.Join(".", parts);
        }

        /// <summary>
        /// Common resolution logic used by all tools. Resolves a symbol from either symbolName+kind or filePath+line+column.
        /// Returns either a resolved element or an error object to return to the caller.
        /// </summary>
        public static (IDeclaredElement element, object error) ResolveFromArgs(
            ISolution solution, string symbolName, string kind, string filePath, int line, int column)
        {
            if (!string.IsNullOrEmpty(symbolName))
            {
                var result = ResolveSymbolByName(solution, symbolName, kind);

                if (result.IsAmbiguous)
                {
                    return (null, new
                    {
                        error = $"Ambiguous symbol '{symbolName}': found {result.Candidates.Count} matches. " +
                                "Use a qualified name, add 'kind' filter, or use filePath+line+column instead.",
                        candidates = result.Candidates.Select(c => new
                        {
                            qualifiedName = c.QualifiedName,
                            kind = c.Kind,
                            file = c.File,
                            line = c.Line
                        }).ToList()
                    });
                }

                if (!result.IsFound)
                    return (null, new { error = $"Symbol not found: {symbolName}" });

                return (result.Element, null);
            }

            if (!string.IsNullOrEmpty(filePath) && line > 0 && column > 0)
            {
                var sourceFile = GetSourceFile(solution, filePath);
                if (sourceFile == null)
                    return (null, new { error = $"File not found in solution: {filePath}" });

                var node = GetNodeAtPosition(sourceFile, line, column);
                if (node == null)
                    return (null, new { error = $"No syntax node found at {line}:{column}" });

                var element = GetDeclaredElement(node);
                if (element == null)
                    return (null, new { error = $"No resolvable symbol found at {line}:{column}" });

                return (element, null);
            }

            return (null, new { error = "Provide either 'symbolName' or 'filePath'+'line'+'column'" });
        }

        /// <summary>
        /// Gets the primary PSI file for a source file.
        /// Tries all available PSI files (supports C#, F#, VB, etc.).
        /// </summary>
        public static IFile GetPsiFile(IPsiSourceFile sourceFile)
        {
            // Try all PSI files for this source file (language-agnostic)
            var psiFiles = sourceFile.GetPsiFiles<KnownLanguage>();
            foreach (var psiFile in psiFiles)
                return psiFile;

            return null;
        }

        public static ITreeNode GetNodeAtPosition(IPsiSourceFile sourceFile, int line, int column)
        {
            var psiFile = GetPsiFile(sourceFile);
            if (psiFile == null) return null;

            var document = sourceFile.Document;
            var docLine = (Int32<DocLine>)(line - 1);
            var docColumn = (Int32<DocColumn>)(column - 1);
            var coords = new DocumentCoords(docLine, docColumn);
            var offset = document.GetOffsetByCoords(coords);
            var treeOffset = psiFile.Translate(new DocumentOffset(document, offset));

            return psiFile.FindNodeAt(treeOffset);
        }

        public static IDeclaredElement GetDeclaredElement(ITreeNode node)
        {
            var current = node;
            for (var depth = 0; current != null && depth < 5; depth++)
            {
                if (current is IDeclaration declaration)
                    return declaration.DeclaredElement;

                var references = current.GetReferences();
                foreach (var reference in references)
                {
                    var resolved = reference.Resolve();
                    if (resolved.DeclaredElement != null)
                        return resolved.DeclaredElement;
                }

                current = current.Parent;
            }

            return null;
        }

        public static (int line, int column) GetLineColumn(DocumentOffset offset)
        {
            var coords = offset.ToDocumentCoords();
            return ((int)coords.Line + 1, (int)coords.Column + 1);
        }

        public static string TruncateSnippet(string text, int maxLength = MaxSnippetLength)
        {
            if (text == null) return null;
            text = text.Trim();
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }
    }
}
