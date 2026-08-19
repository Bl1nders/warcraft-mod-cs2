using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Кролик — горизонталь и разгон. Кузнечик забирается вверх, кролик уходит вперёд:
/// прыгает без остановки, набирает скорость и на ходу держит удар лучше, чем стоя.
/// Ультой расшвыривает всех вплотную каждым приземлением.
/// </summary>
public sealed class Rabbit : Race
{
    private const int Momentum = 0;
    private const int BunnyHop = 1;
    private const int Rollback = 2;
    private const int Stomp = 3;

    /// <summary>Топот бьёт вплотную — чуть шире батута Кузнечика, но не больше.</summary>
    private const float StompRadius = 170f;

    /// <summary>По высоте — только рядом: этажом выше трясти никого не надо.</summary>
    private const float StompHeight = 90f;

    /// <summary>Минимум между двумя топотами: распрыжка иначе бьёт каждый кадр.</summary>
    private const float StompInterval = 0.3f;

    /// <summary>
    /// Скорость, ниже которой разгон не считается, и та, на которой он полный.
    /// Числа привязаны к реальным: шаг и присед — ниже 150, бег с оружием — около 230,
    /// разогнанная распрыжка на полной прокачке — порядка 300. Ставить потолок выше
    /// бессмысленно: движок всё равно режет горизонтальную скорость.
    /// </summary>
    private const float SlowSpeed = 150f;
    private const float FullSpeed = 300f;

    /// <summary>
    /// Прибавка скорости за прыжок и потолок <b>собственной прибавки мода</b> за ранг:
    /// 4 × 20% = +80% на четвёртом ранге.
    ///
    /// Раньше разгон включался только с четвёртого ранга, а ранги 1-3 давали сам
    /// автопрыжок. С 17.08.2026 прыжок раздаёт движок всему серверу, и на тех рангах
    /// не осталось бы ничего — поэтому разгон работает с первого, а ранг поднимает потолок.
    ///
    /// Потолок считается по накопленной прибавке мода, а не по общей скорости, и это
    /// решение принято осознанно 17.08.2026. Общая крышка мерялась от скорости на первом
    /// прыжке серии, и умеющий стрейфить выбирал её сам за три прыжка — способность
    /// работала тем хуже, чем лучше играл человек, что для расы про распрыжку наизнанку.
    /// Теперь стрейфовый разгон идёт сверху и мод к нему только домножает.
    ///
    /// Цена известна и принята: разогнавшись стрейфами до 400, кролик уедет к 720.
    /// Если это окажется неуправляемым, крутить надо <see cref="HopSpeedCapPerRank"/> —
    /// шаг отвечает за то, как долго идти до потолка, а не за то, где он.
    ///
    /// Разгон задаётся <b>самой скоростью</b>, а не множителем. Через
    /// <c>TempSpeedMultiplier</c> он не работал вовсе: множитель уходит в
    /// <c>VelocityModifier</c>, а тот масштабирует наземную скорость, и в распрыжке
    /// игрок на земле практически не бывает. Замер 17.08.2026: множитель рос до 1.3,
    /// на пешке стоял тот же, а горизонтальная скорость не двигалась с 250.
    /// </summary>
    private const float HopSpeedStep = 0.04f;
    private const float HopSpeedCapPerRank = 0.20f;

    /// <summary>
    /// Ниже этой скорости прыжок в зачёт не идёт. Дело не в том, что умножать почти ноль
    /// бессмысленно, а в том, что запас прибавки тратился бы впустую: прыгая на месте,
    /// кролик выбрал бы весь потолок и вышел в разбег уже без него.
    /// </summary>
    private const float MinChainSpeed = 50f;

