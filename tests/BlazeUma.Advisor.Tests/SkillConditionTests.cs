using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>
/// Skill conditions on the race itself (course, direction, season, weather, ground) are scored against the
/// remaining goal races. On turn 14 Daiwa Scarlet has five left: Tulip Sho (Hanshin, right, 03-04), Shuka Sho
/// (Kyoto, right, 10-13), Tenno Sho Autumn (Tokyo, left, 10-29), Arima Kinen (Nakayama, right, 12-22) and the
/// URA Finale, whose course is unknown.
/// </summary>
public class SkillConditionTests
{
    private static SkillAdvisor Advisor(int turn = 14) =>
        new(new CareerContext(Snapshots.Snapshot(Snapshots.Daiwa(), turn: turn), new FakeGameData()));

    [Fact]
    public void Racecourse_CountsTheGoalsRunThere()
    {
        var advisor = Advisor();
        // Kyoto: Shuka Sho only, plus the finale's 1-in-10 guess.
        Assert.Equal((1 + SkillAdvisor.UnknownTrackFit) / 5, advisor.Fit("track_id==10008"), 3);
        Assert.Equal(SkillAdvisor.UnknownTrackFit / 5, advisor.Fit("track_id==10001"), 3);   // Sapporo: no goal there
        Assert.Equal(1, advisor.Fit("track_id==10005", FakeGameData.ArimaKinen));
        Assert.Equal(0, advisor.Fit("track_id==10008", FakeGameData.ArimaKinen));
    }

    [Fact]
    public void Direction_CountsRightAndLeftHandedGoals()
    {
        var advisor = Advisor();
        Assert.Equal((3 + 0.5) / 5, advisor.Fit("rotation==1"), 3);   // Tenno Sho Autumn is the only left-handed one
        Assert.Equal((1 + 0.5) / 5, advisor.Fit("rotation==2"), 3);
    }

    [Fact]
    public void Weather_UsesTheSeasonsOdds()
    {
        var advisor = Advisor();
        // Arima Kinen (12-22) is winter in every season table: rain 8%, snow 5%.
        Assert.Equal(0.08, advisor.Fit("weather==3", FakeGameData.ArimaKinen), 3);
        Assert.Equal(0.05, advisor.Fit("weather==4", FakeGameData.ArimaKinen), 3);
        Assert.Equal(0.10, advisor.Fit("weather==3", FakeGameData.ShukaSho), 3);   // autumn
        Assert.True(advisor.Fit("weather==3") < 0.15);   // Rainy Days ○ is far from a sure thing
    }

    [Fact]
    public void ExclusiveAlternatives_AddUp()
    {
        var advisor = Advisor();
        // Wet Conditions: good, soft or heavy ground. Arima Kinen in winter: 12.14% + 9.25% + 3.6%.
        Assert.Equal(0.2499, advisor.Fit("ground_condition==2@ground_condition==3@ground_condition==4", FakeGameData.ArimaKinen), 3);
        // Spring Runner: spring or cherry blossom. Tulip Sho on 03-04 is spring in one season table of three.
        Assert.Equal(1 / 3.0, advisor.Fit("season==1@season==5", FakeGameData.TulipSho), 3);
        Assert.Equal(0, advisor.Fit("season==1@season==5", FakeGameData.ArimaKinen));
    }

    [Fact]
    public void StandardDistance_AndGrade_FollowTheRace()
    {
        var advisor = Advisor();
        Assert.Equal(1, advisor.Fit("is_basis_distance==1", FakeGameData.ShukaSho));   // 2000 m
        Assert.Equal(0, advisor.Fit("is_basis_distance==1", FakeGameData.ArimaKinen)); // 2500 m
        Assert.Equal(1, advisor.Fit("grade==100", FakeGameData.ArimaKinen));
        Assert.Equal(0, advisor.Fit("grade==100", FakeGameData.TulipSho));             // G2
        Assert.Equal(0, advisor.Fit("time==4"));                                       // no night race among the goals
    }

    [Fact]
    public void CornerAndPhase_StayCertain()
    {
        var advisor = Advisor();
        Assert.Equal(1, advisor.Fit("corner==0"));
        Assert.Equal(1, advisor.Fit("phase==1&is_finalcorner==1"));
    }

    [Fact]
    public void CourseSkill_ScoresBelowAGeneralOne()
    {
        var advisor = Advisor();
        var kyoto = new SkillInfo(200062, "Kyoto Racecourse ○", 1, 20006, 129, 90, "track_id==10008", 27);
        var anyCourse = kyoto with { Id = 1, Name = "general", Condition = "" };
        Assert.True(advisor.Score(kyoto, 90).Value < 0.3 * advisor.Score(anyCourse, 90).Value);
    }

    [Fact]
    public void Fit_WithoutGoalsLeft_UsesUnknownRaceOdds()
    {
        var advisor = Advisor(turn: 78);
        Assert.Equal(0.5, advisor.Fit("rotation==1"));
        Assert.Equal(SkillAdvisor.UnknownTrackFit, advisor.Fit("track_id==10008"));
    }
}
