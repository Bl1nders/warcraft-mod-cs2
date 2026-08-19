using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Нежить — базовая раса на выживание: живёт чужой кровью, ходит быстрее живых,
/// сковывает того, кто под прицелом, и ультой вытягивает здоровье у ближайшего врага,
/// где бы тот ни стоял.
/// </summary>
public sealed class UndeadScourge : Race
{
    private const int Vampirism = 0;
    private const int UnholyAura = 1;

    /// <summary>Прибавка скорости за ранг ауры. Четыре ранга дают потолок в 15%.</summary>
    private const float AuraSpeedPerRank = 0.0375f;
    private const int Numbness = 2;
    private const int Theft = 3;

    /// <summary>Сколько здоровья забирает похищение: 10 + 5 за ранг, до 30 на четвёртом.</summary>
    private const int TheftBase = 10;
    private const int TheftPerRank = 5;

    /// <summary>
    /// Выше этого нежить не поднимается никаким похищением. Ровно её обычный запас:
    /// ульта возвращает потерянное, а не выдаёт сверх положенного.
    /// </summary>
    private const int TheftHealthCap = 100;

    /// <summary>
    /// Ниже этого жертву похищение не опускает. Убивать оно не должно: базовой расе
    /// ульта, снимающая фраг нажатием и без линии видимости, не по чину.
    /// </summary>
    private const int TheftVictimFloor = 1;

    /// <summary>Доля урона, уходящая в здоровье, за ранг. На четвёртом выходит 20%.</summary>
    private const float VampirismPerRank = 0.05f;

    /// <summary>
    /// Прибавка к длительности оцепенения за ранг. Потолок — ровно 2 секунды:
    /// прежние три на awp-карте означали смерть, там за это время успевают дважды.
    /// </summary>
    private const float NumbnessPerRank = 0.25f;

