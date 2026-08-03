using AegisErp.Domain;

namespace AegisErp.Tests;

public class AmountInWordsTests
{
    [Theory]
    [InlineData(0, "Dirham Zero Only")]
    [InlineData(1, "Dirham One Only")]
    [InlineData(19, "Dirham Nineteen Only")]
    [InlineData(100, "Dirham One Hundred Only")]
    [InlineData(1000, "Dirham One Thousand Only")]
    [InlineData(113267.70, "Dirham One Hundred Thirteen Thousand Two Hundred Sixty Seven and Seventy Only")]
    [InlineData(5393.70, "Dirham Five Thousand Three Hundred Ninety Three and Seventy Only")]
    [InlineData(1000000, "Dirham One Million Only")]
    [InlineData(1234567.89, "Dirham One Million Two Hundred Thirty Four Thousand Five Hundred Sixty Seven and Eighty Nine Only")]
    public void ToWords_matches_expected_phrasing(double amount, string expected)
    {
        Assert.Equal(expected, AmountInWords.ToWords((decimal)amount));
    }

    [Fact]
    public void ToWords_rounds_fraction_carry_into_the_whole_part()
    {
        // 99.999 rounds to 100.00 at the cents level, not "Ninety Nine and One Hundred".
        Assert.Equal("Dirham One Hundred Only", AmountInWords.ToWords(99.999m));
    }
}
