using BlazeUma.Memory.Il2Cpp;
using BlazeUma.Memory.Native;

namespace BlazeUma.Memory.Game;

/// <summary>
/// Read-only career state from the live game, via the static roots verified in research/:
/// Singleton&lt;WorkDataManager&gt; → SingleMode → Character, MonoSingleton&lt;SceneManager&gt;, Singleton&lt;StoryManager&gt;.
/// </summary>
public sealed class GameReader : IDisposable
{
    private const string SkillScreen = "Gallop.SingleModeSkillLearningViewController";
    private const string RaceScreen = "Gallop.SingleModeRaceEntryViewController";

    private readonly GameProcess _process;
    private readonly ManagedReader _r;

    public Il2CppRuntime Runtime { get; }
    public ManagedReader ManagedReader => _r;
    public int ProcessId => _process.ProcessId;

    /// <summary>Install directory of the running game (contains GameAssembly.dll).</summary>
    public string GameDirectory => Path.GetDirectoryName(_process.GameAssemblyPath)!;

    private GameReader(GameProcess process, Il2CppRuntime runtime, ManagedReader reader)
    {
        _process = process;
        Runtime = runtime;
        _r = reader;
    }

    public static GameReader Open(int? pid = null, string? metadataPath = null)
    {
        var process = GameProcess.Open(pid);
        try
        {
            var metadata = new GlobalMetadata(metadataPath ?? process.DefaultMetadataPath);
            var runtime = new Il2CppRuntime(process, process.GameAssemblyBase, metadata);
            var keys = new ObscuredKeys(process, runtime);
            return new GameReader(process, runtime, new ManagedReader(process, runtime, keys));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>A consistent snapshot: retried if the turn advances or an Obscured value is mid-update.</summary>
    public CareerSnapshot Snapshot(int attempts = 3)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var snapshot = ReadOnce();
                if (snapshot.Turn == ReadTurn()) return snapshot;
            }
            catch (InconsistentSnapshotException) when (attempt < attempts) { }
            if (attempt >= attempts)
                throw new InconsistentSnapshotException("Game state kept changing during the read.");
            Thread.Sleep(50);
        }
    }

    /// <summary>Current turn, or null outside a career (same rule as the snapshot itself).</summary>
    private int? ReadTurn()
    {
        ulong sm = SingleMode();
        return sm != 0 && _r.Bool(sm, "_isPlaying") ? _r.ObscuredInt(sm, "_totalTurnNum") : null;
    }

    /// <summary>True once the game process this reader is attached to has exited.</summary>
    public bool GameExited => !System.Diagnostics.Process.GetProcessesByName(GameProcess.ExecutableName).Any(p => p.Id == ProcessId);

    private ulong SingleMode()
    {
        ulong manager = _r.SingletonInstance("Gallop.WorkDataManager");
        return manager == 0 ? 0 : _r.Ptr(manager, "SingleMode");
    }

    private CareerSnapshot ReadOnce()
    {
        var errors = new List<string>();
        T? Section<T>(string name, Func<T> read) where T : class?
        {
            try { return read(); }
            catch (Exception e) when (e is MemoryReadException or UnsupportedLayoutException)
            {
                errors.Add($"{name}: {e.Message}");
                return null;
            }
        }

        var screen = Section("screen", ReadScreen);
        ulong sm = SingleMode();
        bool active = sm != 0 && _r.Bool(sm, "_isPlaying");
        int? turn = active ? _r.ObscuredInt(sm, "_totalTurnNum") : null;
        ulong home = active ? _r.Ptr(sm, "_homeInfo") : 0;

        return new CareerSnapshot(
            ReadAt: DateTimeOffset.Now,
            ProcessId: _process.ProcessId,
            Screen: screen,
            CareerActive: active,
            Turn: turn,
            AlarmClocks: home != 0 ? _r.ObscuredInt(home, "_availableContinueNum") : null,
            LastTrainingId: active ? _r.ObscuredInt(sm, "SelectedTrainingCommandId") : null,
            Character: active ? Section("character", () => ReadCharacter(_r.Ptr(sm, "Character"))) : null,
            Commands: home != 0 ? Section("commands", () => ReadCommands(home)) : null,
            Story: active ? Section("story", ReadStory) : null,
            EventChoices: active ? Section("eventChoices", () => ReadEventChoices(_r.Ptr(sm, "EventChoiceRewardDict"))) : null,
            EventChoicesEventId: active ? _r.Int(sm, "_cachedRewardEventId") : null,
            SkillShop: screen?.ViewController == SkillScreen ? Section("skillShop", ReadSkillShop) : null,
            RaceList: screen?.ViewController == RaceScreen ? Section("raceList", ReadRaceList) : null,
            Errors: errors);
    }

    private ulong CurrentViewController()
    {
        ulong scene = _r.SingletonInstance("Gallop.SceneManager");
        return scene == 0 ? 0 : _r.Ptr(scene, "_currentViewController");
    }

    private Screen? ReadScreen()
    {
        ulong scene = _r.SingletonInstance("Gallop.SceneManager");
        if (scene == 0) return null;
        return new Screen(
            _r.ClassName(_r.Ptr(scene, "_currentSceneController")),
            _r.ClassName(_r.Ptr(scene, "_currentViewController")),
            _r.Int(scene, "_nextViewId"));
    }

    private Character ReadCharacter(ulong ch)
    {
        int O(string field) => _r.ObscuredInt(ch, field);
        return new Character(
            CardId: O("_cardId"), ScenarioId: O("_scenarioId"), RouteId: O("_routeId"), TalentLevel: O("TalentLevel"),
            Stats: new Stats(O("_speed"), O("_stamina"), O("_power"), O("_guts"), O("_wiz")),
            MaxStats: new Stats(O("MaxSpeed"), O("MaxStamina"), O("MaxPower"), O("MaxGuts"), O("MaxWiz")),
            Energy: O("_hp"), MaxEnergy: O("_maxHp"),
            Mood: (Mood)O("_motivation"),
            SkillPoints: O("SkillPoint"), Fans: O("_fanCount"),
            RunningStyle: O("_runningStyle"),
            Aptitudes: new Aptitudes(
                O("_properGroundTurf"), O("_properGroundDirt"),
                O("_properDistanceShort"), O("_properDistanceMile"), O("_properDistanceMiddle"), O("_properDistanceLong"),
                O("_properRunningStyleNige"), O("_properRunningStyleSenko"), O("_properRunningStyleSashi"), O("_properRunningStyleOikomi")),
            RouteRaceIds: _r.ObscuredIntArray(_r.Ptr(ch, "_routeRaceIdArray")),
            ReservedRaceProgramId: O("_reservedRaceProgramId"),
            Conditions: _r.ObscuredIntArray(_r.Ptr(ch, "_charaEffectIdArray")),
            Deck: _r.ArrayItems(_r.Ptr(ch, "EquipSupportCardArray"))
                .Select(c => new DeckCard(_r.ObscuredInt(c, "Position"), _r.ObscuredInt(c, "SupportCardId"), _r.ObscuredInt(c, "LimitBreakCount")))
                .ToList(),
            Bonds: _r.ListItems(_r.Ptr(ch, "_evaluationList"))
                .Select(e => new Bond(_r.ObscuredInt(e, "_targetId"), _r.ObscuredInt(e, "_value"), _r.ObscuredInt(e, "_guestCharaId"),
                    _r.ObscuredBool(e, "_isOuting"), _r.ObscuredInt(e, "_storyStep")))
                .ToList(),
            Skills: _r.ListItems(_r.Ptr(ch, "_acquiredSkillList"))
                .Select(s => new AcquiredSkill(_r.ObscuredInt(s, "_masterId"), _r.ObscuredInt(s, "_level")))
                .ToList(),
            Hints: _r.ListItems(_r.Ptr(ch, "_skillTipsList"))
                .Select(h => new SkillHint(_r.ObscuredInt(h, "GroupId"), _r.ObscuredInt(h, "Rarity"), _r.ObscuredInt(h, "Level")))
                .ToList());
    }

    private List<Command> ReadCommands(ulong home)
    {
        var commands = new List<Command>();
        foreach (var (_, list) in _r.IntKeyDictionary(_r.Ptr(home, "_turnInfoListDic")))
        {
            foreach (ulong info in _r.ListItems(list))
            {
                var gains = _r.IntKeyDictionary(_r.Ptr(info, "ParamIncDecInfoDic"))
                    .ToDictionary(g => ParamIds.Name(g.Key), g => new Gain(_r.ObscuredInt(g.Value, "Value"), _r.ObscuredInt(g.Value, "BonusValue")));
                var partners = _r.ListItems(_r.Ptr(info, "TrainingHorseList"))
                    .Select(h => new TrainingPartner(_r.ObscuredInt(h, "PositionId"), _r.ObscuredBool(h, "IsTips")))
                    .ToList();
                commands.Add(new Command(
                    (CommandType)_r.ObscuredInt(info, "_commandType"),
                    _r.ObscuredInt(info, "_commandId"),
                    _r.ObscuredBool(info, "IsEnable"),
                    _r.Int(info, "TrainingFailureRate"),
                    gains, partners));
            }
        }
        return commands;
    }

    private StoryEvent? ReadStory()
    {
        ulong story = _r.SingletonInstance("Gallop.StoryManager");
        if (story == 0) return null;
        ulong data = _r.Ptr(story, "_storyData");
        ulong view = _r.Ptr(story, "_viewController");
        return new StoryEvent(
            StoryId: _r.Int(story, "StoryId"),
            StoryDataId: data != 0 ? _r.Int(data, "Id") : 0,
            CharaId: data != 0 ? _r.Int(data, "CardCharaId") : 0,
            SupportCharaId: data != 0 ? _r.Int(data, "SupportCharaId") : 0,
            EventId: view != 0 ? _r.Int(view, "_eventId") : 0,
            LooksActive: view != 0 && _r.Bool(view, "_needsSingleModeSave") && _r.Bool(view, "_needsUpdateSelectIndex"));
    }

    private List<EventChoice> ReadEventChoices(ulong dict) =>
        _r.ObscuredIntKeyDictionary(dict)
            .Select(c => new EventChoice(c.Key,
                _r.ArrayItems(_r.Ptr(c.Value, "_branchRewardArray"))
                    .Select(branch => (IReadOnlyList<EventEffect>)_r.ArrayItems(_r.Ptr(branch, "_gainParamArray"))
                        .Select(g => new EventEffect(_r.ObscuredInt(g, "_displayId"),
                            _r.ObscuredInt(g, "_effectValue0"), _r.ObscuredInt(g, "_effectValue1"), _r.ObscuredInt(g, "_effectValue2")))
                        .ToList())
                    .ToList()))
            .OrderBy(c => c.Choice)
            .ToList();

    private SkillShop? ReadSkillShop()
    {
        ulong vc = CurrentViewController();
        if (_r.ClassName(vc) != SkillScreen) return null;
        var skills = _r.ListItems(_r.Ptr(vc, "_skillInfoList"))
            .SelectMany(group => _r.ListItems(_r.Ptr(group, "_skillList")))
            .Select(s => new ShopSkill(
                _r.Int(s, "SkillId"), _r.Int(s, "NeedPoint"), _r.Int(s, "OriginNeedPoint"),
                _r.Int(s, "DiscountRateForDisplay"), _r.Int(s, "HintLv"),
                _r.Bool(s, "IsGet"), _r.Bool(s, "IsLock"), _r.Bool(s, "IsSelected")))
            .ToList();
        return new SkillShop(_r.Int(vc, "RemainingPoint"), skills);
    }

    private RaceList? ReadRaceList()
    {
        ulong vc = CurrentViewController();
        if (_r.ClassName(vc) != RaceScreen) return null;
        var races = _r.ListItems(_r.Ptr(vc, "_itemList"))
            .Select(item =>
            {
                ulong program = _r.Ptr(item, "Program");
                return new RaceEntry(_r.Int(program, "Id"), _r.Int(program, "RaceInstanceId"), _r.Int(program, "NeedFanCount"),
                    _r.Int(item, "_programEntryState"), _r.Bool(item, "IsRouteRace"), _r.Bool(item, "IsReservedProgram"));
            })
            .ToList();
        return new RaceList(_r.Int(vc, "_displayTurn"), races);
    }

    public void Dispose() => _process.Dispose();
}
