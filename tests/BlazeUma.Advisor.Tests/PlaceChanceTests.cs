using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

public class PlaceChanceTests
{
    private static readonly RaceProgram JuniorOp = new(900, 0, "Junior OP", 400, 1600, Ground.Turf, 9, 1, 1, 0, 1200);
    private static readonly RaceProgram SeniorG1 = FakeGameData.TennoShoAutumn with { Permission = 4 };

    private static RaceEvaluator.Placing Placing(RaceProgram race, int turn, Stats stats, Mood mood = Mood.Normal) =>
        RaceEvaluator.PlacingFor(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(stats: stats, mood: mood), turn: turn), new FakeGameData()), race);

    [Fact]
    public void StrongerStats_RaiseTheChance()
    {
        var weak = Placing(FakeGameData.Niigata, 16, new Stats(200, 120, 140, 140, 110));
        var strong = Placing(FakeGameData.Niigata, 16, new Stats(260, 180, 180, 150, 150));

        Assert.True(strong.Rating > weak.Rating);
        Assert.True(strong.Win > weak.Win + 0.2, $"{strong} vs {weak}");
        Assert.True(strong.Top3 > weak.Top3, $"{strong} vs {weak}");
        Assert.True(strong.Top3 >= strong.Win && strong.Top5 >= strong.Top3);
    }

    [Fact]
    public void StaminaShortfallAt2500m_LowersIt()
    {
        var enough = Placing(FakeGameData.ArimaKinen, 72, new Stats(560, 950, 360, 350, 320));
        var short_ = Placing(FakeGameData.ArimaKinen, 72, new Stats(560, 300, 360, 350, 320));

        Assert.True(short_.StaminaNeeded > 300);
        Assert.True(short_.Win < enough.Win - 0.2, $"{short_} vs {enough}");
        Assert.True(short_.Top3 < enough.Top3, $"{short_} vs {enough}");
    }

    [Fact]
    public void SeniorG1_IsHarderThanJuniorOp()
    {
        var stats = new Stats(400, 300, 300, 250, 250);
        var op = Placing(JuniorOp, 17, stats);
        var g1 = Placing(SeniorG1, 68, stats);

        Assert.True(g1.Field > op.Field * 2, $"{g1.Field} vs {op.Field}");
        Assert.True(g1.Win < op.Win - 0.3, $"{g1} vs {op}");
    }

    [Fact]
    public void TheLoggedResults_AreRankedLikeTheyFinished()
    {
        // From the turn logs. Wakagoma Stakes (career-100901): Classic OP, won.
        var wakagoma = new RaceProgram(195, 0, "Wakagoma Stakes", 400, 2000, Ground.Turf, 1, 2, 2, 0, 2000);
        Assert.InRange(Placing(wakagoma, 26, new Stats(301, 152, 174, 233, 161), Mood.Great).Win, 0.55, 0.95);

        // Kikuka Sho (career-106802, turn 44): 3000 m G1 run ~80% short of a full spurt, 3rd. It read as a 99% win.
        var kikuka = new RaceProgram(160, 0, "Kikuka Sho", 100, 3000, Ground.Turf, 10, 2, 2, 0, 20000);
        var shortStamina = Placing(kikuka, 44, new Stats(643, 317, 342, 230, 402), Mood.Good);
        Assert.True(shortStamina.Win < 0.4, shortStamina.ToString());
        Assert.InRange(shortStamina.Top3, 0.4, 0.85);

        // Japan Cup (career-106802, turn 70): 2400 m G1 ~27% short, won. Still a strong favourite, but never certain.
        var japanCup = new RaceProgram(161, 0, "Japan Cup", 100, 2400, Ground.Turf, 11, 2, 3, 0, 30000);
        var ready = Placing(japanCup, 70, new Stats(820, 584, 494, 389, 435), Mood.Great);
        Assert.InRange(ready.Win, 0.75, RaceEvaluator.Placing.UpsetCap.Win);
    }

    [Fact]
    public void BadMood_LowersTheChance() =>
        Assert.True(Placing(FakeGameData.Niigata, 16, new Stats(300, 220, 200, 150, 180), Mood.Bad).Win
            < Placing(FakeGameData.Niigata, 16, new Stats(300, 220, 200, 150, 180)).Win);

    [Fact]
    public void FanShare_FollowsTheGamesPlaceTable()
    {
        Assert.Equal(1.0, new RaceEvaluator.Placing(0, 0, 1, 1, 1, 0).FanShare, 3);
        // Always 2nd or 3rd: the mean of 40% and 25%.
        Assert.Equal(0.325, new RaceEvaluator.Placing(0, 0, 0, 1, 1, 0).FanShare, 3);
        Assert.Equal(0.039, new RaceEvaluator.Placing(0, 0, 0, 0, 0, 0).FanShare, 3);
    }

    [Fact]
    public void Evaluate_ExplainsTheChance()
    {
        var ctx = new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(stats: new Stats(300, 220, 200, 150, 180)), turn: 16), new FakeGameData());
        var race = new RaceEvaluator(ctx, new ValueModel(ctx)).Evaluate(FakeGameData.Niigata);
        Assert.Contains(race.Reasons, r => r.StartsWith("win ~") && r.Contains("top 3 ~") && r.Contains("vs field"));
    }
}
