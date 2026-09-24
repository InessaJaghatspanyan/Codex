using LaborRag.Core;

namespace LaborRag.Tests;

public class GlossaryAndStemmerTests
{
    [Theory]
    [InlineData("աշխատողի", "աշխատող")]
    [InlineData("աշխատողների", "աշխատող")]
    [InlineData("տույժի", "տույժ")]
    [InlineData("տույժերը", "տույժ")]
    [InlineData("պատասխանատվության", "պատասխանատվություն")]
    [InlineData("ազատությունը", "ազատություն")] // "freedom" stays distinct from…
    [InlineData("ազատել", "ազատ")]              // …"to dismiss"
    [InlineData("ազատում", "ազատ")]
    [InlineData("overtime", "overtime")] // other scripts are left alone
    [InlineData("օր", "օր")]             // too short to strip
    public void StemsArmenian(string word, string stem) => Assert.Equal(stem, ArmenianStemmer.Stem(word));

    [Fact]
    public void ExpandsEnglishAndRussianTerms()
    {
        Assert.Contains("արտաժամյա աշխատանք", Glossary.Expand("How is overtime paid?"));
        Assert.Contains("փորձաշրջան", Glossary.Expand("Какова продолжительность испытательного срока?"));
        Assert.Contains("ամենամյա արձակուրդ", Glossary.Expand("Сколько дней отпуска?"));
    }

    [Fact]
    public void ShortEnglishPatternsMatchWholeWordsOnly()
    {
        Assert.DoesNotContain("հանգստի ժամանակ հանգստյան օր", Glossary.Expand("Are there restrictions?"));
        Assert.Contains("հանգստի ժամանակ հանգստյան օր", Glossary.Expand("How much rest do I get?"));
        Assert.Empty(Glossary.Expand("աշխատավարձ")); // Armenian questions need no expansion
    }
}

public class PassageTests
{
    [Fact]
    public void ListItemsKeepTheirLeadIn()
    {
        var provisions = PassageFinder.SplitProvisions(
            "1. Սույն հոդվածով սահմանված դեպքերում պայմանագիրը լուծվում է`\n1) առաջին հիմքով լուծում.\n2) երկրորդ հիմքով լուծում:\n(10-րդ հոդ. փոփ. 01.01.20 ՀՕ-1-Ն օրենք)");
        Assert.Equal("1. Սույն հոդվածով սահմանված դեպքերում պայմանագիրը լուծվում է`\n1) առաջին հիմքով լուծում.\n2) երկրորդ հիմքով լուծում:", provisions[0].Text);
        Assert.Equal("1) առաջին հիմքով լուծում.", provisions[1].Text);
        Assert.Equal("1. Սույն հոդվածով սահմանված դեպքերում պայմանագիրը լուծվում է`", provisions[1].Context);
        Assert.DoesNotContain(provisions, p => p.Text.Contains("փոփ.")); // amendment notes are skipped
    }

    [Fact]
    public void LongParagraphsAreSplitIntoSentences()
    {
        var sentence = "Սա բավականաչափ երկար նախադասություն է, որը պարունակում է բազմաթիվ բառեր և մտքեր:";
        var provisions = PassageFinder.SplitProvisions(string.Join(" ", Enumerable.Repeat(sentence, 6)));
        Assert.Equal(6, provisions.Count);
    }

    private static readonly Lazy<SearchIndex> RealCode = new(() => new SearchIndex(CodeParser.LoadCorpus([TestData.CodePdf])));

    /// <summary>Questions whose answer is well known, asked in all three languages.</summary>
    [Theory]
    [InlineData("Քանի՞ օր է ամենամյա նվազագույն արձակուրդը", "159")]
    [InlineData("How is overtime work paid?", "184")]
    [InlineData("Какова продолжительность испытательного срока?", "92")]
    [InlineData("Can an employer fire a pregnant woman?", "114")]
    [InlineData("Կարո՞ղ է գործատուն ազատել հղի կնոջը", "114")]
    [InlineData("What is the maximum working time per week?", "139")]
    [InlineData("նվազագույն աշխատավարձ", "179")]
    [InlineData("զանգվածային ազատումներ", "116")]
    public void FindsTheRightArticleOnTheRealCode(string question, string article)
    {
        var result = PassageFinder.Answer(RealCode.Value, question);
        Assert.Contains(article, result.Passages.Take(2).Select(p => p.Chunk.Article));
    }

    [Fact]
    public void QuotesTheAnswerToTheVacationQuestion()
    {
        var result = PassageFinder.Answer(RealCode.Value, "Քանի՞ օր է ամենամյա նվազագույն արձակուրդը");
        Assert.Contains("20 աշխատանքային օր", result.Passages[0].Text);
    }
}
