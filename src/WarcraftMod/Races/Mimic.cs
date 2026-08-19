using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Оборотень — обман: носит чужое лицо, залечивает раны, умеет исчезнуть из виду
/// и навязать собственный облик живому врагу.
/// </summary>
public sealed class Mimic : Race
{
    private const int Infiltrate = 0;
    private const int Regeneration = 1;
    private const int Vanish = 2;
    private const int Infection = 3;

    public override int UnlockTotalLevel => Unlocks.Tier(2);

    // Вся раса про то, чтобы сойти за чужого. Подпись в табло сводила бы это на нет.
    public override bool HiddenInScoreboard => true;

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "mimic";
    public override string Name => "Оборотень";
    public override string Description => "Носит чужое лицо: выходит из вражеского спавна своим, залечивает раны, растворяется в воздухе и может навязать свою внешность врагу.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Внедрение",
            Description = "Шанс возродиться на вражеском спавне в облике врага: 2.5% за ранг, до 10%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Регенерация",
            Description = "1 HP каждые 2 с, но за жизнь не больше 5 HP за ранг, до 20",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Исчезновение",
            Description = "Пропасть из виду вместе с оружием: 1 с + 1 с за ранг (2-5 с), нельзя ни двигаться, ни стрелять",
            Kind = AbilityKind.Active,
            Cooldown = 30f,
        },
        new Ability
        {
            Name = "Заражение",
            Description = "Враг под прицелом получает вашу внешность и ник: 3 с + 3 с за ранг (6-15 с)",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 55f,
        },
    ];

    public override void OnSpawn(WarcraftPlayer player)
    {
        StartRegeneration(player);

        var infiltrate = player.RankOf(Infiltrate);
        if (infiltrate <= 0) return;

        if (Random.Shared.NextDouble() >= infiltrate * 0.025) return;

        TryInfiltrate(player);
    }

    /// <summary>
    /// Медленное восстановление здоровья с потолком за жизнь — лечит между боями,
    /// но не даёт пережить перестрелку.
    /// </summary>
    private void StartRegeneration(WarcraftPlayer player)
    {
        var rank = player.RankOf(Regeneration);
        if (rank <= 0) return;

        var budget = rank * 5;

        Timer? healTimer = null;
        healTimer = Plugin.AddRoundTimer(2f, () =>
        {
            if (budget <= 0 || player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id)
            {
                healTimer?.Kill();
                return;
            }

            budget -= Effects.Heal(pawn, 1);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>Возродиться среди врагов в их облике. Маскировка держится до конца раунда.</summary>
    private void TryInfiltrate(WarcraftPlayer player)
    {
        if (player.Controller is not { } controller) return;

        var enemyTeam = controller.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        if (Effects.FreeSpawnPoint(enemyTeam) is not { } spawn) return;

        Disguise(player);

        // Переносим на следующем кадре: игра ещё расставляет игрока по своим точкам.
        Server.NextFrame(() =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0) return;

            Effects.TeleportTo(pawn, spawn with { Z = spawn.Z + 10f });
            player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Purple}Внедрение!{ChatColors.Default} Вы среди врагов и выглядите как один из них.");
        });
    }

    /// <summary>
    /// Исчезновение: тело и весь арсенал пропадают из виду. Плата за это — полная
    /// неподвижность: двигаться и стрелять нельзя, пока держится.
    ///
    /// Неподвижность и отличает приём от Тени Ночных Эльфов, у которой ровно те же
    /// секунды: та даёт уйти невидимым, эта — только переждать на месте. Убрать запрет
    /// значит сделать две расы одинаковыми.
    ///
    /// Раньше это была «Ложная смерть» со звуком попадания, кровью и строкой об убийстве
    /// в киллфиде врага. Театр убран по решению владельца 18.08.2026: раса и так живёт
    /// чужим лицом, а обманная смерть добавляла к обману ещё и вранью в интерфейсе.
    /// </summary>
    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Vanish);
        if (rank <= 0) return false;
        if (player.Controller is not { } controller || player.Pawn is not { } pawn || pawn.Health <= 0) return false;

        // В прыжке приём не работает: замереть в воздухе нельзя — висящий невидимка
        // всё равно выдаёт себя, стоит кому-нибудь в него врезаться.
        if (!Effects.IsOnGround(pawn))
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Исчезнуть можно только стоя на земле.");
            return false;
        }

        var duration = 1f + rank;

        // Прячем весь арсенал, а не только то, что в руках: без тела оружие повисло бы в воздухе.
        var carried = Effects.HideWeapons(pawn);

        player.RenderAlpha = 0;
        Effects.SetFrozen(pawn, true);

        // Метка текущей жизни: если игрока убьют по-настоящему, таймер не тронет следующую.
        var lifeToken = player.DisguiseToken;

        // Стрелять исчезнувшему тоже нельзя: выстрел выдаёт его мгновенно, а невидимка
        // с рабочим оружием — уже не хитрость, а преимущество. Запрет подновляем:
        // смена оружия сдвигает время следующей атаки и сняла бы разовый.
        var blockUntil = Server.CurrentTime + duration;
        Effects.BlockAttack(pawn, duration);

        Timer? attackBlock = null;
        attackBlock = Plugin.AddRoundTimer(0.2f, () =>
        {
            var secondsLeft = blockUntil - Server.CurrentTime;
            if (secondsLeft <= 0f || player.DisguiseToken != lifeToken
                || player.Pawn is not { } current || current.Health <= 0)
            {
                attackBlock?.Kill();
                return;
            }

            Effects.BlockAttack(current, secondsLeft);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Purple}Исчезновение{ChatColors.Default} — {duration:0} с. Двигаться и стрелять нельзя.");

        Plugin.AddTimer(duration, () =>
        {
            if (player.DisguiseToken != lifeToken) return;

            player.RenderAlpha = 255;

            // Видимость оружию возвращаем в любом случае, даже мёртвому: иначе выпавший
            // из рук ствол так и остался бы невидимым лежать до конца раунда.
            Effects.ShowWeapons(carried);

            if (player.Pawn is not { } current || current.Health <= 0) return;

            Effects.SetFrozen(current, false);

            // Снимаем запрет явно: остаток от смены оружия иначе задержал бы первый выстрел.
            Effects.BlockAttack(current, 0f);

            player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} Вы снова на виду.");
        });

        return true;
    }

    /// <summary>Надеть облик и ник противника. Маску снимет только смерть или конец раунда.</summary>
    private static bool Disguise(WarcraftPlayer player)
    {
        if (player.Controller is not { } controller || player.Pawn is not { } pawn) return false;

        // Запоминаем свой облик до подмены, чтобы вернуть именно его, а не случайный.
        player.OriginalModel ??= Effects.CurrentModelOf(pawn);

        // Облик и ник снимаем с одного и того же врага. Раньше модель бралась из общего
        // списка агентов, но у рас теперь свой облик: агент, которого не носит ни одна
        // раса, стал бы приметой оборотня вместо маскировки.
        var victim = RandomEnemy(controller);
        var stolenModel = victim?.PlayerPawn.Value is { IsValid: true } victimPawn
            ? Effects.CurrentModelOf(victimPawn)
            : null;

        Effects.SetModel(pawn, stolenModel is { Length: > 0 } ? stolenModel : Disguises.ForEnemyOf(controller.Team));

        // Ник подменяем тоже: иначе обман раскрывается в таблице и в сообщениях об убийствах.
        if (victim?.PlayerName is { Length: > 0 } stolenName) player.ApplyFakeName(stolenName);

        return true;
    }

    /// <summary>Случайный противник, включая мёртвых — выбор шире, обман убедительнее.</summary>
    private static CCSPlayerController? RandomEnemy(CCSPlayerController controller)
    {
        var enemyTeam = controller.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

        var enemies = Utilities.GetPlayers()
            .Where(candidate => candidate.IsValid && candidate.Team == enemyTeam)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.PlayerName))
            .ToList();

        return enemies.Count == 0 ? null : enemies[Random.Shared.Next(enemies.Count)];
    }

    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(Infection);
        if (rank <= 0) return false;
        if (player.Controller is not { } controller || player.Pawn is not { } pawn) return false;

        var target = Effects.FindTargetInAim(controller, maxRange: 2500f, maxAngleDegrees: 25f);
        if (target?.PlayerPawn.Value is not { IsValid: true } targetPawn || targetPawn.Health <= 0)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Наведитесь на врага, чтобы отдать ему свой облик.");
            return false;
        }

        // Свой облик берём текущий: если сейчас на вас маска, заразим именно ею.
        if (Effects.CurrentModelOf(pawn) is not { Length: > 0 } myModel) return false;

        // Оригиналы жертвы запоминаем здесь: у ботов состояния в моде нет, а вернуть надо всем.
        var victimModel = Effects.CurrentModelOf(targetPawn);
        var victimName = target.PlayerName;

        // 6 с на первом ранге, ровно 15 с на четвёртом — полная прокачка даёт целый заход.
        var duration = 3f + rank * 3f;
        var infectorName = controller.PlayerName;

        // Заражение происходит незаметно: ни частиц, ни звука, иначе жертва сразу поймёт причину.
        Effects.SetModel(targetPawn, myModel);
        SetName(target, infectorName);
        Plugin.MarkInfected(target.Slot, duration);

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Purple}Заражение!{ChatColors.Default} {victimName} теперь выглядит как вы ({duration:0} с)");
        target.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Purple}Вы заражены{ChatColors.Default} и выглядите как {ChatColors.Green}{infectorName}{ChatColors.Default}. Свои могут вас подстрелить — урон от них проходит.");

        Plugin.AddTimer(duration, () =>
        {
            if (!target.IsValid) return;

            Plugin.ClearInfection(target.Slot);
            SetName(target, victimName);

            if (target.PlayerPawn.Value is not { IsValid: true } currentPawn || currentPawn.Health <= 0) return;
            if (victimModel is { Length: > 0 } original) Effects.SetModel(currentPawn, original);

            target.PrintToChat($"{WarcraftPlugin.Prefix} Заражение прошло, вы снова выглядите как {victimName}.");
        });

        return true;
    }

    private static void SetName(CCSPlayerController controller, string name)
    {
        controller.PlayerName = name;
        Utilities.SetStateChanged(controller, "CBasePlayerController", "m_iszPlayerName");
    }
}
