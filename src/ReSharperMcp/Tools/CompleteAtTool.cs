using System;
using System.Collections.Generic;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCompletion;
using JetBrains.ReSharper.Feature.Services.CodeCompletion.Infrastructure.AspectLookupItems.BaseInfrastructure;
using JetBrains.ReSharper.Feature.Services.CodeCompletion.Infrastructure.AspectLookupItems.Info;
using JetBrains.ReSharper.Feature.Services.CodeCompletion.Infrastructure.LookupItems;
using JetBrains.ReSharper.Psi;
using JetBrains.TextControl;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using Newtonsoft.Json.Linq;

namespace ReSharperMcp.Tools
{
    /// <summary>
    /// Code completion suggestions at a position (LSP textDocument/completion parity).
    ///
    /// This tool is RISKY / best-effort. Driving code completion headlessly requires the R# main
    /// thread plus a live <see cref="ITextControl"/>; completion providers may legitimately return
    /// nothing outside of a real interactive typing session, and some of them assert on UI state we
    /// cannot fully reproduce here. The whole flow is therefore wrapped defensively: any failure (or
    /// an empty provider result) degrades gracefully to an empty item list with an explanatory note,
    /// and we never block forever — the surrounding HTTP worker abandons after 30s.
    ///
    /// This tool is logically READ-ONLY: it performs no PSI writes. It implements the write marker
    /// <see cref="IMcpSelfTransactingWriteTool"/> ONLY to obtain main-thread dispatch — in v0.8.0 the
    /// only path that runs Execute on the R# main thread is the IMcpWriteTool branch in
    /// McpServerComponent. Choosing the self-transacting marker means the component acquires a write
    /// lock but opens NO auto-committing PSI transaction, which is what we want: we never write, so
    /// there is nothing to commit. We must NOT take locks, open transactions, commit documents, or
    /// spawn threads ourselves.
    /// </summary>
    public class CompleteAtTool : IMcpSelfTransactingWriteTool
    {
        private readonly ISolution _solution;

        public CompleteAtTool(ISolution solution) => _solution = solution;

        public string Name => "complete_at";

        public string Description =>
            "Get code completion suggestions at a position (LSP textDocument/completion parity). " +
            "Provide a file path with a 1-based line/column for the caret. Returns the lookup items " +
            "(text, kind hint, type, and tail/qualifier) the IDE would offer at that location. " +
            "Best-effort: completion is driven through the IDE's intellisense engine and may return " +
            "an empty list when run outside an interactive editing session.";

        public object InputSchema => new
        {
            type = "object",
            properties = new
            {
                filePath = new { type = "string", description = "Absolute path to the file to complete in" },
                line = new { type = "integer", description = "1-based line number of the caret" },
                column = new { type = "integer", description = "1-based column number of the caret" },
                maxResults = new { type = "integer", description = "Maximum number of completion items to return (default 50)" }
            },
            required = new[] { "filePath", "line", "column" }
        };

        public object Execute(JObject arguments)
        {
            var filePath = arguments["filePath"]?.ToString();
            var line = arguments["line"]?.Value<int>() ?? 0;
            var column = arguments["column"]?.Value<int>() ?? 0;
            var maxResults = arguments["maxResults"]?.Value<int>() ?? 50;
            if (maxResults <= 0) maxResults = 50;

            if (string.IsNullOrEmpty(filePath) || line <= 0 || column <= 0)
                return new { error = "Provide 'filePath' plus 1-based 'line' and 'column'" };

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return new { error = $"File not found in solution: {filePath}" };

            var document = sourceFile.Document;
            if (document == null)
                return new { error = $"No document available for file: {filePath}" };

            // Translate 1-based line/column into a document offset (clamped defensively).
            int offset;
            try
            {
                var docLine = (Int32<DocLine>)(line - 1);
                var docColumn = (Int32<DocColumn>)(column - 1);
                var coords = new DocumentCoords(docLine, docColumn);
                offset = document.GetOffsetByCoords(coords);
            }
            catch (Exception ex)
            {
                return new { error = $"Invalid position {line}:{column}: {ex.Message}" };
            }

