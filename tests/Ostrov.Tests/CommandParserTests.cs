using Ostrov.Modules.Player;
using Ostrov.Search;
using Ostrov.Services;
using Xunit;

namespace Ostrov.Tests;

public class CommandParserTests
{
    public CommandParserTests()
    {
        IslandCommands.StartTimer = null;
        IslandCommands.CancelTimer = null;
        IslandCommands.StartStopwatch = null;
        IslandCommands.StopStopwatch = null;
        IslandCommands.FindProject = null;
        IslandCommands.OpenProject = null;
        IslandBus.LastTab = null;
        MediaControl.LastAction = null;
        MediaControl.LastVolume = null;
    }

    [Theory]
    [InlineData("25", 25)]
    [InlineData("25 мин", 25)]
    [InlineData("1:30", 1.5)]
    [InlineData("1ч 30м", 90)]
    [InlineData("90 сек", 1.5)]
    [InlineData("1,5 часа", 90)]
    public void TryDuration_RecognizesCommonForms(string text, double minutes)
    {
        Assert.True(CommandParser.TryDuration(text, out var actual), text);
        Assert.Equal(TimeSpan.FromMinutes(minutes), actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1:2:3:4")]
    [InlineData("1 30")]
    [InlineData("пять минут")]
    public void TryDuration_RejectsInvalid(string text)
    {
        Assert.False(CommandParser.TryDuration(text, out _), text);
    }

    [Fact]
    public async Task TryParse_TimerMinutes_CreatesTimer()
    {
        TimeSpan? started = null;
        IslandCommands.StartTimer = span => started = span;
        var command = CommandParser.TryParse("таймер 25 минут");

        Assert.NotNull(command);
        Assert.Equal("Таймер 25 мин", command!.Label);
        Assert.Null(await command.Run());

        Assert.Equal(TimeSpan.FromMinutes(25), started);
    }

    [Fact]
    public async Task TryParse_TimerCancel()
    {
        var called = false;
        IslandCommands.CancelTimer = () => called = true;
        var command = CommandParser.TryParse("отмени таймер");
        Assert.NotNull(command);
        await command!.Run();
        Assert.True(called);
    }

    [Fact]
    public async Task TryParse_StopwatchAndMusic()
    {
        var stopwatchStarted = false;
        IslandCommands.StartStopwatch = () => stopwatchStarted = true;
        Assert.Null(await CommandParser.TryParse("секундомер")!.Run());
        Assert.True(stopwatchStarted);

        await CommandParser.TryParse("пауза")!.Run();
        Assert.Equal(MediaControl.Action.Pause, MediaControl.LastAction);
        await CommandParser.TryParse("играй")!.Run();
        Assert.Equal(MediaControl.Action.Play, MediaControl.LastAction);
        await CommandParser.TryParse("дальше")!.Run();
        Assert.Equal(MediaControl.Action.Next, MediaControl.LastAction);
        await CommandParser.TryParse("назад")!.Run();
        Assert.Equal(MediaControl.Action.Previous, MediaControl.LastAction);
        await CommandParser.TryParse("громкость 30")!.Run();
        Assert.Equal(30, MediaControl.LastVolume);
    }

    [Fact]
    public async Task TryParse_OpenTab()
    {
        var command = CommandParser.TryParse("открой погоду");
        Assert.NotNull(command);
        Assert.True(command!.AfterClose);
        await command.Run();
        Assert.Equal("weather", IslandBus.LastTab);

        Assert.NotNull(CommandParser.TryParse("вкладка настройки"));
    }

    [Fact]
    public async Task TryParse_OpenProject()
    {
        IslandCommands.FindProject = name => name == "ostrov" ? ("Ostrov", @"C:\projects\ostrov") : null;
        string? opened = null;
        IslandCommands.OpenProject = path => { opened = path; return null; };

        var command = CommandParser.TryParse("открой ostrov");
        Assert.NotNull(command);
        Assert.Null(await command!.Run());
        Assert.Equal(@"C:\projects\ostrov", opened);
    }

    [Fact]
    public void TryParse_NonCommand_ReturnsNull()
    {
        Assert.Null(CommandParser.TryParse("что такое WebSocket"));
        Assert.Null(CommandParser.TryParse(""));
        Assert.Null(CommandParser.TryParse("погода завтра"));
    }

    [Fact]
    public async Task TryParse_MissingAction_ReturnsError()
    {
        var command = CommandParser.TryParse("таймер 25");
        Assert.NotNull(command);
        Assert.Equal("Функция недоступна", await command!.Run());
    }

    [Fact]
    public void FormatSpan_FormatsMinutesAndHours()
    {
        Assert.Equal("25 мин", CommandParser.FormatSpan(TimeSpan.FromMinutes(25)));
        Assert.Equal("1 ч 30 мин", CommandParser.FormatSpan(TimeSpan.FromMinutes(90)));
        Assert.Equal("45 сек", CommandParser.FormatSpan(TimeSpan.FromSeconds(45)));
    }
}
