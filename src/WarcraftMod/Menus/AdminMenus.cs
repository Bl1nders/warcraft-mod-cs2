using CounterStrikeSharp.API;
using WarcraftMod.Core;
using WarcraftMod.Storage;

namespace WarcraftMod.Menus;

/// <summary>
/// Админка для доверенного игрока: смена карты, кик, бан, мут. Открывается по <c>!admin</c>.
///
/// Само меню о правах не знает — доступ проверяет <see cref="WarcraftPlugin.IsAdmin"/>
/// в команде. Так проверка живёт в одном месте, а не размазана по пунктам.
/// </summary>
public static class AdminMenus
{
    public static void OpenAdminMenu(WarcraftPlugin plugin, WarcraftPlayer admin)
    {
        var menu = new ScreenMenu { Title = "АДМИНКА" };

        menu.Add("Карта", target => WarcraftMenus.OpenMapMenu(plugin, target, BackToAdmin(plugin)),
            hint: Server.MapName);

        menu.Add("Кик", target => OpenPlayerMenu(plugin, target, "КОГО КИКНУТЬ", plugin.KickPlayer));

        menu.Add("Бан", target => OpenPlayerMenu(plugin, target, "КОГО ЗАБАНИТЬ",
            (byWhom, slot) => OpenBanDurationMenu(plugin, byWhom, slot)));

        menu.Add("Мут", target => OpenPlayerMenu(plugin, target, "КОГО ЗАГЛУШИТЬ",
            (byWhom, slot) => OpenMuteMenu(plugin, byWhom, slot)));

        var bans = plugin.Bans.Active().Count;
        menu.Add("Баны", target => OpenBanListMenu(plugin, target), hint: bans > 0 ? $"{bans}" : "пусто");

        menu.AddBack("Закрыть");

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>
    /// Действующие баны: выбор снимает. Меню после снятия открываем заново — обычно
    /// разбирают несколько записей подряд, и возвращать в админку каждый раз неудобно.
    /// </summary>
    private static void OpenBanListMenu(WarcraftPlugin plugin, WarcraftPlayer admin)
    {
        var menu = new ScreenMenu { Title = "СНЯТЬ БАН", Parent = BackToAdmin(plugin) };

        foreach (var (steamId, record) in plugin.Bans.Active())
        {
            // Ник мог не сохраниться — тогда показываем сам SteamID, иначе строка будет пустой.
            var name = record.Name.Length > 0 ? record.Name : steamId.ToString();

            menu.Add(name,
                target =>
                {
                    plugin.Unban(target, steamId, name);
                    OpenBanListMenu(plugin, target);
                },
                hint: BanTermHint(record));
        }

        if (menu.Options.Count == 0) menu.Add("Забаненных нет", disabled: true);

        menu.AddBack("Назад", target => OpenAdminMenu(plugin, target));

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>Сколько бана осталось — в тех единицах, в которых число читается с одного взгляда.</summary>
    private static string BanTermHint(BanRecord record)
    {
        if (record.UntilUnix == 0) return "навсегда";

        var left = record.UntilUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (left <= 0) return "истёк";

        var hours = left / 3600;
        return hours >= 24 ? $"{hours / 24} дн."
            : hours >= 1 ? $"{hours} ч."
            : $"{Math.Max(1, left / 60)} мин.";
    }

    /// <summary>Возврат по R в админку. Меню уже подменяется само, поэтому отдаём null.</summary>
    private static Func<WarcraftPlayer, ScreenMenu?> BackToAdmin(WarcraftPlugin plugin) =>
        target =>
        {
            OpenAdminMenu(plugin, target);
            return null;
        };

    /// <summary>
    /// Список игроков для меры. Запоминаем слот, а контроллер берём заново в момент
    /// применения: между открытием меню и нажатием игрок мог выйти или его мог занять другой.
    /// </summary>
    private static void OpenPlayerMenu(WarcraftPlugin plugin, WarcraftPlayer admin, string title,
        Action<WarcraftPlayer, int> onPick, Action<WarcraftPlugin, WarcraftPlayer>? back = null,
        bool includeSelf = false)
    {
        back ??= OpenAdminMenu;

        var menu = new ScreenMenu
        {
            Title = title,
            Parent = target => { back(plugin, target); return null; },
        };

        foreach (var target in Utilities.GetPlayers().Where(player => player is { IsValid: true }))
        {
            // Себя обычно не показываем — кикнуть или забанить себя админ хотел вряд ли.
            // Но выдать себе расу или уровни он как раз может, поэтому список настраивается.
            if (!includeSelf && target.Slot == admin.Slot) continue;

            var slot = target.Slot;
            var hint = target.IsBot ? "бот" : plugin.MuteHint(target.SteamID);

            menu.Add(target.PlayerName, _ => onPick(admin, slot), hint: hint);
        }

        if (menu.Options.Count == 0) menu.Add("Больше никого нет", disabled: true);

        menu.AddBack("Назад", target => back(plugin, target));

        plugin.Menus.Open(admin, menu);
    }

    // ------------------------------------------------------------------
    // Админка мода: расы и уровни
    // ------------------------------------------------------------------

    /// <summary>
    /// Отдельная панель под выдачу. Отделена от общей админки намеренно: там меры к
    /// нарушителям, здесь подарки, и путать их пунктами одного списка не стоит.
    /// </summary>
    public static void OpenModAdminMenu(WarcraftPlugin plugin, WarcraftPlayer admin)
    {
        var menu = new ScreenMenu { Title = "ВЫДАЧА" };

        menu.Add("Выдать расу", target => OpenPlayerMenu(plugin, target, "КОМУ ВЫДАТЬ РАСУ",
            (byWhom, slot) => OpenGrantRaceMenu(plugin, byWhom, slot, granting: true),
            back: OpenModAdminMenu, includeSelf: true));

        menu.Add("Отобрать расу", target => OpenPlayerMenu(plugin, target, "У КОГО ОТОБРАТЬ",
            (byWhom, slot) => OpenGrantRaceMenu(plugin, byWhom, slot, granting: false),
            back: OpenModAdminMenu, includeSelf: true));

        menu.Add("Общий уровень", target => OpenPlayerMenu(plugin, target, "КОМУ ВЫДАТЬ УРОВНИ",
            (byWhom, slot) => OpenAccountLevelMenu(plugin, byWhom, slot),
            back: OpenModAdminMenu, includeSelf: true));

        menu.Add("Уровень расы", target => OpenPlayerMenu(plugin, target, "КОМУ ВЫДАТЬ УРОВНИ",
            (byWhom, slot) => OpenRaceLevelMenu(plugin, byWhom, slot),
            back: OpenModAdminMenu, includeSelf: true));

        menu.AddBack("Закрыть");

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>
    /// Выбор расы для личной выдачи. Показываем только донатные: обычные открываются
    /// уровнем, и личный доступ на них не влияет вовсе — пункт бы молча не работал.
    /// </summary>
    private static void OpenGrantRaceMenu(WarcraftPlugin plugin, WarcraftPlayer admin, int slot, bool granting)
    {
        var target = Utilities.GetPlayerFromSlot(slot);
        var name = target is { IsValid: true } ? target.PlayerName : "игрок";
        var steamId = target?.SteamID ?? 0;

        var menu = new ScreenMenu
        {
            Title = granting ? $"ВЫДАТЬ: {name}" : $"ОТОБРАТЬ: {name}",
            Parent = byWhom => { OpenModAdminMenu(plugin, byWhom); return null; },
        };

        foreach (var race in plugin.Races.All.Where(race => race.DonorOnly))
        {
            var has = steamId != 0 && plugin.HasRaceGrant(steamId, race);

            menu.Add(race.Name,
                byWhom => plugin.ApplyRaceGrant(steamId, race, granting, byWhom.Controller),
                disabled: steamId == 0 || has == granting,
                hint: has ? "выдана" : "нет");
        }

        if (menu.Options.Count == 0) menu.Add("Донатных рас нет", disabled: true);

        menu.AddBack("Назад", byWhom => OpenModAdminMenu(plugin, byWhom));

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>Сколько общих уровней выдать. Общий уровень открывает расы.</summary>
    private static void OpenAccountLevelMenu(WarcraftPlugin plugin, WarcraftPlayer admin, int slot)
    {
        var target = Utilities.GetPlayerFromSlot(slot);
        var name = target is { IsValid: true } ? target.PlayerName : "игрок";

        var menu = new ScreenMenu
        {
            Title = $"ОБЩИЙ УРОВЕНЬ: {name}",
            Parent = byWhom => { OpenModAdminMenu(plugin, byWhom); return null; },
        };

        foreach (var amount in new[] { 5, 25, 50 })
        {
            var levels = amount;
            menu.Add($"+{levels}", byWhom => plugin.GrantAccountLevels(byWhom, slot, levels));
        }

        menu.AddBack("Назад", byWhom => OpenModAdminMenu(plugin, byWhom));

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>Сколько уровней выдать текущей расе игрока.</summary>
    private static void OpenRaceLevelMenu(WarcraftPlugin plugin, WarcraftPlayer admin, int slot)
    {
        var target = Utilities.GetPlayerFromSlot(slot);
        var name = target is { IsValid: true } ? target.PlayerName : "игрок";

        var menu = new ScreenMenu
        {
            Title = $"УРОВЕНЬ РАСЫ: {name}",
            Parent = byWhom => { OpenModAdminMenu(plugin, byWhom); return null; },
        };

        foreach (var amount in new[] { 1, 5 })
        {
            var levels = amount;
            menu.Add($"+{levels}", byWhom => plugin.GrantRaceLevels(byWhom, slot, levels));
        }

        menu.Add("До максимума", byWhom => plugin.GrantRaceLevels(byWhom, slot, XpTable.MaxLevel));

        menu.AddBack("Назад", byWhom => OpenModAdminMenu(plugin, byWhom));

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>
    /// Срок мута. Снятие показываем только заглушённому — иначе пункт стоял бы без дела
    /// и занимал строку, которых в меню всего пять.
    /// </summary>
    private static void OpenMuteMenu(WarcraftPlugin plugin, WarcraftPlayer admin, int slot)
    {
        var target = Utilities.GetPlayerFromSlot(slot);
        var name = target is { IsValid: true } ? target.PlayerName : "игрок";
        var muted = target is { IsValid: true } && plugin.IsMuted(target.SteamID);

        var menu = new ScreenMenu { Title = $"МУТ: {name}", Parent = BackToAdmin(plugin) };

        menu.Add("10 минут", _ => plugin.MutePlayer(admin, slot, 10));
        menu.Add("30 минут", _ => plugin.MutePlayer(admin, slot, 30));
        menu.Add("60 минут", _ => plugin.MutePlayer(admin, slot, 60));

        if (muted) menu.Add("Снять мут", _ => plugin.UnmutePlayer(admin, slot));

        menu.AddBack("Назад", byWhom => OpenAdminMenu(plugin, byWhom));

        plugin.Menus.Open(admin, menu);
    }

    /// <summary>Срок бана. Ноль минут — навсегда.</summary>
    private static void OpenBanDurationMenu(WarcraftPlugin plugin, WarcraftPlayer admin, int slot)
    {
        var target = Utilities.GetPlayerFromSlot(slot);
        var name = target is { IsValid: true } ? target.PlayerName : "игрок";

        var menu = new ScreenMenu { Title = $"БАН: {name}", Parent = BackToAdmin(plugin) };

        menu.Add("30 минут", _ => plugin.BanPlayer(admin, slot, 30));
        menu.Add("1 день", _ => plugin.BanPlayer(admin, slot, 60 * 24));
        menu.Add("Навсегда", _ => plugin.BanPlayer(admin, slot, 0));

        menu.AddBack("Назад", byWhom => OpenAdminMenu(plugin, byWhom));

        plugin.Menus.Open(admin, menu);
    }
}
