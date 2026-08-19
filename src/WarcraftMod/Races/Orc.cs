using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Орда Орков — базовая раса напора. Носит тяжёлое как лёгкое, разгоняется с каждого
/// фрага и подхлёстывает кличем всю команду. Ульта — бешенство: ствол в сторону, удар
/// ножа убивает наотмашь, а следом приходит расплата.
/// </summary>
public sealed class Orc : Race
{
    private const int Charge = 0;
    private const int StrongArms = 1;
    private const int BattleCry = 2;
    private const int AncestralBlood = 3;

    /// <summary>
    /// Скорость налегке — с ножом в руках. От неё считается, насколько тяжёлое оружие
    /// замедлило орка и сколько из этого замедления способность возвращает.
    /// </summary>
    private const float UnburdenedSpeed = 250f;

    /// <summary>Как часто пересчитывается компенсация: оружие меняют посреди боя.</summary>
    private const float ArmsTick = 0.2f;

    /// <summary>
    /// Разбег: прибавка к скорости за ранг и сколько она держится. Потолок ровно +10%
    /// на четвёртом ранге — задан владельцем; выше начиналось бы наслоение с кличем,
    /// и орк после фрага уезжал бы быстрее Соника.
    /// </summary>
    private const float ChargeSpeedPerRank = 0.025f;
    private const float ChargeDuration = 3f;

    /// <summary>
    /// Радиус, длительность и прибавка к скорости за ранг у боевого клича.
    /// Орк попадает под собственный клич наравне с остальными — он стоит в центре
    /// радиуса и в список входит сам, отдельной выдачи для этого не нужно.
    /// </summary>
    private const float CryRadius = 600f;
    private const float CryDuration = 6f;
    private const float CrySpeedPerRank = 0.0375f;

    /// <summary>
    /// Кровь на кулаках живёт в два хода, и второй наступает всегда.
    ///
    /// Первый — окно, в котором один удар ножом убивает наотмашь: 2.5 с за ранг, до 10 с
    /// на четвёртом. Оно кончается либо смертельным ударом, либо само по себе.
    ///
    /// Второй — расплата: 8 секунд без стрельбы. Срок расплаты одинаков на всех рангах
    /// намеренно — растёт только окно, то есть ранг покупает не силу, а время на то,
    /// чтобы подобраться.
    ///
    /// Стрельба закрыта в обоих ходах, и нож открыт в обоих: способность про ближний
    /// бой целиком, а не про удар с последующей перестрелкой. Ранг тем самым удлиняет
    /// не только окно, но и время, проведённое без ствола.
    ///
    /// Запрет стрельбы подновляется тиком: он стоит на тиках следующей атаки у оружия
    /// в руках, и смена ствола его снимает.
    /// </summary>
    private const float LethalWindowPerRank = 2.5f;
    private const float PenaltyDuration = 8f;
    private const float FistsTick = 0.2f;

