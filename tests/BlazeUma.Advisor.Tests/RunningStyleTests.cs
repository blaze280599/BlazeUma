using BlazeUma.Data;
using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor.Tests;

/// <summary>The running style the advisor races with, and its note when a better-graded style is available.</summary>
public class RunningStyleTests
{
    // Daiwa: Front A, Pace A, Late E, End G.
    private static CareerContext Context(int style, Aptitudes? aptitudes = null, RunningStyle? cardStyle = null)
    {
        var daiwa = Snapshots.Daiwa();
        return new(Snapshots.Snapshot(daiwa with { RunningStyle = style, Aptitudes = aptitudes ?? daiwa.Aptitudes }),
            new FakeGameData { CardStyle = cardStyle });
    }

    [Fact]
    public void SetStyle_IsUsed()
    {
        Assert.Equal(RunningStyle.LateSurger, Context(3).Style);
    }

    [Fact]
    public void UnreadStyle_FallsBackToBestGrade()
    {
        var frontOnly = new Aptitudes(7, 2, 3, 7, 7, 7, 7, 6, 3, 1);   // Kitasan Black: Front A, Pace B
        Assert.Equal(RunningStyle.FrontRunner, Context(0, frontOnly).Style);
        Assert.Equal(RunningStyle.PaceChaser, Context(0).Style);        // Front A = Pace A: ties go to Pace
    }

    [Fact]
    public void BestStyle_PrefersTheCardDefault_UnlessOutgraded()
    {
        Assert.Equal(RunningStyle.FrontRunner, Context(0, cardStyle: RunningStyle.FrontRunner).Style);   // tie: card wins
        Assert.Equal(RunningStyle.PaceChaser, Context(0, cardStyle: RunningStyle.LateSurger).Style);     // Late E < Pace A
    }

    [Fact]
    public void Race_NotesBetterGradedStyle()
    {
        var late = Context(3);
        var reasons = new RaceEvaluator(late, new ValueModel(late)).Evaluate(FakeGameData.Niigata).Reasons;
        Assert.Contains(reasons, r => r.Contains("set to Late (E); Pace (A) would race better"));

        var pace = Context(2);
        Assert.DoesNotContain(new RaceEvaluator(pace, new ValueModel(pace)).Evaluate(FakeGameData.Niigata).Reasons,
            r => r.Contains("would race better"));
    }
}
