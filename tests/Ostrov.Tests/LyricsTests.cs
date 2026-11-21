using Ostrov.Modules.Player.Lyrics;
using Xunit;

namespace Ostrov.Tests;

public class LyricsTests
{
    [Fact]
    public void TrackKey_FromMedia_SplitsArtistAndAlbum()
    {
        var key = TrackKey.FromMedia("Песня", "Исполнитель — Альбом", "", TimeSpan.FromSeconds(180));
        Assert.Equal("Песня", key.Title);
        Assert.Equal("Исполнитель", key.Artist);
        Assert.Equal("Альбом", key.Album);
        Assert.Equal(180, key.DurationSeconds);
    }

    [Fact]
    public void TrackKey_FromMedia_ExplicitAlbumWins()
    {
        var key = TrackKey.FromMedia("Песня", "Исполнитель — Старый альбом", "Новый альбом", TimeSpan.FromSeconds(181));
        Assert.Equal("Исполнитель", key.Artist);
        Assert.Equal("Новый альбом", key.Album);
    }

    [Fact]
    public void TrackKey_Id_IsStableAndCaseInsensitive()
    {
        var a = TrackKey.FromMedia("Песня", "Исполнитель", "Альбом", TimeSpan.FromSeconds(180));
        var b = TrackKey.FromMedia("песня", "исполнитель", "альбом", TimeSpan.FromSeconds(180));
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void LrcParser_ParsesTimestampsAndExtraBlankLines()
    {
        var parsed = LrcParser.ParseSynced("[00:12.50]Первая строка\n\n[00:14.00]Вторая [00:15.00]третья строка");
        Assert.Equal(3, parsed.Count);
        Assert.Equal(TimeSpan.FromSeconds(12.5), parsed[0].Time);
        Assert.Equal("Первая строка", parsed[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(14), parsed[1].Time);
        Assert.Equal("Вторая третья строка", parsed[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(15), parsed[2].Time);
        Assert.Equal("Вторая третья строка", parsed[2].Text);
    }

    [Fact]
    public void LrcParser_IgnoresMetadataAndSortsByTime()
    {
        var parsed = LrcParser.ParseSynced("[ar:Исполнитель]\n[01:00]Вторая\n[00:10]Первая");
        Assert.Equal(2, parsed.Count);
        Assert.Equal("Первая", parsed[0].Text);
        Assert.Equal("Вторая", parsed[1].Text);
    }

    [Fact]
    public void LrcParser_ParsesPlainText()
    {
        var parsed = LrcParser.ParsePlain("Первая\n  \nВторая");
        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, line => Assert.Equal(TimeSpan.Zero, line.Time));
    }

    [Fact]
    public void LyricsfileParser_ParsesWordTimings()
    {
        const string yaml = """
            lines:
              - text: "Привет мир"
                start_ms: 1000
                end_ms: 2500
                words:
                  - text: "Привет"
                    start_ms: 1000
                  - text: "мир"
                    start_ms: 1800
            """;
        var parsed = LyricsfileParser.Parse(yaml);
        Assert.NotNull(parsed);
        var line = Assert.Single(parsed);
        Assert.Equal("Привет мир", line.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), line.Time);
        Assert.NotNull(line.Words);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal("Привет", line.Words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), line.Words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1800), line.Words[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), line.Words[1].End);
    }

    [Fact]
    public void LyricsfileParser_ReturnsNullOnGarbage()
    {
        Assert.Null(LyricsfileParser.Parse("not yaml at all"));
    }
}
