using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;

namespace WarcraftMod.Races;

/// <summary>
/// Иллюзионист — обман зрения и слуха. Оборотень подделывает личность, а этот — картинку:
/// оставляет свои копии, отражает чужие выстрелы и уводит врагов на ложный шум.
/// </summary>
public sealed class Mirage : Race
{
    private const int MirrorGleam = 0;
    private const int DeathEcho = 1;
    private const int Double = 2;
    private const int FalseShots = 3;

    /// <summary>Какую долю полученного урона возвращает блик и сколько максимум.</summary>
    private const float GleamShare = 0.5f;
    private const int GleamCap = 25;

    /// <summary>Базовое время жизни копии и прибавка за ранг: на четвёртом выходит 15 с.</summary>
    private const float DoubleBaseLifetime = 3f;
    private const float DoubleLifetimePerRank = 3f;

    /// <summary>На сколько утоплена копия сидящего: жёсткая модель приседать не умеет.</summary>
    private const float CrouchSink = 18f;
    private const float DuckThreshold = 0.5f;

    /// <summary>
    /// Как далеко ставится источник звука. Компромисс: дальше среднего по моду, но не
    /// настолько, чтобы точка часто оказывалась за стеной — проверить стену нечем,
    /// трассировка луча из плагина недоступна.
    /// </summary>
    private const float ShotsRange = 900f;

    /// <summary>
    /// Очередь автомата: длина случайная, шаг ровный — палец держат на спуске.
    /// </summary>
    private const int AutoShotsMin = 5;
    private const int AutoShotsMax = 15;
    private const float AutoInterval = 0.1f;

    /// <summary>
    /// Одиночные: и число выстрелов, и паузы между ними разные — иначе слышно машину,
    /// а должно казаться, что стреляет живой человек.
    /// </summary>
    private const int SingleShotsMin = 2;
    private const int SingleShotsMax = 6;
    private const float SingleIntervalMin = 0.4f;
    private const float SingleIntervalMax = 1f;

