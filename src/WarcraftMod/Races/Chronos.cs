using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Повелитель времени — раса про откат назад. Раньше всех выходит с подготовки,
/// один раз откладывает собственную смерть и умеет вернуть в прошлое сначала себя,
/// а ультой — вообще всех, кто ещё жив.
/// </summary>
public sealed class Chronos : Race
{
    private const int QuickStart = 0;
    private const int Timeout = 1;
    private const int Rewind = 2;
    private const int MassRewind = 3;

    /// <summary>Как часто записываются позиции и насколько глубокую историю храним.</summary>
    private const float HistoryTick = 0.25f;
    private const float HistoryDepth = 12f;

    /// <summary>На сколько назад откатывает активка: 2 с плюс секунда за ранг.</summary>
    private const float RewindBase = 2f;

    /// <summary>Сколько держится оцепенение после отложенной смерти на первом ранге.</summary>
    private const float TimeoutStun = 1.5f;

    /// <summary>Если конвар прочитать не удалось, считаем подготовку стандартной.</summary>
    private const float DefaultFreezeTime = 15f;

    /// <summary>Насколько быстрее Повелитель срывается с места после подготовки.</summary>
    private const float QuickStartSpeed = 1.3f;

    /// <summary>
    /// Небольшой подъём при возврате: точка записана по ногам, и без запаса
    /// игрока вжимает в пол.
    /// </summary>
    private const float ArrivalLift = 4f;

