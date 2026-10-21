using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class TurnRecorderTests
{
    private static Advice Home(string summary = "Train Speed") => new(Situation.TrainingTurn, "h", summary, [], [], []);
    private static Advice EventScreen() => new(Situation.Event, "h", "Event: Top choice", [], [], []);

    private static CareerSnapshot At(int turn, Character c, int lastTraining = 101, StoryEvent? story = null) =>
        Snapshots.Snapshot(c, turn: turn, lastTraining: lastTraining, story: story);

    [Fact]
    public void Compare_MeasuresChanges_AndInfersTraining()
    {
        var before = Snapshots.Daiwa(energy: 80, mood: Mood.Normal);
        var after = before with { Stats = before.Stats with { Speed = 212, Power = 146 }, Energy = 59, SkillPoints = 204 };

        var outcome = TurnRecorder.Compare(At(14, before, lastTraining: 106), At(15, after, lastTraining: 101));

        Assert.Equal("training:101", outcome.InferredAction);
        Assert.Equal(12, outcome.StatChanges["speed"]);
        Assert.Equal(6, outcome.StatChanges["power"]);
        Assert.False(outcome.StatChanges.ContainsKey("stamina"));
        Assert.Equal((80, 59, 4), (outcome.EnergyBefore, outcome.EnergyAfter, outcome.SkillPointChange));
    }

    [Fact]
    public void Infers_Outing_Race_AndRest()
    {
        var c = Snapshots.Daiwa(energy: 50, mood: Mood.Bad, lightHelloOuting: true);
        var outing = c with
        {
            Mood = Mood.Normal, Energy = 70,
            Bonds = c.Bonds.Select(b => b.TargetId == 6 ? b with { Value = b.Value + 5, OutingStep = 1 } : b).ToList(),
        };
        var outcome = TurnRecorder.Compare(At(14, c), At(15, outing));
        Assert.Equal("outing:9008", outcome.InferredAction);
        Assert.Contains("9008:+5", outcome.BondChanges);
        Assert.Contains("9008:outing 0->1", outcome.BondChanges);

        Assert.Equal("race", TurnRecorder.InferAction(At(16, c), At(17, c with { Fans = c.Fans + 3100 })));
        Assert.Equal("rest", TurnRecorder.InferAction(At(16, c), At(17, c with { Energy = 100 })));
    }

    [Fact]
    public void Observe_WritesOneTurnRecordPerCompletedTurn_AndEventsOnce()
    {
        var recorder = new TurnRecorder();
        var c = Snapshots.Daiwa();
        var story = new StoryEvent(809008004, 7501, 0, 9008, 10002, true);

        Assert.Empty(recorder.Observe(At(14, c), Home()));                 // first turn start
        Assert.Empty(recorder.Observe(At(14, c), Home()));                 // same turn, no record
        var evt = recorder.Observe(At(14, c, story: story), EventScreen());
        Assert.Equal("event", Assert.Single(evt).Kind);
        Assert.Empty(recorder.Observe(At(14, c, story: story), EventScreen()));   // same event again

        var done = recorder.Observe(At(15, c with { Energy = 100 }), Home("Rest"));
        var record = Assert.Single(done);
        Assert.Equal(("turn", 14, "Train Speed"), (record.Kind, record.Turn, record.Summary));
        Assert.Equal(15, record.Outcome!.NextTurn);
    }

    [Fact]
    public void Observe_DetectsNewCareer()
    {
        var recorder = new TurnRecorder();
        recorder.Observe(At(40, Snapshots.Daiwa()), Home());
        Assert.Empty(recorder.Observe(At(1, Snapshots.Daiwa()), Home()));
        Assert.True(recorder.NewCareer);
    }
}