    /// <summary>
    /// Снайперские винтовки с продольно-скользящим затвором. Между выстрелами его надо
    /// передёрнуть, и подделка обязана держать ту же паузу: услышав два выстрела AWP
    /// подряд без задержки, любой поймёт, что стреляли не из AWP.
    ///
    /// Числа — паспортный темп стрельбы CS2, он и определяется скоростью затвора.
    /// </summary>
    private static readonly Dictionary<string, float> BoltAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_awp"] = 1.46f,
        ["weapon_ssg08"] = 1.25f,
    };

    /// <summary>
    /// Сколько выстрелов делает поддельный снайпер. Два: больше подряд из болтовки не
    /// стреляют — либо попал, либо цель уже сменила позицию.
    /// </summary>
    private const int BoltShots = 2;

    /// <summary>С какого ранга подделывается основное оружие, а не пистолет.</summary>
    private const int PrimaryRank = 4;

    /// <summary>Пистолеты — по ним отличаем «второе» оружие от основного.</summary>
    private static readonly string[] Pistols =
    [
        "weapon_glock", "weapon_usp_silencer", "weapon_hkp2000", "weapon_p250",
        "weapon_fiveseven", "weapon_tec9", "weapon_cz75a", "weapon_deagle",
        "weapon_revolver", "weapon_elite",
    ];

    /// <summary>Автоматическое оружие — по нему выбирается очередь вместо одиночных.</summary>
    private static readonly string[] Automatics =
    [
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_galilar",
        "weapon_famas", "weapon_aug", "weapon_sg556", "weapon_mp9", "weapon_mac10",
        "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_p90", "weapon_bizon",
        "weapon_negev", "weapon_m249", "weapon_cz75a",
    ];

    private static readonly Random Rng = new();

    /// <summary>Донатная: уровнем не открывается, доступ выдаётся лично.</summary>
    public override bool DonorOnly => true;

    // Идентификатор менять нельзя: по нему хранится прогресс игроков.
    // `illusionist` занят Разведчиком, поэтому здесь свой.
    public override string Id => "mirage";
    public override string Name => "Иллюзионист";
    public override string Description => "Обманывает глаза и уши: оставляет за собой копии, отражает часть чужого урона и уводит врагов на выстрелы, которых не было.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Зеркальный блик",
            Description = "Шанс вернуть стрелявшему половину урона (до 25): 5% за ранг, до 20%",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Послед",
            Description = "После смерти на вашем месте остаётся копия: 1 с за ранг, до 4 с",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Двойник",
            Description = "Копия встаёт ровно на вашем месте: 3 с + 3 с за ранг, до 15 с",
            Kind = AbilityKind.Active,
            Cooldown = 30f,
        },
        new Ability
        {
            Name = "Ложные выстрелы",
            Description = "Стрельба там, куда смотрите: до 3 ранга ваш пистолет, с 4 — основное. Очередь 5-15, одиночных 2-6",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    /// <summary>Копии, стоящие в мире. Ключ — слот хозяина.</summary>
    private readonly Dictionary<int, List<CDynamicProp>> _copies = new();

    public override void OnSpawn(WarcraftPlayer player) => ClearCopies(player.Slot);

    /// <summary>
    /// Зеркальный блик: часть урона возвращается тому, кто стрелял.
    /// Возврат — на следующем кадре: смертельный удар изнутри обработки урона роняет сервер.
    /// </summary>
    public override void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info)
    {
        var rank = victim.RankOf(MirrorGleam);
        if (rank <= 0 || attacker is not { IsValid: true }) return;
        if (attacker.Slot == victim.Slot || attacker.Team == victim.Controller?.Team) return;

        if (Rng.NextDouble() >= rank * 0.05) return;

        var reflected = Math.Min(GleamCap, (int)(info.Damage * GleamShare));
        if (reflected <= 0) return;

        Server.NextFrame(() =>
        {
            if (attacker.PlayerPawn.Value is { IsValid: true } attackerPawn && attackerPawn.Health > 0)
                Effects.ApplyDirectDamage(attackerPawn, reflected);
        });

        CenterText.Print(victim.Controller, "ЗЕРКАЛЬНЫЙ БЛИК");
    }

    /// <summary>Послед: место смерти ещё какое-то время выглядит занятым.</summary>
    public override void OnDeath(WarcraftPlayer player)
    {
        var rank = player.RankOf(DeathEcho);
        if (rank <= 0) return;

        SpawnCopy(player, rank);
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(Double);
        if (rank <= 0 || player.Controller is not { } controller) return false;

        // Копия всегда одна: поставил новую — прежняя рассеялась.
        ClearCopies(player.Slot);

        var lifetime = DoubleBaseLifetime + rank * DoubleLifetimePerRank;
        if (!SpawnCopy(player, lifetime))
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Копия не встала — попробуйте на ровном месте.");
            return false;
        }

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.LightPurple}Двойник{ChatColors.Default} — {lifetime:0} с");
        return true;
    }

    /// <summary>
    /// Копия игрока: та же модель, то же место, тот же поворот. Приседание жёсткая модель
    /// повторить не может, поэтому сидящего изображаем утопленной копией — издали читается,
    /// вблизи ноги уходят в пол.
    /// </summary>
    private bool SpawnCopy(WarcraftPlayer player, float lifetime)
    {
        if (player.Pawn is not { } pawn) return false;
        if (Effects.CurrentModelOf(pawn) is not { Length: > 0 } model) return false;
        if (Effects.Origin(pawn) is not { } origin) return false;

        var ducking = IsDucking(pawn);
        var spot = ducking ? origin with { Z = origin.Z - CrouchSink } : origin;

        if (VisualEffects.SpawnProp(Plugin, model, spot, pawn.V_angle.Y) is not { } copy) return false;

        var slotCopies = Copies(player.Slot);
        slotCopies.Add(copy);

        Plugin.AddTimer(lifetime, () =>
        {
            VisualEffects.RemoveEntity(copy);
            slotCopies.Remove(copy);
        });

        return true;
    }

    private static bool IsDucking(CCSPlayerPawn pawn)
    {
        if (pawn.MovementServices is not { } services) return false;

        return new CCSPlayer_MovementServices(services.Handle).DuckAmount >= DuckThreshold;
    }

    private List<CDynamicProp> Copies(int slot)
    {
        if (_copies.TryGetValue(slot, out var list)) return list;

        list = [];
        _copies[slot] = list;
        return list;
    }

    private void ClearCopies(int slot)
    {
        if (!_copies.TryGetValue(slot, out var list)) return;

        foreach (var copy in list) VisualEffects.RemoveEntity(copy);
        list.Clear();
    }

    /// <summary>
    /// Ложные выстрелы: в точке взгляда слышна стрельба тем оружием, что у вас в руках.
    /// Звук идёт от временной невидимой сущности — иначе его нечем издать в пустом месте.
    /// </summary>
    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(FalseShots);
        if (rank <= 0 || player.Controller is not { } controller || player.Pawn is not { } pawn) return false;

        // До полной прокачки иллюзия тянет только на пистолет — с ним и путают.
        if (PickWeapon(pawn, rank >= PrimaryRank) is not { } weapon)
        {
            controller.PrintToChat(rank >= PrimaryRank
                ? $"{WarcraftPlugin.Prefix} Нужно оружие — ножом шуметь нечем."
                : $"{WarcraftPlugin.Prefix} Нужен пистолет: основное оружие подделывается только с 4 ранга.");
            return false;
        }

        if (Effects.EyePosition(pawn) is not { } eye) return false;

        var spot = eye + Effects.ForwardVector(pawn) * ShotsRange;

        // Источник невидим: он нужен только как точка, из которой звучит выстрел.
        if (VisualEffects.SpawnProp(Plugin, VisualEffects.Models.Padlock, spot) is not { } source) return false;
        Effects.SetRenderVisible(source, false);

        var weaponName = weapon.DesignerName ?? "";
        var itemIndex = ItemIndex(weapon);

        // Три манеры стрельбы. Болтовка проверяется первой: AWP в списке автоматов не
        // числится, но и на «редкие одиночные» её темп не похож — там паузы случайные,
        // а у затвора она всегда одна и та же.
        var bolt = BoltAction.TryGetValue(weaponName, out var boltInterval);
        var automatic = !bolt && Automatics.Contains(weaponName, StringComparer.OrdinalIgnoreCase);

        var shots = bolt ? BoltShots
            : automatic ? Rng.Next(AutoShotsMin, AutoShotsMax + 1)
            : Rng.Next(SingleShotsMin, SingleShotsMax + 1);

        var sound = ShotSound(weaponName, itemIndex);

        // Паузы копим по ходу: у одиночных каждая своя, иначе стрельба звучит как метроном.
        var delay = 0f;
        for (var i = 0; i < shots; i++)
        {
            var at = delay;
            Plugin.AddTimer(at, () =>
            {
                if (source.IsValid) VisualEffects.PlaySound(source, sound);
            });

            delay += bolt ? boltInterval
                : automatic ? AutoInterval
                : SingleIntervalMin + (float)Rng.NextDouble() * (SingleIntervalMax - SingleIntervalMin);
        }

        Plugin.AddTimer(delay + 0.5f, () => VisualEffects.RemoveEntity(source));

        var manner = bolt ? "снайперские" : automatic ? "очередь" : "одиночные";
        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.LightPurple}Ложные выстрелы{ChatColors.Default} — {manner}, {shots} шт.");

        return true;
    }

    /// <summary>
    /// Чем шуметь: пистолетом из своей же кобуры или основным стволом на полной прокачке.
    /// Нож, гранаты, бомба и шокер не годятся — стрельбу ими не изобразить.
    /// </summary>
    private static CBasePlayerWeapon? PickWeapon(CCSPlayerPawn pawn, bool primaryAllowed)
    {
        if (pawn.WeaponServices is not { } services) return null;

        CBasePlayerWeapon? pistol = null;
        CBasePlayerWeapon? primary = null;

        foreach (var handle in services.MyWeapons)
        {
            if (handle.Value is not { IsValid: true } weapon) continue;
            if (weapon.DesignerName is not { Length: > 0 } name || !name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)) continue;

            if (name.Contains("knife", StringComparison.OrdinalIgnoreCase)
                || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase)
                || name.Contains("grenade", StringComparison.OrdinalIgnoreCase)
                || name.Contains("molotov", StringComparison.OrdinalIgnoreCase)
                || name.Contains("healthshot", StringComparison.OrdinalIgnoreCase)
                || name.Contains("taser", StringComparison.OrdinalIgnoreCase)
                || name.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Pistols.Contains(name, StringComparer.OrdinalIgnoreCase)) pistol ??= weapon;
            else primary ??= weapon;
        }

        return primaryAllowed ? primary ?? pistol : pistol;
    }

    /// <summary>
    /// Звук по номеру предмета. Имя ствола различает не всё: у M4A1-S и USP-S движок
    /// сообщает базовое имя, и по нему звучали M4A4 и P2000. Номер предмета у них свой.
    /// </summary>
    /// <remarks>
    /// У тихих стволов своего звукового события нет — проверено. В файле звуков читается
    /// параметр `silenced` внутри общего события, а задать параметр воспроизведением
    /// по имени нельзя; все кандидаты вида `Weapon_M4A1_Silencer.Single` молчат.
    /// Поэтому M4A1-S и USP-S шумят звуком базового ствола: лучше чужой выстрел, чем тишина.
    /// </remarks>
    private static readonly Dictionary<ushort, string> ShotSoundsByItem = new()
    {
        [60] = "Weapon_M4A1.Single",
        [61] = "Weapon_HKP2000.Single",
        [16] = "Weapon_M4A1.Single",
        [32] = "Weapon_HKP2000.Single",
        [23] = "Weapon_MP5SD.Single",
        [63] = "Weapon_CZ75a.Single",
        [64] = "Weapon_Revolver.Single",
    };

    /// <summary>Звуки, которые не выводятся из названия ствола, но и номер знать не обязательно.</summary>
    private static readonly Dictionary<string, string> ShotSoundsByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_deagle"] = "Weapon_Deagle.Single",
        ["weapon_glock"] = "Weapon_Glock.Single",
        ["weapon_elite"] = "Weapon_Elite.Single",
        ["weapon_fiveseven"] = "Weapon_FiveSeven.Single",
        ["weapon_tec9"] = "Weapon_Tec9.Single",
    };

    /// <summary>Имя звукового события выстрела: сначала по номеру предмета, затем по имени.</summary>
    private static string ShotSound(string weaponName, ushort itemIndex)
    {
        if (ShotSoundsByItem.TryGetValue(itemIndex, out var byItem)) return byItem;
        if (ShotSoundsByName.TryGetValue(weaponName, out var byName)) return byName;

        var barrel = weaponName.Replace("weapon_", string.Empty, StringComparison.OrdinalIgnoreCase);
        return $"Weapon_{barrel.ToUpperInvariant()}.Single";
    }

    /// <summary>Номер предмета — единственный способ отличить «тихие» варианты от базовых.</summary>
    private static ushort ItemIndex(CBasePlayerWeapon weapon)
    {
        try
        {
            return weapon.AttributeManager.Item.ItemDefinitionIndex;
        }
        catch
        {
            return 0;
        }
    }
}