    /// <summary>Донатная: уровнем не открывается, доступ выдаётся лично.</summary>
    public override bool DonorOnly => true;

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "chronos";
    public override string Name => "Повелитель времени";
    public override string Description => "Живёт не по общим часам: раньше всех срывается с места, откладывает собственную смерть и возвращает прошлое — сначала себе, потом всей карте.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Ускоренный старт",
            Description = "Как только кончается подготовка, вы срываетесь с места: +30% скорости на 1 с за ранг",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Тайм-аут",
            Description = "Раз за жизнь смертельный удар оставляет 1 HP, но вы замираете: 1.5 с минус 0.25 с за ранг",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Перемотка",
            Description = "Возвращает вас туда, где вы были: 2 с + 1 с за ранг назад (до 6 с)",
            Kind = AbilityKind.Active,
            Cooldown = 26f,
        },
        new Ability
        {
            Name = "Общий откат",
            Description = "Возвращает всех живых на карте туда, где они были: 2 с + 1 с за ранг назад",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 70f,
        },
    ];

    /// <summary>Где кто был, стоя на земле. Ключ — слот, значение — след последних секунд.</summary>
    private readonly Dictionary<int, List<(float Time, Vector3 Position)>> _history = new();

    /// <summary>Кто уже потратил тайм-аут в этой жизни.</summary>
    private readonly HashSet<int> _timeoutUsed = [];

    private Timer? _historyTimer;

    private static readonly Random Rng = new();

    public override void OnRoundStart(WarcraftPlayer player)
    {
        // Прошлое прошлого раунда никому не нужно, а таймер к этому моменту уже погашен.
        _history.Clear();
        _historyTimer = null;
    }

    public override void OnSpawn(WarcraftPlayer player)
    {
        _timeoutUsed.Remove(player.Slot);

        StartHistory();
        StartQuickly(player);
    }

    /// <summary>
    /// Запись ведётся по всем игрокам, а не только по Повелителю: общий откат возвращает
    /// в прошлое всю карту, и след нужен от каждого. Пишет один таймер на раунд.
    /// </summary>
    private void StartHistory()
    {
        if (_historyTimer is not null) return;

        _historyTimer = Plugin.AddRoundTimer(HistoryTick, () =>
        {
            var now = Server.CurrentTime;

            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid || player.PlayerPawn.Value is not { IsValid: true } pawn) continue;
                if (pawn.Health <= 0) continue;

                // Точки в прыжке не запоминаем: возврат в воздух ломает габариты модели —
                // игрока потом вжимает в стены, и камера заглядывает внутрь текстур.
                if (!Effects.IsOnGround(pawn)) continue;

                if (Effects.Origin(pawn) is not { } position) continue;

                if (!_history.TryGetValue(player.Slot, out var trail))
                {
                    trail = [];
                    _history[player.Slot] = trail;
                }

                trail.Add((now, position));

                // Держим только последние секунды: дальше в прошлое никто не заглядывает.
                while (trail.Count > 0 && now - trail[0].Time > HistoryDepth) trail.RemoveAt(0);
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Ускоренный старт: рывок в тот момент, когда кончается подготовка.
    /// </summary>
    /// <remarks>
    /// Изначально задумывалось сокращать саму подготовку, но отпустить одного игрока
    /// раньше нечем: `mp_freezetime` общий, а полей заморозки у игрока в схеме нет —
    /// движок держит всех на уровне правил раунда. Поэтому не «раньше старт», а «быстрее старт».
    /// </remarks>
    private void StartQuickly(WarcraftPlayer player)
    {
        var rank = player.RankOf(QuickStart);
        if (rank <= 0) return;

        var freezeTime = ConVar.Find("mp_freezetime")?.GetPrimitiveValue<int>() ?? (int)DefaultFreezeTime;

        Plugin.AddTimer(freezeTime, () =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id) return;

            player.TempSpeedMultiplier = QuickStartSpeed;
            CenterText.Print(player.Controller, "ВРЕМЯ ПОШЛО");

            Plugin.AddTimer(rank, () =>
            {
                // Снимаем только свой рывок: за это время могли повесить что-то другое.
                if (Math.Abs(player.TempSpeedMultiplier - QuickStartSpeed) < 0.001f)
                    player.TempSpeedMultiplier = 1f;
            });
        });
    }

    /// <summary>
    /// Тайм-аут: смертельный удар не проходит, но время для самого Повелителя замирает —
    /// секунду он стоит на месте с одним очком здоровья и ничего не может сделать.
    /// </summary>
    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var rank = victim.RankOf(Timeout);
        if (rank <= 0 || _timeoutUsed.Contains(victim.Slot)) return;
        if (victim.Pawn is not { } pawn || info.Damage < pawn.Health) return;

        _timeoutUsed.Add(victim.Slot);
        info.Damage = 0f;

        var stun = Math.Max(0.25f, TimeoutStun - rank * 0.25f);

        // Здоровье и заморозку трогаем следующим кадром: посреди обработки урона
        // менять состояние игрока опасно, на этом сервер уже падал.
        Server.NextFrame(() =>
        {
            if (victim.Pawn is not { } current || current.Health <= 0) return;

            Effects.SetHealth(current, 1, current.MaxHealth > 0 ? current.MaxHealth : 100);
            Effects.SetFrozen(current, true);
        });

        victim.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Тайм-аут!{ChatColors.Default} Смерть отложена, но вы замерли на {stun:0.0} с");

        Plugin.AddTimer(stun, () =>
        {
            if (victim.Pawn is { } current && current.Health > 0) Effects.SetFrozen(current, false);
        });
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Rewind);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        var depth = RewindBase + rank;
        if (PositionAgo(player.Slot, depth) is not { } past)
        {
            player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} Прошлого ещё нет — подождите пару секунд после спавна.");
            return false;
        }

        MoveBack(player, past);
        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Перемотка{ChatColors.Default} — вы там, где были {depth:0} с назад");
        return true;
    }

    /// <summary>Общий откат: вся карта возвращается на несколько секунд назад.</summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(MassRewind);
        if (rank <= 0 || player.Controller is not { } controller) return false;

        var depth = RewindBase + rank;
        var moved = 0;

        foreach (var target in Utilities.GetPlayers())
        {
            if (!target.IsValid || target.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Health <= 0) continue;
            if (PositionAgo(target.Slot, depth) is not { } past) continue;

            if (Plugin.Get(target) is { } state) MoveBack(state, past);
            else MoveBackPawn(pawn, past);

            CenterText.Print(target, "ВРЕМЯ ОТКАТИЛОСЬ");
            moved++;
        }

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Общий откат!{ChatColors.Default} Возвращено назад на {depth:0} с: {moved}");
        return moved > 0;
    }

    /// <summary>
    /// Перенос игрока в прошлое. Сам телепорт выполняется следующим кадром: заданный
    /// прямо в обработке команды, он оставляет клиента с рассинхроном — тот продолжает
    /// предсказывать движение от старой точки, и камера заглядывает внутрь стен.
    /// </summary>
    private void MoveBack(WarcraftPlayer player, Vector3 past)
    {
        player.GrantLaunchFallImmunity();

        Server.NextFrame(() =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0) return;

            MoveBackPawn(pawn, past);
        });
    }

    private static void MoveBackPawn(CCSPlayerPawn pawn, Vector3 past)
    {
        Effects.TeleportTo(pawn, SafeArrival(past));
        Effects.ResetStance(pawn);
    }

    /// <summary>
    /// Куда ставить возвращаемого. Точка из прошлого заведомо проходима — игрок там стоял,
    /// причём стоял на земле. Приподнимаем на пару юнитов, чтобы не вжать в пол, и всё.
    /// </summary>
    /// <remarks>
    /// Отступ в сторону при занятой точке пробовали — он и оказался причиной того, что
    /// игрока загоняло в стены: свободное направление выбиралось случайно, а проверить
    /// препятствие нечем. Двоих в одной точке движок расталкивает сам, стену — нет.
    /// </remarks>
    private static Vector3 SafeArrival(Vector3 past) => past with { Z = past.Z + ArrivalLift };

    /// <summary>
    /// Где игрок был примерно столько секунд назад. Берём ближайшую запись:
    /// след пишется раз в четверть секунды, точнее и не нужно.
    /// </summary>
    private Vector3? PositionAgo(int slot, float seconds)
    {
        if (!_history.TryGetValue(slot, out var trail) || trail.Count == 0) return null;

        var target = Server.CurrentTime - seconds;

        var best = trail[0];
        foreach (var sample in trail)
            if (Math.Abs(sample.Time - target) < Math.Abs(best.Time - target))
                best = sample;

        return best.Position;
    }
}
