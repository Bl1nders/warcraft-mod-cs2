using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;

namespace WarcraftMod.Menus;

/// <summary>
/// Меню мода. Точка входа одна — <see cref="OpenMainMenu"/>.
/// Управление клавишами движения, биндить игроку ничего не нужно.
/// </summary>
public static class WarcraftMenus
{
    // ------------------------------------------------------------------
    // Главное меню
    // ------------------------------------------------------------------

    public static void OpenMainMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        var race = warcraftPlayer.Race;
        var title = race is null
            ? "WARCRAFT — раса не выбрана"
            : $"WARCRAFT — {race.Name}";

        var menu = new ScreenMenu { Title = title };

        menu.Add("Расы", target => OpenRaceMenu(plugin, target),
            hint: race is null ? "выберите расу" : $"ур. {warcraftPlayer.Progress.Level}");

        // Общий уровень открывает новые расы, поэтому он на виду в главном меню.
        menu.Add("Общий уровень", target => PrintUnlocks(plugin, target),
            hint: $"{warcraftPlayer.TotalLevel}");

        // Названия держим короткими: длинная строка вместе с подписью переносится на вторую.
        var unspent = warcraftPlayer.UnspentSkillPoints;
        var autoSkills = warcraftPlayer.Progress.AutoSkills == true;
        menu.Add("Прокачка",
            target => OpenSkillsMenu(plugin, target),
            disabled: race is null,
            hint: race is null ? "" : autoSkills ? "автоматом" : unspent > 0 ? $"очков: {unspent}" : "разложено");

        // Мёртвому свои способности тоже показываем страницей, а не в чат.
        menu.Add("Способности",
            target =>
            {
                if (IsDead(target) && target.Race is { } own) OpenRaceDescriptionPage(plugin, target, own);
                else PrintRaceInfo(target);
            },
            disabled: race is null);

        menu.Add("Описание рас", target => OpenRaceInfoMenu(plugin, target));

        // Сброс — единственная дверь к ручной раскладке: пока очки разложены автоматом,
        // свободных нет и вкладывать в меню прокачки нечего. Поэтому подпись говорит,
        // за чем сюда идут, а не только что здесь произойдёт.
        menu.Add("Сброс очков",
            plugin.ResetSkills,
            disabled: race is null || warcraftPlayer.SpentSkillPoints == 0,
            hint: autoSkills ? "разложить самому" : "");

        // Обратный ход показываем только при выключенном автомате: включённый уже всё
        // разложил, и пункт «включить включённое» был бы строкой, которая ничего не делает.
        if (race is not null && !autoSkills)
            menu.Add("Автораспределение",
                target =>
                {
                    plugin.EnableAutoSkills(target);
                    OpenMainMenu(plugin, target);
                },
                hint: "мод разложит очки");

        menu.Add("Помощь", target => PrintHelp(plugin, target.Controller));

        // Ссылка стоит отдельным пунктом, а не строкой внутри помощи: в помощь заходит тот,
        // кто ищет команды, а в главное меню — все, и не по одному разу за вечер.
        // Сам адрес в строке не показываем: из меню его всё равно не скопировать, а нажатие
        // отдаёт его туда, где с ним можно что-то сделать — в чат и в консоль.
        if (!string.IsNullOrWhiteSpace(plugin.Config.DiscordUrl))
            menu.Add("Discord", target => PrintDiscord(plugin, target));

        menu.AddBack("Закрыть");

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    // ------------------------------------------------------------------
    // Выбор расы
    // ------------------------------------------------------------------

    public static void OpenRaceMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        var menu = new ScreenMenu
        {
            Title = "ВЫБОР РАСЫ",
            Parent = target => BuildMainMenu(plugin, target),
        };

        foreach (var race in InUnlockOrder(plugin))
        {
            // Прогресс хранится по каждой расе отдельно — показываем уровень сразу в списке.
            var level = warcraftPlayer.Record.Races.TryGetValue(race.Id, out var progress) ? progress.Level : 1;
            var isCurrent = warcraftPlayer.Race?.Id == race.Id;
            var isPending = warcraftPlayer.PendingRace?.Id == race.Id;
            var unlocked = WarcraftPlugin.IsRaceUnlocked(warcraftPlayer, race);

            var hint = !unlocked ? race.DonorOnly ? "донатная" : $"с {race.UnlockTotalLevel} общего"
                : isCurrent ? $"ур. {level} — сейчас"
                : isPending ? $"ур. {level} — со след. раунда"
                : $"ур. {level}";

            menu.Add(race.Name,
                target =>
                {
                    plugin.SelectRace(target, race);
                    OpenMainMenu(plugin, target);
                },
                disabled: isCurrent || !unlocked,
                hint: hint);
        }

