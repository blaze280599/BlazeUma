using BlazeUma.Data;

namespace BlazeUma.Advisor.Tests;

/// <summary>Integration checks against the installed master.mdb; they pass trivially when the game is not installed.</summary>
public sealed class MasterDatabaseTests : IDisposable
{
    private const string GameDir = @"C:\Program Files (x86)\Steam\steamapps\common\UmamusumePrettyDerby";
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "BlazeUmaTests-" + Guid.NewGuid().ToString("N"));
    private readonly MasterDatabase? _db;

    public MasterDatabaseTests()
    {
        string path = MasterDatabase.PathInGameDirectory(GameDir);
        if (File.Exists(path)) _db = MasterDatabase.Open(path, _cache);
    }

    [Fact]
    public void DaiwaScarletGoals_ResolveToRaces()
    {
        if (_db is null) return;
        var goals = _db.Goals([22, 23, 24, 30, 31]);
        Assert.Equal(["Junior Make Debut", "Tulip Sho", "Oka Sho", "Arima Kinen", "URA Finale Qualifier"], goals.Select(g => g.Name));
        Assert.Equal(1750, goals[1].Race!.NeedFans);
        Assert.Equal(2500, goals[3].Race!.Distance);
        Assert.True(goals[3].MustWin);
        Assert.Null(goals[4].Race);
    }

    [Fact]
    public void Turn14_HasHakodateAndChukyoJuniorStakes()
    {
        if (_db is null) return;
        var names = _db.ProgramsForTurn(14).Where(p => p.Grade != (int)RaceGrade.Maiden).Select(p => p.Name).ToList();
        Assert.Equal(["Hakodate Junior Stakes", "Chukyo Junior Stakes"], names);
        Assert.Equal(3100, _db.ProgramsForTurn(14).First().FirstPlaceFans);
    }

    [Fact]
    public void Skills_AndHints_Resolve()
    {
        if (_db is null) return;
        Assert.Equal(160, _db.Skill(200712)!.BaseCost);
        Assert.Equal("Rosy Outlook", _db.SkillForHint(20071, 1)!.Name);
        // Awakening level 1: Competitive Spirit ○, Preferred Position, Up-Tempo (confirmed on the skill screen).
        Assert.Equal([200282, 200572, 200722], _db.BaseSkillIds(100901, 1).Order());
    }

    [Fact]
    public void Text_Conditions_AndCalendar()
    {
        if (_db is null) return;
        Assert.Equal("Get Well Soon!", _db.StoryTitle(501009713));
        Assert.True(_db.Condition(6)!.IsBad);
        Assert.False(_db.Condition(10)!.IsBad);
        Assert.Equal(new TurnDate(13, 1, 7, 1), _db.Turn(13));
    }

    public void Dispose()
    {
        _db?.Dispose();
        try { Directory.Delete(_cache, true); } catch (IOException) { }
    }
}
