using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using WarcraftMod.Storage;

namespace WarcraftMod.Core;

/// <summary>
/// Состояние игрока на время его нахождения на сервере: выбранная раса,
/// прогресс, кулдауны и временные эффекты от способностей.
/// </summary>
public sealed class WarcraftPlayer
{
    public WarcraftPlayer(int slot, ulong steamId, PlayerRecord record)
    {
        Slot = slot;
        SteamId = steamId;
        Record = record;

        // Отсчёт бездействия ведём с этой минуты. Без этого отметка оставалась нулём
        // до первого нажатия клавиши, а проверка считает бездействие как «сейчас минус
        // отметка» — на сервере, проработавшем дольше AfkSeconds, зашедшего уносило
        // в зрители первой же проверкой, ещё до того как он успевал что-то нажать.
        LastActivityAt = Server.CurrentTime;
    }

    /// <summary>Слот игрока. Меняется при переподключении: вернуться можно в другой слот.</summary>
    public int Slot { get; private set; }

    public ulong SteamId { get; }
    public PlayerRecord Record { get; }

    /// <summary>
    /// Приветствие, выбранное при входе и ждущее первого спавна. Пустая строка — нечего играть.
    ///
    /// Ждёт именно спавна, потому что звук идёт от оболочки игрока, а в выборе команды
    /// оболочки ещё нет: сыгранное на входе приветствие ушло бы в никуда и молча —
    /// ровно с тем же видом, что и несуществующее событие.
    /// </summary>
    public string PendingWelcomeSound { get; set; } = "";

    /// <summary>Текущая раса. null, если игрок ещё не выбрал.</summary>
    public Race? Race { get; private set; }

    public RaceProgress Progress { get; private set; } = new();

    /// <summary>Раса, выбранная посреди раунда. Вступит в силу со следующего.</summary>
    public Race? PendingRace { get; set; }

    /// <summary>
    /// Ранги, которыми раса играет прямо сейчас. Снимок с купленного, снимается на старте
    /// раунда — очко, вложенное посреди боя, вступает в силу со следующего, как и смена расы.
    ///
    /// Раньше ранг читался прямо из сейва, и правило соблюдалось только наполовину: пассивки
    /// выдаются на спавне и до следующего раунда всё равно не менялись, а активная способность
    /// усиливалась сразу по нажатию. Тот, кто взял уровень в бою, тут же получал усиленную
    /// активку — а его противник, взявший уровень минутой позже, нет.
    /// </summary>
    private int[] _ranksInPlay = [];

    /// <summary>Привязать состояние к новому слоту после переподключения.</summary>
    internal void Rebind(int slot)
    {
        Slot = slot;

        // Состояние переиспользуется целиком, и отметка активности в нём осталась
        // с момента обрыва. Вернувшийся не бездействовал — он переподключался.
        LastActivityAt = Server.CurrentTime;
    }

    /// <summary>
    /// Ссылку на Discord этому игроку в консоль уже писали. Пункт меню можно нажимать
    /// сколько угодно, но в консоли строка нужна ровно одна: её открывают, чтобы
    /// скопировать адрес, и десять одинаковых копий подряд там только мешают.
    /// </summary>
    public bool DiscordLinkInConsole { get; set; }

    /// <summary>Игровое время, до которого способность по индексу недоступна.</summary>
    private readonly Dictionary<int, float> _cooldownUntil = new();

    // --- временные эффекты, которые пересчитываются каждый тик ---

    /// <summary>Постоянный множитель скорости от пассивок расы. Ставится при спавне.</summary>
    public float BaseSpeedMultiplier { get; set; } = 1f;

    /// <summary>Временный множитель скорости от активных способностей и замедлений.</summary>
    public float TempSpeedMultiplier { get; set; } = 1f;

    /// <summary>Итоговая скорость, которую применяет плагин каждый тик.</summary>
    public float SpeedMultiplier => BaseSpeedMultiplier * TempSpeedMultiplier;

    /// <summary>Прозрачность модели: 255 — видно полностью, 0 — невидимка.</summary>
    public int RenderAlpha { get; set; } = 255;

    /// <summary>Последняя фактически применённая прозрачность — чтобы не дёргать движок каждый тик.</summary>
    public int AppliedRenderAlpha { get; set; } = 255;

    /// <summary>Пока true, следующий смертельный удар не убивает.</summary>
    public bool HasDeathWard { get; set; }

    /// <summary>Что сказать игроку, когда щит сработает. null — общая фраза от мода.</summary>
    public string? DeathWardMessage { get; set; }

    /// <summary>Сколько здоровья вернуть в момент срабатывания щита. 0 — не лечить.</summary>
    public int DeathWardHeal { get; set; }

    /// <summary>Сколько прыжков доступно сверх обычного.</summary>
    public int ExtraJumps { get; set; }

    /// <summary>Сколько дополнительных прыжков уже потрачено в текущем полёте.</summary>
    public int ExtraJumpsUsed { get; set; }

    /// <summary>Сила дополнительного прыжка по вертикали.</summary>
    public float ExtraJumpPower { get; set; } = 280f;