        menu.AddBack("Назад", target => OpenMainMenu(plugin, target));

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    // ------------------------------------------------------------------
    // Описание рас
    // ------------------------------------------------------------------

    /// <summary>
    /// Список всех рас для чтения, а не для выбора: описание выбранной уходит в чат,
    /// а игрок остаётся при своей расе. Нужно, чтобы посмотреть чужие способности,
    /// не перебирая расы на себе.
    /// </summary>
    public static void OpenRaceInfoMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        var menu = new ScreenMenu
        {
            Title = "ОПИСАНИЕ РАС",
            Parent = target => BuildMainMenu(plugin, target),
        };

        foreach (var race in InUnlockOrder(plugin))
        {
            var level = warcraftPlayer.Record.Races.TryGetValue(race.Id, out var progress) ? progress.Level : 1;

            menu.Add(race.Name,
                target =>
                {
                    // Мёртвому описание показываем прямо в меню: чат ему читать неудобно,
                    // строки уезжают вверх, а времени на чтение у него как раз много.
                    // Живому по-прежнему в чат — он в бою, и панель на пол-экрана ему мешает.
                    if (IsDead(target)) OpenRaceDescriptionPage(plugin, target, race);
                    else
                    {
                        PrintRaceDescription(target, race);

                        // Меню оставляем открытым: обычно смотрят несколько рас подряд.
                        OpenRaceInfoMenu(plugin, target);
                    }
                },
                hint: $"ур. {level}");
        }

        menu.AddBack("Назад", target => OpenMainMenu(plugin, target));

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    /// <summary>Мёртв ли игрок — от этого зависит, куда показывать длинные тексты.</summary>
    private static bool IsDead(WarcraftPlayer warcraftPlayer) => warcraftPlayer.Pawn is not { Health: > 0 };

    /// <summary>
    /// Описание расы страницей в меню, а не в чате. Строки собраны недоступными пунктами:
    /// выбирать в них нечего, поэтому у них нет ни номера, ни курсора — получается просто
    /// текст, который висит перед глазами, пока не нажмут «Назад».
    /// </summary>
    /// <summary>
    /// Предельная длина строки на странице описания, в знаках.
    ///
    /// Меню в мире встаёт по центру самой длинной строки, поэтому одно длинное описание
    /// способности растаскивало всю панель за оба края экрана. Проще ограничить строку,
    /// чем городить измерение ширины: точной ширины сервер всё равно не знает.
    /// </summary>
    private const int PageLineLength = 46;

    public static void OpenRaceDescriptionPage(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer, Race race)
    {
        var known = warcraftPlayer.Record.Races.TryGetValue(race.Id, out var progress);
        var level = known ? progress!.Level : 1;

        // Заголовки способностей и их описания красим по-разному, поэтому строка несёт цвет.
        // Общее описание расы сюда не идёт намеренно: мёртвый пришёл смотреть способности,
        // а лишний абзац сверху только отодвигает их за край экрана.
        var text = new List<(string Text, System.Drawing.Color Color)>
        {
            ($"ваш уровень: {level}", ScreenMenu.ReadableColor),
        };

        for (var i = 0; i < race.Abilities.Count; i++)
        {
            var ability = race.Abilities[i];
            var rank = known && i < progress!.Ranks.Length ? progress.Ranks[i] : 0;

            // Числа в скобках — то же, что и в чате: перезарядка и порог уровня.
            var extra = ability.Kind switch
            {
                AbilityKind.Passive => "",
                AbilityKind.Ultimate when ability.OncePerRound => $" (с {ability.RequiredLevel} ур.)",
                AbilityKind.Ultimate => $" (перезарядка {ability.Cooldown:0} с, с {ability.RequiredLevel} ур.)",
                _ => $" (перезарядка {ability.Cooldown:0} с)",
            };

            // Пустая строка отделяет способности друг от друга — сплошным столбцом их не читать.
            text.Add(("", ScreenMenu.ReadableColor));
            text.Add(($"{KindTag(ability.Kind)} {ability.Name} [{rank}/{ability.MaxRank}]", ScreenMenu.AccentColor));

            foreach (var line in Wrap($"{ability.Description}{extra}"))
                text.Add((line, ScreenMenu.ReadableColor));
        }

        var menu = new ScreenMenu
        {
            Title = race.Name.ToUpperInvariant(),

            // Страница длинная, а листать её мёртвому нечем — показываем целиком.
            PageSize = text.Count + 1,
            Parent = target => BuildRaceInfoMenu(plugin, target),
        };

        foreach (var (line, color) in text) menu.Add(line, disabled: true, color: color);

        menu.AddBack("Назад", target => OpenRaceInfoMenu(plugin, target));

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    /// <summary>Разбить длинный текст по словам на строки не длиннее <see cref="PageLineLength"/>.</summary>
    private static List<string> Wrap(string text)
    {
        var lines = new List<string>();
        var line = "";

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > PageLineLength)
            {
                lines.Add(line);
                line = "";
            }

            line = line.Length == 0 ? word : $"{line} {word}";
        }

