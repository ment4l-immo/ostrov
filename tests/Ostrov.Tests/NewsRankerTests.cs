using Ostrov.Modules.News;
using Xunit;

namespace Ostrov.Tests;

public class NewsRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static NewsItem Item(string id, string title, string source = "Source", double ageHours = 1)
        => new()
        {
            Id = id,
            Title = title,
            Source = source,
            Sphere = "games",
            Link = "https://example.com/" + id,
            Published = Now.AddHours(-ageHours),
        };

    [Fact]
    public void Tokens_ExtractsWordsAndHandlesRussian()
    {
        var tokens = NewsRanker.Tokens("Battlefield 6: трейлер и геймплей!").ToList();
        Assert.Contains("battlefield", tokens);
        Assert.Contains("трейлер", tokens);
        Assert.Contains("геймплей", tokens);
        Assert.DoesNotContain("этот", tokens);
    }

    [Fact]
    public void Score_FreshNewsScoresHigher()
    {
        var fresh = Item("1", "Новый трейлер игры", ageHours: 1);
        var old = Item("2", "Новый трейлер игры", ageHours: 72);
        var profile = new NewsProfile();
        var keywords = NewsRanker.ActiveKeywords(profile);

        Assert.True(NewsRanker.Score(fresh, profile, keywords, Now) > NewsRanker.Score(old, profile, keywords, Now));
    }

    [Fact]
    public void Score_WordsAndSourcesMatter()
    {
        var item = Item("1", "Battlefield трейлер", "PC Gamer");
        var profile = new NewsProfile();
        var plain = NewsRanker.Score(item, profile, NewsRanker.ActiveKeywords(profile), Now);

        var liked = new NewsProfile();
        NewsRanker.React(liked, item, 1, Now);
        Assert.True(NewsRanker.Score(item, liked, NewsRanker.ActiveKeywords(liked), Now) > plain);

        var disliked = new NewsProfile();
        NewsRanker.React(disliked, item, -1, Now);
        Assert.True(NewsRanker.Score(item, disliked, NewsRanker.ActiveKeywords(disliked), Now) < plain);
    }

    [Fact]
    public void Rank_SortsByScoreFiltersSphereAndHidesDisliked()
    {
        var items = new[]
        {
            Item("old", "Старая новость", ageHours: 100),
            Item("new", "Свежая новость", ageHours: 0.1),
            Item("mid", "Средняя новость", ageHours: 10),
            Item("ai", "AI news", ageHours: 0.01),
        };
        items[3].Sphere = "ai";

        var profile = new NewsProfile();
        NewsRanker.React(profile, items[0], -1, Now);

        var ranked = NewsRanker.Rank(items, profile, Now, sphere: "games");
        Assert.Equal(new[] { "new", "mid" }, ranked.Select(i => i.Id));

        var all = NewsRanker.Rank(items, new NewsProfile(), Now, sphere: null);
        Assert.Equal(4, all.Count);
        Assert.Equal("ai", all[0].Id);
    }

    [Fact]
    public void React_RepeatedLikeDoesNotMultiplyWeight()
    {
        var profile = new NewsProfile();
        var item = Item("1", "Battlefield трейлер");
        NewsRanker.React(profile, item, 1, Now);
        var weight = profile.Terms["battlefield"];
        NewsRanker.React(profile, item, 1, Now);
        Assert.Equal(weight, profile.Terms["battlefield"]);
    }

    [Fact]
    public void React_MindChangeRetrains()
    {
        var profile = new NewsProfile();
        var item = Item("1", "Battlefield трейлер");
        NewsRanker.React(profile, item, 1, Now);
        NewsRanker.React(profile, item, -1, Now);
        Assert.Equal(-1, profile.Reactions[item.Id].Value);
        Assert.True(profile.Terms["battlefield"] < 0);
    }

    [Fact]
    public void React_ZeroRemovesReaction()
    {
        var profile = new NewsProfile();
        var item = Item("1", "Battlefield трейлер");
        NewsRanker.React(profile, item, 1, Now);
        NewsRanker.React(profile, item, 0, Now);
        Assert.False(profile.Reactions.ContainsKey(item.Id));
    }

    [Fact]
    public void ActiveKeywords_TakesOnlyEnabledSubtopicsAndCustomWords()
    {
        var profile = new NewsProfile { ActiveSubtopics = ["g-shooters"], CustomKeywords = [" Трейлер ", "x"] };
        var keywords = NewsRanker.ActiveKeywords(profile);
        Assert.Contains("battlefield", keywords.Builtin);
        Assert.DoesNotContain("openai", keywords.Builtin);
        Assert.Equal(["трейлер"], keywords.Custom);
    }
}