    /// <summary>Прибавка к высоте обычного прыжка с земли.</summary>
    public float GroundJumpBonus { get; set; }

    /// <summary>
    /// Предел скорости снижения. 0 — падать как все.
    ///
    /// Пришло на смену иммунитету к падению: на rats-картах урона от падения нет вовсе,
    /// и иммунитет там не давал ничего. Замедленное падение полезно везде — оно даёт
    /// время выбрать, куда приземлиться, а на высоких рангах ещё и снимает урон само
    /// собой, потому что скорость удара о землю не дотягивает до порога.
    /// </summary>
    public float MaxFallSpeed { get; set; }

    /// <summary>
    /// Следить ли за прыжками этого игрока. Сам прыжок делает движок
    /// (<c>sv_autobunnyhopping</c>) и делает его всем без разбора — флаг означает
    /// не «умеет прыгать», а «за прыжок ему причитается от расы».
    /// </summary>
    public bool AutoBhop { get; set; }

    /// <summary>
    /// Взведён ли счёт прыжка: игрок стоял на земле или падал, и следующий отрыв
    /// пойдёт в зачёт. Нужен, чтобы один прыжок не посчитался дважды.
    /// </summary>
    public bool HopArmed { get; set; }

    /// <summary>Держит ли игрок пробел прямо сейчас — считается по нажатиям и отпусканиям.</summary>
    public bool JumpHeld { get; set; }

    /// <summary>
    /// Разовый иммунитет к падению после подброса — держится до приземления.
    /// Отдельно от <see cref="NoFallDamage"/>, чтобы не снять пассивку Кузнечика.
    /// </summary>
    public bool FallImmuneAfterLaunch { get; set; }

    /// <summary>Момент подброса: раньше него приземление не засчитываем.</summary>
    public float LaunchedAt { get; set; }

    /// <summary>Выдать иммунитет к падению до ближайшего приземления.</summary>
    public void GrantLaunchFallImmunity()
    {
        FallImmuneAfterLaunch = true;
        LaunchedAt = Server.CurrentTime;
    }

    /// <summary>Слот того, кто последним нанёс урон. -1 — никто. Ложная смерть по нему выбирает «убийцу».</summary>
    public int LastAttackerSlot { get; set; } = -1;

    // --- определение бездействия ---

    /// <summary>Игровое время последней замеченной активности.</summary>
    public float LastActivityAt { get; set; }

    /// <summary>
    /// Где стоял и куда смотрел при прошлой проверке. Одних нажатий мало: игрок, зажавший
    /// движение об стену, кнопку не отпускает и считался бы активным вечно. А поворот
    /// мыши кнопками не отслеживается вовсе, хотя это тоже присутствие за компьютером.
    /// </summary>
    public Vector3 LastSeenOrigin { get; set; }

    public Vector3 LastSeenAngles { get; set; }

    /// <summary>
    /// Игровое время, когда игрок последний раз стоял на земле или был выше дна карты.
    /// По нему мод отличает улетевшего за пределы карты от того, кто просто падает
    /// в яму: падение кончается землёй за секунду-другую, полёт в пустоте — никогда.
    /// </summary>
    public float LastInBoundsAt { get; set; }

    /// <summary>Игровое время последнего полученного урона.</summary>
    public float LastDamagedAt { get; set; }

    /// <summary>Настоящий ник, пока игрок носит чужой. null — ник свой.</summary>
    public string? OriginalName { get; private set; }

    /// <summary>Надеть чужое имя. Настоящее запоминается при первой подмене.</summary>
    public void ApplyFakeName(string fakeName)
    {
        if (Controller is not { IsValid: true } controller) return;

        OriginalName ??= controller.PlayerName;
        SetName(controller, fakeName);
    }

    /// <summary>Вернуть настоящее имя, если оно было подменено.</summary>
    public void RestoreName()
    {
        if (OriginalName is not { Length: > 0 } original) return;

        if (Controller is { IsValid: true } controller) SetName(controller, original);
        OriginalName = null;
    }

    private static void SetName(CCSPlayerController controller, string name)
    {
        controller.PlayerName = name;
        Utilities.SetStateChanged(controller, "CBasePlayerController", "m_iszPlayerName");
    }

    /// <summary>Модель, с которой игрок появился. К ней возвращает маскировка.</summary>
    public string? OriginalModel { get; set; }

    /// <summary>
    /// Метка текущей жизни для отложенных эффектов маскировки. Растёт при каждом сбросе
    /// временных эффектов, и таймер сверяет её со своим значением, чтобы не снять то,
    /// что относится уже к следующей жизни. Строку модели для этого использовать нельзя:
    /// она обнуляется при сбросе, и снятие переставало срабатывать.
    /// </summary>
    public int DisguiseToken { get; set; }

    public CCSPlayerController? Controller => Utilities.GetPlayerFromSlot(Slot);

    public CCSPlayerPawn? Pawn
    {
        get
        {
            var pawn = Controller?.PlayerPawn.Value;
            return pawn is { IsValid: true } ? pawn : null;
        }
    }

    public bool IsAlive => Pawn is { LifeState: (byte)LifeState_t.LIFE_ALIVE, Health: > 0 };

