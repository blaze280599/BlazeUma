using System.Text.Json;
using BlazeUma.Advisor;
using BlazeUma.Memory.Game;

namespace BlazeUma.Cli;

/// <summary>Appends <see cref="TurnRecorder"/> records to one JSONL file per career (and per CLI session).</summary>
internal sealed class CareerLog(string directory, JsonSerializerOptions json)
{
    private readonly TurnRecorder _recorder = new();

    /// <summary>The log in logs/ next to the exe, written as Advisor mode writes it.</summary>
    public static CareerLog Create() => new(Path.Combine(AppContext.BaseDirectory, "logs"), new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    });

    public string? CurrentPath { get; private set; }

    public void Observe(CareerSnapshot snapshot, Advice advice)
    {
        var records = _recorder.Observe(snapshot, advice);
        if (snapshot.Character is { } character && (CurrentPath is null || _recorder.NewCareer))
        {
            Directory.CreateDirectory(directory);
            CurrentPath = Path.Combine(directory, $"career-{character.CardId}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        }
        if (CurrentPath is null || records.Count == 0) return;
        File.AppendAllLines(CurrentPath, records.Select(r => JsonSerializer.Serialize(r, json)));
    }
}