            // From here on, completion may legitimately fail in headless conditions. Degrade
            // gracefully to an empty list with a note rather than surfacing an exception.
            var unavailable = new Dictionary<string, object>
            {
                ["file"] = filePath,
                ["line"] = line,
                ["column"] = column,
                ["items"] = new List<object>(),
                ["note"] = "completion unavailable headless / needs interactive session"
            };

            try
            {
                ITextControlManager textControlManager;
                IntellisenseManager intellisenseManager;
                try
                {
                    textControlManager = _solution.GetComponent<ITextControlManager>();
                    intellisenseManager = _solution.GetComponent<IntellisenseManager>();
                }
                catch (Exception ex)
                {
                    unavailable["note"] = $"completion unavailable headless / needs interactive session ({ex.Message})";
                    return unavailable;
                }

                if (textControlManager == null || intellisenseManager == null)
                    return unavailable;

                object result = null;

                // Scope the synthetic text control to a temporary lifetime so it is torn down as soon
                // as we are done driving completion. Lifetime.Using runs synchronously on this (main)
                // thread and terminates the lifetime on return.
                Lifetime.Using(lt =>
                {
                    ITextControl textControl;
                    try
                    {
                        textControl = textControlManager.CreateTextControl(lt, document);
                    }
                    catch (Exception ex)
                    {
                        unavailable["note"] = $"completion unavailable headless / needs interactive session ({ex.Message})";
                        result = unavailable;
                        return;
                    }

                    if (textControl == null)
                    {
                        result = unavailable;
                        return;
                    }

                    try
                    {
                        textControl.Caret.MoveTo(offset, CaretVisualPlacement.DontScrollIfVisible);
                    }
                    catch
                    {
                        // Non-fatal: completion uses the caret position but a move failure should not crash.
                    }

                    ICodeCompletionResult completionResult;
                    try
                    {
                        var parameters = new CodeCompletionParameters(CodeCompletionType.BasicCompletion);
                        completionResult = intellisenseManager.GetCompletionResult(parameters, textControl);
                    }
                    catch (Exception ex)
                    {
                        unavailable["note"] = $"completion unavailable headless / needs interactive session ({ex.Message})";
                        result = unavailable;
                        return;
                    }

                    result = ShapeResult(filePath, line, column, completionResult, maxResults);
                });

                return result ?? unavailable;
            }
            catch (Exception ex)
            {
                // Absolute last-resort guard: never let an unexpected failure crash the host.
                unavailable["note"] = $"completion unavailable headless / needs interactive session ({ex.Message})";
                return unavailable;
            }
        }

        /// <summary>
        /// Derives a clean, semantic "kind" string for a completion item.
        ///
        /// Preferred path: recover the backing <see cref="IDeclaredElement"/> and use the same
        /// element-type presentable name pattern the other tools use (see GetSymbolInfoTool:
        /// <c>declaredElement.GetElementType().PresentableName</c>), yielding values like "Method",
        /// "Property", "Field", "Class", "Local", "Parameter". Extension methods are reported as
        /// "ExtensionMethod" via <see cref="IMethod.IsExtensionMethod"/>.
        ///
        /// The declared element is reached by two complementary means, because not every symbol-backed
        /// item implements <see cref="IDeclaredElementLookupItem"/> directly:
        ///   1. <see cref="IDeclaredElementLookupItem.PreferredDeclaredElement"/> on items that do; and
        ///   2. the aspect-based items (the generic <c>LookupItem&lt;TInfo&gt;</c>, which previously
        ///      leaked into the output as the raw "LookupItem`1" type name — e.g. extension methods)
        ///      whose <see cref="DeclaredElementInfo.PreferredDeclaredElement"/> exposes the same
        ///      element. Covariance on <see cref="IAspectLookupItem{TInfo}"/> lets us read the info as
        ///      <see cref="ILookupItemInfo"/> regardless of the concrete <c>TInfo</c>.
        ///
        /// Fallback for non-symbol items (keywords, postfix templates, snippets, etc.) and anything
        /// whose declared element is unreachable: a cleaned-up lookup-item type name (e.g.
        /// "KeywordLookupItem", and "LookupItem" instead of the bare "LookupItem`1"), which is still a
        /// clean token rather than the icon object's verbose ToString that previously leaked out.
        /// </summary>
        private static string GetKind(ILookupItem lookupItem)
        {
            var element = TryGetDeclaredElement(lookupItem);
            if (element != null)
            {
                // Distinguish extension methods cheaply: they are ordinary IMethods flagged as such.
                if (element is IMethod method && method.IsExtensionMethod)
                    return "ExtensionMethod";

                var presentableName = element.GetElementType()?.PresentableName;
                if (!string.IsNullOrEmpty(presentableName))
                    return presentableName;
            }

            return CleanTypeName(lookupItem.GetType().Name);
        }

