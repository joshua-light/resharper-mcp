using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util.dataStructures.TypedIntrinsics;

namespace ReSharperMcp
{
    public static class PsiHelpers
    {
        public const int MaxSnippetLength = 2000;

        /// <summary>
        /// Result of a file lookup: found source file, or diagnostic info about why it failed.
        /// </summary>
        public class FileResolveResult
        {
            public IPsiSourceFile SourceFile { get; set; }
            public IProjectFile ProjectFile { get; set; }
            public string Error { get; set; }

            public bool IsFound => SourceFile != null;
        }

        public static IPsiSourceFile GetSourceFile(ISolution solution, string filePath)
        {
            return ResolveFile(solution, filePath).SourceFile;
        }

        public static FileResolveResult ResolveFile(ISolution solution, string filePath)
        {
            // Lazy enumerable — only materialized if needed by later strategies
            var allFilesQuery = solution.GetAllProjects()
                .SelectMany(p => p.GetAllProjectFiles());

            IProjectFile projectFile = null;

            // 1. Exact match (fastest path — streams without materializing)
            projectFile = allFilesQuery.FirstOrDefault(f => f.Location.FullPath == filePath);

            // 2. If relative path, resolve against solution directory
            if (projectFile == null && !Path.IsPathRooted(filePath))
            {
                var solutionDir = solution.SolutionFilePath?.Directory?.FullPath;
                if (solutionDir != null)
                {
                    var resolved = Path.GetFullPath(Path.Combine(solutionDir, filePath));
                    projectFile = allFilesQuery.FirstOrDefault(f => f.Location.FullPath == resolved);
                }
            }

            // 3. Case-insensitive comparison (handles macOS case differences)
            if (projectFile == null)
            {
                projectFile = allFilesQuery.FirstOrDefault(f =>
                    string.Equals(f.Location.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
            }

            // 4. Suffix match (match from end with path separator boundary, case-insensitive)
            if (projectFile == null)
            {
                var suffix = filePath.Replace('\\', '/');
                projectFile = allFilesQuery.FirstOrDefault(f =>
                {
                    var full = f.Location.FullPath.Replace('\\', '/');
                    return full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && (full.Length == suffix.Length || full[full.Length - suffix.Length - 1] == '/');
                });
            }

            if (projectFile == null)
                return new FileResolveResult
                {
                    Error = $"File not found in solution: {filePath}"
                };

            // Project file found — try to get the PSI source file
            var sourceFile = projectFile.ToSourceFiles().FirstOrDefault();
            if (sourceFile != null)
                return new FileResolveResult { SourceFile = sourceFile, ProjectFile = projectFile };

            // Fallback: search PSI modules directly for a source file at this path.
            // ToSourceFiles() can return empty when the PSI cache is stale or the file
            // hasn't been indexed yet (common in git worktree solutions).
            var targetPath = projectFile.Location.FullPath;
            var psiServices = solution.GetPsiServices();
            foreach (var project in solution.GetAllProjects())
            {
                foreach (var module in psiServices.Modules.GetPsiModules(project))
                {
                    foreach (var sf in module.SourceFiles)
                    {
                        if (sf.GetLocation().FullPath == targetPath)
                            return new FileResolveResult { SourceFile = sf, ProjectFile = projectFile };
                    }
                }
            }

            return new FileResolveResult
            {
                ProjectFile = projectFile,
                Error = $"File exists in project but PSI source is not available (index may be stale): {projectFile.Location.FullPath}"
            };
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

                if (isQualified && !FqnEndsWith(fqn, symbolName)) continue;

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
                            if (isQualified && !FqnEndsWith(fqn, symbolName)) continue;

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

            // Third: search local functions inside method bodies
            // For qualified names like "BattleLoop.Tick" or "BattleLoop.Update.Tick"
            if (candidates.Count == 0 && isQualified && (kind == null || kind == "method"))
            {
                // The containing type is always the first or second-to-last part
                // "BattleLoop.Tick" → search all methods in BattleLoop for local func "Tick"
                // "BattleLoop.Update.Tick" → search BattleLoop.Update for local func "Tick"
                var containingTypeName = nameParts.Length >= 3
                    ? nameParts[nameParts.Length - 3]
                    : nameParts[nameParts.Length - 2];
                var containingMethodName = nameParts.Length >= 3
                    ? nameParts[nameParts.Length - 2]
                    : null;

                foreach (var typeElement in symbolScope.GetElementsByShortName(containingTypeName))
                {
                    if (!(typeElement is ITypeElement te)) continue;

                    foreach (var member in te.GetMembers())
                    {
                        if (containingMethodName != null && member.ShortName != containingMethodName)
                            continue;

                        foreach (var decl in member.GetDeclarations())
                        {
                            foreach (var lfd in FindAllLocalFunctions(decl))
                            {
                                if (lfd.DeclaredName != shortName) continue;
                                var el = lfd.DeclaredElement;
                                if (el == null) continue;
                                var fqn = te.ShortName + "." + member.ShortName + "." + shortName;
                                if (!seenFqns.Add(fqn)) continue;
                                candidates.Add((el, fqn));
                            }
                        }
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
                // Find the best declaration with a valid file path
                string filePath = null;
                var line = 0;
                foreach (var d in element.GetDeclarations())
                {
                    var s = d.GetSourceFile();
                    var path = s?.GetLocation().FullPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        filePath = path;
                        var range = TreeNodeExtensions.GetDocumentRange(d);
                        if (range.IsValid())
                        {
                            var (l, _) = GetLineColumn(range.StartOffset);
                            line = l;
                        }
                        break;
                    }
                }

                candidateInfos.Add(new SymbolCandidate
                {
                    Name = element.ShortName,
                    QualifiedName = fqn,
                    Kind = element.GetElementType().PresentableName,
                    File = filePath ?? "[no source]",
                    Line = line
                });
            }

            return new SymbolResolveResult { Candidates = candidateInfos };
        }

        /// <summary>
        /// Checks whether fqn ends with suffix on a dot boundary.
        /// e.g. FqnEndsWith("A.B.C.D", "C.D") == true, FqnEndsWith("A.B.CD", "C.D") == false.
        /// </summary>
        private static bool FqnEndsWith(string fqn, string suffix)
        {
            if (fqn == suffix) return true;
            return fqn.Length > suffix.Length
                && fqn.EndsWith(suffix)
                && fqn[fqn.Length - suffix.Length - 1] == '.';
        }

        private static bool MatchesKind(IDeclaredElement element, string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "type": return element is ITypeElement;
                case "method": return element is IMethod ||
                    element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction;
                case "property": return element is IProperty;
                case "field": return element is IField;
                case "event": return element is IEvent;
                default: return false;
            }
        }

        private static IEnumerable<ILocalFunctionDeclaration> FindAllLocalFunctions(ITreeNode node)
        {
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is ILocalFunctionDeclaration lfd)
                {
                    yield return lfd;
                    // Also search nested local functions inside this one
                    foreach (var nested in FindAllLocalFunctions(lfd))
                        yield return nested;
                }
                else
                {
                    foreach (var found in FindAllLocalFunctions(child))
                        yield return found;
                }
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
                {
                    if (kind != null)
                    {
                        // Check if the symbol exists with a different kind
                        var withoutKind = ResolveSymbolByName(solution, symbolName, null);
                        if (withoutKind.IsFound)
                        {
                            var actualKind = withoutKind.Element.GetElementType().PresentableName;
                            return (null, new { error = $"No {kind} named '{symbolName}' found. Did you mean the {actualKind} '{symbolName}'?" });
                        }
                        if (withoutKind.IsAmbiguous)
                        {
                            var kinds = string.Join(", ", withoutKind.Candidates.Select(c => c.Kind).Distinct());
                            return (null, new { error = $"No {kind} named '{symbolName}' found. Found symbols with that name of kind(s): {kinds}" });
                        }
                    }
                    return (null, new { error = $"Symbol not found: {symbolName}" });
                }

                return (result.Element, null);
            }

            if (!string.IsNullOrEmpty(filePath) && line > 0 && column > 0)
            {
                var resolved = ResolveFile(solution, filePath);
                if (!resolved.IsFound)
                    return (null, new { error = resolved.Error });
                var sourceFile = resolved.SourceFile;

                var node = GetNodeAtPosition(sourceFile, line, column);
                if (node == null)
                    return (null, new { error = $"No syntax node found at {line}:{column}" });

                var element = GetDeclaredElement(node);
                if (element == null)
                {
                    // Try to extract reference name for a more helpful error message
                    var refName = GetNearestReferenceName(node);
                    if (refName != null)
                        return (null, new { error = $"Cannot resolve symbol '{refName}' at {line}:{column}. It may be from an external/compiled assembly." });
                    return (null, new { error = $"No resolvable symbol found at {line}:{column}" });
                }

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
            var originalOffset = node.GetTreeStartOffset();

            // Phase 1: Walk up looking for resolvable references (what the cursor points AT).
            // This handles usages, type references, member access, etc.
            var current = node;
            for (var depth = 0; current != null && depth < 5; depth++)
            {
                foreach (var reference in current.GetReferences())
                {
                    var resolved = reference.Resolve();
                    if (resolved.DeclaredElement != null)
                        return resolved.DeclaredElement;
                }
                current = current.Parent;
            }

            // Phase 2: No references resolved — look for a nearby declaration.
            // This handles clicking on a declaration's name, keyword, or modifier.
            // Limited to 3 levels to avoid jumping to distant ancestor declarations
            // (e.g. returning a class when the cursor was on an unresolvable type reference
            // deep inside the class body).
            current = node;
            for (var depth = 0; current != null && depth < 3; depth++)
            {
                if (current is IDeclaration declaration)
                    return declaration.DeclaredElement;
                current = current.Parent;
            }

            return null;
        }

        /// <summary>
        /// Walks up the tree from a node to find the nearest reference name.
        /// Used to produce better error messages when symbol resolution fails.
        /// </summary>
        private static string GetNearestReferenceName(ITreeNode node)
        {
            var current = node;
            for (int i = 0; i < 3 && current != null; i++)
            {
                foreach (var r in current.GetReferences())
                    return r.GetName();
                current = current.Parent;
            }
            // Fall back to the token text
            var text = node.GetText()?.Trim();
            return !string.IsNullOrEmpty(text) && text.Length <= 100 ? text : null;
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

        /// <summary>
        /// Formats a compact one-line signature for a declared element.
        /// Examples: "property Id : int", "static method DoStuff(x:int, y:string) : void", "class MyClass"
        /// </summary>
        public static string FormatSignature(IDeclaredElement element)
        {
            var lang = element.PresentationLanguage ?? CSharpLanguage.Instance;
            var sb = new StringBuilder();

            // Modifiers
            if (element is IModifiersOwner mod)
            {
                if (mod.IsStatic) sb.Append("static ");
                if (mod.IsAbstract) sb.Append("abstract ");
                if (element is IMethod m)
                {
                    if (m.IsVirtual) sb.Append("virtual ");
                    if (m.IsOverride) sb.Append("override ");
                }
            }

            // Kind + Name
            sb.Append(element.GetElementType().PresentableName);
            sb.Append(' ');
            sb.Append(element.ShortName);

            // Parameters (methods/local functions always get parens; others only if they have params, e.g. indexers)
            if (element is IMethod || element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction)
                AppendParams(sb, (IParametersOwner)element, lang);
            else if (element is IParametersOwner po && po.Parameters.Count > 0)
                AppendParams(sb, po, lang);

            // Type: return type for methods/local functions, declared type for fields/properties
            if (element is IMethod method)
            {
                sb.Append(" : ");
                sb.Append(method.ReturnType.GetPresentableName(lang));
            }
            else if (element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction localFunc)
            {
                sb.Append(" : ");
                sb.Append(localFunc.ReturnType.GetPresentableName(lang));
            }
            else if (element is ITypeOwner typeOwner)
            {
                sb.Append(" : ");
                sb.Append(typeOwner.Type.GetPresentableName(lang));
            }

            return sb.ToString();
        }

        private static void AppendParams(StringBuilder sb, IParametersOwner owner, PsiLanguageType lang)
        {
            sb.Append('(');
            var first = true;
            foreach (var p in owner.Parameters)
            {
                if (!first) sb.Append(", ");
                sb.Append(p.ShortName);
                sb.Append(':');
                sb.Append(p.Type.GetPresentableName(lang));
                first = false;
            }
            sb.Append(')');
        }
    }
}
