using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Разведчик — информация и контроль зрения: сам находит врагов, слепит того, кто под
/// прицелом, мерцает под огнём и подтягивает союзника себе на помощь.
/// </summary>
public sealed class Illusionist : Race
{
    private const int Sense = 0;
    private const int Shimmer = 1;

    /// <summary>Шанс мерцания за ранг. Четыре ранга дают потолок в 20%.</summary>
    private const double ShimmerChancePerRank = 0.05;
    private const int Flash = 2;
    private const int Gather = 3;

    private static readonly Random Rng = new();

    public override int UnlockTotalLevel => Unlocks.Tier(3);

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "illusionist";
    public override string Name => "Разведчик";
    public override string Description => "Знает, где все: сам показывает врагов на радаре, слепит того, кто под прицелом, мерцает под огнём и может выдернуть союзника к себе.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Чутьё",
            Description = "Сам показывает врагов на радаре на 2 с: раз в 20 с на 1 ранге, раз в 8 с на 4-м",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Мерцание",
            Description = "Шанс стать полупрозрачным на 2 с при получении урона: 5% за ранг, до 20%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Слепящая вспышка",
            Description = "Ослепляет врага под прицелом: 0.3 с за ранг, до 1.2 с",
            Kind = AbilityKind.Active,
            Cooldown = 20f,
        },
        new Ability
        {
            Name = "Призыв",
            Description = "Телепортирует к вам случайного живого союзника",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 70f,
        },
    ];

    public override void OnSpawn(WarcraftPlayer player)
    {
        var rank = player.RankOf(Sense);
        if (rank <= 0) return;

        var interval = Math.Max(8f, 24f - rank * 4f);

        Timer? senseTimer = null;
        senseTimer = Plugin.AddRoundTimer(interval, () =>
        {
            // Чутьё живёт вместе с носителем — иначе таймер тикал бы весь матч.
            if (player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id)
            {
                senseTimer?.Kill();
                return;
            }

            RevealEnemies(player, 2f);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RevealEnemies(WarcraftPlayer player, float duration)
    {
        if (player.Controller is not { } controller) return;

        var enemyTeam = controller.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        var enemies = Effects.AlivePlayersOfTeam(enemyTeam);
        if (enemies.Count == 0) return;

        CenterText.Print(controller, $"ЧУТЬЁ: на радаре врагов — {enemies.Count}");
        Spot(enemies, true);

        // Движок сбрасывает пометку сам, поэтому её приходится подновлять всё время действия.
        const float refresh = 0.2f;
        var ticksLeft = (int)(duration / refresh);

        Timer? spotTimer = null;
        spotTimer = Plugin.AddRoundTimer(refresh, () =>
        {
            var keep = --ticksLeft > 0;
            Spot(enemies, keep);
            if (!keep) spotTimer?.Kill();
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void Spot(List<CCSPlayerController> enemies, bool spotted)
    {
        foreach (var enemy in enemies)
            if (enemy.IsValid && enemy.PlayerPawn.Value is { IsValid: true } pawn && pawn.Health > 0)
                Effects.MarkSpotted(pawn, spotted);
    }

    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var rank = victim.RankOf(Shimmer);
        if (rank <= 0 || victim.RenderAlpha < 255) return;

        if (Rng.NextDouble() >= rank * ShimmerChancePerRank) return;

        victim.RenderAlpha = 60;
        CenterText.Print(victim.Controller, "МЕРЦАНИЕ");

        // Оружие прячем вместе с телом: полупрозрачный игрок с чётким стволом в руках
        // выдаёт себя ровно тем, что должно его скрывать.
        var carried = victim.Pawn is { } pawn ? Effects.HideWeapons(pawn) : [];

        Plugin.AddTimer(2f, () =>
        {
            if (victim.RenderAlpha == 60) victim.RenderAlpha = 255;
            Effects.ShowWeapons(carried);
        });
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Flash);
        if (rank <= 0 || player.Controller is not { } controller) return false;

        var target = Effects.FindTargetInAim(controller, maxRange: 2000f, maxAngleDegrees: 20f);
        if (target?.PlayerPawn.Value is not { IsValid: true } targetPawn || targetPawn.Health <= 0)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Наведитесь на врага, чтобы ослепить его.");
            return false;
        }

        var duration = rank * 0.3f;
        Effects.Blind(targetPawn, duration);
        VisualEffects.SpawnParticleAt(Plugin, VisualEffects.Fx.Sparks, targetPawn, heightOffset: 55f, lifetime: 1.5f,
            tint: Color.FromArgb(255, 255, 255, 255));
        VisualEffects.PlaySound(targetPawn, "Flashbang.Explode");

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.LightBlue}Вспышка{ChatColors.Default} → {target.PlayerName} ({duration:0.0} с)");
        return true;
    }

    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        if (player.RankOf(Gather) <= 0) return false;
        if (player.Controller is not { } controller || player.Pawn is not { } pawn) return false;
        if (Effects.Origin(pawn) is not { } center) return false;

        // Берём игроков из движка, а не из реестра мода: в реестре нет ботов.
        var mates = Effects.AlivePlayersOfTeam(controller.Team)
            .Where(mate => mate.Slot != player.Slot)
            .ToList();

        if (mates.Count == 0)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Некого призывать — живых союзников нет.");
            return false;
        }

        var chosen = mates[Rng.Next(mates.Count)];
        if (chosen.PlayerPawn.Value is not { IsValid: true } chosenPawn) return false;

        // Ставим рядом, а не в ту же точку, иначе призванный застрянет в вас.
        var angle = (float)(Rng.NextDouble() * MathF.Tau);
        var offset = new System.Numerics.Vector3(MathF.Cos(angle) * 70f, MathF.Sin(angle) * 70f, 10f);

        Effects.TeleportTo(chosenPawn, center + offset);
        VisualEffects.SpawnParticleAt(Plugin, VisualEffects.Fx.Sparks, chosenPawn, heightOffset: 40f, lifetime: 2f,
            tint: Color.FromArgb(255, 200, 120, 255));

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.LightPurple}Призыв!{ChatColors.Default} К вам перенесён {chosen.PlayerName}");
        CenterText.Print(chosen, "ВАС ПРИЗВАЛИ");
        return true;
    }
}
