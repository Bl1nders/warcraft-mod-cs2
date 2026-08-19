namespace WarcraftMod.Storage;

/// <summary>Прогресс игрока по одной конкретной расе. Сохраняется на диск.</summary>
public sealed class RaceProgress
{
    public int Level { get; set; } = 1;
    public int Xp { get; set; }

    /// <summary>Сколько секунд отыграно этой расой. Покажет расы, которые никто не берёт.</summary>
    public long PlayedSeconds { get; set; }

    /// <summary>Ранг каждой способности расы, по индексу из Race.Abilities.</summary>
    public int[] Ranks { get; set; } = [];

    /// <summary>
    /// Раскладывать свободные очки самому. Включено у всех, кто не разложил их руками.
    ///
    /// Заведено по замерам первой недели: пять игроков из шести дошли до уровня и не
    /// вложили ни одного очка. Строка в чат «распределите очки» тонет между киллфидом
    /// и болтовнёй, и раса с седьмым уровнем не давала человеку ровно ничего.
    ///
    /// Тип с вопросом не случаен. <c>null</c> — запись, сделанная до этой правки, и
    /// решение по ней принимается один раз в <see cref="ProgressFor"/>: нетронутая
    /// раскладка переходит на автомат, а собранная руками остаётся как есть. Без этого
    /// обновление молча затёрло бы чужую сборку своим порядком.
    /// </summary>
    public bool? AutoSkills { get; set; }
}

/// <summary>
/// Всё, что мы помним про игрока между заходами. Ключ — SteamID64.
/// Прогресс хранится по каждой расе отдельно, чтобы смена расы не обнуляла старую прокачку.
/// </summary>
public sealed class PlayerRecord
{
    public string LastKnownName { get; set; } = "";
    public string CurrentRaceId { get; set; } = "";

    /// <summary>
    /// Раса, выбранная посреди раунда и ещё не вступившая в силу. Хранится на диске:
    /// иначе короткий обрыв связи стирал бы выбор и возвращал игрока к прежней расе.
    /// </summary>
    public string PendingRaceId { get; set; } = "";
    public Dictionary<string, RaceProgress> Races { get; set; } = new();

    /// <summary>
    /// Общий опыт — тот, что открывает расы. Копится всегда, с любой расы и в любом
    /// её состоянии, включая потолок: иначе доведённая до конца любимая раса обнуляла бы
    /// смысл дальше играть, и приходилось бы уходить на нелюбимую ради прогресса.
    ///
    /// Раньше общий уровень был суммой уровней рас. Перенос со старой схемы делает
    /// <c>WarcraftPlugin.RegisterPlayer</c> при первом заходе.
    /// </summary>
    public long AccountXp { get; set; }

    /// <summary>
    /// Время в игре, в секундах. Считается только в командах — зритель опыт не зарабатывает,
    /// и его время испортило бы главное число всей проверки баланса: опыт в час.
    ///
    /// Задним числом это не восстановить: в сейве нет ничего, привязанного ко времени.
    /// Поэтому счётчик заведён до открытия сервера, а не после.
    /// </summary>
    public long PlayedSeconds { get; set; }

    /// <summary>Когда игрока увидели впервые и в последний раз, unix-время. Для оценки удержания.</summary>
    public long FirstSeenUnix { get; set; }

    public long LastSeenUnix { get; set; }

    /// <summary>
    /// Когда игроку последний раз играли приветствие, unix-время. Держит суточную паузу
    /// у возвращающихся. Ноль — не играли ни разу.
    /// </summary>
    public long LastWelcomeUnix { get; set; }

    /// <summary>
    /// Расы, выданные игроку лично. Нужны для донатных: уровнем они не открываются,
    /// доступ раздаёт владелец сервера и может забрать обратно.
    /// </summary>
    public List<string> GrantedRaces { get; set; } = [];

    public RaceProgress ProgressFor(string raceId, int abilityCount)
    {
        if (!Races.TryGetValue(raceId, out var progress))
        {
            progress = new RaceProgress { Ranks = new int[abilityCount] };
            Races[raceId] = progress;
        }

        // Раса могла получить новую способность после обновления мода — расширяем массив.
        if (progress.Ranks.Length != abilityCount)
        {
            var resized = new int[abilityCount];
            Array.Copy(progress.Ranks, resized, Math.Min(progress.Ranks.Length, abilityCount));
            progress.Ranks = resized;
        }

        // Старая запись без флага: решаем по раскладке и запоминаем решение навсегда.
        // Пустая — человек до меню не дошёл, ему автомат и нужен; непустая — это чужой
        // выбор, и трогать его нельзя.
        progress.AutoSkills ??= progress.Ranks.Sum() == 0;

        return progress;
    }
}
