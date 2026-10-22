using BlazeUma.Memory.Game;

namespace BlazeUma.Advisor;

/// <summary>
/// Remembers which turns the trainee raced, across the snapshots of one session, and stamps them on each
/// snapshot as <see cref="CareerSnapshot.RaceTurns"/>. The game keeps a race history
/// (WorkSingleModeData._raceHistoryInfoList, research 2026-10-21) but its layout is not mapped yet, so a turn
/// counts as raced when fans went up between its first snapshot and the next turn's (as
/// <see cref="TurnRecorder.InferAction"/> does). Turns read before the session started are unknown.
/// </summary>
public sealed class RaceHistory
{
    private readonly SortedSet<int> _turns = [];
    private CareerSnapshot? _turnStart;

    public IReadOnlyCollection<int> Turns => _turns;

    public CareerSnapshot Stamp(CareerSnapshot snapshot)
    {
        if (snapshot.Turn is not int turn || snapshot.Character is not { } now) return snapshot;
        if (_turnStart is { Turn: int startTurn, Character: { } before })
        {
            if (turn < startTurn || before.CardId != now.CardId)
            {
                _turns.Clear();
                _turnStart = snapshot;
            }
            else if (turn > startTurn)
            {
                // A race takes one turn; across a gap in the readings the fans cannot be placed on a turn.
                if (turn == startTurn + 1 && now.Fans > before.Fans) _turns.Add(startTurn);
                _turnStart = snapshot;
            }
        }
        else
        {
            _turnStart = snapshot;
        }
        return snapshot with { RaceTurns = _turns.ToList() };
    }
}
