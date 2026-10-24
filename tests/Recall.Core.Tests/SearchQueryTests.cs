using Recall.Core.Search;

namespace Recall.Core.Tests;

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData("clear sky index", "\"clear\"* AND \"sky\"* AND \"index\"*")]
    [InlineData("  paper_final.md  ", "\"paper\"* AND \"final\"* AND \"md\"")]
    [InlineData("café déjà-vu", "\"café\"* AND \"déjà\"* AND \"vu\"")]
    [InlineData("one OR two", "\"one\"* AND \"OR\" AND \"two\"*")]
    [InlineData("judul 日本語 123", "\"judul\"* AND \"日本語\"* AND \"123\"*")]
    [InlineData("BAP", "\"BAP\"*")]
    [InlineData("BA", "\"BA\"")]
    [InlineData("BÁ", "\"BÁ\"")]
    [InlineData("BA\u0301", "\"BA\u0301\"")]
    [InlineData("A\u0301\u0308", "\"A\u0301\u0308\"")]
    [InlineData("BA\u0301P", "\"BA\u0301P\"*")]
    [InlineData("cafe\u0301", "\"cafe\u0301\"*")]
    [InlineData("\u0301 café \u0308", "\"café\"*")]
    public void BuildsLiteralAndExpressionFromWords(string input, string expected)
    {
        Assert.Equal(expected, SearchQuery.BuildFtsExpression(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--- /// ")]
    [InlineData("\u0301\u0308")]
    [InlineData("\u0903")]
    [InlineData("\u20DD")]
    [InlineData("\u0301 \u0903\u20DD")]
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
