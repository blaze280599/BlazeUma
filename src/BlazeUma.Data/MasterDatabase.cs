using Microsoft.Data.Sqlite;

namespace BlazeUma.Data;

/// <summary>
/// Read-only access to the game's master.mdb (plain SQLite). Works on a private copy so the game can keep
/// updating its own file; the copy is refreshed whenever the source changes.
/// </summary>
public sealed class MasterDatabase : IGameData, IDisposable
{
    private const int TurnSetUra = 1;
    private const int TextCharacter = 6, TextSkill = 47, TextSupportCard = 75,
        TextCondition = 142, TextStoryTitle = 181, TextEventEffect = 394;

    private readonly SqliteConnection _db;
    private readonly Dictionary<(int, int), string?> _text = [];
    private readonly Dictionary<int, RaceProgram?> _programs = [];
    private readonly Dictionary<int, SkillInfo?> _skills = [];

    public string SourcePath { get; }

    private MasterDatabase(string sourcePath, string workingCopy)
    {
        SourcePath = sourcePath;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workingCopy,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        _db.Open();
    }

    /// <summary>master.mdb inside a game install directory (the folder containing GameAssembly.dll).</summary>
    public static string PathInGameDirectory(string gameDirectory) =>
        Path.Combine(gameDirectory, "UmamusumePrettyDerby_Data", "Persistent", "master", "master.mdb");

    public static MasterDatabase Open(string sourcePath, string? cacheDirectory = null)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("master.mdb not found", sourcePath);
        cacheDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazeUma");
        Directory.CreateDirectory(cacheDirectory);
        string copy = Path.Combine(cacheDirectory, "master.mdb");
        var source = new FileInfo(sourcePath);
        var cached = new FileInfo(copy);
        if (!cached.Exists || cached.Length != source.Length || cached.LastWriteTimeUtc < source.LastWriteTimeUtc)
        {
            // FileShare.ReadWrite: the game may hold the file open.
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(copy, FileMode.Create, FileAccess.Write);
            input.CopyTo(output);
        }
        return new MasterDatabase(sourcePath, copy);
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private string? Text(int category, int index)
    {
        if (_text.TryGetValue((category, index), out var cached)) return cached;
        using var command = Command("select text from text_data where category = $c and \"index\" = $i", ("$c", category), ("$i", index));
        return _text[(category, index)] = command.ExecuteScalar() as string;
    }

