using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>The trainee card's stat growth bonuses on values built from base training gains.</summary>
public class GrowthRateTests
{
    private static ValueModel Values(Dictionary<int, int> growth)
    {
        var data = new FakeGameData();
        foreach (var (param, pct) in growth) data.Growth[param] = pct;
        return new ValueModel(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa()), data));
    }

    [Fact]
    public void Growth_RaisesFriendshipValueOnTheBoostedTraining_Only()
    {
        var plain = Values([]);
        var speedy = Values(new() { [ParamIds.Speed] = 20 });

        double speed = plain.FriendshipTrainingExtra(TrainingIds.Speed, 30);
        double boosted = speedy.FriendshipTrainingExtra(TrainingIds.Speed, 30);
        Assert.True(boosted > speed * 1.1, $"{boosted} vs {speed}");
        Assert.True(boosted < speed * 1.2);   // the secondary Power gain isn't boosted
        Assert.Equal(plain.FriendshipTrainingExtra(TrainingIds.Stamina, 30), speedy.FriendshipTrainingExtra(TrainingIds.Stamina, 30), 6);   // no Speed in it
    }

    [Fact]
    public void Growth_RaisesCampPremium()
    {
        Assert.True(Values(new() { [ParamIds.Speed] = 20 }).CampTrainingPremium() > Values([]).CampTrainingPremium());
    }
}
