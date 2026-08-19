using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Альянс Людей — базовая раса поддержки. Сам по себе слаб, зато снабжает команду
/// гранатами, закрывает прорыв дымом и приносит пользу даже смертью: убийца остаётся
/// подсвеченным для своих. Чем дольше живёт подряд, тем крепче становится.
/// </summary>
public sealed class HumanAlliance : Race
{
    private const int LastLook = 0;
    private const int Veteran = 1;
    private const int SmokeKit = 2;
    private const int SupplyCrate = 3;

    /// <summary>
    /// Ветеран: сколько здоровья добавляет каждый прожитый подряд раунд и каков потолок
    /// прибавки за ранг. На четвёртом выходит ровно 110 HP — предел задан владельцем.
    ///
    /// Прибавка по единице намеренно: десять раундов подряд без единой смерти — это и
    /// есть та редкость, за которую способность платит. Быстрый набор превратил бы её
    /// в постоянные 110, то есть в скучную прибавку к здоровью.
    /// </summary>
    private const int VeteranPerRound = 1;
    private const int VeteranCapBase = 2;
    private const int VeteranCapPerRank = 2;

    /// <summary>
    /// Сколько держится контур на убийце: 0.75 с за ранг, три секунды на четвёртом.
    /// Способность намеренно скромная — она срабатывает, когда вы уже мертвы, и
    /// служит команде, а не вам.
    /// </summary>
    private const float LastLookPerRank = 0.75f;

    /// <summary>С какого ранга к дыму добавляется светошумовая.</summary>
    private const int FlashbangRank = 3;

    /// <summary>Гранаты, которые может выдать ящик. Зажигательная подбирается по команде.</summary>
    private static readonly string[] CrateGrenades =
    [
        "weapon_smokegrenade",
        "weapon_flashbang",
        "weapon_hegrenade",
        "weapon_decoy",
    ];

    private static readonly Random Rng = new();