    public TurnDate Turn(int turn)
    {
        var rows = Query("select year, month, half from single_mode_turn where turn_set_id = $s and turn = $t",
            r => new TurnDate(turn, r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)), ("$s", TurnSetUra), ("$t", turn));
        return rows.Count > 0 ? rows[0] : throw new ArgumentOutOfRangeException(nameof(turn), $"No calendar entry for turn {turn}.");
    }

    private const string ProgramSelect = """
        select p.id, p.race_instance_id, coalesce(t.text, ''), r.grade, cs.distance, cs.ground,
               p.month, p.half, p.race_permission, p.need_fan_count,
               coalesce((select fan_count from single_mode_fan_count f where f.fan_set_id = p.fan_set_id and f."order" = 1), 0)
        from single_mode_program p
        join race_instance ri on ri.id = p.race_instance_id
        join race r on r.id = ri.race_id
        join race_course_set cs on cs.id = r.course_set
        left join text_data t on t.category = 28 and t."index" = p.race_instance_id
        """;

    private static RaceProgram MapProgram(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2).Replace("\\n", " "), r.GetInt32(3), r.GetInt32(4), (Ground)r.GetInt32(5),
        r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10));

    public RaceProgram? Program(int programId)
    {
        if (_programs.TryGetValue(programId, out var cached)) return cached;
        var rows = Query(ProgramSelect + " where p.id = $id", MapProgram, ("$id", programId));
        return _programs[programId] = rows.FirstOrDefault();
    }

    public IReadOnlyList<RaceProgram> ProgramsForTurn(int turn)
    {
        var date = Turn(turn);
        return Query(ProgramSelect + " where p.month = $m and p.half = $h and p.program_group = 0 order by r.grade, cs.distance",
                MapProgram, ("$m", date.Month), ("$h", date.Half))
            .Where(p => p.OpenInYear(date.Year))
            .ToList();
    }

    public IReadOnlyList<GoalRace> Goals(IReadOnlyList<int> routeRaceIds)
    {
        var goals = new List<GoalRace>();
        foreach (int id in routeRaceIds)
        {
            var rows = Query("select sort_id, turn, condition_id, condition_value_1, target_type from single_mode_route_race where id = $id",
                r => (Sort: r.GetInt32(0), Turn: r.GetInt32(1), Condition: r.GetInt32(2), Place: r.GetInt32(3), Target: r.GetInt32(4)),
                ("$id", id));
            if (rows.Count == 0) continue;
            var row = rows[0];
            var program = Program(row.Condition);
            string name = program?.Name ?? row.Condition switch
            {
                10001 => "URA Finale Qualifier",
                10002 => "URA Finale Semifinal",
                10003 => "URA Finale Final",
                _ => $"Goal {row.Condition}",
            };
            goals.Add(new GoalRace(id, row.Sort, row.Turn, row.Place, name, program));
        }
        return goals.OrderBy(g => g.Turn).ThenBy(g => g.SortId).ToList();
    }

    public SkillInfo? Skill(int skillId)
    {
        if (_skills.TryGetValue(skillId, out var cached)) return cached;
        var rows = Query("""
            select s.id, s.rarity, s.group_id, s.grade_value, coalesce(n.need_skill_point, 0), s.condition_1, s.ability_type_1_1
            from skill_data s left join single_mode_skill_need_point n on n.id = s.id where s.id = $id
            """,
            r => new SkillInfo(r.GetInt32(0), Text(TextSkill, r.GetInt32(0)) ?? $"skill {r.GetInt32(0)}", r.GetInt32(1), r.GetInt32(2),
                r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetInt32(6)),
            ("$id", skillId));
        return _skills[skillId] = rows.FirstOrDefault();
    }

    public SkillInfo? SkillForHint(int groupId, int rarity)
    {
        var ids = Query("select id from skill_data where group_id = $g and rarity = $r order by id", r => r.GetInt32(0), ("$g", groupId), ("$r", rarity));
        return ids.Count > 0 ? Skill(ids[0]) : null;
    }

    public IReadOnlyList<int> BaseSkillIds(int cardId, int talentLevel) =>
        Query("select skill_id from available_skill_set where available_skill_set_id = $c and need_rank <= $t order by id",
            r => r.GetInt32(0), ("$c", cardId), ("$t", talentLevel));

    public SupportCardInfo? SupportCard(int supportCardId) =>
        Query("select id, chara_id, support_card_type, command_id from support_card_data where id = $id",
            r => new SupportCardInfo(r.GetInt32(0), Text(TextSupportCard, r.GetInt32(0)) ?? $"card {r.GetInt32(0)}",
                r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)),
            ("$id", supportCardId)).FirstOrDefault();

    public ConditionInfo? Condition(int conditionId) =>
        Query("select id, effect_type from single_mode_chara_effect where id = $id",
            r => new ConditionInfo(r.GetInt32(0), Text(TextCondition, r.GetInt32(0)) ?? $"condition {r.GetInt32(0)}", r.GetInt32(1) == 2),
            ("$id", conditionId)).FirstOrDefault();

    public string? StoryTitle(int storyId) => Text(TextStoryTitle, storyId)?.Replace("\\n", " ");

    public string? CharacterName(int charaId) => Text(TextCharacter, charaId);

    public EventEffectFormat? EventEffect(int displayId) =>
        Query("select effect_value_type_0, effect_value_type_1, effect_value_type_2 from single_mode_event_choice_reward where id = $id",
            r => new EventEffectFormat(displayId, Text(TextEventEffect, displayId) ?? "", r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)),
            ("$id", displayId)).FirstOrDefault();

    public void Dispose() => _db.Dispose();
}