    public override int UnlockTotalLevel => Unlocks.Tier(1);

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "rabbit";
    public override string Name => "Кролик";
    public override string Description => "Живёт в движении: разгоняется с каждым прыжком, на ходу держит удар лучше, чем стоя, отпрыгивает назад не отводя прицела и в топоте расшвыривает всех при каждом приземлении.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Разгон",
            Description = "Урон по вам меньше в движении: 4% за ранг, до 16% — вполсилы на бегу, полностью в распрыжке, на месте не работает",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Банни-хоп",
            Description = "Каждый прыжок разгоняет: +4% к текущей скорости, сверх вашего разгона — до +20% за ранг, +80% на четвёртом",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Кувырок назад",
            Description = "Отпрыгнуть назад, не отводя прицела: сила 350 + 70 за ранг",
            Kind = AbilityKind.Active,
            Cooldown = 12f,
        },
        new Ability
        {
            Name = "Топот",
            Description = "10 с: каждое приземление подбрасывает врагов вплотную и трясёт им экран; сила 300 + 40 за ранг",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>Игровое время, до которого у игрока топочет приземление. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _stompUntil = new();

    /// <summary>Когда топот срабатывал в последний раз — чтобы распрыжка не била каждый кадр.</summary>
    private readonly Dictionary<int, float> _lastStompAt = new();

    /// <summary>
    /// Сколько мод уже накрутил сверху в текущей серии прыжков: 1.0 — ещё ничего.
    /// Ключ — слот. Именно это число упирается в потолок, а не скорость игрока.
    /// </summary>
    private readonly Dictionary<int, float> _hopBoost = new();

    public override void OnSpawn(WarcraftPlayer player)
    {
        _stompUntil.Remove(player.Slot);
        _lastStompAt.Remove(player.Slot);
        _hopBoost.Remove(player.Slot);

        if (player.RankOf(BunnyHop) <= 0) return;

        player.AutoBhop = true;
        StartChainReset(player);
    }

    /// <summary>
    /// Разгон: попасть по несущемуся кролику труднее. Считаем по горизонтальной скорости —
    /// защита растёт от обычного бега к разогнанному и полностью пропадает на месте.
    /// </summary>
    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var rank = victim.RankOf(Momentum);
        if (rank <= 0 || victim.Pawn is not { } pawn) return;

        var velocity = pawn.AbsVelocity;
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (speed <= SlowSpeed) return;

        var share = MathF.Min(1f, (speed - SlowSpeed) / (FullSpeed - SlowSpeed));
        info.Damage *= 1f - rank * 0.04f * share;
    }

    /// <summary>
    /// Серия прыжков закончилась — возвращаем запас прибавки, чтобы следующая серия
    /// начиналась с полным. Гасить саму скорость не надо: остановился кролик или нет,
    /// за него решит трение, как за всех остальных.
    /// </summary>
    private void StartChainReset(WarcraftPlayer player)
    {
        Timer? resetTimer = null;
        resetTimer = Plugin.AddRoundTimer(0.25f, () =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id)
            {
                resetTimer?.Kill();
                return;
            }

            if (player.JumpHeld || !Effects.IsOnGround(pawn)) return;

            _hopBoost.Remove(player.Slot);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    public override void OnAutoHop(WarcraftPlayer player)
    {
        // Прыжковое приземление — это и есть касание земли, на котором топочет ульта.
        // Обычные приземления ловит её собственный таймер: там пробел не держат.
        if (IsStomping(player.Slot)) TryStomp(player);

        var rank = player.RankOf(BunnyHop);
        if (rank <= 0 || player.Pawn is not { } pawn) return;

        var velocity = pawn.AbsVelocity;
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        if (speed < MinChainSpeed) return;

        // Потолок стоит на прибавке мода, а не на скорости игрока: свой разгон стрейфами
        // кролик набирает сверх него и ни во что не упирается.
        var boost = _hopBoost.GetValueOrDefault(player.Slot, 1f);
        var cap = 1f + rank * HopSpeedCapPerRank;
        if (boost >= cap) return;

        var next = MathF.Min(cap, boost * (1f + HopSpeedStep));
        _hopBoost[player.Slot] = next;

        // Домножаем на прирост запаса, а не на полный шаг: последний прыжок перед
        // потолком добавляет ровно остаток, без перелёта.
        var scale = next / boost;

        // Разгоняем по направлению движения, не меняя его: множитель на обе оси сразу.
        pawn.AbsVelocity.X = velocity.X * scale;
        pawn.AbsVelocity.Y = velocity.Y * scale;
    }


    /// <summary>
    /// Кувырок назад: уводит из-под огня, не разворачивая игрока. Прицел остаётся
    /// на противнике — в этом весь смысл, отступать спиной вперёд можно и без способности.
    /// </summary>
    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Rollback);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        // Строго против взгляда по горизонтали: вверх добавим отдельно, иначе
        // взгляд в пол уводил бы кувырок в потолок, а взгляд в небо — в землю.
        var forward = Effects.ForwardVector(pawn);
        var back = new Vector3(-forward.X, -forward.Y, 0f);
        if (back.LengthSquared() < 0.01f) back = -Vector3.UnitX;

        var direction = Vector3.Normalize(back) + new Vector3(0f, 0f, 0.35f);
        Effects.Push(pawn, direction, 350f + rank * 70f);

        // После своего же подброса кролик не должен разбиваться о землю.
        player.GrantLaunchFallImmunity();

        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Lime}Кувырок!");
        return true;
    }

    /// <summary>
    /// Топот: на десять секунд каждое приземление кролика бьёт по земле вокруг.
    /// Ульта не заменяет распрыжку, а требует её — стоя на месте она бесполезна.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        if (player.RankOf(Stomp) <= 0) return false;
        if (player.Controller is not { } controller || player.Pawn is null) return false;

        const float duration = 10f;
        _stompUntil[player.Slot] = Server.CurrentTime + duration;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Lime}Топот!{ChatColors.Default} {duration:0} с — прыгайте, каждое приземление бьёт по земле");

        // Приземления с зажатым пробелом ловит OnAutoHop, обычные — этот таймер.
        var wasAirborne = false;

        Timer? watch = null;
        watch = Plugin.AddRoundTimer(0.1f, () =>
        {
            if (!IsStomping(player.Slot) || player.Pawn is not { } current || current.Health <= 0)
            {
                _stompUntil.Remove(player.Slot);
                watch?.Kill();
                return;
            }

            var onGround = Effects.IsOnGround(current);
            if (onGround && wasAirborne) TryStomp(player);
            wasAirborne = !onGround;
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }

    private bool IsStomping(int slot) =>
        _stompUntil.TryGetValue(slot, out var until) && until > Server.CurrentTime;

    /// <summary>Удар по земле вокруг приземлившегося кролика. Урона нет — подброс и сбитый прицел.</summary>
    private void TryStomp(WarcraftPlayer player)
    {
        if (_lastStompAt.TryGetValue(player.Slot, out var last) && Server.CurrentTime - last < StompInterval) return;
        if (player.Controller is not { } controller || player.Pawn is not { } pawn) return;
        if (Effects.Origin(pawn) is not { } center) return;

        _lastStompAt[player.Slot] = Server.CurrentTime;

        var force = 300f + player.RankOf(Stomp) * 40f;
        var enemyTeam = controller.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

        foreach (var enemy in Effects.PlayersInRadius(center, StompRadius, enemyTeam))
        {
            if (enemy.PlayerPawn.Value is not { IsValid: true } enemyPawn) continue;
            if (Effects.Origin(enemyPawn) is not { } position) continue;

            // Радиус считаем по горизонтали, высоту ограничиваем отдельно — иначе
            // топот доставал бы через перекрытие тех, кто стоит этажом выше или ниже.
            if (MathF.Abs(position.Z - center.Z) > StompHeight) continue;

            Effects.Push(enemyPawn, new Vector3(0f, 0f, 1f), force);

            // Подбросили — значит и за приземление отвечаем мы: раса без урона.
            Plugin.Get(enemy)?.GrantLaunchFallImmunity();

            // Тряска ставится в точке самого подброшенного и с малым радиусом:
            // сбить прицел надо ему, а не всем, кто оказался рядом.
            VisualEffects.ScreenShake(Plugin, position, amplitude: 10f, radius: 120f, duration: 0.7f);

            CenterText.Print(enemy, "ТОПОТ!");
        }
    }
}