        if (line.Length > 0) lines.Add(line);

        return lines;
    }

    /// <summary>
    /// Список рас в чат, с номерами для команд. Нужен там, где меню бесполезно: мёртвому
    /// и зрителю движок не доставляет нажатия клавиш — меню рисуется, но управлять им нечем.
    /// Чат работает всегда, поэтому весь просмотр и выбор рас продублирован командами.
    /// </summary>
    public static void PrintRaceList(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        var controller = warcraftPlayer.Controller;
        if (controller is not { IsValid: true }) return;

        // Название на первом месте намеренно: увидев расу в бою, игрок знает её имя,
        // а номер ему взять негде.
        controller.PrintToChat($"{WarcraftPlugin.Prefix} Расы: {ChatColors.Green}!raceinfo <название>{ChatColors.Default} — способности, {ChatColors.Green}!race <название>{ChatColors.Default} — выбрать");
        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Grey}Хватит части названия: {ChatColors.Default}!race орда{ChatColors.Grey}. Номер из списка тоже подойдёт.");

        var number = 0;
        foreach (var race in InUnlockOrder(plugin))
        {
            number++;

            var level = warcraftPlayer.Record.Races.TryGetValue(race.Id, out var progress) ? progress.Level : 1;
            var unlocked = WarcraftPlugin.IsRaceUnlocked(warcraftPlayer, race);

            var status = warcraftPlayer.Race?.Id == race.Id ? $"{ChatColors.Gold}сейчас"
                : warcraftPlayer.PendingRace?.Id == race.Id ? $"{ChatColors.Gold}со след. раунда"
                : !unlocked ? race.DonorOnly
                    ? $"{ChatColors.Grey}донатная"
                    : $"{ChatColors.Grey}с {race.UnlockTotalLevel} общего"
                : $"{ChatColors.Default}ур. {level}";

            controller.PrintToChat($"  {ChatColors.Green}{number}{ChatColors.Default}. {race.Name} — {status}");
        }
    }

    /// <summary>
    /// Раса по номеру из <see cref="PrintRaceList"/> или по части названия. null — не нашли.
    /// Порядок тот же, что в списке и в меню, иначе номера разъехались бы.
    /// </summary>
    public static Race? FindRace(WarcraftPlugin plugin, string query)
    {
        var ordered = InUnlockOrder(plugin).ToList();

        if (int.TryParse(query, out var number))
            return number >= 1 && number <= ordered.Count ? ordered[number - 1] : null;

        return ordered.FirstOrDefault(race => race.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
               ?? ordered.FirstOrDefault(race => race.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Описание любой расы в чат: сама раса, её способности и ваш прогресс по ней.</summary>
    public static void PrintRaceDescription(WarcraftPlayer warcraftPlayer, Race race)
    {
        var controller = warcraftPlayer.Controller;
        if (controller is not { IsValid: true }) return;

        var known = warcraftPlayer.Record.Races.TryGetValue(race.Id, out var progress);
        var level = known ? progress!.Level : 1;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Green}{race.Name}{ChatColors.Default} — ваш уровень по ней: {ChatColors.Gold}{level}");
        controller.PrintToChat($"{WarcraftPlugin.Prefix} {race.Description}");

        for (var i = 0; i < race.Abilities.Count; i++)
        {
            var ability = race.Abilities[i];

            // Показываем и ваши ранги: по чужой расе они почти всегда нулевые,
            // а по знакомой сразу видно, что уже вложено.
            var rank = known && i < progress!.Ranks.Length ? progress.Ranks[i] : 0;
            var rankColor = rank > 0 ? ChatColors.Gold : ChatColors.Grey;

            // У ультимейта перезарядка не ограничивает ничего: раз за раунд проверяется
            // раньше неё, а на новом раунде кулдауны сбрасываются. Поэтому у него — только
            // порог уровня. Число вернётся само, если раса разрешит себе OncePerRound = false.
            var extra = ability.Kind switch
            {
                AbilityKind.Passive => "",
                AbilityKind.Ultimate when ability.OncePerRound =>
                    $" {ChatColors.Grey}(с {ability.RequiredLevel} ур.){ChatColors.Default}",
                AbilityKind.Ultimate =>
                    $" {ChatColors.Grey}(перезарядка {ability.Cooldown:0} с, с {ability.RequiredLevel} ур.){ChatColors.Default}",
                _ => $" {ChatColors.Grey}(перезарядка {ability.Cooldown:0} с){ChatColors.Default}",
            };

            controller.PrintToChat(
                $"  {KindTag(ability.Kind)} {ChatColors.Green}{ability.Name}{ChatColors.Default} " +
                $"[{rankColor}{rank}/{ability.MaxRank}{ChatColors.Default}] — {ability.Description}{extra}");
        }
    }

    // ------------------------------------------------------------------
    // Голосование за следующую карту
    // ------------------------------------------------------------------

    /// <summary>
    /// Выбор следующей карты. Открывается всем в начале последнего раунда, на время
    /// удлинённой подготовки: игрок в ней и так обездвижен, и меню, которое его
    /// замораживает, ничего у него не отнимает.
    /// </summary>
    public static void OpenMapVoteMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        if (!plugin.MapVote.IsOpen) return;

        var menu = new ScreenMenu { Title = "СЛЕДУЮЩАЯ КАРТА" };

        var number = 0;
        foreach (var entry in plugin.MapVote.Candidates)
        {
            number++;
            var choice = number;
            var votes = plugin.MapVote.VotesFor(entry);

            menu.Add(entry.Name,
                target => plugin.CastMapVote(target, choice),
                hint: votes > 0 ? $"голосов: {votes}" : "");
        }

        // Закрыть можно, но пункт назван честно: не проголосовавших просто не считают.
        menu.AddBack("Не голосовать");

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    // ------------------------------------------------------------------
    // Смена карты
    // ------------------------------------------------------------------

    /// <summary>
    /// Быстрая смена карты между тестами. Сам список живёт в <see cref="MapPool"/> —
    /// добавлять и убирать карты нужно там, меню просто показывает то, что в нём есть.
    /// </summary>
    public static void OpenMapMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer,
        Func<WarcraftPlayer, ScreenMenu?>? parent = null)
    {
        // Родитель зависит от того, откуда пришли: из админки R возвращает в неё,
        // из голой команды !map возвращать некуда — там R просто закрывает меню.
        var menu = new ScreenMenu { Title = "СМЕНА КАРТЫ", Parent = parent };

        foreach (var entry in MapPool.All)
        {
            // Текущую не отключаем: выбрать её — законный способ перезагрузить карту начисто.
            var isCurrent = string.Equals(entry.Name, Server.MapName, StringComparison.OrdinalIgnoreCase);

            menu.Add(entry.Name,
                _ => plugin.ChangeMap(entry),
                hint: isCurrent ? "сейчас" : entry.WorkshopId > 0 ? "Workshop" : "стоковая");
        }

        menu.AddBack("Закрыть");

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    // ------------------------------------------------------------------
    // Прокачка
    // ------------------------------------------------------------------

    public static void OpenSkillsMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Race is not { } race) return;

        var unspent = warcraftPlayer.UnspentSkillPoints;
        var menu = new ScreenMenu
        {
            Title = $"{race.Name} — свободно очков: {unspent}",
            Parent = target => BuildMainMenu(plugin, target),
        };

        for (var i = 0; i < race.Abilities.Count; i++)
        {
            var ability = race.Abilities[i];

            // Показываем купленное — это то, за что отданы очки. Рабочий ранг отстаёт до
            // следующего раунда, и когда он отстаёт, число рядом об этом говорит.
            var rank = warcraftPlayer.BoughtRankOf(i);
            var inPlay = warcraftPlayer.RankOf(i);
            var index = i; // копия для замыкания

            var locked = warcraftPlayer.Progress.Level < ability.RequiredLevel;
            var maxed = rank >= ability.MaxRank;

            var hint = locked ? $"с {ability.RequiredLevel} ур."
                : rank > inPlay ? $"{rank}/{ability.MaxRank} (в бою {inPlay})"
                : maxed ? $"{rank}/{ability.MaxRank} максимум"
                : $"{rank}/{ability.MaxRank}";

            menu.Add($"{KindTag(ability.Kind)} {ability.Name}",
                target =>
                {
                    var error = plugin.UpgradeAbility(target, index);
                    if (error is not null) target.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {error}");

                    // Пока очки есть — остаёмся в прокачке, иначе возвращаемся в главное.
                    if (target.UnspentSkillPoints > 0) OpenSkillsMenu(plugin, target);
                    else OpenMainMenu(plugin, target);
                },
                disabled: locked || maxed || unspent <= 0,
                hint: hint);
        }

        menu.AddBack("Назад", target => OpenMainMenu(plugin, target));

        plugin.Menus.Open(warcraftPlayer, menu);
    }

    /// <summary>
    /// Расы в порядке открытия: сверху стартовые, ниже — по возрастанию требуемого
    /// общего уровня. Реестр находит классы рефлексией, порядок оттуда случайный,
    /// поэтому равные пороги дополнительно сортируем по названию.
    /// </summary>
    private static IEnumerable<Race> InUnlockOrder(WarcraftPlugin plugin) =>
        plugin.Races.All
            // Донатные — в самом низу: порог у них нулевой, иначе они встали бы к стартовым.
            .OrderBy(race => race.DonorOnly ? 1 : 0)
            .ThenBy(race => race.UnlockTotalLevel)
            .ThenBy(race => race.Name, StringComparer.CurrentCulture);

    /// <summary>Главное меню как объект — нужно для возврата по R без повторного открытия.</summary>
    private static ScreenMenu? BuildMainMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        OpenMainMenu(plugin, warcraftPlayer);
        return null; // OpenMainMenu уже подменил активное меню
    }

    /// <summary>Список описаний как объект — по тому же правилу, что и главное меню.</summary>
    private static ScreenMenu? BuildRaceInfoMenu(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        OpenRaceInfoMenu(plugin, warcraftPlayer);
        return null;
    }

    // ------------------------------------------------------------------
    // Текстовые справки
    // ------------------------------------------------------------------

    public static void PrintRaceInfo(WarcraftPlayer warcraftPlayer)
    {
        var controller = warcraftPlayer.Controller;
        if (controller is not { IsValid: true }) return;

        if (warcraftPlayer.Race is not { } race)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Раса не выбрана — {ChatColors.Green}!wc");
            return;
        }

        var progress = warcraftPlayer.Progress;
        var toNext = progress.Level >= XpTable.MaxLevel
            ? "макс."
            : $"{progress.Xp}/{XpTable.XpToNextLevel(progress.Level)}";

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Green}{race.Name}{ChatColors.Default} — уровень {ChatColors.Gold}{progress.Level}{ChatColors.Default}, опыт {toNext}");

        for (var i = 0; i < race.Abilities.Count; i++)
        {
            var ability = race.Abilities[i];
            var rank = warcraftPlayer.BoughtRankOf(i);
            var rankColor = rank > 0 ? ChatColors.Gold : ChatColors.Grey;

            // Купленное посреди раунда ещё не работает — говорим об этом у самой способности,
            // а не общей строкой: строк тут много, и общую припишут не к той.
            var pending = warcraftPlayer.IsUpgradePending(i)
                ? $" {ChatColors.Grey}(в бою {warcraftPlayer.RankOf(i)} — новый ранг со следующего раунда){ChatColors.Default}"
                : "";

            controller.PrintToChat(
                $"  {KindTag(ability.Kind)} {ChatColors.Green}{ability.Name}{ChatColors.Default} " +
                $"[{rankColor}{rank}/{ability.MaxRank}{ChatColors.Default}] — {ability.Description}{pending}");
        }
    }

    /// <summary>Что уже открыто и что откроется дальше — по общему уровню.</summary>
    public static void PrintUnlocks(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        var controller = warcraftPlayer.Controller;
        if (controller is not { IsValid: true }) return;

        var total = warcraftPlayer.TotalLevel;
        var (into, needed) = XpTable.AccountProgress(warcraftPlayer.Record.AccountXp);

        controller.PrintToChat($"{WarcraftPlugin.Prefix} Общий уровень: {ChatColors.Gold}{total}{ChatColors.Default} ({into}/{needed}) — растёт от любой игры, даже на расе, доведённой до потолка.");

        // Ускорение показываем, только когда оно есть: строка «в 1 раз быстрее» бессмысленна.
        var multiplier = XpTable.RaceXpMultiplier(warcraftPlayer.MaxedRaces);
        if (multiplier > 1f)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} Освоено рас до конца: {ChatColors.Gold}{warcraftPlayer.MaxedRaces}{ChatColors.Default} — новые качаются в {ChatColors.Gold}{multiplier:0.#}{ChatColors.Default} раза быстрее.");
        }

        // Донатные в этот список не берём: уровнем их не открыть, обещать их прокачкой нечестно.
        var locked = plugin.Races.All
            .Where(race => !race.DonorOnly && !WarcraftPlugin.IsRaceUnlocked(warcraftPlayer, race))
            .OrderBy(race => race.UnlockTotalLevel)
            .ToList();

        if (locked.Count == 0)
        {
            controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Green}Открыты все расы.");
            return;
        }

        // Называем только ближайшую: она и есть цель. Полный список закрытых рас отсюда
        // убран намеренно — он занимал полэкрана чата ради сведений, которые целиком
        // есть в !races, и превращал справку про уровень в стену текста.
        var next = locked[0];
        controller.PrintToChat($"{WarcraftPlugin.Prefix} Следующая: {ChatColors.Green}{next.Name}{ChatColors.Default} — нужно {ChatColors.Gold}{next.UnlockTotalLevel}{ChatColors.Default}, осталось {ChatColors.Gold}{next.UnlockTotalLevel - total}");
    }

    /// <summary>
    /// Ссылка на Discord: в чат — чтобы увидели сразу, в консоль — чтобы скопировали.
    /// В CS2 ссылка не кликается и текст чата не выделяется, так что консоль — единственный
    /// способ не набирать адрес руками. Тем же приёмом работает <c>!wcbind</c>.
    /// </summary>
    public static void PrintDiscord(WarcraftPlugin plugin, WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Controller is not { IsValid: true } controller) return;

        var url = plugin.Config.DiscordUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} Наш Discord: {ChatColors.Green}{url}");
        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Grey}Новости, донат, жалобы и идеи. Ссылка продублирована в консоль — оттуда её можно скопировать.");

        // В консоль — один раз за сессию. Нажать пункт можно сколько угодно, но там строка
        // нужна ровно одна: консоль открывают, чтобы скопировать адрес, а не читать его копии.
        if (warcraftPlayer.DiscordLinkInConsole) return;
        warcraftPlayer.DiscordLinkInConsole = true;

        controller.PrintToConsole("");
        controller.PrintToConsole($"=== Discord: {url} ===");
        controller.PrintToConsole("");
    }

    /// <summary>
    /// Справка по командам. В чат уходит короткая выжимка, полный сборник — в консоль:
    /// в чате он занял бы полтора экрана и вытолкнул бы всё остальное, а из консоли
    /// команду ещё и копируют вместе с аргументами.
    ///
    /// Зовётся и из серверной консоли, где <c>controller</c> пуст: на боевом сервере
    /// панель хостинга — это единственный доступ, и справочник нужен там не меньше.
    /// </summary>
    public static void PrintReference(WarcraftPlugin plugin, CCSPlayerController? controller)
    {
        var write = controller is { IsValid: true }
            ? controller.PrintToConsole
            : (Action<string>)(line => Console.WriteLine($"[WarcraftMod] {line}"));

        // Из серверной консоли админку показываем всегда: там и так полный доступ.
        var admin = controller is not { IsValid: true } || plugin.IsAdmin(controller);

        write("");
        write("=== Warcraft: команды ===");
        write("Работают и в чате через ! , и в консоли через css_ — !wc это то же, что css_wc.");
        write("Русская раскладка тоже понимается: !цс равно !wc.");
        write("");
        write("  Игроку");
        write("    !wc [номер]           меню мода: W/S выбор, E принять, R назад");
        write("    !wcc                  закрыть меню");
        write("    !races                список рас в чат");
        write("    !raceinfo <название>  способности расы, например: !raceinfo соник");
        write("    !race <название>      выбрать расу");
        write("    !skills               распределить очки");
        write("    !resetskills          сбросить распределение очков");
        write("    !ability              активная способность");
        write("    !ult                  ультимейт");
        write("    !wcinfo               ваша раса, уровень и ранги");
        write("    !wcbind               готовые бинды в консоль");
        write("    !map [номер|имя]      смена карты");

        if (admin)
        {
            write("");
            write("  Админу");
            write("    !admin                карта, кик, бан, мут");
            write("    !wcadmin              выдача рас и уровней — уровни выдаются только отсюда");
            write("    !wcwho                кто сейчас на сервере: ник, SteamID64, раса, уровни");
            write("    !wcplayers            кто заходил вообще: часы и последний визит");
            write("    !wcstats              статистика прогрессии, главное число — опыт в час");
            write("    !wcpos                где я стою: координаты и выход за коробку спавнов");
            write("    !wcgrant <кто> <раса>    выдать расу лично, обходя порог уровня");
            write("    !wcrevoke <кто> <раса>   отобрать выданную расу");
            write("    !wcunban <SteamID64>     снять бан");
            write("");
            write("    <кто> — SteamID64, [U:1:N], STEAM_1:Y:Z или часть ника того, кто на сервере.");
            write("    Номера брать из !wcwho. <раса> — идентификатор, он же в !races.");
        }

        write("");
        write($"  Сборка от {plugin.BuildTime:dd.MM.yyyy HH:mm}");
        write("");
    }

    public static void PrintHelp(WarcraftPlugin plugin, CCSPlayerController? controller)
    {
        PrintReference(plugin, controller);

        if (controller is not { IsValid: true }) return;

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Gold}Команды мода:");
        controller.PrintToChat($"  {ChatColors.Green}!wc{ChatColors.Default} — меню: {ChatColors.Green}W/S{ChatColors.Default} выбор, {ChatColors.Green}E{ChatColors.Default} принять, {ChatColors.Green}R{ChatColors.Default} назад");
        controller.PrintToChat($"  {ChatColors.Green}!races{ChatColors.Default} — список всех рас");
        controller.PrintToChat($"  {ChatColors.Green}!raceinfo <название>{ChatColors.Default} — способности расы. Пример: {ChatColors.Grey}!raceinfo соник");
        controller.PrintToChat($"  {ChatColors.Green}!race <название>{ChatColors.Default} — выбрать расу. Пример: {ChatColors.Grey}!race соник");
        controller.PrintToChat($"  {ChatColors.Green}!wcinfo{ChatColors.Default} — ваша раса, уровень и ранги");
        controller.PrintToChat($"  {ChatColors.Green}!wcbind{ChatColors.Default} — готовые бинды в консоль");
        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Grey}Мёртвым клавиши не доходят:{ChatColors.Default} {ChatColors.Green}!wc <номер>{ChatColors.Grey} выбор, {ChatColors.Green}!wc 0{ChatColors.Grey} дальше, {ChatColors.Green}!wcc{ChatColors.Grey} закрыть.");

        // Дата сборки — чтобы из игры было видно, какая версия мода живёт на сервере.
        // Дважды подряд время уходило на «обновление залито, а ведёт себя по-старому»,
        // и оба раза вопрос решался этим одним числом.
        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Grey}Сборка от {plugin.BuildTime:dd.MM.yyyy HH:mm}");

        if (plugin.IsAdmin(controller))
            controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Grey}Админка:{ChatColors.Default} {ChatColors.Green}!admin{ChatColors.Default} — карта, кик, бан, мут; {ChatColors.Green}!wcadmin{ChatColors.Default} — выдача рас и уровней");

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Grey}Полный сборник команд — в консоли, {ChatColors.Green}~");
    }

    private static string KindTag(AbilityKind kind) => kind switch
    {
        AbilityKind.Passive => "[П]",
        AbilityKind.Active => "[А]",
        AbilityKind.Ultimate => "[У]",
        _ => "[?]",
    };
}
