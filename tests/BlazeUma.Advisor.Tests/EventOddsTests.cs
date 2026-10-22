using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class EventOddsTests
{
    private readonly FakeGameData _data = new();

    private static EventEffect Param(int param, int amount) => new(1, param, amount, 0);

    /// <summary>One choice: +5 speed or +30 speed; the other: a sure +15 speed.</summary>
    private static readonly List<EventChoice> Gamble =
    [
        new(1, [[Param(ParamIds.Speed, 5)], [Param(ParamIds.Speed, 30)]]),
        new(2, [[Param(ParamIds.Speed, 15)]]),
    ];

    private IReadOnlyList<ScoredOption> Rank(int storyId, EventOdds odds, IReadOnlyList<EventChoice>? choices = null)
    {
        choices ??= Gamble;
        var story = new StoryEvent(storyId, 1, 1009, 0, 1, LooksActive: true);
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(), story: story, choices: choices), _data);
        return new EventAdvisor(ctx, new ValueModel(ctx), odds).Rank(choices, story);
    }

    private static EventOdds Logged(int storyId, int choice, params int[] counts)
    {
        var odds = new EventOdds();
        for (int o = 0; o < counts.Length; o++)
            for (int k = 0; k < counts[o]; k++)
                odds.Add(new EventResult(storyId, choice, o + 1, 0, null!));
        return odds;
    }

    [Fact]
    public void LearnedOdds_AboveThreshold_AreUsed()
    {
        // 8 of 9 results were the +5: smoothed (8+1)/(9+2) ≈ 82%, so the sure +15 wins.
        var ranked = Rank(830_001_001, Logged(830_001_001, 1, 8, 1));
        Assert.Equal("Bottom choice", ranked[0].Label);
        var top = ranked.Single(o => o.Label == "Top choice");
        Assert.Contains("odds from 9 logged results", top.Reasons);
        Assert.StartsWith("82%", top.Reasons[1]);
    }

    [Fact]
    public void LearnedOdds_BelowThreshold_AreIgnored()
    {
        var top = Rank(830_001_001, Logged(830_001_001, 1, 4, 0)).Single(o => o.Label == "Top choice");
        Assert.Contains("odds unknown; outcomes weighted equally", top.Reasons);
        Assert.DoesNotContain(top.Reasons, r => r.StartsWith("odds from"));
        Assert.StartsWith("50%", top.Reasons[0]);
    }

    [Fact]
    public void TraineeEvents_PoolAcrossTrainees()
    {
        // Logged on another trainee's copy of the same event (501 + chara + event).
        var top = Rank(501_009_801, Logged(501_042_801, 1, 6, 0)).Single(o => o.Label == "Top choice");
        Assert.Contains("odds from 6 logged results", top.Reasons);
    }

    [Fact]
    public void HardCodedOdds_TakePriority()
    {
        // Get Well Soon! (713): the table says 92/8 for the top choice; the logs say the opposite.
        var choices = new List<EventChoice>
        {
            new(1, [[Param(ParamIds.Speed, 5)], [Param(ParamIds.Speed, 30)]]),
            new(2, [[Param(ParamIds.Speed, 15)], [Param(ParamIds.Speed, 5)], [Param(ParamIds.Speed, 1)]]),
        };
        var top = Rank(501_009_713, Logged(501_009_713, 1, 0, 20), choices).Single(o => o.Label == "Top choice");
        Assert.StartsWith("92%", top.Reasons[0]);
        Assert.DoesNotContain(top.Reasons, r => r.StartsWith("odds from"));
    }

    [Fact]
    public void Counts_RoundTripThroughFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"event-odds-{Guid.NewGuid():N}.json");
        try
        {
            Logged(501_009_801, 2, 3, 4).Save(path);
            var loaded = EventOdds.Load(path);
            Assert.Equal([3, 4], loaded.Counts[501_000_801][2]);
            Assert.Empty(EventOdds.Load(path + ".missing").Counts);
        }
        finally { File.Delete(path); }
    }
}
