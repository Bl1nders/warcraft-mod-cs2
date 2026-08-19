using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;

namespace WarcraftMod.Races;

/// <summary>
/// Соник — скорость по земле. Кролик набирает разгон прыжками, Соник просто быстрее всех:
/// бежит, разгоняется от чужих пуль, входит в поворот заносом и один раз переживает смерть.
/// </summary>
public sealed class Sonic : Race
{
    private const int Speed = 0;
    private const int Adrenaline = 1;
    private const int Drift = 2;
    private const int Rings = 3;

    /// <summary>Ниже этой скорости заносить нечего — способность просто не сработает.</summary>
    private const float MinDriftSpeed = 120f;

    /// <summary>
    /// Насколько поднимается потолок скорости на время заноса и сколько он держится.
    /// Без поднятого потолка движок срезает разгон в тот же тик, и рывок по земле
    /// не получается вовсе — ровно поэтому остальные рывки в моде подбрасывают вверх.
    /// </summary>
    private const float DriftSpeedCap = 2.2f;
    private const float DriftDuration = 0.5f;

    /// <summary>Сколько держится ускорение от полученного урона и как часто оно доступно.</summary>
    private const float AdrenalineDuration = 2f;
    private const float AdrenalineCooldown = 10f;

    public override int UnlockTotalLevel => Unlocks.Tier(2);

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "sonic";
    public override string Name => "Соник";
    public override string Description => "Самый быстрый на карте: разгоняется от чужих пуль, входит в повороты заносом и один раз переживает смертельный удар.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Скорость",
            Description = "Скорость передвижения: +7.5% за ранг, до +30%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Адреналин",
            Description = "Получив урон, ускоряетесь на 2 с: +8% за ранг, до +32%. Не чаще раза в 10 с",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Занос",
            Description = "Рывок в ту сторону, куда вы бежите: +220 к скорости и ещё 40 за ранг. На месте не работает",
            Kind = AbilityKind.Active,
            Cooldown = 8f,
        },
        new Ability
        {
            Name = "Кольца",
            Description = "10 с: смертельный удар не убивает и возвращает 5 HP за ранг",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 70f,
        },
    ];

    /// <summary>Игровое время, начиная с которого адреналин снова сработает. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _adrenalineReadyAt = new();

    public override void OnSpawn(WarcraftPlayer player)
    {
        _adrenalineReadyAt.Remove(player.Slot);

        var rank = player.RankOf(Speed);
        if (rank > 0) player.BaseSpeedMultiplier = 1f + rank * 0.075f;
    }

    /// <summary>
    /// Адреналин: чужой выстрел подстёгивает. Ускорение приходит после того, как вас нашли,
    /// поэтому помогает уйти, а не догнать. Перезарядка не даёт держать его постоянно
    /// под плотным огнём — иначе самая опасная для Соника ситуация стала бы самой выгодной.
    /// </summary>
    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var rank = victim.RankOf(Adrenaline);
        if (rank <= 0) return;

        if (_adrenalineReadyAt.TryGetValue(victim.Slot, out var readyAt) && readyAt > Server.CurrentTime) return;

        _adrenalineReadyAt[victim.Slot] = Server.CurrentTime + AdrenalineCooldown;

        var boost = 1f + rank * 0.08f;
        victim.TempSpeedMultiplier = boost;

        Plugin.AddTimer(AdrenalineDuration, () =>
        {
            // Снимаем только своё ускорение: за это время могли повесить что-то другое.
            if (Math.Abs(victim.TempSpeedMultiplier - boost) < 0.001f) victim.TempSpeedMultiplier = 1f;
        });
    }

    /// <summary>
    /// Занос: короткий толчок туда, куда игрок уже бежит, — независимо от того, куда он смотрит.
    /// Бежите вбок при взгляде вперёд — унесёт вбок. Скорости не добавляет сверх той,
    /// что уже набрана, и на месте бесполезен: разгоняться всё равно придётся самому.
    /// </summary>
    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Drift);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        var velocity = pawn.AbsVelocity;
        var direction = new Vector3(velocity.X, velocity.Y, 0f);

        if (direction.Length() < MinDriftSpeed)
        {
            player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} Занос работает только на ходу — сначала разгонитесь.");
            return false;
        }

        var heading = Vector3.Normalize(direction);
        var boosted = direction.Length() + 220f + rank * 40f;

        // Сначала поднимаем потолок — и сразу движку, не дожидаясь общего тика мода,
        // иначе он успеет срезать впрыснутую скорость.
        player.TempSpeedMultiplier = DriftSpeedCap;
        pawn.VelocityModifier = player.SpeedMultiplier;

        // Саму скорость ставим на следующем кадре: заданную с земли в этом же кадре
        // движок затирает своей. На тех же граблях стоит перенос прибавки к прыжку.
        Server.NextFrame(() =>
        {
            if (player.Pawn is not { } current || current.Health <= 0) return;

            current.AbsVelocity.X = heading.X * boosted;
            current.AbsVelocity.Y = heading.Y * boosted;
        });

        Plugin.AddTimer(DriftDuration, () =>
        {
            // Возвращаем потолок только если он всё ещё наш: за полсекунды мог сработать адреналин.
            if (Math.Abs(player.TempSpeedMultiplier - DriftSpeedCap) < 0.001f) player.TempSpeedMultiplier = 1f;
        });

        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Занос!");
        return true;
    }

    /// <summary>Кольца: один смертельный удар уходит в них, а не в ежа.</summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(Rings);
        if (rank <= 0 || player.Pawn is null) return false;

        const float duration = 10f;

        player.HasDeathWard = true;
        player.DeathWardMessage = "Кольца рассыпались, но вы живы!";
        player.DeathWardHeal = rank * 5;

        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Gold}Кольца собраны{ChatColors.Default} — {duration:0} с смертельный удар не пройдёт");

        Plugin.AddTimer(duration, () =>
        {
            if (!player.HasDeathWard) return;

            player.HasDeathWard = false;
            player.DeathWardMessage = null;
            player.DeathWardHeal = 0;
            player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} Кольца растеряны.");
        });

        return true;
    }
}