    public void SetRace(Race race)
    {
        Race = race;
        Record.CurrentRaceId = race.Id;
        Progress = Record.ProgressFor(race.Id, race.Abilities.Count);

        // Ранги новой расы вводим сразу: сама смена расы уже отложена до раунда там, где надо,
        // а без этого игрок весь ближайший раунд ходил бы с расой без единой способности.
        ApplyBoughtRanks();

        _cooldownUntil.Clear();
        ClearTemporaryEffects();
    }

    /// <summary>
    /// Полный сброс: ни расы, ни уровней, ни очков. Общий уровень обнуляется вместе
    /// с прогрессом по каждой расе, поэтому закрытые расы закрываются обратно.
    /// </summary>
    public void ForgetEverything()
    {
        Race = null;
        PendingRace = null;
        Progress = new RaceProgress();
        ApplyBoughtRanks();

        Record.Races.Clear();
        Record.CurrentRaceId = "";
        Record.PendingRaceId = "";
        Record.AccountXp = 0;

        _cooldownUntil.Clear();
        ClearSpentThisRound();
        ClearTemporaryEffects();
    }

    /// <summary>
    /// Ранг, которым способность работает в этом раунде. 0 — не изучена. Именно его
    /// спрашивают расы: сила способности не должна меняться посреди боя.
    /// </summary>
    public int RankOf(int abilityIndex) =>
        abilityIndex >= 0 && abilityIndex < _ranksInPlay.Length ? _ranksInPlay[abilityIndex] : 0;

    /// <summary>Купленный ранг — то, за что уже отданы очки. Его показывают меню и справки.</summary>
    public int BoughtRankOf(int abilityIndex) =>
        abilityIndex >= 0 && abilityIndex < Progress.Ranks.Length ? Progress.Ranks[abilityIndex] : 0;

    /// <summary>Куплено, но ещё не в силе: ждёт начала следующего раунда.</summary>
    public bool IsUpgradePending(int abilityIndex) => BoughtRankOf(abilityIndex) > RankOf(abilityIndex);

    /// <summary>Ввести купленные ранги в игру. Зовётся на старте раунда и при смене расы.</summary>
    public void ApplyBoughtRanks() => _ranksInPlay = (int[])Progress.Ranks.Clone();

    public int SpentSkillPoints => Progress.Ranks.Sum();

    /// <summary>
    /// Общий уровень — по нему открываются расы. Считается из общего опыта, а не из суммы
    /// уровней рас, как было раньше: та схема требовала качать расы, которые игроку не
    /// нравятся, и выбрасывала весь опыт с расы, доведённой до потолка.
    /// </summary>
    public int TotalLevel => XpTable.AccountLevelFromXp(Record.AccountXp);

    /// <summary>Сколько рас доведено до потолка. От этого числа зависит ускорение новых.</summary>
    public int MaxedRaces => Record.Races.Values.Count(progress => progress.Level >= XpTable.MaxLevel);

    public int UnspentSkillPoints => XpTable.SkillPointsAtLevel(Progress.Level) - SpentSkillPoints;

    // --- кулдауны ---

    public bool IsOnCooldown(int abilityIndex, out float secondsLeft)
    {
        secondsLeft = 0f;
        if (!_cooldownUntil.TryGetValue(abilityIndex, out var until)) return false;

        secondsLeft = until - Server.CurrentTime;
        return secondsLeft > 0f;
    }

    public void StartCooldown(int abilityIndex, float seconds) =>
        _cooldownUntil[abilityIndex] = Server.CurrentTime + seconds;

    public void ClearCooldowns() => _cooldownUntil.Clear();

    /// <summary>Способности с ограничением «раз за раунд», уже потраченные в этом раунде.</summary>
    private readonly HashSet<int> _spentThisRound = [];

    public bool IsSpentThisRound(int abilityIndex) => _spentThisRound.Contains(abilityIndex);

    public void MarkSpentThisRound(int abilityIndex) => _spentThisRound.Add(abilityIndex);

    /// <summary>Новый раунд — разовые способности снова доступны.</summary>
    public void ClearSpentThisRound() => _spentThisRound.Clear();

    /// <summary>Сбросить всё, что действует только до конца жизни/раунда.</summary>
    public void ClearTemporaryEffects()
    {
        BaseSpeedMultiplier = 1f;
        TempSpeedMultiplier = 1f;
        RenderAlpha = 255;
        AppliedRenderAlpha = 255;
        HasDeathWard = false;
        DeathWardMessage = null;
        DeathWardHeal = 0;
        ExtraJumps = 0;
        ExtraJumpsUsed = 0;
        ExtraJumpPower = 280f;
        GroundJumpBonus = 0f;
        MaxFallSpeed = 0f;
        FallImmuneAfterLaunch = false;
        AutoBhop = false;
        HopArmed = false;
        JumpHeld = false;
        LastAttackerSlot = -1;

        // Чужое имя не должно пережить раунд или смерть, даже если таймер снятия не отработал.
        RestoreName();

        // Счётчик сдвигаем, чтобы таймеры от прошлой жизни не трогали новую модель.
        DisguiseToken++;
    }
}
