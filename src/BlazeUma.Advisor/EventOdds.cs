using System.Text.Json;

namespace BlazeUma.Advisor;

/// <summary>
/// Event outcome counts learned from career logs ("event-result" records): story key → choice → count per
/// outcome (index 0 = outcome 1). Trainee events are pooled across trainees by <see cref="Key"/>. Saved as
/// data/event-odds.json next to the exe; rebuild it with <c>BlazeUma event-odds &lt;logs&gt;...</c>.
/// </summary>
public sealed class EventOdds
{
    /// <summary>Fewest logged results for a choice before its learned odds replace equal weights.</summary>
    public const int MinSamples = 5;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Lazy<EventOdds> Shipped = new(() => Load(DefaultPath));

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "event-odds.json");

    /// <summary>The odds file shipped beside the exe, or none if it is missing or unreadable.</summary>
    public static EventOdds Default => Shipped.Value;

    public Dictionary<int, Dictionary<int, int[]>> Counts { get; }

    public EventOdds(Dictionary<int, Dictionary<int, int[]>>? counts = null) => Counts = counts ?? [];

    /// <summary>
    /// Trainee story ids are 501 + chara + event; their events share the last three digits across trainees (as
    /// <see cref="EventAdvisor"/>'s hard-coded table does), so they pool as 501_000_000 + event. Other ids as is.
    /// </summary>
    public static int Key(int storyId) => storyId / 1_000_000 == 501 ? 501_000_000 + storyId % 1000 : storyId;

    public void Add(EventResult result)
    {
        if (result is not { Choice: int choice, Outcome: int outcome } || outcome < 1) return;
        var choices = Counts.TryGetValue(Key(result.StoryId), out var c) ? c : Counts[Key(result.StoryId)] = [];
        int[] counts = choices.GetValueOrDefault(choice) ?? [];
        if (counts.Length < outcome) Array.Resize(ref counts, outcome);
        counts[outcome - 1]++;
        choices[choice] = counts;
    }

    /// <summary>
    /// Laplace-smoothed odds for a choice with <paramref name="outcomes"/> outcomes, and how many results they
    /// rest on; null below <see cref="MinSamples"/> or when the counts name more outcomes than the preview shows.
    /// </summary>
    public (double[] Odds, int Samples)? For(int storyId, int choice, int outcomes)
    {
        if (!Counts.TryGetValue(Key(storyId), out var choices) || !choices.TryGetValue(choice, out var counts)) return null;
        int n = counts.Sum();
        if (n < MinSamples || counts.Length > outcomes) return null;
        return (Enumerable.Range(0, outcomes).Select(o => (counts.ElementAtOrDefault(o) + 1.0) / (n + outcomes)).ToArray(), n);
    }

    public static EventOdds Load(string path)
    {
        try { return new EventOdds(JsonSerializer.Deserialize<Dictionary<int, Dictionary<int, int[]>>>(File.ReadAllText(path))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new EventOdds(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var sorted = Counts.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value.OrderBy(c => c.Key).ToDictionary(c => c.Key, c => c.Value));
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, Json) + Environment.NewLine);
    }
}
