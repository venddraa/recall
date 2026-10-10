using Recall.Core.Search;

namespace Recall.Core.Tests;

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData("clear sky index", "\"clear\" AND \"sky\" AND \"index\"")]
    [InlineData("  paper_final.md  ", "\"paper\" AND \"final\" AND \"md\"")]
    [InlineData("café déjà-vu", "\"café\" AND \"déjà\" AND \"vu\"")]
    [InlineData("one OR two", "\"one\" AND \"OR\" AND \"two\"")]
    [InlineData("judul 日本語 123", "\"judul\" AND \"日本語\" AND \"123\"")]
    public void BuildsLiteralAndExpressionFromWords(string input, string expected)
    {
        Assert.Equal(expected, SearchQuery.BuildFtsExpression(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--- /// ")]
    public void ReturnsNullWhenThereAreNoSearchableWords(string input)
    {
        Assert.Null(SearchQuery.BuildFtsExpression(input));
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SearchQuery.BuildFtsExpression(null!));
    }
}
