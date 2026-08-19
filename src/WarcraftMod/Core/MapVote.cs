namespace WarcraftMod.Core;

/// <summary>
/// Голосование за следующую карту. Открывается на последнем раунде, закрывается вместе
/// с ним.
///
/// Голоса собираются в чате, а не в меню, и это не лень: меню мода замораживает игрока,
/// пока открыто. Посреди раунда это отобрало бы у него как раз тот раунд, за который
/// он голосует.
/// </summary>
public sealed class MapVote
{
    private readonly Dictionary<int, MapEntry> _votes = new();

    /// <summary>Карты, между которыми идёт выбор. Пусто — голосование не идёт.</summary>
    public IReadOnlyList<MapEntry> Candidates { get; private set; } = [];

    public bool IsOpen => Candidates.Count > 0;

    public int VoteCount => _votes.Count;

    /// <summary>
    /// Начать голосование. Текущая карта в кандидаты не идёт: предлагать остаться там,
    /// где все и так стоят, — не выбор, а лишняя строка.
    /// </summary>
    public void Open(string currentMap)
    {
        _votes.Clear();
        Candidates = MapPool.All
            .Where(entry => !entry.Name.Equals(currentMap, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Сколько голосов у карты — для подписи в меню.</summary>
    public int VotesFor(MapEntry entry) =>
        _votes.Values.Count(vote => vote.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Голос по номеру из списка. false — номера нет.</summary>
    public bool TryVote(int slot, int number, out MapEntry? chosen)
    {
        chosen = null;
        if (number < 1 || number > Candidates.Count) return false;

        chosen = Candidates[number - 1];

        // Голос перезаписывается, а не добавляется: передумать до конца раунда можно,
        // накрутить за счёт повторных нажатий — нет.
        _votes[slot] = chosen;
        return true;
    }

    /// <summary>Забыть голос вышедшего — иначе он считался бы и после его ухода.</summary>
    public void Forget(int slot) => _votes.Remove(slot);

    /// <summary>
    /// Победитель и число его голосов. При равенстве и при полном отсутствии голосов
    /// выбирается случайный из кандидатов: пустое голосование не повод оставлять всё
    /// как есть, иначе сервер навсегда застрянет на одной карте.
    /// </summary>
    public (MapEntry Winner, int Votes) Finish()
    {
        var candidates = Candidates;
        Candidates = [];

        if (_votes.Count == 0) return (candidates[Random.Shared.Next(candidates.Count)], 0);

        var tally = _votes.Values
            .GroupBy(entry => entry.Name)
            .Select(group => (Entry: group.First(), Count: group.Count()))
            .ToList();

        var best = tally.Max(item => item.Count);
        var leaders = tally.Where(item => item.Count == best).ToList();

        return (leaders[Random.Shared.Next(leaders.Count)].Entry, best);
    }
}
