using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Коротышка — раса маленького роста. Модель на четверть ниже обычной, и цена этому
/// высокая: всего 50 HP. Попасть по нему трудно, но любое попадание почти смертельно.
/// Ползает быстро, прыгает высоко и оставляет всех вокруг без оружия.
/// </summary>
public sealed class Shorty : Race
{
    private const int Nimbleness = 0;
    private const int Crawl = 1;
    private const int Flea = 2;
    private const int Havoc = 3;

    /// <summary>Во сколько раз меньше обычного игрока и сколько здоровья за это отдаёт.</summary>
    /// <summary>
    /// Насколько Коротышка мельче обычного.
    ///
    /// В отличие от Бигфута, здесь запас есть: замер 18.08.2026 показал, что вниз масштаб
    /// работает честно. По уменьшенному боту попадания идут ровно по видимой модели,
    /// включая голову, а выстрелы над макушкой не засчитываются — проверено вплоть до 0.5.
    /// Расхождение хитбоксов с картинкой опасно только при увеличении, поэтому у Бигфута
    /// потолок 1.2, а сжатие можно брать смелее.
    /// </summary>
    public override float BodyScale => 0.75f;
    private const int SmallHealth = 50;

    /// <summary>С какого приседания считаем, что игрок ползёт, и как часто это проверяем.</summary>
    private const float DuckThreshold = 0.5f;
    private const float CrawlTick = 0.2f;

    /// <summary>Переполох: кого достаёт и сколько после него можно только ножом.</summary>
    private const float HavocRadius = 300f;
    private const float KnifeOnlyDuration = 4f;