    /// <summary>Ящик снабжения: к кому дотягивается и как часто проверяет подошедших.</summary>
    private const float CrateRadius = 120f;
    private const float CrateHeight = 80f;
    private const float CrateTick = 0.5f;

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "human";
    public override string Name => "Альянс Людей";
    public override string Description => "Играет на команду: раздаёт гранаты из ящика, закрывает прорыв дымом и даже погибнув показывает своим убийцу. Чем дольше живёт без смертей, тем больше запас.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Последний взгляд",
            Description = "Ваш убийца подсвечивается контуром вашей команде сквозь стены: 0.75 с за ранг, до 3 с",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Ветеран",
            Description = "Каждый прожитый подряд раунд даёт +1 HP на спавне; потолок 2 + 2 за ранг (до 110 HP). Смерть сбрасывает",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Дымовая шашка",
            Description = "Выдаёт вам дымовую гранату; с 3 ранга ещё и светошумовую",
            Kind = AbilityKind.Active,
            Cooldown = 20f,
        },
        new Ability
        {
            Name = "Оружейный ящик",
            Description = "Ящик под ноги на 10 с + 5 с за ранг: каждому подошедшему союзнику одна случайная граната из недостающих",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>Выставленный ящик. Ключ — слот того, кто его поставил.</summary>
    private readonly Dictionary<int, CDynamicProp> _crates = new();

    /// <summary>Сколько раундов подряд игрок прожил. Ключ — слот.</summary>
    private readonly Dictionary<int, int> _survivedStreak = new();

    /// <summary>Погиб ли игрок в текущем раунде. По нему решается, продлевать ли серию.</summary>
    private readonly HashSet<int> _diedThisRound = [];

    public override void OnSpawn(WarcraftPlayer player)
    {
        ClearCrate(player.Slot);

        var rank = player.RankOf(Veteran);
        if (rank <= 0 || player.Pawn is not { } pawn) return;

        var cap = VeteranCapBase + rank * VeteranCapPerRank;
        var bonus = Math.Min(cap, _survivedStreak.GetValueOrDefault(player.Slot) * VeteranPerRound);
        if (bonus <= 0) return;

        // Запас задаём на спавне: он же становится потолком лечения, поэтому чужая
        // аура не срежет ветерана обратно до сотни.
        var total = 100 + bonus;
        Effects.SetHealth(pawn, total, total);
    }

    /// <summary>
    /// Серия обрывается смертью и продлевается началом раунда. Считать надо именно так,
    /// а не наращивать прямо в <see cref="OnDeath"/>: раунд, в котором игрок погиб,
    /// не должен идти в зачёт, а сам факт смерти становится известен раньше, чем
    /// начинается следующий раунд.
    /// </summary>
    public override void OnRoundStart(WarcraftPlayer player)
    {
        if (_diedThisRound.Remove(player.Slot))
        {
            _survivedStreak[player.Slot] = 0;
            return;
        }

        _survivedStreak[player.Slot] = _survivedStreak.GetValueOrDefault(player.Slot) + 1;
    }

    /// <summary>
    /// Последний взгляд: погибая, вы успеваете показать своим, кто это сделал.
    /// Убийца берётся из <c>LastAttackerSlot</c> — он пишется в хуке урона, тем же
    /// полем пользуется ложная смерть Оборотня.
    ///
    /// Способность работает после вашей смерти и вам самому не даёт ничего: это
    /// подарок команде, и в этом вся её мера — базовой расе большего не положено.
    /// </summary>
    public override void OnDeath(WarcraftPlayer player)
    {
        // Серию ветерана обрывает любая смерть, независимо от того, вложены ли в него очки.
        _diedThisRound.Add(player.Slot);
        _survivedStreak[player.Slot] = 0;

        var rank = player.RankOf(LastLook);
        if (rank <= 0 || player.Controller is not { } controller) return;

        if (Utilities.GetPlayerFromSlot(player.LastAttackerSlot) is not { IsValid: true } killer) return;
        if (killer.Team == controller.Team) return;

        var viewers = Utilities.GetPlayers()
            .Where(ally => ally is { IsValid: true } && ally.Team == controller.Team && ally.Slot != player.Slot)
            .Select(ally => ally.Slot)
            .ToList();

        if (viewers.Count == 0) return;

        Plugin.HighlightPlayer(killer, viewers, rank * LastLookPerRank);
    }


    /// <summary>
    /// Дымовая шашка: способность не строит облако сама, а выдаёт гранату в руки.
    /// Дым получается родной, со всеми правилами игры, и бросает его игрок сам.
    /// </summary>
    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(SmokeKit);
        if (rank <= 0 || player.Controller is not { IsValid: true } controller) return false;

        controller.GiveNamedItem("weapon_smokegrenade");

        if (rank >= FlashbangRank)
        {
            controller.GiveNamedItem("weapon_flashbang");
            controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Выдан дым и светошумовая");
            return true;
        }

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Выдана дымовая шашка");
        return true;
    }

    /// <summary>
    /// Оружейный ящик: точка снабжения, работающая без хозяина. Каждому союзнику
    /// достаётся одна граната и только один раз — ящик не заменяет закупку.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(SupplyCrate);
        if (rank <= 0 || player.Controller is not { } controller || player.Pawn is not { } pawn) return false;

        // Ящик ставится под ноги и только с земли: точку взгляда пришлось бы проверять
        // трассировкой луча, которой в API нет, и ящик повисал бы в воздухе над обрывом.
        if (!Effects.IsOnGround(pawn))
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Ящик ставится только на землю — приземлитесь.");
            return false;
        }

        if (Effects.Origin(pawn) is not { } spot) return false;

        ClearCrate(player.Slot);
        if (VisualEffects.SpawnProp(Plugin, VisualEffects.Models.AmmoBoxSmall, spot) is { } crate)
            _crates[player.Slot] = crate;

        var lifetime = 10f + rank * 5f;
        var ticksLeft = (int)(lifetime / CrateTick);
        var team = controller.Team;

        // Кому ящик уже отдал своё. Помечаем только после реальной выдачи: подошедший
        // с полным набором сможет вернуться, когда что-нибудь израсходует.
        var served = new HashSet<int>();

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Blue}Ящик выставлен{ChatColors.Default} — {lifetime:0} с, по одной гранате каждому");

        Timer? crateTimer = null;
        crateTimer = Plugin.AddRoundTimer(CrateTick, () =>
        {
            if (--ticksLeft <= 0)
            {
                ClearCrate(player.Slot);
                crateTimer?.Kill();
                return;
            }

            foreach (var ally in Effects.PlayersInRadius(spot, CrateRadius, team))
            {
                if (served.Contains(ally.Slot)) continue;
                if (ally.PlayerPawn.Value is not { IsValid: true } allyPawn || allyPawn.Health <= 0) continue;
                if (Effects.Origin(allyPawn) is not { } position) continue;
                if (MathF.Abs(position.Z - spot.Z) > CrateHeight) continue;

                if (PickMissingGrenade(ally) is not { } grenade) continue;

                ally.GiveNamedItem(grenade);
                served.Add(ally.Slot);
                CenterText.Print(ally, "СНАБЖЕНИЕ");
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }

    /// <summary>
    /// Случайная граната из тех, которых у союзника нет. Выдавать имеющуюся бессмысленно:
    /// игра всё равно откажет по лимиту на каждый тип.
    /// </summary>
    private static string? PickMissingGrenade(CCSPlayerController ally)
    {
        if (ally.PlayerPawn.Value is not { IsValid: true } pawn || pawn.WeaponServices is not { } services) return null;

        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in services.MyWeapons)
            if (handle.Value is { IsValid: true } weapon && weapon.DesignerName is { Length: > 0 } name)
                carried.Add(name);

        // Зажигательная у команд разная, поэтому в список её добавляем по месту.
        var fire = ally.Team == CsTeam.Terrorist ? "weapon_molotov" : "weapon_incgrenade";

        var missing = CrateGrenades.Append(fire).Where(grenade => !carried.Contains(grenade)).ToList();
        return missing.Count == 0 ? null : missing[Rng.Next(missing.Count)];
    }

    private void ClearCrate(int slot)
    {
        if (!_crates.Remove(slot, out var crate)) return;

        VisualEffects.RemoveEntity(crate);
    }
}
