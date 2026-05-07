using Xunit;

namespace ReSharperMcp.Tools
{
    public class ListTestsParserTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ParseFrameworkFilter_TreatsMissingOrBlankAsNoFilter(string framework)
        {
            Assert.Null(ListTestsParser.ParseFrameworkFilter(framework));
        }

        [Theory]
        [InlineData("xunit", "xunit")]
        [InlineData("NUnit", "nunit")]
        [InlineData(" mstest ", "mstest")]
        public void ParseFrameworkFilter_NormalizesKnownFrameworks(string framework, string expected)
        {
            Assert.Equal(expected, ListTestsParser.ParseFrameworkFilter(framework));
        }

        [Fact]
        public void ParseFrameworkFilter_RejectsUnknownFramework()
        {
            Assert.Null(ListTestsParser.ParseFrameworkFilter("junit"));
        }

        [Theory]
        [InlineData("[Fact] public void Runs() {}", "xunit", "Fact")]
        [InlineData("[Theory] public void Runs() {}", "xunit", "Theory")]
        [InlineData("[Xunit.FactAttribute] public void Runs() {}", "xunit", "Xunit.FactAttribute")]
        [InlineData("[Test] public void Runs() {}", "nunit", "Test")]
        [InlineData("[TestCase(1)] public void Runs() {}", "nunit", "TestCase")]
        [InlineData("[TestCaseSource(nameof(Cases))] public void Runs() {}", "nunit", "TestCaseSource")]
        [InlineData("[TestMethod] public void Runs() {}", "mstest", "TestMethod")]
        [InlineData("[DataTestMethod] public void Runs() {}", "mstest", "DataTestMethod")]
        public void TryGetTestAttribute_DetectsSupportedFrameworkAttributes(
            string declarationText,
            string expectedFramework,
            string expectedAttribute)
        {
            var attribute = ListTestsParser.TryGetTestAttribute(declarationText, frameworkFilter: null);

            Assert.NotNull(attribute);
            Assert.Equal(expectedFramework, attribute.Framework);
            Assert.Equal(expectedAttribute, attribute.Attribute);
        }

        [Fact]
        public void TryGetTestAttribute_IgnoresCommasInsideAttributeArguments()
        {
            var attribute = ListTestsParser.TryGetTestAttribute(
                "[TestCase(1, \"a,b\", new[] { 1, 2 })] public void Runs() {}",
                frameworkFilter: null);

            Assert.NotNull(attribute);
            Assert.Equal("nunit", attribute.Framework);
            Assert.Equal("TestCase", attribute.Attribute);
        }

        [Fact]
        public void TryGetTestAttribute_FindsTestAttributeAfterNonTestAttributeInSameSection()
        {
            var attribute = ListTestsParser.TryGetTestAttribute(
                "[Trait(\"Category\", \"Fast\"), Fact] public void Runs() {}",
                frameworkFilter: null);

            Assert.NotNull(attribute);
            Assert.Equal("xunit", attribute.Framework);
            Assert.Equal("Fact", attribute.Attribute);
        }

        [Fact]
        public void TryGetTestAttribute_FindsFrameworkMatchAfterDifferentFrameworkAttribute()
        {
            var attribute = ListTestsParser.TryGetTestAttribute(
                "[Test]\n[Fact] public void Runs() {}",
                frameworkFilter: "xunit");

            Assert.NotNull(attribute);
            Assert.Equal("xunit", attribute.Framework);
            Assert.Equal("Fact", attribute.Attribute);
        }

        [Fact]
        public void TryGetTestAttribute_SupportsTargetedGlobalQualifiedAttributes()
        {
            var attribute = ListTestsParser.TryGetTestAttribute(
                "[method: global::Xunit.FactAttribute] public void Runs() {}",
                frameworkFilter: null);

            Assert.NotNull(attribute);
            Assert.Equal("xunit", attribute.Framework);
            Assert.Equal("Xunit.FactAttribute", attribute.Attribute);
        }

        [Fact]
        public void TryGetTestAttribute_ReturnsNullWhenFilterDoesNotMatch()
        {
            Assert.Null(ListTestsParser.TryGetTestAttribute(
                "[Fact] public void Runs() {}",
                frameworkFilter: "nunit"));
        }
    }
}