    public override int UnlockTotalLevel => Unlocks.Tier(4);

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "shorty";
    public override string Name => "Коротышка";
    public override string Description => "Ростом на четверть ниже прочих, но всего с 50 HP: попасть по нему трудно, а хватает одного попадания. Быстро ползает пригнувшись, прыгает как блоха и оставляет всех вокруг без оружия.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Юркость",
            Description = "Скорость передвижения: +3% за ранг, до +12%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Пролаз",
            Description = "В приседе двигаетесь быстрее: +15% за ранг, до +60%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Блоха",
            Description = "Высокий прыжок: сила 680 + 80 за ранг, приземление безопасно",
            Kind = AbilityKind.Active,
            Cooldown = 12f,
        },
        new Ability
        {
            Name = "Переполох",
            Description = "Все враги вплотную роняют оружие, но вам самим 4 с доступен только нож и не работают прибавки к скорости",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>До какого времени у игрока действует расплата за Переполох. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _havocUntil = new();

    private bool InHavoc(int slot) =>
        _havocUntil.TryGetValue(slot, out var until) && until > Server.CurrentTime;

    public override void OnSpawn(WarcraftPlayer player)
    {
        _havocUntil.Remove(player.Slot);

        if (player.Pawn is not { } pawn) return;

        // Запас здоровья — черта расы, а не способность: даётся независимо от прокачки.
        // Рост объявлен через BodyScale, его надевает плагин сразу за обликом.
        Effects.SetHealth(pawn, SmallHealth, SmallHealth);

        var nimble = player.RankOf(Nimbleness);
        if (nimble > 0) player.BaseSpeedMultiplier = 1f + nimble * 0.03f;

        StartCrawl(player);
    }

    /// <summary>
    /// Пролаз: приседающий Коротышка не ползёт, а перемещается почти как в полный рост.
    /// Прибавка временная и снимается, как только он встал, — постоянной ей быть нельзя.
    /// </summary>
    private void StartCrawl(WarcraftPlayer player)
    {
        var rank = player.RankOf(Crawl);
        if (rank <= 0) return;

        var boost = 1f + rank * 0.15f;

        Timer? crawlTimer = null;
        crawlTimer = Plugin.AddRoundTimer(CrawlTick, () =>
        {
            if (player.Pawn is not { } pawn || pawn.Health <= 0 || player.Race?.Id != Id)
            {
                crawlTimer?.Kill();
                return;
            }

            // На время Переполоха прибавки к скорости не работают — в том числе эта.
            if (InHavoc(player.Slot))
            {
                if (player.TempSpeedMultiplier > 1f) player.TempSpeedMultiplier = 1f;
                return;
            }

            var ducking = IsDucking(pawn);

            if (ducking && Math.Abs(player.TempSpeedMultiplier - boost) > 0.001f)
                player.TempSpeedMultiplier = boost;

            // Встал — снимаем только свою прибавку, чужие эффекты не трогаем.
            if (!ducking && Math.Abs(player.TempSpeedMultiplier - boost) < 0.001f)
                player.TempSpeedMultiplier = 1f;
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static bool IsDucking(CCSPlayerPawn pawn)
    {
        if (pawn.MovementServices is not { } services) return false;

        // Доля приседания, а не флаг: так учитывается и переход в присед, и выход из него.
        return new CCSPlayer_MovementServices(services.Handle).DuckAmount >= DuckThreshold;
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Flea);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        if (!Effects.IsOnGround(pawn))
        {
            player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} Прыгать можно только с земли.");
            return false;
        }

        // Высота растёт от квадрата скорости, поэтому вдвое ниже — это не половина
        // скорости, а деление на корень из двух. Половина уронила бы прыжок вчетверо.
        var power = 680f + rank * 80f;

        // Скорость задаём следующим кадром: заданную с земли в этом же кадре движок затирает.
        Server.NextFrame(() =>
        {
            if (player.Pawn is { } current && current.Health > 0) current.AbsVelocity.Z = power;
        });

        player.GrantLaunchFallImmunity();
        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Lime}Блоха!");
        return true;
    }

    /// <summary>
    /// Переполох: враги вплотную теряют оружие из рук. Плата — свои же четыре секунды
    /// с одним ножом: обезоружить всех и тут же расстрелять было бы слишком.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        if (player.RankOf(Havoc) <= 0) return false;
        if (player.Controller is not { } controller || player.Pawn is not { } pawn) return false;
        if (Effects.Origin(pawn) is not { } center) return false;

        var enemyTeam = controller.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

        var disarmed = 0;
        foreach (var enemy in Effects.PlayersInRadius(center, HavocRadius, enemyTeam))
        {
            enemy.DropActiveWeapon();
            CenterText.Print(enemy, "ОРУЖИЕ ВЫБИТО");
            disarmed++;
        }

        // Расплата за обезоруживание: ни стрельбы, ни привычной прыти.
        _havocUntil[player.Slot] = Server.CurrentTime + KnifeOnlyDuration;
        player.BaseSpeedMultiplier = 1f;
        player.TempSpeedMultiplier = 1f;

        StartKnifeOnly(player);

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Lime}Переполох!{ChatColors.Default} Обезоружено: {disarmed}. {KnifeOnlyDuration:0} с без стрельбы и без прыти");
        return true;
    }

    /// <summary>
    /// Запрет на всё, кроме ножа: стрельба блокируется, пока в руках не нож.
    /// Проверяем часто — иначе переключился на ствол и стреляй.
    /// </summary>
    private void StartKnifeOnly(WarcraftPlayer player)
    {
        var until = Server.CurrentTime + KnifeOnlyDuration;

        Timer? knifeTimer = null;
        knifeTimer = Plugin.AddRoundTimer(0.2f, () =>
        {
            var secondsLeft = until - Server.CurrentTime;
            if (secondsLeft <= 0f || player.Pawn is not { } pawn || pawn.Health <= 0)
            {
                knifeTimer?.Kill();
                _havocUntil.Remove(player.Slot);

                // Возвращаем свою скорость: постоянную прибавку заново, приседную вернёт её таймер.
                var nimble = player.RankOf(Nimbleness);
                player.BaseSpeedMultiplier = nimble > 0 ? 1f + nimble * 0.03f : 1f;
                return;
            }

            Effects.BlockGuns(pawn, secondsLeft);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }
}
