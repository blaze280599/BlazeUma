namespace BlazeUma.Data;

/// <summary>
/// Season, weather and ground condition odds for a race (skill_data season / weather / ground_condition values),
/// from master.mdb's season_data and race_condition tables.
/// </summary>
public static class RaceConditions
{
    /// <summary>
    /// season_data types 1–3 as (season, first MMDD, last MMDD); 1 spring, 2 summer, 3 autumn, 4 winter,
    /// 5 cherry blossom. Which type a race uses isn't known, so a date counts toward each season by the share of
    /// types that put it there (Tulip Sho on 03-04 is spring in type 1, winter in types 2 and 3).
    /// </summary>
    private static readonly (int Season, int From, int To)[][] SeasonTypes =
    [
        [(1, 421, 626), (2, 627, 917), (3, 918, 1130), (4, 1201, 1231), (4, 101, 229), (1, 301, 319), (5, 320, 420)],
        [(1, 421, 531), (2, 601, 930), (3, 1001, 1130), (4, 1201, 1231), (4, 101, 319), (5, 320, 420)],
        [(1, 421, 630), (2, 701, 930), (3, 1001, 1130), (4, 1201, 1231), (4, 101, 319), (5, 320, 420)],
    ];

    /// <summary>race_condition (area 0, the only one): rate per (season, weather, ground condition), out of 1e8.</summary>
    private static readonly IReadOnlyDictionary<int, (int Weather, int Ground, int Rate)[]> Rates = new Dictionary<int, (int, int, int)[]>
    {
        [1] = Mild, [2] = Mild, [3] = Mild, [5] = Mild,
        [4] = [(1, 1, 50050000), (1, 2, 4950000), (2, 1, 24960000), (2, 2, 7040000), (3, 3, 4400000), (3, 4, 3600000),
               (4, 2, 150000), (4, 3, 4850000)],
    };

    private static (int, int, int)[] Mild => [(1, 1, 54600000), (1, 2, 5400000), (2, 1, 23400000), (2, 2, 6600000), (3, 3, 5500000), (3, 4, 4500000)];

    /// <summary>Share of season types that put <paramref name="mmdd"/> in <paramref name="season"/>; equal odds for the four main seasons when the date is unknown.</summary>
    public static double SeasonShare(int? mmdd, int season)
    {
        if (mmdd is not { } d) return season is >= 1 and <= 4 ? 0.25 : 0;
        return SeasonTypes.Count(t => t.Any(r => r.Season == season && d >= r.From && d <= r.To)) / (double)SeasonTypes.Length;
    }

    /// <summary>Chance of <paramref name="weather"/> (1 sunny, 2 cloudy, 3 rainy, 4 snowy) on a race held on <paramref name="mmdd"/>.</summary>
    public static double WeatherShare(int? mmdd, int weather) => Share(mmdd, r => r.Weather == weather);

    /// <summary>Chance of ground condition <paramref name="ground"/> (1 firm, 2 good, 3 soft, 4 heavy) on a race held on <paramref name="mmdd"/>.</summary>
    public static double GroundShare(int? mmdd, int ground) => Share(mmdd, r => r.Ground == ground);

    private static double Share(int? mmdd, Func<(int Weather, int Ground, int Rate), bool> match) =>
        Rates.Sum(s => SeasonShare(mmdd, s.Key) * s.Value.Where(match).Sum(r => r.Rate) / 1e8);
}