    /// <summary>Насколько медленнее двигается оцепеневший.</summary>
    // Ровно вдвое, и одинаково на всех рангах: рангом растёт длительность, а не сила.
    private const float NumbnessSlow = 0.5f;

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "undead";
    public override string Name => "Нежить";
    public override string Description => "Живёт за счёт чужой крови: лечится от нанесённого урона, ходит быстрее живых, сковывает врага под прицелом и вытягивает здоровье через всю карту.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Вампиризм",
            Description = "Возвращает в здоровье часть нанесённого урона: 5% за ранг, до 20%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Аура нежити",
            Description = "Скорость передвижения: +3.75% за ранг, до +15%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Оцепенение",
            Description = "Замедляет врага под прицелом вдвое на любом ранге: 1 с + 0.25 с за ранг (1.25-2 с)",
            Kind = AbilityKind.Active,
            Cooldown = 30f,
        },
        new Ability
        {
            Name = "Похищение",
            Description = "Забирает 10 + 5 за ранг здоровья у ближайшего врага, где бы он ни стоял. Вас не поднимет выше 100, его не опустит ниже 1",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    public override void OnSpawn(WarcraftPlayer player)
    {
        var rank = player.RankOf(UnholyAura);
        if (rank > 0) player.BaseSpeedMultiplier = 1f + rank * AuraSpeedPerRank;
    }

    public override void OnDealDamage(WarcraftPlayer attacker, CCSPlayerController victim, CTakeDamageInfo info)
    {
        var rank = attacker.RankOf(Vampirism);
        if (rank <= 0 || attacker.Pawn is not { } pawn) return;

        var healed = Effects.Heal(pawn, (int)(info.Damage * rank * VampirismPerRank));
        if (healed > 0) CenterText.Print(attacker.Controller, $"+{healed} HP");
    }

    /// <summary>
    /// Оцепенение: цель вязнет на месте. Урона нет — это способ догнать убегающего
    /// или, наоборот, оторваться от того, кто догоняет вас.
    /// </summary>
    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Numbness);
        if (rank <= 0 || player.Controller is not { } controller) return false;

        var target = Effects.FindTargetInAim(controller, maxRange: 1200f, maxAngleDegrees: 15f);
        if (target is null)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Наведитесь на врага, которого видите.");
            return false;
        }

        if (Plugin.Get(target) is not { } state)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} По этой цели оцепенение не работает.");
            return false;
        }

        var duration = 1f + rank * NumbnessPerRank;
        state.TempSpeedMultiplier = NumbnessSlow;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Purple}Оцепенение{ChatColors.Default} → {target.PlayerName} ({duration:0.0} с)");
        CenterText.Print(target, "ОЦЕПЕНЕНИЕ");

        Plugin.AddTimer(duration, () =>
        {
            // Не снимаем чужое замедление, если за это время повесили что-то посильнее.
            if (Math.Abs(state.TempSpeedMultiplier - NumbnessSlow) < 0.001f) state.TempSpeedMultiplier = 1f;
        });

        return true;
    }

    /// <summary>
    /// Похищение: нежить тянет здоровье у ближайшего врага, не глядя на него и не
    /// нуждаясь в прямой видимости. Стен для этого голода нет.
    ///
    /// Две границы, обе намеренные. Себя ульта не поднимает выше обычной сотни —
    /// она возвращает потерянное, а не выдаёт сверх положенного, иначе нежить копила
    /// бы запас между стычками. Жертву не опускает ниже одного: базовой расе не по чину
    /// ульта, снимающая фраг нажатием и через полкарты.
    ///
    /// Здоровье жертве снимаем напрямую, а не уроном: урон прошёл бы через броню,
    /// поднял бы хуки рас и мог бы убить — а убивать эта способность не должна.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(Theft);
        if (rank <= 0 || player.Controller is not { } controller || player.Pawn is not { } pawn) return false;
        if (Effects.Origin(pawn) is not { } origin) return false;

        if (NearestEnemy(controller, origin) is not { } victim)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Красть не у кого — живых врагов нет.");
            return false;
        }

        if (victim.PlayerPawn.Value is not { IsValid: true } victimPawn) return false;

        var wanted = TheftBase + rank * TheftPerRank;

        // Берём столько, сколько у жертвы есть сверх единицы, и не больше запрошенного.
        var taken = Math.Min(wanted, Math.Max(0, victimPawn.Health - TheftVictimFloor));
        if (taken <= 0)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} У ближайшего врага брать уже нечего.");
            return false;
        }

        victimPawn.Health -= taken;
        Utilities.SetStateChanged(victimPawn, "CBaseEntity", "m_iHealth");

        // Себе добавляем не больше, чем не хватает до сотни: излишек просто пропадает.
        var healed = Math.Min(taken, Math.Max(0, TheftHealthCap - pawn.Health));
        if (healed > 0)
        {
            pawn.Health += healed;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }

        controller.PrintToChat(
            $"{WarcraftPlugin.Prefix} {ChatColors.Purple}Похищение{ChatColors.Default} → {victim.PlayerName}: отнято {taken}, себе {healed}");
        CenterText.Print(victim, "У ВАС КРАДУТ ЖИЗНЬ");

        return true;
    }

    /// <summary>
    /// Ближайший живой враг без оглядки на стены и дистанцию. Прямая видимость здесь
    /// не нужна намеренно — в этом вся суть способности.
    /// </summary>
    private static CCSPlayerController? NearestEnemy(CCSPlayerController source, Vector3 origin)
    {
        CCSPlayerController? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in Utilities.GetPlayers())
        {
            if (candidate is not { IsValid: true } || candidate.Team == source.Team) continue;
            if (candidate.PlayerPawn.Value is not { IsValid: true } candidatePawn || candidatePawn.Health <= 0) continue;
            if (Effects.Origin(candidatePawn) is not { } position) continue;

            var distance = (position - origin).LengthSquared();
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = candidate;
        }

        return best;
    }
}
