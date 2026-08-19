using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;

namespace WarcraftMod.Races;

/// <summary>
/// Бигфут — большая мишень с большим запасом. Крупнее остальных, поэтому его видят
/// первым и попадают по нему чаще: вся раса про то, чтобы это пережить и заставить
/// стрелявшего пожалеть о выборе цели.
/// </summary>
public sealed class Bigfoot : Race
{
    private const int Health = 0;
    private const int ThickHide = 1;
    private const int Taunt = 2;
    private const int LeaderHide = 3;

    /// <summary>Снижение урона за ранг у Шкуры вожака. Четыре ранга дают потолок в 15%.</summary>
    private const float LeaderHidePerRank = 0.0375f;

    /// <summary>Во сколько раз крупнее обычного игрока. Черта расы, а не способность.</summary>
    /// <summary>
    /// Насколько Бигфут крупнее обычного. Число подобрано стрельбой 18.08.2026, а не
    /// на глаз: хитбоксы следуют за масштабом не полностью, и чем он больше, тем сильнее
    /// они расходятся с картинкой. При 1.5 у растянутой модели выпадала голова целиком —
    /// попадания кончались на высоте шеи; при 3 не засчитывалось почти ничего. На 1.2
    /// стрельба неотличима от обычной, включая выстрелы в голову, а рост читается глазом.
    /// Поднимать без новой проверки стрельбой нельзя.
    /// </summary>
    public override float BodyScale => 1.2f;

    private const int BaseHealth = 100;

    /// <summary>Прибавка здоровья за ранг. Четыре ранга дают потолок в 120.</summary>
    private const int HealthPerRank = 5;

    /// <summary>Насколько медленнее двигается тот, кто выстрелил в провоцирующего, и надолго ли.</summary>
    // Замедление на 80%: попавший по Бигфуту остаётся на пятой части скорости. Сильно
    // намеренно — вся раса про то, чтобы стрелявший пожалел, что выбрал эту цель.
    private const float TauntSlow = 0.2f;
    private const float TauntSlowDuration = 2f;

    public override int UnlockTotalLevel => Unlocks.Tier(3);

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    public override string Id => "bigfoot";
    public override string Name => "Бигфут";
    public override string Description => "Крупный и толстокожий: держит урон лучше всех, а тот, кто по нему попал, сам вязнет на месте. Ростом выше прочих, поэтому его замечают первым.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Здоровье",
            Description = "Максимум здоровья выше: +5 за ранг, до 120",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Толстая шкура",
            Description = "Урон по вам меньше: 4% за ранг, до 16%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Провокация",
            Description = "2 с + 1 с за ранг (3-6 с): всякий, кто по вам попал, замедляется на 80% на 2 с",
            Kind = AbilityKind.Active,
            Cooldown = 26f,
        },
        new Ability
        {
            Name = "Шкура вожака",
            Description = "7 с: урон по вам меньше ещё на 3.75% за ранг, до 15%",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>Игровое время, до которого держится шкура вожака. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _leaderHideUntil = new();

    /// <summary>Игровое время, до которого провокация наказывает стреляющих. Ключ — слот.</summary>
    private readonly Dictionary<int, float> _tauntUntil = new();

    public override void OnSpawn(WarcraftPlayer player)
    {
        _leaderHideUntil.Remove(player.Slot);
        _tauntUntil.Remove(player.Slot);

        if (player.Pawn is not { } pawn) return;

        var rank = player.RankOf(Health);
        if (rank <= 0) return;

        // Полный запас выдаём сразу: добирать его в бою Бигфуту нечем.
        var maxHealth = BaseHealth + rank * HealthPerRank;
        Effects.SetHealth(pawn, maxHealth, maxHealth);
    }

    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var reduction = victim.RankOf(ThickHide) * 0.04f;

        if (_leaderHideUntil.TryGetValue(victim.Slot, out var until) && until > Server.CurrentTime)
            reduction += victim.RankOf(LeaderHide) * LeaderHidePerRank;

        if (reduction > 0f) info.Damage *= 1f - reduction;

        PunishShooter(victim, attacker);
    }

    /// <summary>
    /// Расплата за выстрел в провоцирующего Бигфута: стрелок вязнет на пару секунд.
    /// Урона нет — есть потеря манёвра, и решает стрелок, стоило ли оно того.
    /// </summary>
    private void PunishShooter(WarcraftPlayer victim, CCSPlayerController? attacker)
    {
        if (!_tauntUntil.TryGetValue(victim.Slot, out var until) || until <= Server.CurrentTime) return;
        if (attacker is not { IsValid: true } || attacker.Slot == victim.Slot) return;
        if (attacker.Team == victim.Controller?.Team) return;
        if (Plugin.Get(attacker) is not { } shooter) return;

        shooter.TempSpeedMultiplier = TauntSlow;
        CenterText.Print(attacker, "ВЫ УВЯЗЛИ");

        Plugin.AddTimer(TauntSlowDuration, () =>
        {
            // Не снимаем чужое замедление, если за это время повесили что-то посильнее.
            if (Math.Abs(shooter.TempSpeedMultiplier - TauntSlow) < 0.001f) shooter.TempSpeedMultiplier = 1f;
        });
    }

    /// <summary>
    /// Провокация: на несколько секунд Бигфут становится невыгодной мишенью.
    /// Сама по себе не защищает — вся её польза в том, что стрелявший теряет ноги.
    /// </summary>
    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Taunt);
        if (rank <= 0 || player.Controller is not { } controller || player.Pawn is null) return false;

        var duration = 2f + rank;
        _tauntUntil[player.Slot] = Server.CurrentTime + duration;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Olive}Провокация!{ChatColors.Default} {duration:0} с — попавший по вам увязнет");
        return true;
    }

    /// <summary>
    /// Шкура вожака: несколько секунд Бигфут держит удар лучше обычного.
    ///
    /// Запрета стрельбы здесь больше нет (снят 18.08.2026). Он стоял, пока ультимейт
    /// снимал 35-50% урона: за такую защиту было чем платить. С потолком в 15% плата
    /// стала дороже самой способности — восемь секунд без ответного огня ради малой
    /// прибавки не стоили нажатия, и применять ультимейт было незачем.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(LeaderHide);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        const float duration = 7f;
        _leaderHideUntil[player.Slot] = Server.CurrentTime + duration;

        var reduction = (int)(rank * LeaderHidePerRank * 100);
        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Olive}Шкура вожака{ChatColors.Default} — {duration:0} с, урон меньше на {reduction}%");

        return true;
    }
}