    private static readonly Random Rng = new();

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "orc";
    public override string Name => "Орда Орков";
    public override string Description => "Прёт вперёд и тащит за собой: тяжёлое оружие ему не помеха, после фрага разгоняется, кличем ускоряет команду. В бешенстве бросает ствол и убивает ножом наотмашь.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Разбег",
            Description = "После убийства скорость выше 3 с: +2.5% за ранг, до +10%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Сильные руки",
            Description = "Тяжёлое оружие замедляет вас меньше: четверть замедления за ранг, на четвёртом не замедляет вовсе",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Боевой клич",
            Description = "6 с: вы и союзники в радиусе 600 быстрее на 3.75% за ранг, до 15%",
            Kind = AbilityKind.Active,
            Cooldown = 25f,
        },
        new Ability
        {
            Name = "Кровь на кулаках",
            Description = "2.5 с за ранг (до 10 с) вы бьётесь одним ножом, и удар убивает наотмашь. Потом 8 с расплаты: нож остаётся, но бьёт обычно",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>До какого времени ждёт смертельный удар. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _lethalUntil = new();

    /// <summary>До какого времени орк лишён стрельбы. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _penaltyUntil = new();

    private bool LethalReady(int slot) =>
        _lethalUntil.TryGetValue(slot, out var until) && until > Server.CurrentTime;

    private bool InPenalty(int slot) =>
        _penaltyUntil.TryGetValue(slot, out var until) && until > Server.CurrentTime;

    /// <summary>
    /// Расплата намеренно переживает конец раунда — иначе последний фраг раунда уходил
    /// бы безнаказанным, — но начало следующего пережить не должна.
    ///
    /// Сбрасывать её надо именно здесь, а не на спавне. Спавн отрабатывает кадром позже
    /// события, и таймер расплаты успевал тикнуть до него: запрет ложился уже в новом
    /// раунде, и первый выстрел не проходил почти секунду. Этот хук зовётся до спавна,
    /// и щели не остаётся.
    /// </summary>
    public override void OnRoundStart(WarcraftPlayer player)
    {
        _lethalUntil.Remove(player.Slot);
        _penaltyUntil.Remove(player.Slot);
    }

    public override void OnSpawn(WarcraftPlayer player)
    {
        _lethalUntil.Remove(player.Slot);
        _penaltyUntil.Remove(player.Slot);

        // Снимать запрет здесь нечего, и пробовать нельзя. Оружия в руках на спавне
        // ещё нет, поэтому щадящий нож помощник пропускает вызов дальше и ставит
        // «атаковать сейчас» — а движок в этот самый момент выставляет туда время
        // доставания оружия. Ноль его стирал, и первая секунда раунда уходила игроку
        // даром. Переживать смерть запрету всё равно нечем: пишется он остатком до
        // конца способности, а оружие на спавне выдаётся новыми сущностями.
        StartStrongArms(player);
    }

    /// <summary>
    /// Сильные руки: возвращают ту скорость, которую отняло тяжёлое оружие.
    ///
    /// Пересчитывается тиком, а не выдаётся на спавне: оружие меняют посреди боя, и
    /// прибавка, посчитанная один раз под винтовку, осталась бы висеть с ножом в руках.
    ///
    /// Считаем от <c>MovementServices.Maxspeed</c> — это и есть предел, который движок
    /// выставил под оружие в руках. Компенсация идёт в <c>BaseSpeedMultiplier</c>, потому
    /// что мод применяет его как <c>VelocityModifier</c>, а тот умножает как раз этот
    /// предел: отношение «налегке к нынешнему» возвращает орка ровно к скорости с ножом.
    /// </summary>
    private void StartStrongArms(WarcraftPlayer player)
    {
        var rank = player.RankOf(StrongArms);
        if (rank <= 0) return;

        var share = rank / 4f;

        Timer? armsTimer = null;
        armsTimer = Plugin.AddRoundTimer(ArmsTick, () =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id)
            {
                armsTimer?.Kill();
                return;
            }

            var limit = pawn.MovementServices?.Maxspeed ?? 0f;

            // Ноль означает, что предел ещё не выставлен, а значение выше налегке —
            // что замедлять нечего. В обоих случаях прибавка не нужна.
            if (limit <= 1f || limit >= UnburdenedSpeed)
            {
                if (player.BaseSpeedMultiplier > 1f) player.BaseSpeedMultiplier = 1f;
                return;
            }

            player.BaseSpeedMultiplier = 1f + (UnburdenedSpeed / limit - 1f) * share;
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Смертельный удар: один раз за ульту и только ножом. Потратив его, орк сразу
    /// уходит в расплату — окно закрывается ударом, а не только сроком.
    ///
    /// Число ставим большое, а не убиваем сами: вызывать смерть изнутри хука урона
    /// нельзя, сервер от этого падает. Пусть добьёт движок своим обычным путём.
    /// </summary>
    public override void OnDealDamage(WarcraftPlayer attacker, CCSPlayerController victim, CTakeDamageInfo info)
    {
        if (!LethalReady(attacker.Slot)) return;
        if (attacker.Pawn is not { } pawn || !Effects.HoldsKnife(pawn)) return;

        info.Damage = 500f;
        _lethalUntil.Remove(attacker.Slot);

        CenterText.Print(attacker.Controller, "НАСМЕРТЬ!");
        StartPenalty(attacker);
    }

    /// <summary>
    /// Расплата: десять секунд без стрельбы. Нож остаётся и бьёт обычно.
    ///
    /// Таймер здесь обычный, а не раундовый, и это не оплошность. Смертельный удар часто
    /// оказывается последним фрагом раунда, а конец раунда гасит все раундовые таймеры —
    /// расплата исчезала вместе с ним, и орк доигрывал добивание со свободной стрельбой.
    /// Своих ограничителей обычному таймеру хватает: он гаснет по сроку, по смерти, по
    /// смене расы и по смене карты, а спавн сбрасывает саму расплату.
    /// </summary>
    private void StartPenalty(WarcraftPlayer player)
    {
        var until = Server.CurrentTime + PenaltyDuration;
        _penaltyUntil[player.Slot] = until;

        player.Controller?.PrintToChat(
            $"{WarcraftPlugin.Prefix} {ChatColors.Red}Руки в крови{ChatColors.Default} — {PenaltyDuration:0} с без стрельбы");

        // Первый запрет ставим сразу: таймер заходит только через тик, и этой щели
        // хватало, чтобы сразу после ножа прошёл выстрел.
        if (player.Pawn is { } pawn && pawn.Health > 0) Effects.BlockGuns(pawn, PenaltyDuration);

        Timer? penaltyTimer = null;
        penaltyTimer = Plugin.AddTimer(FistsTick, () =>
        {
            var secondsLeft = until - Server.CurrentTime;

            if (secondsLeft <= 0f || !InPenalty(player.Slot)
                || player.Pawn is not { } current || current.Health <= 0 || player.Race?.Id != Id)
            {
                penaltyTimer?.Kill();
                _penaltyUntil.Remove(player.Slot);
                return;
            }

            // Подновляем весь остаток, а не пару тиков вперёд: пропущенный заход таймера
            // тогда не открывает окна для выстрела.
            Effects.BlockGuns(current, secondsLeft);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Разбег: убил — беги дальше. Способность про инерцию, а не про награду, поэтому
    /// держится недолго и ровно столько, чтобы успеть сменить позицию или догнать второго.
    /// </summary>
    public override void OnKill(WarcraftPlayer killer, CCSPlayerController victim, EventPlayerDeath ev)
    {
        var rank = killer.RankOf(Charge);
        if (rank <= 0) return;

        var boost = 1f + rank * ChargeSpeedPerRank;
        killer.TempSpeedMultiplier = boost;

        CenterText.Print(killer.Controller, "РАЗБЕГ!");

        Plugin.AddTimer(ChargeDuration, () =>
        {
            // Снимаем только свой разгон: за три секунды могли повесить клич или замедление.
            if (Math.Abs(killer.TempSpeedMultiplier - boost) < 0.001f) killer.TempSpeedMultiplier = 1f;
        });
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(BattleCry);
        if (rank <= 0 || player.Controller is not { } controller || player.Pawn is not { } pawn) return false;
        if (Effects.Origin(pawn) is not { } center) return false;

        var boost = 1f + rank * CrySpeedPerRank;

        var raised = 0;
        foreach (var ally in Effects.PlayersInRadius(center, CryRadius, controller.Team))
        {
            if (Plugin.Get(ally) is not { } state) continue;

            state.TempSpeedMultiplier = boost;
            raised++;

            if (ally.Slot != player.Slot)
                CenterText.Print(ally, "БОЕВОЙ КЛИЧ!");

            Plugin.AddTimer(CryDuration, () =>
            {
                // Не снимаем чужой эффект скорости, если за это время повесили другой.
                if (Math.Abs(state.TempSpeedMultiplier - boost) < 0.001f) state.TempSpeedMultiplier = 1f;
            });
        }

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Red}Боевой клич!{ChatColors.Default} Поднято своих: {raised}");
        return true;
    }

    /// <summary>
    /// Кровь на кулаках: окно на один смертельный удар, за которым всегда следует
    /// расплата. Не сложилось — расплата всё равно придёт, и в этом вся ставка.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(AncestralBlood);
        if (rank <= 0 || player.Pawn is null) return false;

        var window = rank * LethalWindowPerRank;
        _lethalUntil[player.Slot] = Server.CurrentTime + window;

        player.Controller?.PrintToChat(
            $"{WarcraftPlugin.Prefix} {ChatColors.Red}Кровь на кулаках{ChatColors.Default} — {window:0.0} с на один удар ножом наотмашь, стрелять нельзя");

        // Первый запрет ставим сразу, не дожидаясь захода таймера: иначе выстрел
        // проходит именно в тот момент, когда способность только нажали.
        if (player.Pawn is { } armed && armed.Health > 0) Effects.BlockGuns(armed, window);

        Timer? windowTimer = null;
        windowTimer = Plugin.AddRoundTimer(FistsTick, () =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id)
            {
                windowTimer?.Kill();
                return;
            }

            // Удар мог закрыть окно раньше срока — тогда расплату завёл он сам.
            if (LethalReady(player.Slot))
            {
                Effects.BlockGuns(pawn, _lethalUntil[player.Slot] - Server.CurrentTime);
                return;
            }

            windowTimer?.Kill();

            // Запрет не снимаем: расплата подхватывает его тем же тиком, и щели между
            // окном и расплатой не остаётся.
            if (!InPenalty(player.Slot)) StartPenalty(player);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }
}