        /// <summary>
        /// Best-effort extraction of the backing <see cref="IDeclaredElement"/> from a lookup item,
        /// trying every cheap, headless-safe accessor we know of. Returns null for items that genuinely
        /// have no declared element (keywords, templates, etc.) or if any accessor throws.
        /// </summary>
        private static IDeclaredElement TryGetDeclaredElement(ILookupItem lookupItem)
        {
            // 1. Items that expose the declared element directly.
            if (lookupItem is IDeclaredElementLookupItem declaredElementItem)
            {
                IDeclaredElement element = declaredElementItem.PreferredDeclaredElement?.Element;
                if (element != null)
                    return element;
            }

            // 2. Aspect-based items (the generic LookupItem<TInfo>). IAspectLookupItem<out TInfo> is
            //    covariant, so we can read Info as ILookupItemInfo for any concrete TInfo and then probe
            //    for the declared-element-bearing info shape.
            if (lookupItem is IAspectLookupItem<ILookupItemInfo> aspectItem
                && aspectItem.Info is DeclaredElementInfo declaredElementInfo)
            {
                IDeclaredElement element = declaredElementInfo.PreferredDeclaredElement?.Element;
                if (element != null)
                    return element;
            }

            return null;
        }

        /// <summary>
        /// Strips the CLR arity suffix (e.g. "`1") from a generic type's runtime name so a fallback kind
        /// reads as "LookupItem" rather than "LookupItem`1".
        /// </summary>
        private static string CleanTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeName;

            int backtick = typeName.IndexOf('`');
            return backtick >= 0 ? typeName.Substring(0, backtick) : typeName;
        }

        private static object ShapeResult(
            string filePath, int line, int column, ICodeCompletionResult completionResult, int maxResults)
        {
            var items = new List<object>();

            var lookupItems = completionResult?.LookupItems;
            if (lookupItems != null)
            {
                foreach (var evaluated in lookupItems)
                {
                    if (items.Count >= maxResults) break;

                    // EvaluatedLookupItem is a struct; access its LookupItem field directly.
                    var lookupItem = evaluated.LookupItem;
                    if (lookupItem == null) continue;

                    string text = null;
                    string type = null;
                    string kind = null;

                    try { text = lookupItem.DisplayName?.Text; } catch { /* presentation may throw */ }
                    try { type = lookupItem.DisplayTypeName?.Text; } catch { /* optional */ }
                    try { kind = GetKind(lookupItem); } catch { /* optional */ }

                    if (string.IsNullOrEmpty(text)) continue;

                    var item = new Dictionary<string, object> { ["text"] = text };
                    if (!string.IsNullOrEmpty(kind)) item["kind"] = kind;
                    if (!string.IsNullOrEmpty(type)) item["type"] = type;
                    items.Add(item);
                }
            }

            var output = new Dictionary<string, object>
            {
                ["file"] = filePath,
                ["line"] = line,
                ["column"] = column,
                ["items"] = items
            };

            if (items.Count == 0)
                output["note"] = "completion unavailable headless / needs interactive session";

            return output;
        }
    }
}
