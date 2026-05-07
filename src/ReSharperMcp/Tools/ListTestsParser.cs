using System;
using System.Collections.Generic;

namespace ReSharperMcp.Tools
{
    internal static class ListTestsParser
    {
        public static string ParseFrameworkFilter(string framework)
        {
            if (string.IsNullOrWhiteSpace(framework)) return null;
            var normalized = framework.Trim().ToLowerInvariant();
            return normalized == "xunit" || normalized == "nunit" || normalized == "mstest"
                ? normalized
                : null;
        }

        public static TestAttribute TryGetTestAttribute(string declarationText, string frameworkFilter)
        {
            foreach (var attributeName in GetLeadingAttributeNames(declarationText))
            {
                var normalized = NormalizeAttributeName(attributeName);
                var testAttribute = MatchTestAttribute(normalized, attributeName);
                if (testAttribute == null) continue;
                if (frameworkFilter != null && testAttribute.Framework != frameworkFilter) continue;
                return testAttribute;
            }

            return null;
        }

        private static IEnumerable<string> GetLeadingAttributeNames(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            var index = 0;
            while (index < text.Length)
            {
                index = SkipTrivia(text, index);
                if (index >= text.Length || text[index] != '[') yield break;

                var end = FindMatchingBracket(text, index);
                if (end < 0) yield break;

                foreach (var name in SplitAttributeSection(text.Substring(index + 1, end - index - 1)))
                    yield return name;

                index = end + 1;
            }
        }

        private static IEnumerable<string> SplitAttributeSection(string section)
        {
            foreach (var rawAttribute in SplitTopLevel(section, ','))
            {
                var attribute = rawAttribute.Trim();
                if (attribute.Length == 0) continue;

                if (attribute.StartsWith("global::", StringComparison.Ordinal))
                    attribute = attribute.Substring("global::".Length);

                var colon = attribute.IndexOf(':');
                if (colon >= 0)
                    attribute = attribute.Substring(colon + 1).TrimStart();

                if (attribute.StartsWith("global::", StringComparison.Ordinal))
                    attribute = attribute.Substring("global::".Length);

                var end = 0;
                while (end < attribute.Length)
                {
                    var c = attribute[end];
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.')) break;
                    end++;
                }

                if (end > 0)
                    yield return attribute.Substring(0, end);
            }
        }

        private static IEnumerable<string> SplitTopLevel(string text, char separator)
        {
            var start = 0;
            var parenDepth = 0;
            var bracketDepth = 0;
            var inString = false;
            var stringChar = '\0';

            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == stringChar && (i == 0 || text[i - 1] != '\\')) inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    stringChar = c;
                }
                else if (c == '(') parenDepth++;
                else if (c == ')' && parenDepth > 0) parenDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']' && bracketDepth > 0) bracketDepth--;
                else if (c == separator && parenDepth == 0 && bracketDepth == 0)
                {
                    yield return text.Substring(start, i - start);
                    start = i + 1;
                }
            }

            yield return text.Substring(start);
        }

        private static int SkipTrivia(string text, int index)
        {
            while (index < text.Length)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

                if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/')
                {
                    index += 2;
                    while (index < text.Length && text[index] != '\r' && text[index] != '\n') index++;
                    continue;
                }

                if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                    index = Math.Min(index + 2, text.Length);
                    continue;
                }

                break;
            }

            return index;
        }

        private static int FindMatchingBracket(string text, int start)
        {
            var depth = 0;
            var inString = false;
            var stringChar = '\0';

            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == stringChar && (i == 0 || text[i - 1] != '\\')) inString = false;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    stringChar = c;
                }
                else if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }

            return -1;
        }

        private static TestAttribute MatchTestAttribute(string normalizedName, string originalName)
        {
            if (NameEndsWith(normalizedName, "fact") || NameEndsWith(normalizedName, "theory"))
                return new TestAttribute("xunit", originalName);

            if (NameEndsWith(normalizedName, "test") || NameEndsWith(normalizedName, "testcase") ||
                NameEndsWith(normalizedName, "testcasesource"))
                return new TestAttribute("nunit", originalName);

            if (NameEndsWith(normalizedName, "testmethod") || NameEndsWith(normalizedName, "datatestmethod"))
                return new TestAttribute("mstest", originalName);

            return null;
        }

        private static bool NameEndsWith(string name, string shortName)
        {
            return name == shortName || name.EndsWith("." + shortName, StringComparison.Ordinal);
        }

        private static string NormalizeAttributeName(string attributeName)
        {
            var name = attributeName.Trim().ToLowerInvariant();
            return name.EndsWith("attribute", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - "attribute".Length)
                : name;
        }
    }

    internal sealed class TestAttribute
    {
        public readonly string Framework;
        public readonly string Attribute;

        public TestAttribute(string framework, string attribute)
        {
            Framework = framework;
            Attribute = attribute;
        }
    }
}