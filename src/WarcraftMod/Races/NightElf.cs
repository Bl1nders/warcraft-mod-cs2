using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Ночные Эльфы — подвижность и уклонение: часть пуль проходит мимо, гравитация ниже
/// обычной, а в тени эльфа не видно вовсе. Ультой ставит силок под ноги преследователю.
/// </summary>
public sealed class NightElf : Race
{
    private const int Evasion = 0;
    private const int Lightness = 1;
    private const int ShadowMeld = 2;
    private const int Snare = 3;

    /// <summary>Размер силка, дальность установки и допуск по высоте.</summary>
    private const float SnareRadius = 100f;
    private const float SnareRange = 250f;
    private const float SnareHeight = 60f;

    /// <summary>Как часто силок проверяет, не наступил ли кто. Ждёт он до конца раунда.</summary>
    private const float SnareTick = 0.2f;

    /// <summary>
    /// Чем обозначен силок на земле. Капкана в CS2 нет, поэтому берём мелкий предмет,
    /// который на полу читается как подложенное устройство.
    /// </summary>
    private const string SnareModel = VisualEffects.Models.Spikes;

    /// <summary>Насколько медленнее ползёт вырвавшийся и как долго.</summary>
    private const float SnareSlow = 0.5f;
    private const float SnareSlowDuration = 2f;

    /// <summary>
    /// Прозрачность на максимальном ранге. Силуэт остаётся отчётливым, просто тусклым:
    /// эльф прячется от невнимательных, а не исчезает совсем.
    /// </summary>
    private const int ShadowMinAlpha = 100;

    private static readonly Random Rng = new();

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "nightelf";
    public override string Name => "Ночные Эльфы";
    public override string Description => "Ускользает, а не отвечает: часть пуль проходит мимо, прыжок лёгкий, а в нужный момент растворяется в тени. Преследователю ставит силок под ноги.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Уклонение",
            Description = "Шанс полностью избежать урона: 5% за ранг, до 20%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Лёгкость",
            Description = "Гравитация ниже: 8% за ранг, до 32% — выше прыжок",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Тень",
            Description = "Растворяет вас вместе с оружием: 1 с + 1 с за ранг (2-5 с). На максимуме остаётся тусклый силуэт",
            Kind = AbilityKind.Active,
            Cooldown = 25f,
        },
        new Ability
        {
            Name = "Ловушка охотника",
            Description = "Силок до конца раунда: первый враг замирает на 0.5 с + 0.25 с за ранг, потом 2 с ползёт",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>Метка поставленного силка. Ключ — слот хозяина ловушки.</summary>
    private readonly Dictionary<int, CDynamicProp> _snareMarks = new();

    public override void OnSpawn(WarcraftPlayer player)
    {
        // Раунд кончился — ловушка вместе с ним. Таймер гаснет сам, метку убираем руками.
        ClearSnareMark(player.Slot);

        var rank = player.RankOf(Lightness);
        if (rank > 0 && player.Pawn is { } pawn) Effects.SetGravity(pawn, 1f - rank * 0.08f);
    }

    private void ClearSnareMark(int slot)
    {
        if (!_snareMarks.Remove(slot, out var mark)) return;

        VisualEffects.RemoveEntity(mark);
    }

    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var rank = victim.RankOf(Evasion);
        if (rank <= 0) return;

        if (Rng.NextDouble() >= rank * 0.05) return;

        info.Damage = 0f;
        CenterText.Print(victim.Controller, "УКЛОНЕНИЕ");
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(ShadowMeld);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        var duration = 1f + rank;

        // Ранг 1 почти не прячет, ранг 4 оставляет тусклый, но отчётливый силуэт.
        player.RenderAlpha = Math.Max(255 - rank * 40, ShadowMinAlpha);

        // Прозрачное тело с висящим в воздухе оружием никого не обманет.
        var carried = Effects.HideWeapons(pawn);

        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.BlueGrey}Вы растворились в тени{ChatColors.Default} ({duration:0} с)");

        Plugin.AddTimer(duration, () =>
        {
            if (player.RenderAlpha < 255) player.RenderAlpha = 255;

            // Оружию видимость возвращаем всегда, даже если эльфа уже убили.
            Effects.ShowWeapons(carried);
        });

        return true;
    }

    /// <summary>
    /// Ловушка охотника: силок под ногами, который ждёт добычу и срабатывает один раз.
    /// Эльф ставит его и уходит — способность работает без него.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(Snare);
        if (rank <= 0 || player.Controller is not { } controller || player.Pawn is not { } pawn) return false;

        // Ставим туда, куда смотрим, но близко. Не смотрит в пол — кладём под ноги.
        var placement = Effects.AimPointOnGround(pawn, SnareRange) ?? Effects.Origin(pawn);
        if (placement is not { } snare) return false;

        var hold = 0.5f + rank * 0.25f;
        var enemyTeam = controller.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

        // Метка на земле: без неё ловушка невидима, и попасть в неё можно только случайно.
        ClearSnareMark(player.Slot);
        if (VisualEffects.SpawnProp(Plugin, SnareModel, snare with { Z = snare.Z + 3f }) is { } mark)
            _snareMarks[player.Slot] = mark;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Green}Ловушка поставлена{ChatColors.Default} — ждёт до конца раунда");

        // Срок жизни силка задаёт сам раунд: таймер раунда гаснет вместе с ним.
        Timer? snareTimer = null;
        snareTimer = Plugin.AddRoundTimer(SnareTick, () =>
        {
            foreach (var enemy in Effects.PlayersInRadius(snare, SnareRadius, enemyTeam))
            {
                if (enemy.PlayerPawn.Value is not { IsValid: true } enemyPawn || enemyPawn.Health <= 0) continue;
                if (Effects.Origin(enemyPawn) is not { } position) continue;

                // Радиус считаем по горизонтали: под ноги стоящему этажом выше силок не ставится.
                if (MathF.Abs(position.Z - snare.Z) > SnareHeight) continue;

                // Силок одноразовый: сработал — и больше никого не ждёт.
                snareTimer?.Kill();
                ClearSnareMark(player.Slot);
                SpringSnare(player, enemy, enemyPawn, hold);
                return;
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }

    /// <summary>Захлопнуть силок: жертва замирает, а вырвавшись, ещё какое-то время ползёт.</summary>
    private void SpringSnare(WarcraftPlayer hunter, CCSPlayerController victim, CCSPlayerPawn victimPawn, float hold)
    {
        Effects.SetFrozen(victimPawn, true);
        CenterText.Print(victim, "ЛОВУШКА!");
        hunter.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Green}В ловушку попал {victim.PlayerName}");

        Plugin.AddTimer(hold, () =>
        {
            // Мёртвого размораживать нечего, а живому движение возвращаем обязательно —
            // иначе он останется стоять до конца раунда.
            if (victim.PlayerPawn.Value is { IsValid: true } current && current.Health > 0)
                Effects.SetFrozen(current, false);

            if (Plugin.Get(victim) is not { } state) return;

            state.TempSpeedMultiplier = SnareSlow;

            Plugin.AddTimer(SnareSlowDuration, () =>
            {
                // Не снимаем чужое замедление, если за это время повесили что-то посильнее.
                if (Math.Abs(state.TempSpeedMultiplier - SnareSlow) < 0.001f) state.TempSpeedMultiplier = 1f;
            });
        });
    }
}
