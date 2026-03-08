using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util.dataStructures.TypedIntrinsics;

namespace ReSharperMcp
{
    public static class PsiHelpers
    {
        public const int MaxSnippetLength = 200;

        public static IPsiSourceFile GetSourceFile(ISolution solution, string filePath)
        {
            var projectFile = solution.GetAllProjects()
                .SelectMany(p => p.GetAllProjectFiles())
                .FirstOrDefault(f => f.Location.FullPath == filePath);

            return projectFile?.ToSourceFiles().FirstOrDefault();
        }

        /// <summary>
        /// Gets the primary PSI file for a source file using CSharpLanguage.
        /// </summary>
        public static IFile GetPsiFile(IPsiSourceFile sourceFile)
        {
            return sourceFile.GetDominantPsiFile<CSharpLanguage>();
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
