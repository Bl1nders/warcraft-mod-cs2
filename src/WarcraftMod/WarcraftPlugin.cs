using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Config;
using WarcraftMod.Core;
using WarcraftMod.Menus;
using WarcraftMod.Storage;
// System.Threading тоже приносит Timer — фиксируем нужный явно.
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod;

public sealed class WarcraftPlugin : BasePlugin
{
    public override string ModuleName => "Warcraft Mod";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "PROJEACT";
    public override string ModuleDescription => "Расы, способности и прокачка в стиле Warcraft для CS2";

    public static readonly string Prefix = $" {ChatColors.Gold}[WC]{ChatColors.Default}";

    /// <summary>
    /// С какой скорости вверх считаем, что игрок оттолкнулся от земли, а не был подброшен
    /// способностью. Прыжок движка даёт около 300, самый сильный толчок мода по вертикали —
    /// Кувырок Кролика на четвёртом ранге — около 210. Порог стоит между ними: ниже начал
    /// бы считать прыжком кувырок, выше пропускал бы настоящие прыжки.
    /// </summary>
    private const float HopTakeoffSpeed = 240f;

    private readonly RaceRegistry _races = new();
    private readonly Dictionary<int, WarcraftPlayer> _players = new();

    private JsonPlayerStore _store = null!;
    private BanStore _bans = null!;
    private WarcraftConfig _config = null!;

    /// <summary>
    /// Заглушённые: SteamID64 — unix-время окончания. Ключ именно SteamID, а не слот:
    /// иначе мут снимался бы переподключением и не стоил бы ничего. По той же причине
    /// срок считаем по настоящим часам, а не по Server.CurrentTime — тот обнуляется
    /// на смене карты, и десятиминутный мут пережил бы её либо мгновенно, либо навсегда.
    ///
    /// В файл не пишется: мут — мера на сейчас, для длинного наказания есть бан.
    /// </summary>
    private readonly Dictionary<ulong, long> _mutedUntil = new();

    public RaceRegistry Races => _races;
    public WarcraftConfig Config => _config;

    /// <summary>
    /// Когда собран файл плагина. Показывается в <c>!wchelp</c>: без этого «залил обновление,
    /// а мод ведёт себя по-старому» неотличимо от настоящей неполадки, и оба раза, когда так
    /// случалось, ответ был именно в дате файла.
    /// </summary>
    public DateTime BuildTime => File.GetLastWriteTime(Path.Combine(ModuleDirectory, "WarcraftMod.dll"));

    private ScreenMenuManager _menus = null!;
    public ScreenMenuManager Menus => _menus;

    /// <summary>Таймеры способностей, живущие только до конца раунда.</summary>
    private readonly List<Timer> _roundTimers = [];

    /// <summary>Слоты заражённых и время, до которого действует заражение.</summary>
    private readonly Dictionary<int, float> _infectedUntil = new();

    /// <summary>
    /// Состояния только что отключившихся. Короткий обрыв связи приходит в мод как выход
    /// и вход заново, и без этой передержки терялось бы всё, чего нет в сохранении:
    /// отложенный выбор расы, кулдауны, потраченный за раунд ультимейт.
    /// </summary>
    private readonly Dictionary<ulong, (WarcraftPlayer Player, float LeftAt)> _recentlyLeft = new();

    /// <summary>Сколько ждём возвращения, прежде чем забыть состояние.</summary>
    private const float ReconnectGrace = 180f;

    /// <summary>Накопленное начисление для показа в центре экрана.</summary>
    private sealed class XpNotice
    {
        public int Amount;
        public bool AccountOnly;
        public readonly List<string> Reasons = [];
    }

    /// <summary>
    /// Начисления, ещё не показанные игроку. Копим и выводим одной строкой: события
    /// приходят пачкой — убийство, закрывшее раунд, и награда за сам раунд разделены
    /// долями секунды, и вторая надпись затирала первую. Игрок видел только последнюю
    /// и считал, что опыт за убийство пропал.
    /// </summary>
    private readonly Dictionary<int, XpNotice> _xpNotices = new();

    /// <summary>
    /// Пометить игрока заражённым: по нему начинает проходить урон от своих.
    /// Хранится по слоту, а не в состоянии игрока, потому что заразить можно и бота.
    /// </summary>
    public void MarkInfected(int slot, float duration) =>
        _infectedUntil[slot] = Server.CurrentTime + duration;

    public void ClearInfection(int slot) => _infectedUntil.Remove(slot);

    private bool IsInfected(int slot) =>
        _infectedUntil.TryGetValue(slot, out var until) && until > Server.CurrentTime;

    /// <summary>
    /// Таймер для эффекта, который не должен пережить раунд: зона батута, аура и подобное.
    /// Такие эффекты действуют в мире, и без гашения оставались бы висеть в следующем раунде.
    /// </summary>
    public Timer AddRoundTimer(float interval, Action callback, TimerFlags? flags = null)
    {
        var timer = AddTimer(interval, callback, flags);
        _roundTimers.Add(timer);
        return timer;
    }

    private void KillRoundTimers()
    {
        foreach (var timer in _roundTimers)
        {
            try
            {
                timer.Kill();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WarcraftMod] Не удалось остановить таймер раунда: {ex.Message}");
            }
        }

        _roundTimers.Clear();
    }

    // ------------------------------------------------------------------
    // Жизненный цикл плагина
    // ------------------------------------------------------------------

    public override void Load(bool hotReload)
    {
        _config = WarcraftConfig.LoadOrCreate(Path.Combine(ModuleDirectory, "warcraft_config.json"));
        _store = new JsonPlayerStore(Path.Combine(ModuleDirectory, "warcraft_players.json"));
        _bans = new BanStore(Path.Combine(ModuleDirectory, "warcraft_bans.json"));
        _races.DiscoverAndBind(this);
        _menus = new ScreenMenuManager(
            new MenuLayout(
                _config.MenuFontName,
                _config.MenuFontSize,
                _config.MenuShiftRight,
                _config.MenuShiftUp,
                _config.MenuFollowView,
                _config.MenuBackground,
                _config.MenuShadow),
            _config.MenuInWorldText,
            (CenterStyle)_config.MenuCenterStyle,
            _config.MenuCenterMarkup);

        CenterText.Enabled = _config.ShowCenterMessages;
        RaceModels.Configure(_config.UseRaceModels, _config.RaceModels);

        // Меню в мире — обычные сущности, и чужие надо вырезать: иначе меню одного игрока
        // висит посреди карты на виду у всех.
        RegisterListener<Listeners.CheckTransmit>(infoList =>
        {
            var hasPanels = _menus.HasWorldPanels;
            if (!hasPanels && _glowMarks.Count == 0) return;

            foreach (var (info, viewer) in infoList)
            {
                if (viewer is not { IsValid: true }) continue;

                if (hasPanels) _menus.HideForeignPanels(info, viewer.Slot);
                if (_glowMarks.Count > 0) FilterGlow(info, viewer);
            }
        });

        Console.WriteLine($"[WarcraftMod] Загружено рас: {_races.All.Count}");

        // Урон правим до его применения — только так работают крит, уклонение и снижение урона.
        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnEntityTakeDamagePre);

        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnectPost>(OnClientDisconnectPost);

        // Бан проверяем на авторизации: раньше этого момента SteamID ещё не известен.
        RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) =>
        {
            if (!_bans.IsBanned(steamId.SteamId64, out var ban)) return;

            var until = ban!.UntilUnix == 0
                ? "навсегда"
                : DateTimeOffset.FromUnixTimeSeconds(ban.UntilUnix).ToLocalTime().ToString("dd.MM HH:mm");

            Console.WriteLine($"[WarcraftMod] Забаненный {ban.Name} ({steamId.SteamId64}) отклонён, бан до: {until}");

            // Рвём соединение следующим кадром: делать это прямо в обработке входа опасно.
            Server.NextFrame(() =>
            {
                if (Utilities.GetPlayerFromSlot(slot)?.UserId is { } userId)
                    Server.ExecuteCommand($"kickid {userId}");
            });
        });

        // Меню рисуется каждый тик — текст в центре экрана иначе гаснет.
        // Там же живёт банни-хоп: прыжок надо поймать ровно в кадр приземления.
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);

        // Карта успевает выставить своё сразу после загрузки — задержка нужна, чтобы
        // оказаться после неё, а не до. Разминки хватает с запасом, и первый раунд
        // начинается уже с нашими значениями.
        RegisterListener<Listeners.OnMapStart>(_ => AddTimer(5f, EnforceServerRules));

        // Частицы обязательно объявлять до загрузки карты, иначе EffectName молча игнорируется.
        // Модели маскировки сюда добавлять нельзя: сервер падал с access violation сразу
        // после загрузки карты. Модели агентов игра грузит сама, отдельного объявления не нужно.
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            foreach (var effect in VisualEffects.Fx.All) manifest.AddResource(effect);

            // Модели предметов, которые мод ставит в мир, — без объявления они невидимы.
            foreach (var model in VisualEffects.Models.All) manifest.AddResource(model);
        });

        RegisterMenuHotkeys();
        CollectChatCommands();

        // Перехватываем чат, чтобы дописать расу к нику.
        AddCommandListener("say", (controller, info) => OnPlayerSay(controller, info, teamOnly: false));
        AddCommandListener("say_team", (controller, info) => OnPlayerSay(controller, info, teamOnly: true));

        AddTimer(0.1f, ApplyPersistentEffects, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        // Подсветка обслуживается без остановки на смене карты: истёкшие метки надо
        // снимать всегда, а не только пока идёт раунд.
        AddTimer(0.1f, MaintainGlow, TimerFlags.REPEAT);
        AddTimer(_config.SaveIntervalSeconds, () => _store.FlushIfDirty(), TimerFlags.REPEAT);

        // Муты считаются минутами, поэтому раз в пять секунд проверить срок — с запасом.
        AddTimer(5f, ExpireMutes, TimerFlags.REPEAT);

        // Начисления копятся долю секунды и показываются одной строкой — иначе награда
        // за раунд затирает надпись про убийство, которое этот раунд и закончило.
        AddTimer(0.4f, FlushXpNotices, TimerFlags.REPEAT);

        AddTimer(PlaytimeTickSeconds, AccumulatePlaytime, TimerFlags.REPEAT);
        AddTimer(AfkCheckSeconds, CheckAfk, TimerFlags.REPEAT);
        AddTimer(StrayCheckSeconds, ReturnStrayPlayers, TimerFlags.REPEAT);

        // При горячей перезагрузке игроки уже на сервере — восстанавливаем их состояние.
        if (hotReload)
        {
            foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
                RegisterPlayer(player.Slot);

            // Края карты обычно замеряются в начале раунда, но перезагрузка случается
            // посреди него — без этого возврат улетевших молчал бы до конца раунда.
            MeasureMapBounds();
        }
    }

    public override void Unload(bool hotReload) => _store.FlushIfDirty();

    // ------------------------------------------------------------------
    // Игроки
    // ------------------------------------------------------------------

    private void OnClientPutInServer(int slot)
    {
        RegisterPlayer(slot);
        ReapplyMute(slot);
    }

    private void OnClientDisconnectPost(int slot)
    {
        _menus.ForceForget(slot);

        // Голос вышедшего не считаем: карту выбирают те, кто на ней останется играть.
        _mapVote.Forget(slot);

        // Мут при выходе намеренно не снимаем: он висит на SteamID и ждёт возвращения.

        if (!_players.Remove(slot, out var leaving)) return;

        // Не выбрасываем состояние сразу: игрок может вернуться через секунду.
        _recentlyLeft[leaving.SteamId] = (leaving, Server.CurrentTime);
        _store.FlushIfDirty();
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (Get(player) is not { } warcraftPlayer) return;

        // Любое нажатие — признак присутствия за компьютером.
        warcraftPlayer.LastActivityAt = Server.CurrentTime;

        // Пробел отслеживаем по обеим сторонам: банни-хопу нужно знать, держат ли его сейчас.
        if (pressed.HasFlag(PlayerButtons.Jump)) warcraftPlayer.JumpHeld = true;
        if (released.HasFlag(PlayerButtons.Jump)) warcraftPlayer.JumpHeld = false;

        // Меню забирает управление целиком, пока открыто.
        if (_menus.HandleButtons(warcraftPlayer, pressed)) return;

        if (pressed.HasFlag(PlayerButtons.Jump)) TryExtraJump(warcraftPlayer);
    }

    private void OnTick()
    {
        _menus.RenderAll();
        HandleAutoHop();
    }

    /// <summary>
    /// Банни-хоп. Сам прыжок мод больше не делает — его делает движок
    /// (<c>sv_autobunnyhopping 1</c>), а мод только замечает и раздаёт за него расе.
    ///
    /// Прыгать отсюда нельзя, и причина не в красоте. Клиент предсказывает своё
    /// движение сам, про мод он не знает ничего, и подброс с сервера доходил до него
    /// на круг задержки позже: игрок успевал прилипнуть к земле, а трение за эти
    /// лишние тики съедало ровно ту скорость, ради которой распрыжка и нужна. Чем
    /// выше пинг, тем хуже — вплоть до неиграбельного. Движковый прыжок клиент
    /// предсказывает наравне с сервером, поэтому паузы нет ни при каком пинге.
    /// Возвращать подброс сюда нельзя: он разойдётся с предсказанием клиента и
    /// вместо паузы даст дёрганье, что заметно противнее.
    /// </summary>
    private void HandleAutoHop()
    {
        foreach (var warcraftPlayer in _players.Values)
        {
            if (!warcraftPlayer.AutoBhop || warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0)
            {
                warcraftPlayer.HopArmed = false;
                continue;
            }

            var speedZ = pawn.AbsVelocity.Z;

            // Взводим и на земле, и в падении. Одной землёй обойтись нельзя: движок
            // успевает подбросить игрока внутри того же тика, и кадра касания мод
            // может не увидеть вовсе — тогда прыжки не засчитывались бы никогда.
            if (speedZ <= 0f || Effects.IsOnGround(pawn))
            {
                warcraftPlayer.HopArmed = true;
                continue;
            }

            // Отрыв: взведены и пошли вверх быстрее, чем даёт любой толчок способностей.
            if (!warcraftPlayer.HopArmed || speedZ < HopTakeoffSpeed) continue;

            warcraftPlayer.HopArmed = false;

            if (!warcraftPlayer.JumpHeld || _menus.IsOpen(warcraftPlayer.Slot)) continue;

            warcraftPlayer.Race?.OnAutoHop(warcraftPlayer);
        }
    }

    /// <summary>
    /// Прыжки. С земли движок прыгает сам — мы только добавляем прибавку к высоте,
    /// причём на следующем кадре, иначе движок перезапишет нашу скорость своей.
    /// В воздухе прыжок целиком наш.
    /// </summary>
    private void TryExtraJump(WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0) return;

        if (Effects.IsOnGround(pawn))
        {
            if (warcraftPlayer.GroundJumpBonus <= 0f) return;

            var bonus = warcraftPlayer.GroundJumpBonus;
            Server.NextFrame(() =>
            {
                if (warcraftPlayer.Pawn is { } current && current.Health > 0) current.AbsVelocity.Z += bonus;
            });
            return;
        }

        if (warcraftPlayer.ExtraJumps <= 0) return;
        if (warcraftPlayer.ExtraJumpsUsed >= warcraftPlayer.ExtraJumps) return;

        pawn.AbsVelocity.Z = warcraftPlayer.ExtraJumpPower;
        warcraftPlayer.ExtraJumpsUsed++;
    }

    /// <summary>Сколько секунд ждём подтверждения личности от Steam, по попытке в секунду.</summary>
    private const int RegistrationAttempts = 15;

    private void RegisterPlayer(int slot, int attempt = 0)
    {
        var controller = Utilities.GetPlayerFromSlot(slot);
        if (controller is not { IsValid: true } || controller.IsBot) return;

        var steamId = controller.AuthorizedSteamID?.SteamId64 ?? 0;
        if (steamId == 0)
        {
            // Steam подтверждает личность не в момент входа. На локальном сервере это
            // происходило мгновенно, и одной попытки хватало; на боевом занимает секунды,
            // и без повторов игрок оставался незарегистрированным до конца сессии — а
            // значит все команды мода молчали, ничего при этом не сообщая.
            if (attempt < RegistrationAttempts)
            {
                AddTimer(1f, () => RegisterPlayer(slot, attempt + 1));
                return;
            }

            // Досюда доходить не должно. Если дошло — говорим громко: немой отказ
            // выглядит как «мод сломался целиком» и ищется часами.
            Console.WriteLine($"[WarcraftMod] {controller.PlayerName}: Steam не подтвердил SteamID за {RegistrationAttempts} с. " +
                              "Игрок остался без мода. Проверьте GSLT-токен и связь сервера со Steam.");
            return;
        }

        // Тот же игрок в том же слоте — состояние не пересоздаём. Короткий обрыв связи
        // приходит сюда так же, как первый вход, и новое состояние стирало бы бонусы расы
        // до конца раунда: выдаются они на возрождении, а игрок в этот момент уже жив.
        if (_players.TryGetValue(slot, out var known) && known.SteamId == steamId)
        {
            AnnounceRace(known, controller);
            PrintMenuHint(controller);
            ScheduleMenuHints(steamId);
            return;
        }

        // Вернулся тот, кто только что отвалился, — забираем его состояние целиком.
        // В сохранении лежит не всё: отложенный выбор расы там появился только сейчас,
        // а кулдауны и потраченный ультимейт не хранятся вовсе.
        ForgetStaleReconnects();
        if (_recentlyLeft.Remove(steamId, out var cached))
        {
            cached.Player.Rebind(slot);
            _players[slot] = cached.Player;

            AnnounceRace(cached.Player, controller);
            PrintMenuHint(controller);
            ScheduleMenuHints(steamId);
            ApplyRaceBonusesIfAlive(cached.Player);
            return;
        }

        var record = _store.Get(steamId);
        record.LastKnownName = controller.PlayerName;
        MigrateAccountXp(record);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (record.FirstSeenUnix == 0) record.FirstSeenUnix = now;
        record.LastSeenUnix = now;

        var warcraftPlayer = new WarcraftPlayer(slot, steamId, record);
        _players[slot] = warcraftPlayer;

        // Приветствие только выбираем — играть его рано, оболочки у игрока ещё нет.
        warcraftPlayer.PendingWelcomeSound = ChooseWelcomeSound(record, now);

        var race = _races.Find(record.CurrentRaceId);
        if (race is not null)
        {
            warcraftPlayer.SetRace(race);

            // Выбор, сделанный посреди прошлого раунда, переживает и перезаход.
            warcraftPlayer.PendingRace = _races.Find(record.PendingRaceId);

            AnnounceRace(warcraftPlayer, controller);
            PrintMenuHint(controller);

            // Регистрация приходит и к живому игроку — после переподключения или горячей
            // перезагрузки плагина. Возрождения ждать нечего, бонусы расы выдаём сразу.
            ApplyRaceBonusesIfAlive(warcraftPlayer);
        }
        else
        {
            // Новичку раса достаётся сама, случайной из стартовых. Выбор из списка при
            // первом входе — это экран вместо игры: человек зашёл посмотреть, что тут
            // за сервер, а получает вопрос, ответа на который ещё не знает. Со случайной
            // расой он сразу в бою, а сменить её — два нажатия в !wc.
            controller.PrintToChat($"{Prefix} Добро пожаловать! Меню мода — {ChatColors.Green}!wc{ChatColors.Default} (удобно забиндить: {ChatColors.Grey}bind f css_wc{ChatColors.Default})");

            if (RandomStarterRace() is { } starter)
            {
                ApplyRace(warcraftPlayer, starter);
                controller.PrintToChat($"{Prefix} Раса выдана случайно. Поменять — {ChatColors.Green}!wc{ChatColors.Default}, там же прокачка.");
            }

            // Меню всё равно открываем, если так велено настройкой: раса уже есть,
            // и это уже не допрос новичка, а показ того, из чего вообще выбирают.
            if (_config.ShowRaceMenuOnFirstJoin) WarcraftMenus.OpenRaceMenu(this, warcraftPlayer);
        }

        // Приглашение в Discord — обоим путям выше, и ровно один раз за вход. Место выбрано
        // намеренно: человек только зашёл, в чате пусто, и это единственный момент, когда
        // строку читают целиком. В повторы подсказки её не кладём — это приглашение,
        // а не напоминание, и от второго показа оно становится рекламой.
        PrintDiscordInvite(controller);

        // Повторы нужны и новичку: первую строку легко пропустить в любом случае.
        ScheduleMenuHints(steamId);

        _store.MarkDirty();
    }

    /// <summary>
    /// Какую реплику приветствия играть вошедшему — и играть ли вообще.
    ///
    /// Возвращает имя звукового события или пустую строку. Отметку о проигрывании здесь
    /// намеренно не ставим: игрок может выйти, не дождавшись спавна, и тогда суточная
    /// пауза началась бы от приветствия, которого он не слышал.
    /// </summary>
    private string ChooseWelcomeSound(PlayerRecord record, long now)
    {
        // Новичок — тот, кому приветствие ещё ни разу не звучало, а не тот, кого сервер
        // впервые видит. Разница неочевидна, но существенна: отметку о первом визите мод
        // ставит при регистрации, а приветствие играет на спавне. Между этими моментами
        // человек успевает уйти из выбора команды, отвалиться по связи или поймать
        // css_plugins reload — и при отметке-по-визиту своё единственное приветствие
        // он терял навсегда, ни разу не услышав. Поймано замером 18.08.2026: перезагрузка
        // плагина между заходом и спавном подменила реплику новичка на реплику возвращения.
        //
        // Цена решения названа вслух: те, кто играл до появления озвучки, услышат
        // приветствие новичка по одному разу. Это лучше обратного промаха — они его
        // и правда ни разу не слышали.
        if (record.LastWelcomeUnix == 0) return _config.WelcomeSoundNew ?? "";

        var options = _config.WelcomeSoundReturning;
        if (options is null || options.Length == 0) return "";

        var pause = (long)(_config.WelcomeSoundHours * 3600f);
        if (pause > 0 && record.LastWelcomeUnix > 0 && now - record.LastWelcomeUnix < pause) return "";

        return options[Random.Shared.Next(options.Length)];
    }

    /// <summary>
    /// Разовый перенос со старой схемы, где общий уровень был суммой уровней рас.
    /// Без него все, кто уже играл, зашли бы с общим уровнем 1 и закрытыми расами.
    ///
    /// Признак «ещё не переносили» — нулевой общий опыт при непустом списке рас.
    /// У новичка рас нет вовсе, так что спутать их нельзя.
    /// </summary>
    private static void MigrateAccountXp(PlayerRecord record)
    {
        if (record.AccountXp > 0 || record.Races.Count == 0) return;

        var oldTotalLevel = record.Races.Values.Sum(progress => progress.Level);
        record.AccountXp = XpTable.AccountXpForLevel(oldTotalLevel);
    }

    /// <summary>
    /// Напоминание про меню. Вторая строка — про мёртвых и зрителей: движок не доставляет
    /// им нажатия клавиш, меню у них открывается, но не управляется. Без подсказки это
    /// выглядит как поломка, и человек решает, что мод не работает.
    /// </summary>
    private static void PrintMenuHint(CCSPlayerController? controller)
    {
        if (controller is not { IsValid: true }) return;

        controller.PrintToChat($"{Prefix} Меню мода — {ChatColors.Green}!wc{ChatColors.Default}, справка — {ChatColors.Green}!wchelp");
        controller.PrintToChat($"{Prefix} {ChatColors.Grey}Мёртвым и зрителям клавиши не работают — расы смотреть через {ChatColors.Default}{ChatColors.Green}!races{ChatColors.Default}");
    }

    /// <summary>
    /// Приглашение в Discord одной строкой. Отдельной, а не хвостом чужой подсказки:
    /// ссылку в CS2 нельзя ни кликнуть, ни выделить, её перепечатывают руками — а для
    /// этого адрес должен попасться на глаза целиком.
    ///
    /// Повод назван прямо в строке. «Заходи в Discord» никого не двигает; двигает то,
    /// ради чего туда идут, и донатные расы здесь единственное, что нельзя получить
    /// на самом сервере.
    /// </summary>
    private void PrintDiscordInvite(CCSPlayerController? controller)
    {
        if (controller is not { IsValid: true }) return;

        var url = _config.DiscordUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        controller.PrintToChat($"{Prefix} Наш Discord: {ChatColors.Green}{url}{ChatColors.Default} — новости, донат, жалобы и идеи");
    }

    /// <summary>
    /// Повторить подсказку вошедшему через заданные промежутки. Зашедший в разгар боя
    /// первую строку часто не замечает — повторы догоняют его, когда он осмотрелся.
    ///
    /// Игрока ищем по SteamID, а не по слоту: за две минуты он мог выйти, а слот занять
    /// кто-то другой, и тот получил бы чужую подсказку по второму кругу.
    /// </summary>
    private void ScheduleMenuHints(ulong steamId)
    {
        if (_config.HintRepeats <= 0 || _config.HintRepeatSeconds <= 0) return;

        for (var repeat = 1; repeat <= _config.HintRepeats; repeat++)
        {
            // Задержку считаем в локальную переменную: общая переменная цикла к моменту
            // срабатывания таймеров была бы уже последней для всех сразу.
            var delay = _config.HintRepeatSeconds * repeat;
            AddTimer(delay, () => PrintMenuHint(FindOnline(steamId)));
        }
    }

    /// <summary>Забыть тех, кто отключился давно и уже не вернётся.</summary>
    private void ForgetStaleReconnects()
    {
        if (_recentlyLeft.Count == 0) return;

        var stale = _recentlyLeft
            .Where(entry => Server.CurrentTime - entry.Value.LeftAt > ReconnectGrace)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var steamId in stale) _recentlyLeft.Remove(steamId);
    }

    private void AnnounceRace(WarcraftPlayer warcraftPlayer, CCSPlayerController controller)
    {
        if (warcraftPlayer.Race is not { } race) return;

        // Заход — то место, где автомат догоняет старые записи: очки, накопленные до
        // этой правки, раскладываются в первый же приход и сразу идут в бой.
        var upgrades = AutoSpendSkillPoints(warcraftPlayer);
        if (upgrades.Count > 0) warcraftPlayer.ApplyBoughtRanks();

        controller.PrintToChat($"{Prefix} С возвращением! Раса: {ChatColors.Green}{race.Name}{ChatColors.Default}, уровень {ChatColors.Gold}{warcraftPlayer.Progress.Level}{ChatColors.Default}.");

        if (upgrades.Count > 0)
            controller.PrintToChat($"{Prefix} Очки вложены сами: {DescribeUpgrades(upgrades)}. Своя раскладка — {ChatColors.Green}!skills");
        else
            NotifyUnspentPoints(warcraftPlayer);

        UpdateClanTag(warcraftPlayer);
    }

    /// <summary>
    /// Пишет расу в клановый тег — CS2 рисует его в табло перед ником. Скрытным расам
    /// достаётся пустая строка, ею же тег стирается при отказе от расы.
    /// </summary>
    private void UpdateClanTag(WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Controller is not { IsValid: true, IsBot: false } controller) return;

        // Палочки по краям отделяют расу от ника: слитную подпись глаз в табло не ловит.
        var tag = warcraftPlayer.Race is { HiddenInScoreboard: false } race ? $"|{race.Name}|" : "";
        if (controller.Clan == tag) return;

        controller.Clan = tag;
        Utilities.SetStateChanged(controller, "CCSPlayerController", "m_szClan");

        // Немой отказ здесь недопустим: без этой строки «тега не видно» означало бы сразу
        // и «движок не принял запись», и «код не выполнился» — а это разные починки.
        if (controller.Clan != tag)
            Console.WriteLine($"[WarcraftMod] Клановый тег не записался: {controller.PlayerName} → «{tag}»");

        RequestScoreboardRefresh();
    }

    /// <summary>Ждёт ли табло перерисовки. Несколько смен тега подряд дают одну рассылку.</summary>
    private bool _scoreboardDirty;

    /// <summary>
    /// Попросить клиентов перестроить табло. Записанный тег сам собой там не появляется:
    /// клиент держит свой список игроков и перечитывает его только по поводу, поэтому
    /// смена расы висела в табло прежней по нескольку раундов.
    ///
    /// Перерисовка рассылается событием смены уровня — клиент понимает его как повод
    /// собрать интерфейс заново. Один раз этот приём уже выбрасывали, 16.08.2026: он
    /// заставлял мигать белым меню в центре экрана. Меню с тех пор переехало на надписи
    /// в мире (<c>point_worldtext</c>), а их перестройка интерфейса не касается — поэтому
    /// приём и вернулся. Если мигание вернётся вместе с ним, виноват будет этот вызов.
    ///
    /// Просьбы копятся до конца кадра: на старте раунда тег меняется у многих сразу,
    /// и рассылать событие на каждого — это разослать его десять раз подряд.
    /// </summary>
    private void RequestScoreboardRefresh()
    {
        if (_scoreboardDirty) return;

        _scoreboardDirty = true;

        Server.NextFrame(() =>
        {
            _scoreboardDirty = false;

            try
            {
                new EventNextlevelChanged(true).FireEvent(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WarcraftMod] Не удалось перерисовать табло: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Выдать бонусы расы игроку, который уже жив. Обычно это делает спавн, но регистрация
    /// живого игрока случается и без него — тогда без этого вызова он остался бы
    /// без пассивок до следующего раунда.
    /// </summary>
    private static void ApplyRaceBonusesIfAlive(WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Race is not { } race) return;
        if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0) return;

        if (ApplyRaceModel(warcraftPlayer) is { } raceModel) warcraftPlayer.OriginalModel = raceModel;
        else warcraftPlayer.OriginalModel ??= Effects.CurrentModelOf(pawn);

        Effects.SetModelScale(pawn, race.BodyScale);

        race.OnSpawn(warcraftPlayer);
    }

    /// <summary>
    /// Надеть на игрока облик его расы и вернуть путь к нему, или null — если расе
    /// положен обычный вид. Модель берётся своя на каждую команду: она же и есть
    /// главный признак «свой-чужой», и один облик на обе команды сделал бы Орка
    /// неотличимым от врага для собственной команды.
    /// </summary>
    private static string? ApplyRaceModel(WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Race is not { } race) return null;
        if (warcraftPlayer.Controller is not { IsValid: true } controller) return null;
        if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0) return null;
        if (RaceModels.For(race.Id, controller.Team) is not { } model) return null;

        Effects.SetModel(pawn, model);
        return model;
    }

    /// <summary>
    /// Команды css_wc1..css_wc9 для выбора пункта меню цифрой.
    /// Движок CS2 не сообщает плагину о нажатии цифровых клавиш, поэтому единственный
    /// путь — привязать их через bind к консольным командам.
    /// </summary>
    private void RegisterMenuHotkeys()
    {
        for (var i = 1; i <= 9; i++)
        {
            var number = i;
            AddCommand($"css_wc{i}", $"Выбрать пункт меню №{i}", (controller, _) =>
            {
                // Сначала своё меню: раньше цифры уходили только во встроенное меню
                // CounterStrikeSharp, и бинды из !wcbind не делали ровным счётом ничего.
                if (Get(controller) is { } warcraftPlayer && _menus.SelectByNumber(warcraftPlayer, number)) return;

                if (controller is { IsValid: true }) MenuManager.OnKeyPress(controller, number);
            });
        }
    }

    public WarcraftPlayer? Get(CCSPlayerController? controller) =>
        controller is { IsValid: true } ? _players.GetValueOrDefault(controller.Slot) : null;

    public IEnumerable<WarcraftPlayer> AllPlayers => _players.Values;

    // ------------------------------------------------------------------
    // Игровые события
    // ------------------------------------------------------------------

    /// <summary>
    /// Сыграть приветствие, если оно дожидалось спавна. Идёт до проверки расы: у новичка
    /// раса появляется сама, но полагаться на это здесь незачем — приветствие про сервер,
    /// а не про расу.
    ///
    /// Кадром позже, чем событие: на самом player_spawn оболочка ещё не готова, и звук
    /// от неё не пойдёт. Та же причина, по которой ниже отложен и весь спавн расы.
    /// </summary>
    private void PlayPendingWelcome(WarcraftPlayer warcraftPlayer, CCSPlayerController? controller)
    {
        if (string.IsNullOrWhiteSpace(warcraftPlayer.PendingWelcomeSound)) return;

        Server.NextFrame(() =>
        {
            if (controller is not { IsValid: true }) return;

            // Ждём именно живого, и проверяем это состоянием жизни, а не здоровьем.
            // Оболочка создаётся заранее, ещё до выбора стороны: здоровье у неё уже сто,
            // а живой она не считается — проверка по здоровью пропускала наблюдателя,
            // и реплика начиналась прямо при заходе, а на выборе стороны обрывалась
            // вместе с уничтожением наблюдательской оболочки. Замер 18.08.2026.
            if (!warcraftPlayer.IsAlive) return;

            // В разминке не начинаем. Её конец перезапускает раунд, а перезапуск
            // уничтожает оболочку — звук идёт от неё и обрывается на полуслове.
            // Приветствие при этом остаётся в очереди и прозвучит на первом живом
            // спавне настоящего раунда, целиком.
            if (IsWarmup()) return;

            // Забираем только теперь: если этот спавн был наблюдательским, приветствие
            // должно остаться и дождаться настоящего.
            var sound = warcraftPlayer.PendingWelcomeSound;
            if (string.IsNullOrWhiteSpace(sound)) return;
            warcraftPlayer.PendingWelcomeSound = "";

            VisualEffects.PlaySoundTo(controller, sound, _config.WelcomeSoundVolume);

            // Отметка ставится по факту, а не при выборе реплики: иначе зашедший и сразу
            // вышедший истратил бы приветствие, ни разу его не услышав.
            warcraftPlayer.Record.LastWelcomeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _store.MarkDirty();
        });
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        var warcraftPlayer = Get(ev.Userid);
        if (warcraftPlayer is null) return HookResult.Continue;

        // Меню могло остаться открытым с того света. Живому его надо убрать: заморозка
        // осталась на прежней пешке, а вот W и S меню продолжало бы забирать себе.
        // Именно ForceForget, а не Close: восстанавливать движение новой пешке нечего.
        _menus.ForceForget(warcraftPlayer.Slot);

        // Смена команды и вход на сервер тег сбрасывают, поэтому подновляем его на спавне.
        UpdateClanTag(warcraftPlayer);

        PlayPendingWelcome(warcraftPlayer, ev.Userid);

        if (warcraftPlayer.Race is null) return HookResult.Continue;

        // Спавн отрабатываем на следующем кадре: на самом событии pawn ещё не готов.
        Server.NextFrame(() =>
        {
            if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0) return;

            warcraftPlayer.ClearTemporaryEffects();
            Effects.SetRenderAlpha(pawn, 255);
            Effects.SetGravity(pawn, 1f);

            // Облик расы надеваем здесь, до OnSpawn: раса потом растягивает уже свою модель,
            // а не ту, что игрок выбрал в инвентаре.
            var raceModel = ApplyRaceModel(warcraftPlayer);

            // Размер — сразу за обликом и ровно один раз за спавн. Единица у обычных рас
            // работает и как сброс: без него увеличенная модель Бигфута осталась бы
            // на игроке после смены расы.
            Effects.SetModelScale(pawn, warcraftPlayer.Race.BodyScale);

            // Модель этой жизни — эталон для возврата после маскировки. Берём именно ту,
            // что надели: прочитать её обратно с пешки в этом же кадре движок не обещает.
            warcraftPlayer.OriginalModel = raceModel ?? Effects.CurrentModelOf(pawn);

            warcraftPlayer.Race.OnSpawn(warcraftPlayer);
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        // Здесь стоял отказ, если пешка жертвы ещё числится живой — защита от ложной смерти
        // Оборотня. Защита оказалась вредной: событие приходит раньше, чем движок помечает
        // пешку мёртвой, и отсекались все настоящие смерти вместе с опытом за убийства.
        // Подделке сюда не попасть: FireEventToClient шлёт событие конкретным клиентам,
        // серверные обработчики его не видят.

        var victim = Get(ev.Userid);
        if (victim is not null)
        {
            // Меню при смерти намеренно оставляем открытым: мёртвому как раз и нужно
            // время посмотреть расы и способности. Размораживать труп нечего, а закроется
            // меню на возрождении.
            victim.Race?.OnDeath(victim);
            victim.ClearTemporaryEffects();

        }

        var killer = Get(ev.Attacker);
        if (killer is not null && killer.Slot != victim?.Slot && ev.Attacker is { IsValid: true })
        {
            var xp = _config.XpPerKill;
            if (ev.Headshot) xp += _config.XpHeadshotBonus;
            if (ev.Weapon.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
                ev.Weapon.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
                xp += _config.XpKnifeBonus;

            GrantXp(killer, xp, "убийство");
            killer.Race?.OnKill(killer, ev.Userid!, ev);
        }

        var assister = Get(ev.Assister);
        if (assister is not null) GrantXp(assister, _config.XpPerAssist, "помощь");

        return HookResult.Continue;
    }

    /// <summary>
    /// Выставить настройки, на которых держится мод.
    ///
    /// Вызывается на старте карты и каждого раунда, и не зря: воркшоп-карты умеют
    /// выполнять свои команды сами — внутри лежит <c>logic_auto</c>, который на загрузке
    /// шлёт их через <c>point_servercommand</c>. Происходит это после всех конфигов,
    /// поэтому переспорить карту файлом нельзя, можно только выставить своё следом.
    /// Проверено на opc_rats: она ставит себе десять минут на раунд и гасит урон
    /// от падения. Стреляет карта один раз, на загрузке, так что второго захода не будет
    /// и наше значение остаётся последним.
    /// </summary>
    private void EnforceServerRules()
    {
        // На выключенном огне по своим держится ультимейт Оборотня: урон союзников
        // проходит только по заражённым. Конфиги игровых режимов включают его обратно.
        Server.ExecuteCommand("mp_friendlyfire 0");

        // Автопрыжок — движковый и общий для всех. Здесь он потому, что стал частью
        // ощущения сервера: мод сам больше не прыгает, и карта, погасившая этот конвар,
        // отняла бы распрыжку целиком, а не подпортила бы её.
        Server.ExecuteCommand("sv_autobunnyhopping 1");

        // Разгон между прыжками не обрезается на приземлении — без этого автопрыжок
        // даёт частоту, но не даёт скорости, и весь смысл распрыжки пропадает.
        Server.ExecuteCommand("sv_enablebunnyhopping 1");

        // Урон от падения выключен намеренно и на всех картах сразу: на rats-картах
        // падение это способ передвижения, а не угроза, и разное поведение на разных
        // картах путало бы и нас, и игроков.
        Server.ExecuteCommand("sv_falldamage_scale 0");

        // Голосование за следующую карту у мода своё, во фризтайме последнего раунда.
        // Движковое надо выключить, иначе в конце матча поверх всего встаёт его панель
        // со счётом и списком карт — тот самый долгий экран, ради которого игроки уходят.
        Server.ExecuteCommand("mp_endmatch_votenextmap 0");

        // Конец матча укорачиваем: движковые 15 секунд рассчитаны на турнир, где итог
        // разглядывают. Карту к этому моменту мод уже выбрал и меняет её сам через
        // секунду после последнего раунда, так что пауза здесь ничем не занята.
        if (_config.MatchEndSeconds > 0)
        {
            var matchEnd = _config.MatchEndSeconds.ToString(CultureInfo.InvariantCulture);
            Server.ExecuteCommand($"mp_match_restart_delay {matchEnd}");
        }

        // Ноль означает «не вмешиваться»: время раунда тогда остаётся за конфигом сервера.
        if (_config.RoundTimeMinutes <= 0) return;

        // Задавать надо все три. На de-картах движок читает mp_roundtime_defuse,
        // на cs-картах mp_roundtime_hostage, и mp_roundtime на них не действует вовсе —
        // из-за этого de_rats_room шла по 2:15 при честной тройке в конфиге.
        //
        // Число печатаем с инвариантной культурой: под русской локалью вышло бы «3,5»,
        // и движок такую команду не понял бы.
        var minutes = _config.RoundTimeMinutes.ToString(CultureInfo.InvariantCulture);
        Server.ExecuteCommand($"mp_roundtime {minutes}");
        Server.ExecuteCommand($"mp_roundtime_defuse {minutes}");
        Server.ExecuteCommand($"mp_roundtime_hostage {minutes}");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        // Подстраховка: раунд мог начаться и без события окончания предыдущего.
        KillRoundTimers();

        // Подсветка смену раунда не переживает: копии моделей движок вычищает, а запись
        // без них становится пустой и мешает завести новую.
        ClearGlow();

        EnforceServerRules();

        MeasureMapBounds();

        // Последний раунд — открываем голосование за следующую карту. Подготовка под него
        // уже удлинена в конце прошлого раунда.
        if (_config.MapVoteFreezeSeconds > 0 && MaxRounds() > 0 && RoundsPlayed() + 1 >= MaxRounds())
            OpenMapVote();

        foreach (var warcraftPlayer in _players.Values)
        {
            // Отложенная смена расы вступает в силу здесь — до раздачи бонусов на спавне.
            if (warcraftPlayer.PendingRace is { } pending) ApplyRace(warcraftPlayer, pending);

            // Здесь же вступает в силу и прокачка: очки, вложенные посреди прошлого раунда,
            // становятся рабочими рангами. Порядок важен — бонусы на спавне читают уже их.
            warcraftPlayer.ApplyBoughtRanks();

            warcraftPlayer.ClearCooldowns();
            warcraftPlayer.ClearSpentThisRound();
            warcraftPlayer.ClearTemporaryEffects();
            warcraftPlayer.Race?.OnRoundStart(warcraftPlayer);
        }

        return HookResult.Continue;
    }


    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        // Эффекты прошлого раунда в следующий не переносятся.
        KillRoundTimers();
        _infectedUntil.Clear();

        var winner = (CsTeam)ev.Winner;
        foreach (var warcraftPlayer in _players.Values)
        {
            if (warcraftPlayer.Controller is not { IsValid: true } controller) continue;

            // Зрителям не начисляем: раунд они не играли.
            var team = controller.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) continue;

            // Участие получают все и всегда — это пол прогрессии для тех, кто мало убивает
            // и часто проигрывает. Выживание и победа ложатся сверху.
            var reward = _config.XpPerRound;
            var reasons = new List<string> { "раунд" };

            if (warcraftPlayer.IsAlive)
            {
                reward += _config.XpSurvived;
                reasons.Add("выжил");
            }

            if (team == winner)
            {
                reward += _config.XpRoundWin;
                reasons.Add("победа");
            }

            GrantXp(warcraftPlayer, reward, string.Join(", ", reasons));
        }

        _store.FlushIfDirty();

        // Голосование шло — значит этот раунд был последним, подводим итог и меняем карту.
        // Иначе смотрим, не последний ли раунд следующий, и удлиняем ему подготовку.
        if (_mapVote.IsOpen) FinishMapVote();
        else PrepareMapVoteIfLastRoundNext();

        return HookResult.Continue;
    }

    // ------------------------------------------------------------------
    // Урон
    // ------------------------------------------------------------------

    private HookResult OnEntityTakeDamagePre(CBaseEntity victimEntity, CTakeDamageInfo info)
    {
        if (victimEntity is not { IsValid: true } || victimEntity.DesignerName != "player") return HookResult.Continue;

        // Урон от падения гасим здесь, а не конваром. Карты выставляют sv_falldamage_scale
        // себе сами — opc_rats ставит 0.2 из logic_auto на загрузке, — и спорить с ними
        // значениями означает гонку, которую мы выигрываем не всегда. В хуке урона мод
        // последний по определению, и карта сюда не дотянется.
        if (info.BitsDamageType.HasFlag(DamageTypes_t.DMG_FALL)) return HookResult.Handled;

        var damageBefore = info.Damage;

        var victimPawn = new CCSPlayerPawn(victimEntity.Handle);
        var victimController = victimPawn.OriginalController.Value;
        var attackerController = ControllerFromEntity(info.Attacker.Value);

        // По заражённому урон от своих должен проходить. Движок такой урон гасит сам,
        // поэтому наносим его напрямую — здоровьем, минуя систему урона.
        if (victimController is not null && attackerController is not null
            && attackerController.Slot != victimController.Slot
            && attackerController.Team == victimController.Team
            && IsInfected(victimController.Slot))
        {
            var friendlyDamage = (int)info.Damage;
            if (friendlyDamage > 0)
            {
                // Наносим на следующем кадре: смертельный удар изнутри обработки урона
                // приводит к вызову смерти внутри неё же и роняет сервер.
                Server.NextFrame(() =>
                {
                    // С учётом брони: это подмена обычного выстрела, а не магический урон.
                    if (victimPawn.IsValid) Effects.ApplyDamageWithArmor(victimPawn, friendlyDamage);
                });
            }
        }

        // Сначала атакующий (крит, доп. урон, вампиризм), затем защищающийся (блок, уклонение).
        if (attackerController is not null && attackerController.Slot != victimController?.Slot)
        {
            var attacker = Get(attackerController);
            if (attacker?.Race is not null && victimController is not null)
                attacker.Race.OnDealDamage(attacker, victimController, info);
        }

        var victim = Get(victimController);

        // Запоминаем обидчика: ложной смерти Оборотня нужно, кого показать «убийцей».
        if (victim is not null && attackerController is not null && attackerController.Slot != victim.Slot)
        {
            victim.LastAttackerSlot = attackerController.Slot;
            victim.LastDamagedAt = Server.CurrentTime;
        }

        // Разовый иммунитет после нашего же подброса — до всех расовых обработчиков.
        // Засчитываем его не только по флагу урона от падения, но и по «урону от самого
        // себя»: приземление после подброса приходило с типом, флагу не соответствующим.
        //
        // Постоянного иммунитета у рас больше нет: Кузнечик вместо него замедляет падение,
        // и урон отсекается сам собой — скорость удара о землю не дотягивает до порога.
        var selfInflicted = attackerController is null || attackerController.Slot == victimController?.Slot;
        var fallDamage = info.BitsDamageType.HasFlag(DamageTypes_t.DMG_FALL);

        if (victim is not null && victim.FallImmuneAfterLaunch && (fallDamage || selfInflicted))
        {
            info.Damage = 0f;
            return HookResult.Changed;
        }

        if (victim?.Race is not null)
        {
            victim.Race.OnTakeDamage(victim, attackerController, info);

            // Щит от смерти: один смертельный удар не проходит. Формулировку задаёт раса,
            // потому что называется он у каждой по-своему.
            if (victim.HasDeathWard && info.Damage >= victimPawn.Health)
            {
                victim.HasDeathWard = false;
                info.Damage = 0f;

                var message = victim.DeathWardMessage ?? "Смертельный удар не прошёл!";
                victimController?.PrintToChat($"{Prefix} {ChatColors.LightBlue}{message}");

                // Лечение переносим на следующий кадр: менять здоровье посреди обработки
                // урона — тот же случай, что и добивание, лучше туда не лезть.
                var heal = victim.DeathWardHeal;
                victim.DeathWardHeal = 0;
                if (heal > 0)
                    Server.NextFrame(() =>
                    {
                        if (victim.Pawn is { } pawn && pawn.Health > 0) Effects.Heal(pawn, heal);
                    });
            }
        }

        return Math.Abs(info.Damage - damageBefore) > 0.001f ? HookResult.Changed : HookResult.Continue;
    }

    private static CCSPlayerController? ControllerFromEntity(CBaseEntity? entity)
    {
        if (entity is not { IsValid: true } || entity.DesignerName != "player") return null;

        var pawn = new CCSPlayerPawn(entity.Handle);
        return pawn.OriginalController.Value;
    }

    // ------------------------------------------------------------------
    // Опыт и уровни
    // ------------------------------------------------------------------

    public void GrantXp(WarcraftPlayer warcraftPlayer, int amount, string reason)
    {
        if (amount <= 0) return;

        // Общий опыт капает всегда — даже без расы и даже с расы на потолке.
        // Это и есть мерило времени, по которому открываются новые расы.
        GrantAccountXp(warcraftPlayer, amount);

        if (warcraftPlayer.Race is null) return;

        var progress = warcraftPlayer.Progress;
        var controller = warcraftPlayer.Controller;

        if (progress.Level >= XpTable.MaxLevel)
        {
            QueueXpNotice(warcraftPlayer, amount, reason, accountOnly: true);
            return;
        }

        // Каждая освоенная раса ускоряет следующие: при десятках рас одинаковая цена
        // за каждую превратила бы знакомство с новой в многодневную повинность.
        amount = (int)Math.Round(amount * XpTable.RaceXpMultiplier(warcraftPlayer.MaxedRaces));

        progress.Xp += amount;
        var leveledUp = false;

        while (progress.Level < XpTable.MaxLevel && progress.Xp >= XpTable.XpToNextLevel(progress.Level))
        {
            progress.Xp -= XpTable.XpToNextLevel(progress.Level);
            progress.Level++;
            leveledUp = true;
        }

        if (progress.Level >= XpTable.MaxLevel) progress.Xp = 0;

        _store.MarkDirty();

        if (!leveledUp)
        {
            QueueXpNotice(warcraftPlayer, amount, reason, accountOnly: false);
            return;
        }

        // Новый уровень пишем на диск немедленно, не дожидаясь таймера. Потерять минуту
        // опыта игрок переживёт, а потерять взятый уровень — то, что он запомнил, — уже нет.
        _store.FlushIfDirty();

        // Сообщение личное. Объявление на весь сервер отсюда убрано: уровни берут часто,
        // и в бою чужие достижения — это строки, за которыми не видно своих.
        //
        // Очко вкладывается само и в той же строке называется вслух. Прежний текст звал
        // в !skills — за первую неделю по этому зову не пришёл почти никто, и уровень
        // оставался числом без последствий.
        var upgrades = AutoSpendSkillPoints(warcraftPlayer);

        if (upgrades.Count == 0)
        {
            controller?.PrintToChat($"{Prefix} {ChatColors.Gold}Вы повысили уровень: {progress.Level}!{ChatColors.Default} Распределите очки — {ChatColors.Green}!skills");
            return;
        }

        // Про отсрочку говорим ровно тогда, когда она есть: ранг, взятый посреди раунда,
        // вступает в силу со следующего, и молча выданное «2/4» читалось бы как поломка.
        var delay = upgrades.Any(u => warcraftPlayer.IsUpgradePending(u.Index))
            ? $" — со {ChatColors.Gold}следующего раунда{ChatColors.Default}"
            : "";

        controller?.PrintToChat(
            $"{Prefix} {ChatColors.Gold}Уровень {progress.Level}!{ChatColors.Default} {DescribeUpgrades(upgrades)}{delay}");
    }

    // ------------------------------------------------------------------
    // Голосование за следующую карту
    // ------------------------------------------------------------------

    private readonly MapVote _mapVote = new();
    public MapVote MapVote => _mapVote;

    /// <summary>Обычная длительность подготовки — вернуть после голосования.</summary>
    private int? _freezeTimeBeforeVote;

    /// <summary>
    /// Сколько раундов сыграно в матче. Нужно, чтобы поймать последний: голосование
    /// должно открыться именно в нём, а подготовку под него удлинить ещё раньше.
    /// </summary>
    private static int RoundsPlayed() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules?.TotalRoundsPlayed ?? 0;

    private static int MaxRounds() => ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 0;

    /// <summary>
    /// Удлинить подготовку следующего раунда, если он последний. Делать это надо здесь,
    /// в конце предыдущего: длительность заморозки движок вычисляет в момент старта
    /// раунда, и правка во время самой заморозки уже ничего не изменит.
    /// </summary>
    private void PrepareMapVoteIfLastRoundNext()
    {
        if (_config.MapVoteFreezeSeconds <= 0 || MapPool.All.Count < 2) return;

        var maxRounds = MaxRounds();
        if (maxRounds <= 0) return;

        // Следующий раунд последний.
        if (RoundsPlayed() + 1 < maxRounds) return;
        if (ConVar.Find("mp_freezetime") is not { } freezeTime) return;

        _freezeTimeBeforeVote ??= freezeTime.GetPrimitiveValue<int>();
        freezeTime.SetValue((int)_config.MapVoteFreezeSeconds);
    }

    /// <summary>Открыть голосование всем, кто на сервере.</summary>
    private void OpenMapVote()
    {
        _mapVote.Open(Server.MapName);
        if (!_mapVote.IsOpen) return;

        Server.PrintToChatAll($"{Prefix} {ChatColors.Gold}Последний раунд.{ChatColors.Default} Выберите следующую карту — меню открыто.");

        foreach (var warcraftPlayer in _players.Values)
            WarcraftMenus.OpenMapVoteMenu(this, warcraftPlayer);
    }

    /// <summary>Принять голос и закрыть меню — переспрашивать одно и то же незачем.</summary>
    public void CastMapVote(WarcraftPlayer warcraftPlayer, int number)
    {
        if (!_mapVote.TryVote(warcraftPlayer.Slot, number, out var chosen) || chosen is null) return;

        warcraftPlayer.Controller?.PrintToChat($"{Prefix} Ваш голос: {ChatColors.Green}{chosen.Name}");
    }

    /// <summary>Подвести итог и сменить карту. Вызывается в конце последнего раунда.</summary>
    private void FinishMapVote()
    {
        if (!_mapVote.IsOpen) return;

        var votes = _mapVote.VoteCount;
        var (winner, _) = _mapVote.Finish();

        // Подготовку возвращаем к обычной: удлинённая нужна была только под голосование.
        if (_freezeTimeBeforeVote is { } saved)
        {
            ConVar.Find("mp_freezetime")?.SetValue(saved);
            _freezeTimeBeforeVote = null;
        }

        Server.PrintToChatAll(votes == 0
            ? $"{Prefix} Никто не голосовал — карта выбрана случайно: {ChatColors.Gold}{winner.Name}"
            : $"{Prefix} Победила {ChatColors.Gold}{winner.Name}{ChatColors.Default} — голосов: {ChatColors.Gold}{votes}");

        ChangeMap(winner);
    }

    /// <summary>Как часто проверять бездействие. Чаще незачем: речь о минутах.</summary>
    private const float AfkCheckSeconds = 5f;

    /// <summary>Насколько надо сдвинуться или повернуться, чтобы это считалось действием.</summary>
    private const float AfkMoveThreshold = 8f;
    private const float AfkTurnThreshold = 2f;

    /// <summary>
    /// Перенести бездействующих в зрители. Место в команде на публичном сервере —
    /// ресурс: один стоящий столбом отнимает его у того, кто хочет играть.
    /// </summary>
    private void CheckAfk()
    {
        if (_config.AfkSeconds <= 0) return;

        var now = Server.CurrentTime;

        foreach (var warcraftPlayer in _players.Values.ToList())
        {
            if (warcraftPlayer.Controller is not { IsValid: true, IsBot: false } controller) continue;

            var team = controller.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) continue;

            // Мёртвый двигаться не может — время бездействия ему не пишем.
            if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0 || pawn.AbsOrigin is not { } origin)
            {
                warcraftPlayer.LastActivityAt = now;
                continue;
            }

            var position = new Vector3(origin.X, origin.Y, origin.Z);
            var angles = pawn.EyeAngles is { } eye ? new Vector3(eye.X, eye.Y, eye.Z) : Vector3.Zero;

            var moved = Vector3.Distance(position, warcraftPlayer.LastSeenOrigin) > AfkMoveThreshold;
            var turned = Vector3.Distance(angles, warcraftPlayer.LastSeenAngles) > AfkTurnThreshold;

            warcraftPlayer.LastSeenOrigin = position;
            warcraftPlayer.LastSeenAngles = angles;

            if (moved || turned)
            {
                warcraftPlayer.LastActivityAt = now;
                continue;
            }

            if (now - warcraftPlayer.LastActivityAt < _config.AfkSeconds) continue;

            // Сбрасываем счётчик до переноса: иначе на следующей проверке он всё ещё
            // превышен, и зрителя дёрнет вторым сообщением.
            warcraftPlayer.LastActivityAt = now;

            var minutes = Math.Max(1, (int)Math.Round(_config.AfkSeconds / 60f));
            controller.ChangeTeam(CsTeam.Spectator);

            Server.PrintToChatAll($"{Prefix} {ChatColors.Gold}{controller.PlayerName}{ChatColors.Default} перемещён в зрители — {minutes} мин. без движения.");
            controller.PrintToChat($"{Prefix} Вернуться в игру можно через меню выбора команды.");
        }
    }

    /// <summary>Как часто проверять, не улетел ли кто за пределы карты.</summary>
    private const float StrayCheckSeconds = 0.5f;

    /// <summary>
    /// Сколько секунд подряд надо пробыть ниже дна карты и без опоры под ногами,
    /// чтобы мод счёл это вылетом. Падение в яму кончается землёй быстрее, полёт
    /// в пустоте под миром не кончается вовсе.
    /// </summary>
    private const float StrayFallSeconds = 3f;

    /// <summary>На сколько приподнять возвращаемого над точкой возрождения, чтобы не вжать в пол.</summary>
    private const float StrayArrivalLift = 10f;

    /// <summary>Дно карты: ниже этой высоты играть негде. Замеряется на старте раунда.</summary>
    private float _mapFloorZ = float.MinValue;

    /// <summary>Рамка по горизонтали, если она задана для этой карты. Замеряется там же.</summary>
    private bool _fenceOn;
    private float _fenceMinX, _fenceMaxX, _fenceMinY, _fenceMaxY;

    /// <summary>
    /// Края карты считаем от точек возрождения: они стоят там, где игра начинается.
    /// Дно — ниже самой низкой из них, рамка — вокруг всех сразу. Числа под каждую
    /// карту так не нужны: нужен только запас, а его берёт настройка.
    /// </summary>
    private void MeasureMapBounds()
    {
        _mapFloorZ = float.MinValue;
        _fenceOn = false;

        var points = Effects.SpawnPoints();
        if (points.Count == 0) return;

        if (_config.OutOfBoundsDropUnits > 0f)
            _mapFloorZ = points.Min(point => point.Z) - _config.OutOfBoundsDropUnits;

        if (FenceMarginForMap() is not { } margin) return;

        _fenceMinX = points.Min(point => point.X) - margin;
        _fenceMaxX = points.Max(point => point.X) + margin;
        _fenceMinY = points.Min(point => point.Y) - margin;
        _fenceMaxY = points.Max(point => point.Y) + margin;
        _fenceOn = true;

        Logger.LogInformation("Рамка карты {Map}: X {MinX:0}..{MaxX:0}, Y {MinY:0}..{MaxY:0}, дно Z {Floor:0}",
            Server.MapName, _fenceMinX, _fenceMaxX, _fenceMinY, _fenceMaxY, _mapFloorZ);
    }

    /// <summary>
    /// Запас за коробкой спавнов для текущей карты. Имя сверяем без учёта регистра:
    /// в конфиг его переписывают руками, и «awp_lego_2» с большой буквы не должно
    /// молча оставлять карту без забора.
    /// </summary>
    private float? FenceMarginForMap()
    {
        foreach (var (map, margin) in _config.OutOfBoundsFence)
        {
            if (!string.Equals(map, Server.MapName, StringComparison.OrdinalIgnoreCase)) continue;

            return margin > 0f ? margin : null;
        }

        return null;
    }

    /// <summary>
    /// Возврат улетевших за карту. Способности мода подбрасывают высоко, и на картах
    /// вроде awp_lego этим перелетают стены вокруг арены: игрок уходит под мир, где
    /// его не убивает даже падение (<c>sv_falldamage_scale 0</c>) и раунд для него
    /// заканчивается ожиданием.
    ///
    /// Двух условий сразу — ниже дна и без земли под ногами — хватает, чтобы не трогать
    /// того, кто честно падает в низину: приземление снимает счётчик.
    /// </summary>
    private void ReturnStrayPlayers()
    {
        if (_mapFloorZ <= float.MinValue && !_fenceOn) return;

        var now = Server.CurrentTime;

        foreach (var warcraftPlayer in _players.Values)
        {
            if (warcraftPlayer.Controller is not { IsValid: true } controller) continue;
            if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0) continue;
            if (Effects.Origin(pawn) is not { } position) continue;

            // За рамкой возвращаем сразу и без поблажек: туда не проваливаются,
            // туда приходят ногами и остаются играть — этого и нельзя допускать.
            if (OutsideFence(position))
            {
                warcraftPlayer.LastInBoundsAt = now;
                ReturnToSpawn(warcraftPlayer, controller, position, "ушёл за рамку карты");
                continue;
            }

            if (position.Z >= _mapFloorZ || Effects.IsOnGround(pawn))
            {
                warcraftPlayer.LastInBoundsAt = now;
                continue;
            }

            if (now - warcraftPlayer.LastInBoundsAt < StrayFallSeconds) continue;

            warcraftPlayer.LastInBoundsAt = now;
            ReturnToSpawn(warcraftPlayer, controller, position, $"провалился ниже дна {_mapFloorZ:0}");
        }
    }

    /// <summary>Вышел ли игрок за рамку карты. Без рамки — не вышел никогда.</summary>
    private bool OutsideFence(Vector3 position) =>
        _fenceOn && (position.X < _fenceMinX || position.X > _fenceMaxX ||
                     position.Y < _fenceMinY || position.Y > _fenceMaxY);

    /// <summary>
    /// Вернуть игрока на свободную точку возрождения его команды.
    ///
    /// Переносим следующим кадром и чуть выше точки. Телепорт в том же кадре оставляет
    /// клиента предсказывать движение от старой точки: модель разъезжается с настоящим
    /// положением, а камера от первого лица уходит внутрь текстур. Тем же порядком
    /// переносят Повелитель времени и Оборотень.
    /// </summary>
    private void ReturnToSpawn(WarcraftPlayer player, CCSPlayerController controller, Vector3 from, string reason)
    {
        if (Effects.FreeSpawnPoint(controller.Team) is not { } spawn) return;

        Server.NextFrame(() =>
        {
            if (player.Pawn is not { } arrived || arrived.Health <= 0) return;

            Effects.TeleportUpright(arrived, spawn with { Z = spawn.Z + StrayArrivalLift });
            Effects.ResetStance(arrived);
        });

        CenterText.Print(controller, "ВАС ВЕРНУЛО НА КАРТУ");

        // Пишем через Logger, а не в стандартный вывод: тот на диск не попадает, а
        // log-cssharp читается и с боевого сервера. Место вылета нужно, чтобы правило
        // можно было поправить по настоящим числам, а не по ощущению.
        Logger.LogInformation("{Player} {Reason}: X={X:0} Y={Y:0} Z={Z:0} — возвращён на спавн.",
            controller.PlayerName, reason, from.X, from.Y, from.Z);
    }

    /// <summary>Шаг накопления времени. Мелкими кусками — чтобы падение сервера съело секунды, а не сеанс.</summary>
    private const float PlaytimeTickSeconds = 15f;

    /// <summary>
    /// Копим время в игре. Считаем только тех, кто в командах: зритель опыт не зарабатывает,
    /// и его время испортило бы главное число проверки баланса — опыт в час.
    /// </summary>
    private void AccumulatePlaytime()
    {
        if (_players.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var warcraftPlayer in _players.Values)
        {
            if (warcraftPlayer.Controller is not { IsValid: true } controller) continue;

            var team = controller.Team;
            if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) continue;

            var record = warcraftPlayer.Record;
            record.PlayedSeconds += (long)PlaytimeTickSeconds;
            record.LastSeenUnix = now;

            if (warcraftPlayer.Race is { } race)
                record.ProgressFor(race.Id, race.Abilities.Count).PlayedSeconds += (long)PlaytimeTickSeconds;
        }

        _store.MarkDirty();
    }

    private void QueueXpNotice(WarcraftPlayer warcraftPlayer, int amount, string reason, bool accountOnly)
    {
        if (!_xpNotices.TryGetValue(warcraftPlayer.Slot, out var notice))
        {
            notice = new XpNotice();
            _xpNotices[warcraftPlayer.Slot] = notice;
        }

        notice.Amount += amount;
        notice.AccountOnly = accountOnly;

        // Одно и то же основание дважды в строке не нужно: два убийства подряд — это
        // «+80 опыта (убийство)», а не «убийство, убийство».
        if (!notice.Reasons.Contains(reason)) notice.Reasons.Add(reason);
    }

    /// <summary>Показать накопленное одной строкой и очистить.</summary>
    private void FlushXpNotices()
    {
        if (_xpNotices.Count == 0) return;

        foreach (var (slot, notice) in _xpNotices)
        {
            // «Общего» пишем, когда раса на потолке и опыт ей девать некуда.
            var label = notice.AccountOnly ? "общего опыта" : "опыта";
            CenterText.Print(Utilities.GetPlayerFromSlot(slot), $"+{notice.Amount} {label} ({string.Join(", ", notice.Reasons)})");
        }

        _xpNotices.Clear();
    }

    /// <summary>
    /// Начислить общий опыт и объявить, если он открыл новые расы. Открытие — главное
    /// событие прогрессии, и молча его пропускать нельзя: игрок просто не заметит,
    /// что ему стало доступно.
    /// </summary>
    /// <summary>Шаг вех общего уровня, которые объявляются всему серверу.</summary>
    private const int MilestoneStep = 10;

    /// <summary>
    /// Выше этого уровня вехи молчат: 145 — порог последней волны рас, дальше открывать
    /// нечего, и объявление превращается в счётчик чужих часов. Сам игрок свои уровни
    /// видит всегда.
    /// </summary>
    private const int MilestoneCeiling = 145;

    /// <summary>
    /// Круглая веха, пройденная между двумя общими уровнями, или null, если ни одной.
    /// Берём наибольшую пройденную, а не сам новый уровень: крупная награда может
    /// перепрыгнуть десятку, и веха потерялась бы молча.
    /// </summary>
    private static int? MilestoneReached(int before, int after)
    {
        var highest = Math.Min(after, MilestoneCeiling) / MilestoneStep * MilestoneStep;

        return highest >= MilestoneStep && highest > before ? highest : null;
    }

    private void GrantAccountXp(WarcraftPlayer warcraftPlayer, int amount)
    {
        var before = warcraftPlayer.TotalLevel;
        warcraftPlayer.Record.AccountXp += amount;
        _store.MarkDirty();

        var after = warcraftPlayer.TotalLevel;
        if (after == before) return;

        // Общий уровень открывает расы — потеря такого события заметнее всего.
        _store.FlushIfDirty();

        var controller = warcraftPlayer.Controller;
        controller?.PrintToChat($"{Prefix} Общий уровень: {ChatColors.Gold}{after}");

        // Круглые десятки объявляем всем. Каждый уровень объявлять нельзя — их берут
        // часто, и чужие строки в бою заслоняют свои; но совсем без объявлений новичок
        // не видит, что тут вообще есть куда расти. Десятка — редкая веха и понятная.
        if (MilestoneReached(before, after) is { } milestone && controller is not null)
        {
            Server.PrintToChatAll(
                $"{Prefix} {ChatColors.Green}{controller.PlayerName}{ChatColors.Default} взял общий уровень " +
                $"{ChatColors.Gold}{milestone}");
        }

        // Донатные сюда не попадают: уровнем они не открываются никогда.
        var opened = _races.All
            .Where(race => !race.DonorOnly && race.UnlockTotalLevel > before && race.UnlockTotalLevel <= after)
            .ToList();

        if (opened.Count == 0) return;

        controller?.PrintToChat($"{Prefix} {ChatColors.Gold}Открыто:{ChatColors.Default} {string.Join(", ", opened.Select(race => race.Name))} — {ChatColors.Green}!races");
    }

    private void NotifyUnspentPoints(WarcraftPlayer warcraftPlayer)
    {
        var unspent = warcraftPlayer.UnspentSkillPoints;
        if (unspent > 0)
            warcraftPlayer.Controller?.PrintToChat($"{Prefix} У вас {ChatColors.Gold}{unspent}{ChatColors.Default} нераспределённых очков — {ChatColors.Green}!skills");
    }

    /// <summary>
    /// Дописать расу к сообщению игрока в чате.
    ///
    /// Сначала расу пробовали показывать клановым тегом в табло — CS2 его не рисует,
    /// сколько ни выставляй. Чат надёжнее: мы не надеемся на отрисовку, а перехватываем
    /// сообщение и печатаем своё.
    ///
    /// Не вмешиваемся в двух случаях. Первый — раса не выбрана: добавлять нечего, и пусть
    /// игра печатает сама. Второй — Оборотень в чужом обличье: он носит чужой ник, и
    /// честная приписка «Оборотень» выдала бы его первым же словом в чат.
    /// </summary>
    /// <summary>Имена команд мода без приставки css_ — по ним узнаём команду, набранную не в той раскладке.</summary>
    private readonly HashSet<string> _chatCommands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Собрать имена своих команд из их же объявлений. Списком руками их держать нельзя:
    /// команды добавляют и переименовывают, а забытое имя перестало бы работать в русской
    /// раскладке молча — то есть ровно так, как эту поломку и не заметить.
    /// </summary>
    private void CollectChatCommands()
    {
        foreach (var method in GetType().GetMethods())
        {
            foreach (var attribute in method.GetCustomAttributes<ConsoleCommandAttribute>())
            {
                if (attribute.Command.StartsWith("css_")) _chatCommands.Add(attribute.Command[4..]);
            }
        }

        // Цифровые команды меню заводятся в коде, атрибута у них нет.
        for (var number = 1; number <= 9; number++) _chatCommands.Add($"wc{number}");
    }

    /// <summary>
    /// Команда, набранная в русской раскладке. «!цс 1» — это те же клавиши, что «!wc 1»,
    /// и отвергать её значит наказывать за язык ввода, о котором в бою никто не помнит.
    ///
    /// Переводится только само имя команды. Аргументы остаются как есть: там бывают ники,
    /// и русский ник, переведённый по клавишам, превратился бы в мусор.
    /// </summary>
    private bool TryLayoutCommand(CCSPlayerController controller, string message)
    {
        var body = message[1..];
        if (body.Length == 0) return false;

        var space = body.IndexOf(' ');
        var name = space < 0 ? body : body[..space];
        var rest = space < 0 ? "" : body[space..];

        if (!KeyboardLayout.HasCyrillic(name)) return false;

        var latin = KeyboardLayout.ToLatin(name);
        if (!_chatCommands.Contains(latin)) return false;

        controller.ExecuteClientCommandFromServer($"css_{latin}{rest}");
        return true;
    }

    private HookResult OnPlayerSay(CCSPlayerController? controller, CommandInfo info, bool teamOnly)
    {
        if (controller is not { IsValid: true, IsBot: false }) return HookResult.Continue;

        var message = info.GetArg(1);
        if (string.IsNullOrWhiteSpace(message)) return HookResult.Continue;

        // Команды мода и любые другие оставляем движку и CounterStrikeSharp: печатать их
        // как реплики нельзя, у нас они вдобавок скрыты из чата намеренно.
        if (message.StartsWith('!') || message.StartsWith('/') || message.StartsWith('.') || message.StartsWith('@'))
            return TryLayoutCommand(controller, message) ? HookResult.Handled : HookResult.Continue;

        if (Get(controller) is not { } warcraftPlayer) return HookResult.Continue;
        if (warcraftPlayer.OriginalName is not null) return HookResult.Continue;
        if (warcraftPlayer.Race is not { } race) return HookResult.Continue;

        // Цвета как в обычном чате CS2: ник по команде, текст белым. Раса стоит перед ником
        // золотом — она подпись игрока, а не служебная пометка, и сереть ей не за что.
        // Серым остаётся только служебное: метка смерти и пометка «команда».
        var nameColor = controller.Team == CsTeam.CounterTerrorist ? ChatColors.Blue : ChatColors.Gold;

        var dead = controller.PawnIsAlive ? "" : $"{ChatColors.Grey}☠ ";
        var scope = teamOnly ? $"{ChatColors.Grey}(команда) " : "";
        var line = $" {dead}{scope}{ChatColors.Gold}[{race.Name}] {nameColor}{controller.PlayerName}{ChatColors.Default}: {message}";

        if (teamOnly)
        {
            foreach (var teammate in Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: false } && player.TeamNum == controller.TeamNum))
                teammate.PrintToChat(line);
        }
        else
        {
            Server.PrintToChatAll(line);
        }

        return HookResult.Handled;
    }

    // ------------------------------------------------------------------
    // Постоянные эффекты (скорость, невидимость)
    // ------------------------------------------------------------------

    /// <summary>Действующая подсветка: пара копий, кому её видно и до какого времени.</summary>
    private sealed class GlowMark
    {
        public required CDynamicProp Relay { get; init; }
        public required CDynamicProp Glow { get; init; }
        public required HashSet<int> Viewers { get; init; }
        public required float Until { get; set; }
    }

    /// <summary>Действующие подсветки. Ключ — слот подсвеченного.</summary>
    private readonly Dictionary<int, GlowMark> _glowMarks = new();

    /// <summary>
    /// Подсветить игрока контуром сквозь стены — только названным зрителям и только
    /// на заданное время. Повторный вызов по той же цели продлевает срок.
    ///
    /// Свечение прямо на пешке игрока движок игнорирует: проверено 17.08.2026 вживую,
    /// <c>pawn.Glow</c> пишется целиком, включая <c>GlowTeam</c>, и не рисуется ничего.
    /// А предметы наследуют <c>CBaseModelEntity</c>, где свечение и живёт, поэтому
    /// подсветка вешается на копию модели игрока, прицепленную к нему самому.
    /// </summary>
    public void HighlightPlayer(CCSPlayerController target, IEnumerable<int> viewerSlots, float seconds)
    {
        if (target is not { IsValid: true }) return;
        if (target.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Health <= 0) return;

        var until = Server.CurrentTime + seconds;

        if (_glowMarks.TryGetValue(target.Slot, out var existing)
            && existing.Relay is { IsValid: true } && existing.Glow is { IsValid: true })
        {
            existing.Until = MathF.Max(existing.Until, until);
            return;
        }

        RemoveGlow(target.Slot);

        if (Effects.CurrentModelOf(pawn) is not { Length: > 0 } model) return;
        if (SpawnGlowDouble(pawn, model) is not { } pair) return;

        _glowMarks[target.Slot] = new GlowMark
        {
            Relay = pair.Relay,
            Glow = pair.Glow,
            Viewers = viewerSlots.ToHashSet(),
            Until = until,
        };
    }

    /// <summary>
    /// Обслуживание подсветки: снимает истёкшие, погибших и развалившиеся копии.
    ///
    /// Проверять надо не только хозяина, но и сами предметы. Смена раунда вычищает
    /// созданные модом сущности, а запись о них жила дальше — и подсветка молча
    /// пропадала у того, кто раунд пережил, потому что «копия уже есть».
    /// </summary>
    private void MaintainGlow()
    {
        if (_glowMarks.Count == 0) return;

        foreach (var (slot, mark) in _glowMarks.ToList())
        {
            if (mark.Until <= Server.CurrentTime
                || mark.Relay is not { IsValid: true } || mark.Glow is not { IsValid: true })
            {
                RemoveGlow(slot);
                continue;
            }

            var owner = Utilities.GetPlayerFromSlot(slot);
            if (owner is not { IsValid: true }
                || owner.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Health <= 0)
                RemoveGlow(slot);
        }
    }

    /// <summary>Снять всю подсветку разом. Зовётся на старте раунда.</summary>
    private void ClearGlow()
    {
        foreach (var slot in _glowMarks.Keys.ToList()) RemoveGlow(slot);
    }

    private void RemoveGlow(int slot)
    {
        if (!_glowMarks.Remove(slot, out var mark)) return;

        VisualEffects.RemoveEntity(mark.Glow);
        VisualEffects.RemoveEntity(mark.Relay);
    }

    /// <summary>
    /// Решает по каждой подсветке, уходит ли она этому зрителю.
    ///
    /// Прячем в двух случаях: зритель не в списке и — главное — цель ему и так видна
    /// напрямую. Подсветка нужна ровно тогда, когда врага не видно; поверх видимого
    /// силуэта она только мешает целиться.
    ///
    /// Видимость спрашиваем у родной системы обнаружения: трассировки луча плагинам
    /// не выдают, а она считается по настоящей прямой видимости. Отсюда требование —
    /// пометку обнаружения самим не выставлять, иначе ответом будет наша же запись.
    ///
    /// Известная цена: система обнаружения и загорается, и гаснет с запозданием около
    /// двух секунд, поэтому контур опаздывает в обе стороны. Убрать это можно, только
    /// погасив послесвечение пометки, а на нём держится привычное послесвечение точки
    /// на радаре — пробовали и отказались (17.08.2026).
    /// </summary>
    private void FilterGlow(CCheckTransmitInfo info, CCSPlayerController viewer)
    {
        foreach (var (slot, mark) in _glowMarks)
        {
            var hide = !mark.Viewers.Contains(viewer.Slot);

            if (!hide && Utilities.GetPlayerFromSlot(slot) is { IsValid: true } owner
                      && owner.PlayerPawn.Value is { IsValid: true } ownerPawn)
            {
                hide = Effects.IsSpottedBy(ownerPawn, viewer.Slot);
            }

            if (!hide) continue;

            if (mark.Relay is { IsValid: true }) info.TransmitEntities.Remove(mark.Relay);
            if (mark.Glow is { IsValid: true }) info.TransmitEntities.Remove(mark.Glow);
        }
    }

    /// <summary>Погасить тело копии: наружу должен идти только контур.</summary>
    private static void HideProbeBody(CDynamicProp prop)
    {
        prop.Render = System.Drawing.Color.FromArgb(0, 255, 255, 255);
        Utilities.SetStateChanged(prop, "CBaseModelEntity", "m_clrRender");
    }

    /// <summary>
    /// Создаёт пару копий и цепляет их к игроку. Пара, а не один предмет: одиночная
    /// копия тянется за игроком рывками, промежуточная сглаживает.
    /// </summary>
    private static (CDynamicProp Relay, CDynamicProp Glow)? SpawnGlowDouble(CCSPlayerPawn pawn, string model)
    {
        try
        {
            var relay = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
            if (relay is not { IsValid: true }) return null;

            relay.SetModel(model);
            relay.DispatchSpawn();
            relay.Collision.SolidType = SolidType_t.SOLID_NONE;
            relay.AcceptInput("FollowEntity", pawn, pawn, "!activator");

            var glowProp = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
            if (glowProp is not { IsValid: true })
            {
                VisualEffects.RemoveEntity(relay);
                return null;
            }

            glowProp.SetModel(model);
            glowProp.DispatchSpawn();
            glowProp.Collision.SolidType = SolidType_t.SOLID_NONE;
            glowProp.AcceptInput("FollowEntity", relay, relay, "!activator");

            HideProbeBody(relay);
            HideProbeBody(glowProp);

            var glow = glowProp.Glow;
            glow.Glowing = true;
            glow.GlowColorOverride = System.Drawing.Color.Red;
            glow.GlowType = 3;
            glow.GlowTeam = -1;
            glow.GlowRange = 5000;
            glow.GlowRangeMin = 0;

            Utilities.SetStateChanged(glowProp, "CBaseModelEntity", "m_Glow");

            return (relay, glowProp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Подсветка не создалась: {ex.Message}");
            return null;
        }
    }

    private void ApplyPersistentEffects()
    {
        foreach (var warcraftPlayer in _players.Values)
        {
            if (warcraftPlayer.Pawn is not { } pawn || pawn.Health <= 0) continue;

            // Движок сбрасывает модификатор скорости, поэтому переприменяем его постоянно.
            pawn.VelocityModifier = warcraftPlayer.SpeedMultiplier;

            // Замедленное падение: подрезаем скорость снижения. Делать это надо постоянно —
            // гравитация разгоняет заново после каждого вмешательства, поэтому одного раза
            // при прыжке не хватит.
            if (warcraftPlayer.MaxFallSpeed > 0f && pawn.AbsVelocity.Z < -warcraftPlayer.MaxFallSpeed)
                pawn.AbsVelocity.Z = -warcraftPlayer.MaxFallSpeed;

            if (!Effects.IsOnGround(pawn))
            {
                // Строкой выше потолок скорости уже выставлен, но в воздухе движок его
                // не применяет — там горизонтальную скорость считают sv_airaccelerate
                // и воздушный предел. Разгонам это не мешает (набранное на земле едет
                // с игроком), а замедления без этого пропадали целиком: прыгающая цель
                // уходила от Оцепенения, Ловушки охотника и Провокации.
                Effects.EnforceAirSlow(pawn, warcraftPlayer.SpeedMultiplier);
                continue;
            }

            // Приземлились — запас прыжков восстановлен.
            if (warcraftPlayer.ExtraJumpsUsed > 0) warcraftPlayer.ExtraJumpsUsed = 0;

            // И разовый иммунитет от подброса снят. Небольшая задержка нужна потому,
            // что в момент самого подброса игрок ещё стоит на земле.
            if (warcraftPlayer.FallImmuneAfterLaunch && Server.CurrentTime - warcraftPlayer.LaunchedAt > 0.3f)
                warcraftPlayer.FallImmuneAfterLaunch = false;

            // Прозрачность, наоборот, держится сама — трогаем движок только при изменении.
            if (warcraftPlayer.RenderAlpha != warcraftPlayer.AppliedRenderAlpha)
            {
                Effects.SetRenderAlpha(pawn, warcraftPlayer.RenderAlpha);
                warcraftPlayer.AppliedRenderAlpha = warcraftPlayer.RenderAlpha;
            }
        }
    }

    // ------------------------------------------------------------------
    // Действия, доступные меню и командам
    // ------------------------------------------------------------------

    /// <summary>
    /// Открыта ли раса игроку. Стартовые доступны сразу, остальные — по общему уровню,
    /// то есть по сумме уровней всех рас: качая одну, игрок приближает следующую.
    /// Раса, которой уже играют, остаётся доступной, даже если порог поднимут позже.
    /// Донатные живут по своим правилам: только личная выдача, прокачка их не открывает.
    /// </summary>
    public static bool IsRaceUnlocked(WarcraftPlayer warcraftPlayer, Race race)
    {
        // Личная выдача открывает любую расу, а не только донатную. Раньше её смотрели
        // лишь у донатных, и `css_wcgrant grasshopper` записывал доступ, которым нельзя
        // было воспользоваться: команда отвечала «выдана», а раса в меню оставалась
        // закрытой. Молчаливое расхождение ответа с делом — худший вид отказа.
        if (warcraftPlayer.Record.GrantedRaces.Contains(race.Id, StringComparer.OrdinalIgnoreCase))
            return true;

        if (race.DonorOnly) return false;

        return race.UnlockTotalLevel <= 0
               || warcraftPlayer.TotalLevel >= race.UnlockTotalLevel
               || warcraftPlayer.Record.Races.ContainsKey(race.Id);
    }

    public void SelectRace(WarcraftPlayer warcraftPlayer, Race race)
    {
        if (!IsRaceUnlocked(warcraftPlayer, race))
        {
            warcraftPlayer.Controller?.PrintToChat(race.DonorOnly
                ? $"{Prefix} {ChatColors.Green}{race.Name}{ChatColors.Default} выдаётся лично — прокачкой её не открыть."
                : $"{Prefix} {ChatColors.Green}{race.Name}{ChatColors.Default} откроется на {ChatColors.Gold}{race.UnlockTotalLevel}{ChatColors.Default} общем уровне. Сейчас у вас {ChatColors.Gold}{warcraftPlayer.TotalLevel}{ChatColors.Default}.");
            return;
        }

        // Живому расу сразу не меняем: иначе он получил бы активки новой расы,
        // не потеряв уже выданных бонусов старой, и лишился бы её пассивок посреди боя.
        if (warcraftPlayer.Race is not null && warcraftPlayer.IsAlive)
        {
            warcraftPlayer.PendingRace = race;

            // Пишем выбор на диск сразу: до следующего раунда может случиться обрыв связи.
            warcraftPlayer.Record.PendingRaceId = race.Id;
            _store.MarkDirty();

            warcraftPlayer.Controller?.PrintToChat(
                $"{Prefix} {ChatColors.Green}{race.Name}{ChatColors.Default} вступит в силу со следующего раунда. Сейчас играете за {warcraftPlayer.Race.Name}.");
            return;
        }

        ApplyRace(warcraftPlayer, race);
    }

    /// <summary>
    /// Случайная стартовая раса — та, что открыта всем сразу и не выдаётся лично.
    /// Ими и только ими встречают новичка: остальные закрыты общим уровнем, и выдать
    /// такую при входе значило бы обойти прогрессию, ради которой всё и сделано.
    /// </summary>
    private Race? RandomStarterRace()
    {
        var starters = _races.All
            .Where(race => race is { DonorOnly: false, UnlockTotalLevel: <= 0 })
            .ToList();

        return starters.Count > 0 ? starters[Random.Shared.Next(starters.Count)] : _races.Default;
    }

    private void ApplyRace(WarcraftPlayer warcraftPlayer, Race race)
    {
        warcraftPlayer.PendingRace = null;
        warcraftPlayer.Record.PendingRaceId = "";
        warcraftPlayer.SetRace(race);
        _store.MarkDirty();

        UpdateClanTag(warcraftPlayer);

        // Очки новой расы раскладываем сразу и вводим в игру тем же движением. Ждать
        // следующего раунда здесь нечего: сама смена расы уже отложена там, где надо,
        // а раса без единой способности — это ровно то, из-за чего автомат и заведён.
        var upgrades = AutoSpendSkillPoints(warcraftPlayer);
        if (upgrades.Count > 0) warcraftPlayer.ApplyBoughtRanks();

        var controller = warcraftPlayer.Controller;
        controller?.PrintToChat($"{Prefix} Ваша раса: {ChatColors.Green}{race.Name}{ChatColors.Default} (уровень {warcraftPlayer.Progress.Level})");
        controller?.PrintToChat($"{Prefix} {race.Description}");

        if (upgrades.Count > 0)
            controller?.PrintToChat($"{Prefix} Очки вложены сами: {DescribeUpgrades(upgrades)}. Своя раскладка — {ChatColors.Green}!skills");
        else
            NotifyUnspentPoints(warcraftPlayer);
    }

    /// <summary>
    /// Разложить свободные очки самому — по одному в ту способность, где ранг сейчас ниже
    /// всех, при равенстве в ту, что выше в списке расы. Возвращает способности, у которых
    /// ранг вырос, с их новым значением; пустой список — не тронуто ничего.
    ///
    /// Зачем это вообще: за первую неделю сервера из шести игроков, добравшихся до расы,
    /// очки вложил ровно один. Остальные брали седьмой уровень и играли расой без единой
    /// способности — мод у них не выключен, его просто нет. Строка «распределите очки»
    /// в чате эту дыру не закрыла и закрыть не могла: она уходит в общий поток посреди боя.
    ///
    /// Порядок выбран так, чтобы раса собиралась вширь, а не вглубь. Заливая по очереди
    /// в первую способность, человек к четвёртому уровню имел бы одну прокачанную пассивку
    /// и ни активной, ни ультимейта — то есть по-прежнему нечего нажать. Ровный набор даёт
    /// активную к третьему уровню, а ультимейт — сразу как его пустит <c>RequiredLevel</c>.
    /// </summary>
    public List<(int Index, Ability Ability, int Rank)> AutoSpendSkillPoints(WarcraftPlayer warcraftPlayer)
    {
        var upgrades = new List<(int, Ability, int)>();

        if (warcraftPlayer.Race is not { } race) return upgrades;
        if (warcraftPlayer.Progress.AutoSkills != true) return upgrades;

        var ranks = warcraftPlayer.Progress.Ranks;
        var count = Math.Min(race.Abilities.Count, ranks.Length);
        var before = (int[])ranks.Clone();
        var spent = 0;

        while (warcraftPlayer.UnspentSkillPoints > 0)
        {
            var pick = -1;

            for (var i = 0; i < count; i++)
            {
                var ability = race.Abilities[i];

                // Ультимейт закрыт до своего уровня — очко на него не тратим, оно
                // подождёт: ранний ультимейт был бы обходом правила, а не удобством.
                if (warcraftPlayer.Progress.Level < ability.RequiredLevel) continue;
                if (ranks[i] >= ability.MaxRank) continue;
                if (pick < 0 || ranks[i] < ranks[pick]) pick = i;
            }

            // Вкладывать больше некуда: всё либо на максимуме, либо ждёт уровня.
            // Остаток очков остаётся у игрока и разложится, когда откроется.
            if (pick < 0) break;

            ranks[pick]++;
            spent++;
        }

        if (spent == 0) return upgrades;

        _store.MarkDirty();

        for (var i = 0; i < count; i++)
            if (ranks[i] != before[i]) upgrades.Add((i, race.Abilities[i], ranks[i]));

        return upgrades;
    }

    /// <summary>Строка вида «Скачок 2/4, Батут 1/4» для сообщения о вложенных очках.</summary>
    private static string DescribeUpgrades(List<(int Index, Ability Ability, int Rank)> upgrades) =>
        string.Join(", ", upgrades.Select(u =>
            $"{ChatColors.Green}{u.Ability.Name}{ChatColors.Default} {u.Rank}/{u.Ability.MaxRank}"));

    /// <summary>
    /// Вернуть очки под автомат и тут же их разложить. Обратный ход к «Сбросу очков»,
    /// без него ручная раскладка была бы дорогой в один конец.
    /// </summary>
    public void EnableAutoSkills(WarcraftPlayer warcraftPlayer)
    {
        if (warcraftPlayer.Race is null) return;

        warcraftPlayer.Progress.AutoSkills = true;
        _store.MarkDirty();

        var upgrades = AutoSpendSkillPoints(warcraftPlayer);
        var controller = warcraftPlayer.Controller;

        if (upgrades.Count == 0)
        {
            controller?.PrintToChat($"{Prefix} Автораспределение включено. Свободных очков нет.");
            return;
        }

        controller?.PrintToChat(
            $"{Prefix} Автораспределение включено: {DescribeUpgrades(upgrades)}. Заработает со {ChatColors.Gold}следующего раунда{ChatColors.Default}.");
    }

    /// <summary>
    /// Вложить очко в способность. Возвращает текст ошибки, либо null при успехе.
    ///
    /// Очко списывается сразу, а вот работать способность на новом ранге начнёт со следующего
    /// раунда: рабочие ранги снимаются на его старте. Считаем купленное, а не рабочее, иначе
    /// в одну способность можно было бы весь раунд класть очко за очком в один и тот же ранг.
    /// </summary>
    public string? UpgradeAbility(WarcraftPlayer warcraftPlayer, int abilityIndex)
    {
        if (warcraftPlayer.Race is not { } race) return "Сначала выберите расу: !race";
        if (abilityIndex < 0 || abilityIndex >= race.Abilities.Count) return "Такой способности нет.";

        var ability = race.Abilities[abilityIndex];
        var currentRank = warcraftPlayer.BoughtRankOf(abilityIndex);

        if (currentRank >= ability.MaxRank) return $"«{ability.Name}» уже прокачана до максимума.";
        if (warcraftPlayer.UnspentSkillPoints <= 0) return "Нет свободных очков навыков.";
        if (warcraftPlayer.Progress.Level < ability.RequiredLevel)
            return $"«{ability.Name}» открывается с {ability.RequiredLevel} уровня.";

        warcraftPlayer.Progress.Ranks[abilityIndex] = currentRank + 1;

        // Человек вложил очко сам — значит раскладку дальше ведёт он, и дописывать за ним
        // свои ранги мод больше не должен. Обычный путь сюда — через «Сброс очков», который
        // автомат уже выключил; эта строка ловит остальные, вроде очка, ждавшего уровня.
        warcraftPlayer.Progress.AutoSkills = false;
        _store.MarkDirty();

        // Про отсрочку говорим прямо: молча выданный, но не работающий ранг читается как поломка.
        var delay = warcraftPlayer.IsUpgradePending(abilityIndex)
            ? $" — заработает со {ChatColors.Gold}следующего раунда{ChatColors.Default}"
            : "";

        warcraftPlayer.Controller?.PrintToChat(
            $"{Prefix} {ChatColors.Green}{ability.Name}{ChatColors.Default} → ранг {ChatColors.Gold}{currentRank + 1}{ChatColors.Default}/{ability.MaxRank}{delay}");

        return null;
    }


    /// <summary>
    /// Сбросить раскладку и отдать очки игроку. Заодно выключает автомат — иначе сброс
    /// не делал бы ничего: очки тут же разложились бы обратно тем же порядком, и человек
    /// не смог бы собрать расу по-своему. Вернуть автомат — <see cref="EnableAutoSkills"/>.
    /// </summary>
    public void ResetSkills(WarcraftPlayer warcraftPlayer)
    {
        Array.Clear(warcraftPlayer.Progress.Ranks);
        warcraftPlayer.Progress.AutoSkills = false;
        _store.MarkDirty();

        // Рабочие ранги не трогаем: сброс посреди раунда — та же прокачка, и она вступает
        // в силу со следующего. Иначе игрок остался бы посреди боя вообще без способностей.
        warcraftPlayer.Controller?.PrintToChat(
            $"{Prefix} Очки сброшены, автораспределение выключено. Свободно: {ChatColors.Gold}{warcraftPlayer.UnspentSkillPoints}{ChatColors.Default} — разложите их в {ChatColors.Green}!skills{ChatColors.Default}. Новая раскладка заработает со следующего раунда.");
    }

    // ------------------------------------------------------------------
    // Команды
    // ------------------------------------------------------------------

    /// <summary>
    /// Главное меню — единственная команда, которую нужно забиндить.
    ///
    /// С номером выбирает пункт открытого меню: <c>!wc 2</c>. Это единственный способ
    /// управлять меню мёртвому и зрителю — нажатия им забирает камера наблюдателя,
    /// а команды доходят всегда.
    /// </summary>
    [ConsoleCommand("css_wc", "Главное меню мода, с номером — выбор пункта")]
    [ConsoleCommand("css_warcraft", "Главное меню мода")]
    [CommandHelper(usage: "<номер пункта, пусто = открыть меню>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnMainMenuCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is not { } warcraftPlayer) return;

        // Номер работает только по открытому меню. Не открыто — значит человек просто
        // ошибся аргументом, и правильнее показать ему меню, чем промолчать.
        if (int.TryParse(command.GetArg(1), out var number) && _menus.SelectByNumber(warcraftPlayer, number)) return;

        WarcraftMenus.OpenMainMenu(this, warcraftPlayer);
    }

    /// <summary>
    /// Закрыть меню. Нужна ровно тем, кому не доходят клавиши: мёртвому и зрителю.
    /// Пункт «Закрыть» в списке есть, но он может оказаться на другой странице,
    /// а выхода из меню человек ищет сразу и не должен его выискивать.
    /// </summary>
    [ConsoleCommand("css_wcc", "Закрыть меню мода")]
    [ConsoleCommand("css_wcclose", "Закрыть меню мода")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCloseMenuCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is { } warcraftPlayer) _menus.Close(warcraftPlayer);
    }

    [ConsoleCommand("css_race", "Выбрать расу: без аргумента — меню, с номером или названием — сразу")]
    [ConsoleCommand("css_changerace", "Выбрать расу")]
    [CommandHelper(usage: "<номер или название, пусто = меню>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRaceCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is not { } warcraftPlayer) return;

        // Без аргумента — как раньше, меню. С аргументом выбираем напрямую: мёртвому
        // и зрителю меню бесполезно, движок не доставляет им нажатия клавиш.
        if (command.ArgCount <= 1)
        {
            WarcraftMenus.OpenRaceMenu(this, warcraftPlayer);
            return;
        }

        if (WarcraftMenus.FindRace(this, command.GetArg(1)) is not { } race)
        {
            RaceNotFound(controller, command.GetArg(1));
            return;
        }

        SelectRace(warcraftPlayer, race);
    }

    [ConsoleCommand("css_races", "Список рас в чат — работает и мёртвым, и зрителю")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRaceListCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is { } warcraftPlayer) WarcraftMenus.PrintRaceList(this, warcraftPlayer);
    }

    [ConsoleCommand("css_raceinfo", "Способности расы в чат: !raceinfo <название>")]
    [CommandHelper(usage: "<название, пусто = список рас>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRaceDescriptionCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is not { } warcraftPlayer) return;

        // Без аргумента показываем список: человек, набравший команду наугад, должен
        // увидеть, что вообще можно спросить, а не отповедь про синтаксис.
        if (command.ArgCount <= 1)
        {
            WarcraftMenus.PrintRaceList(this, warcraftPlayer);
            return;
        }

        if (WarcraftMenus.FindRace(this, command.GetArg(1)) is not { } race)
        {
            RaceNotFound(controller, command.GetArg(1));
            return;
        }

        WarcraftMenus.PrintRaceDescription(warcraftPlayer, race);
    }

    private void RaceNotFound(CCSPlayerController? controller, string query) =>
        controller?.PrintToChat($"{Prefix} Расы «{query}» не нашёл. Хватит части названия — {ChatColors.Green}!race орда{ChatColors.Default}. Весь список: {ChatColors.Green}!races");

    [ConsoleCommand("css_skills", "Распределить очки навыков")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnSkillsCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is not { } warcraftPlayer) return;

        if (warcraftPlayer.Race is null)
        {
            controller?.PrintToChat($"{Prefix} Сначала выберите расу: {ChatColors.Green}!race");
            return;
        }

        WarcraftMenus.OpenSkillsMenu(this, warcraftPlayer);
    }

    [ConsoleCommand("css_ability", "Применить активную способность")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAbilityCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is { } warcraftPlayer) TryActivate(warcraftPlayer, AbilityKind.Active);
    }

    [ConsoleCommand("css_ult", "Применить ультимейт")]
    [ConsoleCommand("css_ultimate", "Применить ультимейт")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnUltimateCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is { } warcraftPlayer) TryActivate(warcraftPlayer, AbilityKind.Ultimate);
    }

    [ConsoleCommand("css_wcinfo", "Информация о вашей расе")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnInfoCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is not { } warcraftPlayer) return;
        WarcraftMenus.PrintRaceInfo(warcraftPlayer);
    }

    [ConsoleCommand("css_resetskills", "Сбросить распределение очков")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnResetSkillsCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (Get(controller) is { } warcraftPlayer) ResetSkills(warcraftPlayer);
    }

    [ConsoleCommand("css_map", "Смена карты: !map — меню, !map <номер или часть имени> — сразу")]
    [CommandHelper(minArgs: 0, usage: "[номер или часть имени]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnMapMenuCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (!AdminOnly(controller) || Get(controller) is not { } warcraftPlayer) return;
        if (controller is not { IsValid: true } admin) return;

        var query = command.ArgCount > 1 ? command.GetArg(1).Trim() : "";

        if (query.Length == 0)
        {
            // Мёртвому и зрителю меню не отвечает: нажатия забирает камера наблюдателя.
            // Открытый список он прочитает, а выбрать в нём не сможет ничего — поэтому
            // ему сразу печатаем карты в чат, набирать он может всегда.
            if (!admin.PawnIsAlive) PrintMapList(admin);
            else WarcraftMenus.OpenMapMenu(this, warcraftPlayer);
            return;
        }

        // Номером строки — тем же способом, каким выбирается раса через !race <n>.
        if (int.TryParse(query, out var number))
        {
            if (number >= 1 && number <= MapPool.All.Count) ChangeMap(MapPool.All[number - 1]);
            else PrintMapList(admin);
            return;
        }

        var matches = MapPool.All
            .Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            ChangeMap(matches[0]);
            return;
        }

        // Смена карты рвёт раунд всем, поэтому на двусмысленности останавливаемся и
        // переспрашиваем, а не берём первую подходящую.
        admin.PrintToChat(matches.Count == 0
            ? $"{Prefix} Карты «{query}» в пуле нет."
            : $"{Prefix} Подходит несколько карт — уточните запрос.");
        PrintMapList(admin);
    }

    /// <summary>Пул карт в чат: единственный путь к смене для мёртвого.</summary>
    private void PrintMapList(CCSPlayerController controller)
    {
        controller.PrintToChat($"{Prefix} Карты — {ChatColors.Green}!map <номер или часть имени>{ChatColors.Default}:");

        for (var index = 0; index < MapPool.All.Count; index++)
        {
            var entry = MapPool.All[index];
            var current = string.Equals(entry.Name, Server.MapName, StringComparison.OrdinalIgnoreCase) ? " (сейчас)" : "";
            controller.PrintToChat($" {ChatColors.Gold}{index + 1}{ChatColors.Default}. {entry.Name}{current}");
        }
    }

    // ------------------------------------------------------------------
    // Админка
    // ------------------------------------------------------------------

    /// <summary>
    /// Доверенный ли игрок. Единственный источник — список SteamID64 в конфиге.
    /// Прежде админку заодно открывал флаг отладки, но отладочные команды из мода убраны,
    /// и обходных путей больше нет: не вписан в список — не админ.
    /// </summary>
    public bool IsAdmin(CCSPlayerController? controller) =>
        controller is { IsValid: true } && _config.Admins.Contains(controller.SteamID.ToString());

    /// <summary>
    /// Проверка с отказом в чат. В отказе называем SteamID спросившего: настраивая сервер,
    /// владелец первым делом упирается в «а какой у меня номер», и гонять его за этим
    /// в серверную консоль — лишний шаг. Номер и так виден любому, кто открыл профиль.
    /// </summary>
    private bool AdminOnly(CCSPlayerController? controller)
    {
        if (IsAdmin(controller)) return true;

        if (controller is { IsValid: true })
        {
            controller.PrintToChat($"{Prefix} Команда только для доверенных. Ваш SteamID: {ChatColors.Gold}{controller.SteamID}");
        }

        return false;
    }

    public BanStore Bans => _bans;

    /// <summary>Снять бан по SteamID. Имя передаём отдельно: забаненного на сервере нет.</summary>
    public void Unban(WarcraftPlayer admin, ulong steamId, string name)
    {
        if (!_bans.Remove(steamId))
        {
            admin.Controller?.PrintToChat($"{Prefix} Этот бан уже снят.");
            return;
        }

        Server.PrintToChatAll($"{Prefix} Бан снят: {ChatColors.Gold}{name}");
    }

    public bool IsMuted(ulong steamId) =>
        _mutedUntil.TryGetValue(steamId, out var until) && until > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Подпись для меню: сколько мута осталось. Пусто — игрок не заглушён.</summary>
    public string MuteHint(ulong steamId)
    {
        if (!_mutedUntil.TryGetValue(steamId, out var until)) return "";

        var left = until - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return left <= 0 ? "" : $"мут {Math.Max(1, left / 60)} мин";
    }

    /// <summary>
    /// Снять муты, у которых вышел срок. Игрока ищем по SteamID, а не по слоту:
    /// за время мута слот мог занять кто-то другой.
    /// </summary>
    private void ExpireMutes()
    {
        if (_mutedUntil.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (steamId, until) in _mutedUntil.ToList())
        {
            if (until > now) continue;

            _mutedUntil.Remove(steamId);

            if (FindOnline(steamId) is not { } player) continue;

            player.VoiceFlags = VoiceFlags.Normal;
            player.PrintToChat($"{Prefix} Мут снят — время вышло.");
        }
    }

    /// <summary>
    /// Игрок на сервере по SteamID. Сверяем и с подтверждённым Steam номером, и с сырым:
    /// регистрация берёт первый, а часть кода — второй, и на боевом сервере они могут
    /// разойтись. Несовпадение здесь ничего не ломает громко — просто молча не находит,
    /// и способность или подсказка тихо пропадают.
    /// </summary>
    private static CCSPlayerController? FindOnline(ulong steamId) =>
        Utilities.GetPlayers().FirstOrDefault(player =>
            player is { IsValid: true, IsBot: false }
            && ((player.AuthorizedSteamID?.SteamId64 ?? 0) == steamId || player.SteamID == steamId));

    /// <summary>
    /// Вернуть мут вошедшему. Без этого выход и заход снимали бы заглушку,
    /// и любой срок обходился бы за десять секунд.
    /// </summary>
    private void ReapplyMute(int slot)
    {
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false } player) return;
        if (!IsMuted(player.SteamID)) return;

        player.VoiceFlags = VoiceFlags.Muted;
    }

    [ConsoleCommand("css_admin", "Админка: карта, кик, бан, мут")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAdminCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (!AdminOnly(controller) || Get(controller) is not { } warcraftPlayer) return;

        AdminMenus.OpenAdminMenu(this, warcraftPlayer);
    }

    [ConsoleCommand("css_wcadmin", "Админка мода: выдача рас и уровней")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnModAdminCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (!AdminOnly(controller) || Get(controller) is not { } warcraftPlayer) return;

        AdminMenus.OpenModAdminMenu(this, warcraftPlayer);
    }

    public void KickPlayer(WarcraftPlayer admin, int slot)
    {
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } target)
        {
            admin.Controller?.PrintToChat($"{Prefix} Этого игрока уже нет на сервере.");
            return;
        }

        var name = target.PlayerName;
        var userId = target.UserId;

        // Рвём соединение следующим кадром: изнутри обработки нажатия это опасно.
        Server.NextFrame(() =>
        {
            if (userId is { } id) Server.ExecuteCommand($"kickid {id}");
        });

        Server.PrintToChatAll($"{Prefix} {ChatColors.Gold}{name}{ChatColors.Default} кикнут.");
    }

    /// <summary>Забанить по SteamID. <paramref name="minutes"/> = 0 — навсегда.</summary>
    public void BanPlayer(WarcraftPlayer admin, int slot, int minutes)
    {
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } target)
        {
            admin.Controller?.PrintToChat($"{Prefix} Этого игрока уже нет на сервере.");
            return;
        }

        if (target.IsBot)
        {
            // Бан хранится по SteamID, а у бота его нет — запись просто не на что повесить.
            admin.Controller?.PrintToChat($"{Prefix} Бота забанить нельзя — у него нет SteamID. Кик работает.");
            return;
        }

        var until = minutes <= 0 ? 0 : DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
        var name = target.PlayerName;
        var userId = target.UserId;

        _bans.Add(target.SteamID, new BanRecord
        {
            Name = name,
            UntilUnix = until,
            By = admin.Controller?.PlayerName ?? "консоль",
        });

        Server.NextFrame(() =>
        {
            if (userId is { } id) Server.ExecuteCommand($"kickid {id}");
        });

        var term = minutes <= 0 ? "навсегда" : minutes >= 1440 ? $"на {minutes / 1440} дн." : $"на {minutes} мин.";
        Server.PrintToChatAll($"{Prefix} {ChatColors.Gold}{name}{ChatColors.Default} забанен {term}");
    }

    /// <summary>Заглушить на <paramref name="minutes"/> минут.</summary>
    public void MutePlayer(WarcraftPlayer admin, int slot, int minutes)
    {
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } target)
        {
            admin.Controller?.PrintToChat($"{Prefix} Этого игрока уже нет на сервере.");
            return;
        }

        if (target.IsBot)
        {
            // Мут держится по SteamID, а у бота его нет. Да и говорить ему нечем.
            admin.Controller?.PrintToChat($"{Prefix} Бота глушить нечего — он и так молчит.");
            return;
        }

        _mutedUntil[target.SteamID] = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
        target.VoiceFlags = VoiceFlags.Muted;

        Server.PrintToChatAll($"{Prefix} {ChatColors.Gold}{target.PlayerName}{ChatColors.Default} заглушён на {minutes} мин.");
    }

    public void UnmutePlayer(WarcraftPlayer admin, int slot)
    {
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } target)
        {
            admin.Controller?.PrintToChat($"{Prefix} Этого игрока уже нет на сервере.");
            return;
        }

        _mutedUntil.Remove(target.SteamID);
        target.VoiceFlags = VoiceFlags.Normal;

        Server.PrintToChatAll($"{Prefix} {ChatColors.Gold}{target.PlayerName}{ChatColors.Default} снова может говорить.");
    }

    [ConsoleCommand("css_wcstats", "Статистика прогрессии — отчёт в консоль")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnStatsCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not null && !AdminOnly(controller)) return;

        // В консоль, а не в чат: строк много, а из консоли их ещё и скопировать можно.
        var write = controller is { IsValid: true }
            ? controller.PrintToConsole
            : (Action<string>)(line => Console.WriteLine($"[WarcraftMod] {line}"));

        var records = _store.All().Where(entry => entry.Record.PlayedSeconds > 0).ToList();

        write("");
        write("=== Warcraft: статистика прогрессии ===");

        if (records.Count == 0)
        {
            write("Данных пока нет: время в игре ещё ни у кого не накопилось.");
            write("");
            controller?.PrintToChat($"{Prefix} Данных пока нет — никто ещё не играл после включения счётчика.");
            return;
        }

        var totalSeconds = records.Sum(entry => entry.Record.PlayedSeconds);
        var totalXp = records.Sum(entry => entry.Record.AccountXp);
        var hours = totalSeconds / 3600.0;
        var xpPerHour = hours > 0 ? totalXp / hours : 0;

        write($"Игроков: {records.Count}, наиграно суммарно: {hours:0.0} ч");
        write("");

        // Главное число: на нём стоят все расчёты волн и часов на расу.
        write($"ОПЫТ В ЧАС: {xpPerHour:0}   (в расчёт прогрессии заложено 1500)");
        var ratio = xpPerHour / 1500.0;
        write(ratio is > 0.75 and < 1.35
            ? "  -> расчёт подтверждается, пороги волн менять не нужно."
            : $"  -> расходится в {ratio:0.00} раза. Пороги волн в Core/Unlocks.cs надо умножить примерно на это число.");
        write("");

        var levels = records.Select(entry => XpTable.AccountLevelFromXp(entry.Record.AccountXp)).OrderBy(level => level).ToList();
        write($"Общий уровень: медиана {levels[levels.Count / 2]}, максимум {levels[^1]}");

        var maxedAny = records.Count(entry => entry.Record.Races.Values.Any(p => p.Level >= XpTable.MaxLevel));
        write($"Довели хотя бы одну расу до потолка: {maxedAny} из {records.Count}");
        write("");

        // Время по расам показывает то, чего не видно ниоткуда больше: расы, которые не берут.
        write("Время по расам:");
        var byRace = records
            .SelectMany(entry => entry.Record.Races)
            .GroupBy(pair => pair.Key)
            .Select(group => (Id: group.Key, Seconds: group.Sum(pair => pair.Value.PlayedSeconds)))
            .ToDictionary(item => item.Id, item => item.Seconds);

        foreach (var race in _races.All.OrderByDescending(race => byRace.GetValueOrDefault(race.Id)))
        {
            var raceSeconds = byRace.GetValueOrDefault(race.Id);
            var share = totalSeconds > 0 ? raceSeconds * 100.0 / totalSeconds : 0;
            var mark = raceSeconds == 0 ? "   <- не берут вовсе" : "";
            write($"  {race.Name,-24} {raceSeconds / 3600.0,6:0.0} ч  {share,5:0}%{mark}");
        }

        write("");
        controller?.PrintToChat($"{Prefix} Опыт в час: {ChatColors.Gold}{xpPerHour:0}{ChatColors.Default} (заложено 1500). Полный отчёт в консоли — {ChatColors.Green}~");
    }

    [ConsoleCommand("css_wcunban", "Снять бан: css_wcunban <SteamID64>")]
    [CommandHelper(minArgs: 1, usage: "<SteamID64>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnUnbanCommand(CCSPlayerController? controller, CommandInfo command)
    {
        // Из серверной консоли — всегда: забаненного в меню не выбрать, он уже не на сервере.
        if (controller is not null && !AdminOnly(controller)) return;

        if (!ulong.TryParse(command.GetArg(1), out var steamId))
        {
            Reply(controller, "Нужен SteamID64 — 17 цифр. Список банов лежит в warcraft_bans.json рядом с плагином.");
            return;
        }

        Reply(controller, _bans.Remove(steamId)
            ? $"Бан снят: {steamId}"
            : $"Такого бана нет: {steamId}");
    }


    /// <summary>
    /// Переключить сервер на карту из пула. Саму смену откладываем на секунду: смена уровня
    /// рвёт всё состояние, и делать её прямо в обработке нажатия — то же самое, что убивать
    /// изнутри хука урона. Заодно сообщение успевает дойти до игроков.
    /// </summary>
    public void ChangeMap(MapEntry entry)
    {
        Server.PrintToChatAll($"{Prefix} Смена карты: {ChatColors.Gold}{entry.Name}{ChatColors.Default}");

        AddTimer(1f, () =>
        {
            // Печатаем ровно ту строку, которую отдаём движку. Без этого при расхождении
            // «выбрал одну карту, загрузилась другая» неясно даже, кто ошибся — меню,
            // мод или сервер, и приходится гадать вместо чтения.
            Console.WriteLine($"[WarcraftMod] Выполняю: {entry.ChangeCommand}");
            Server.ExecuteCommand(entry.ChangeCommand);
        });
    }




    /// <summary>
    /// Где я стою. Печатает координаты и, главное, на сколько игрок вышел за коробку
    /// точек возрождения — это то самое число, которое просит настройка
    /// <c>OutOfBoundsFence</c>.
    ///
    /// Команда нужна ровно потому, что подобрать рамку иначе нечем: на глаз её ставить
    /// нельзя — мало, и людей выкидывает с честных углов арены, много — и чужая площадка
    /// остаётся открытой. Встать в дальнем углу арены, встать там, куда попадать нельзя,
    /// и взять что-то между.
    /// </summary>
    /// <summary>
    /// Проиграть себе звук по имени события. Инструмент проверки, а не игровая команда.
    ///
    /// Нужна ровно потому, что иначе кастомную озвучку нечем измерить: движок на
    /// несуществующее имя события молчит без ошибки — ровно так же, как на верное имя
    /// при неподключённом аддоне. Отличить «имя не то» от «аддон не встал» можно только
    /// сравнением с заведомо рабочим встроенным звуком, а для этого нужно уметь позвать
    /// любое имя руками.
    ///
    /// Порядок проверки: сначала встроенный (`css_wcsound UIPanorama.Chat_Message`) —
    /// он подтверждает, что звук вообще доходит; и только потом своё имя из аддона.
    /// </summary>
    [ConsoleCommand("css_wcsound", "Проверка звука: css_wcsound <имя события> [громкость]")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnSoundCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not { IsValid: true } || !AdminOnly(controller)) return;

        var soundEvent = command.GetArg(1);
        if (string.IsNullOrWhiteSpace(soundEvent))
        {
            controller.PrintToChat($"{Prefix} Нужно имя события: {ChatColors.Green}css_wcsound <имя> [громкость]");
            controller.PrintToChat($"{Prefix} {ChatColors.Grey}Заведомо рабочее для сверки: UIPanorama.Chat_Message");
            return;
        }

        var volume = 1f;
        if (command.ArgCount > 2 && float.TryParse(command.GetArg(2), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            volume = Math.Clamp(parsed, 0f, 4f);

        VisualEffects.PlaySoundTo(controller, soundEvent, volume);

        // Движок не сообщает, нашлось событие или нет, поэтому не обещаем звук, а называем
        // то, что действительно сделали. Иначе строка в чате читалась бы как подтверждение.
        controller.PrintToChat($"{Prefix} Отправлено событие {ChatColors.Gold}{soundEvent}{ChatColors.Default} (громкость {volume:0.##}). Тишина = события нет.");
    }

    [ConsoleCommand("css_wcpos", "Где я стою: координаты и выход за коробку спавнов")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnPosCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not { IsValid: true } || !AdminOnly(controller)) return;
        if (Get(controller) is not { Pawn: { } pawn } || pawn.Health <= 0) return;
        if (Effects.Origin(pawn) is not { } position) return;

        var points = Effects.SpawnPoints();
        if (points.Count == 0)
        {
            controller.PrintToChat($"{Prefix} На карте нет точек возрождения — считать не от чего.");
            return;
        }

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);

        // Насколько вы за коробкой: отрицательное число — вы внутри, и это запас до края.
        var beyond = Math.Max(Math.Max(minX - position.X, position.X - maxX),
                              Math.Max(minY - position.Y, position.Y - maxY));

        controller.PrintToConsole("");
        controller.PrintToConsole($"=== Warcraft: где я стою ({Server.MapName}) ===");
        controller.PrintToConsole($"  Вы:            X={position.X:0} Y={position.Y:0} Z={position.Z:0}");
        controller.PrintToConsole($"  Коробка спавнов: X от {minX:0} до {maxX:0}, Y от {minY:0} до {maxY:0}, низ Z={points.Min(point => point.Z):0}");
        controller.PrintToConsole($"  За коробкой:   {beyond:0} юнитов (минус — вы внутри)");
        controller.PrintToConsole($"  Рамка сейчас:  {(_fenceOn ? $"X {_fenceMinX:0}..{_fenceMaxX:0}, Y {_fenceMinY:0}..{_fenceMaxY:0}" : "не задана для этой карты")}");
        controller.PrintToConsole("");

        controller.PrintToChat($"{Prefix} За коробкой спавнов: {ChatColors.Gold}{beyond:0}{ChatColors.Default} юнитов. Подробности в консоли, {ChatColors.Green}~");
    }

    /// <summary>
    /// Кто на сервере, с их SteamID64. Без этого выдача с боевого сервера упиралась
    /// в нехватку номера: `status` показывает [U:1:N], в чате текст не выделяется,
    /// а панель хостинга — та же консоль. Отчёт печатается туда, откуда его можно
    /// скопировать прямо в css_wcgrant.
    /// </summary>
    [ConsoleCommand("css_wcwho", "Кто на сервере: ник, SteamID64, раса")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnWhoCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not null && !AdminOnly(controller)) return;

        var write = controller is { IsValid: true }
            ? controller.PrintToConsole
            : (Action<string>)(line => Console.WriteLine($"[WarcraftMod] {line}"));

        write("");
        write("=== Warcraft: кто на сервере ===");

        var found = 0;
        foreach (var player in Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: false }))
        {
            var steamId = player.AuthorizedSteamID?.SteamId64 ?? player.SteamID;

            // Незарегистрированный игрок для мода не существует вовсе — ни меню, ни опыта.
            // Случай редкий, но немой: здесь он виден сразу, а не через час поисков.
            var state = Get(player) is { } known
                ? $"{known.Race?.Name ?? "расы нет",-20} ур. {known.Progress.Level,-3} общий {known.TotalLevel}"
                : "НЕ ЗАРЕГИСТРИРОВАН (см. GSLT и логи)";

            write($"  {player.PlayerName,-20} {steamId}  {state}");
            found++;
        }

        if (found == 0) write("  Живых игроков нет — только боты.");

        write("");
        write("  Выдать расу:  css_wcgrant <SteamID64> <id расы>");
        write("  Отобрать:     css_wcrevoke <SteamID64> <id расы>");
        write($"  Расы: {string.Join(", ", _races.All.Select(race => race.Id))}");
        write("");

        controller?.PrintToChat($"{Prefix} Список игроков со SteamID — в консоли, {ChatColors.Green}~");
    }

    /// <summary>
    /// Кто вообще когда-либо заходил: сколько отыграл в сумме и когда был в последний раз.
    ///
    /// Отличается от <c>css_wcwho</c> тем, что читает сохранение, а не текущий сервер:
    /// в списке те, кого прямо сейчас нет. Отличается от <c>!wcstats</c> тем, что там
    /// общие числа по всем сразу, а здесь строка на человека — именно она отвечает на
    /// вопрос «кто-нибудь возвращается или все заходят по одному разу».
    ///
    /// Время считается только пока игрок в команде: зритель ничего не зарабатывает,
    /// и его часы испортили бы главное число.
    /// </summary>
    [ConsoleCommand("css_wcplayers", "Кто заходил: сколько отыграл и когда был в последний раз")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnPlayersCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not null && !AdminOnly(controller)) return;

        var write = controller is { IsValid: true }
            ? controller.PrintToConsole
            : (Action<string>)(line => Console.WriteLine($"[WarcraftMod] {line}"));

        // Показываем всех подряд, включая заглянувших на секунды: пока сервер борется
        // за первых игроков, каждый заход это данные, а не шум. Появится поток — можно
        // будет отсечь порогом по PlayedSeconds.
        var records = _store.All()
            .OrderByDescending(entry => entry.Record.LastSeenUnix)
            .ToList();

        write("");
        write("=== Warcraft: кто заходил ===");

        if (records.Count == 0)
        {
            write("  В сохранении пока никого.");
            write("");
            controller?.PrintToChat($"{Prefix} Данных пока нет.");
            return;
        }

        write($"  {"Ник",-20} {"Всего",-10} {"Последний раз",-16} {"Первый раз",-12} Уровень");

        foreach (var (steamId, record) in records.Take(50))
        {
            var name = string.IsNullOrWhiteSpace(record.LastKnownName) ? steamId.ToString() : record.LastKnownName;

            write($"  {Shorten(name, 20),-20} {Playtime(record.PlayedSeconds),-10} " +
                  $"{Ago(record.LastSeenUnix),-16} {OnDate(record.FirstSeenUnix),-12} " +
                  $"{XpTable.AccountLevelFromXp(record.AccountXp)}");
        }

        if (records.Count > 50) write($"  ...и ещё {records.Count - 50}");

        var total = records.Sum(entry => entry.Record.PlayedSeconds);
        var returning = records.Count(entry => entry.Record.LastSeenUnix - entry.Record.FirstSeenUnix > 86400);

        write("");
        write($"  Всего игроков: {records.Count}, суммарно наиграно {Playtime(total)}");

        // Возвращаемость — главное число этого отчёта. Заходившие в разные дни это те,
        // ради кого сервер и держат: остальные попробовали один раз и не вернулись.
        write($"  Заходили в разные дни: {returning} — это и есть удержание");
        write("");

        controller?.PrintToChat($"{Prefix} Список игроков — в консоли, {ChatColors.Green}~");
    }

    private static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "…";

    private static string Playtime(long seconds)
    {
        if (seconds < 60) return $"{seconds} с";
        if (seconds < 3600) return $"{seconds / 60} мин";

        return $"{seconds / 3600.0:0.0} ч";
    }

    /// <summary>Сколько прошло с этого момента. Для «последнего раза» это читается легче даты.</summary>
    private static string Ago(long unix)
    {
        if (unix <= 0) return "неизвестно";

        var passed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix;

        if (passed < 60) return "только что";
        if (passed < 3600) return $"{passed / 60} мин назад";
        if (passed < 86400) return $"{passed / 3600} ч назад";

        return $"{passed / 86400} дн назад";
    }

    private static string OnDate(long unix) =>
        unix <= 0 ? "неизвестно" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd.MM.yyyy");

    [ConsoleCommand("css_wcgrant", "Выдать игроку расу: css_wcgrant <SteamID64 или часть ника> <id расы>")]
    [CommandHelper(usage: "<SteamID64 или часть ника> <id расы>")]
    public void OnGrantCommand(CCSPlayerController? controller, CommandInfo command) =>
        ChangeGrant(controller, command, granting: true);

    [ConsoleCommand("css_wcrevoke", "Отобрать у игрока выданную расу: css_wcrevoke <SteamID64 или часть ника> <id расы>")]
    [CommandHelper(usage: "<SteamID64 или часть ника> <id расы>")]
    public void OnRevokeCommand(CCSPlayerController? controller, CommandInfo command) =>
        ChangeGrant(controller, command, granting: false);

    /// <summary>
    /// Выдача и отзыв личного доступа к расе. Это не отладка, а рабочий инструмент:
    /// им раздаются донатные расы и открывается любая другая в обход порога.
    /// Из серверной консоли разрешена всегда, из игры — администраторам из списка в конфиге.
    /// </summary>
    private void ChangeGrant(CCSPlayerController? controller, CommandInfo command, bool granting)
    {
        if (controller is not null && !AdminOnly(controller)) return;

        if (command.ArgCount < 3)
        {
            Reply(controller, $"Нужно: {(granting ? "css_wcgrant" : "css_wcrevoke")} <SteamID64 или часть ника> <id расы>");
            Reply(controller, $"Расы: {string.Join(", ", _races.All.Select(race => race.Id))}");
            Reply(controller, "Кто на сервере и их SteamID — css_wcwho");
            return;
        }

        var raceId = command.GetArg(2);
        if (_races.Find(raceId) is not { } race)
        {
            Reply(controller, $"Раса '{raceId}' не найдена. Идентификаторы: {string.Join(", ", _races.All.Select(r => r.Id))}");
            return;
        }

        if (ResolveSteamId(command.GetArg(1)) is not { } steamId)
        {
            Reply(controller, "Игрок не найден. Годится SteamID64 (17 цифр), [U:1:N] или STEAM_1:Y:Z из status, либо часть ника того, кто сейчас на сервере.");
            Reply(controller, "Список игроков со SteamID — css_wcwho");
            return;
        }

        ApplyRaceGrant(steamId, race, granting, controller);
    }

    /// <summary>
    /// Выдать или отобрать личный доступ к расе. Общий путь для консольной команды
    /// и для меню — чтобы правила выдачи жили в одном месте, а не в двух.
    /// </summary>
    public void ApplyRaceGrant(ulong steamId, Race race, bool granting, CCSPlayerController? admin)
    {
        var record = _store.Get(steamId);
        var already = record.GrantedRaces.Contains(race.Id, StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrEmpty(record.LastKnownName) ? "ник неизвестен" : record.LastKnownName;

        // Когда менять нечего — так и говорим. Прежний ответ был один и тот же для
        // «выдал» и «уже было выдано», и по нему нельзя было понять, попал ли админ
        // по тому игроку: промах по номеру выглядел как успех.
        if (granting == already)
        {
            Reply(admin, granting
                ? $"{race.Name} у игрока {steamId} ({name}) уже есть."
                : $"{race.Name} игроку {steamId} ({name}) не выдавалась.");
            return;
        }

        if (granting) record.GrantedRaces.Add(race.Id);
        else record.GrantedRaces.RemoveAll(id => id.Equals(race.Id, StringComparison.OrdinalIgnoreCase));

        // Пишем сразу: выдача доступа не должна теряться при падении сервера.
        _store.MarkDirty();
        _store.FlushIfDirty();

        Reply(admin, granting
            ? $"{race.Name} выдана игроку {steamId} ({name})"
            : $"{race.Name} отобрана у игрока {steamId} ({name})");

        // Если игрок на сервере — скажем ему сами.
        FindOnline(steamId)?.PrintToChat(granting
            ? $"{Prefix} Вам открыта раса {ChatColors.Green}{race.Name}{ChatColors.Default} — выбрать в {ChatColors.Green}!wc"
            : $"{Prefix} Доступ к расе {ChatColors.Green}{race.Name}{ChatColors.Default} закрыт.");
    }

    /// <summary>Есть ли у игрока личный доступ к расе — для отметки в меню выдачи.</summary>
    public bool HasRaceGrant(ulong steamId, Race race) =>
        _store.Get(steamId).GrantedRaces.Contains(race.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Добавить игроку общих уровней. Считаем через опыт, а не подменой уровня:
    /// общий уровень производный от него, и накопленный остаток внутри уровня сохраняется.
    /// </summary>
    public void GrantAccountLevels(WarcraftPlayer admin, int slot, int levels)
    {
        if (Get(Utilities.GetPlayerFromSlot(slot)) is not { } target)
        {
            admin.Controller?.PrintToChat($"{Prefix} Этого игрока уже нет на сервере.");
            return;
        }

        var before = target.TotalLevel;
        var after = Math.Min(before + levels, XpTable.AccountMaxLevel);
        target.Record.AccountXp += XpTable.AccountXpForLevel(after) - XpTable.AccountXpForLevel(before);
        _store.MarkDirty();

        admin.Controller?.PrintToChat($"{Prefix} {target.Controller?.PlayerName}: общий уровень {ChatColors.Gold}{before}{ChatColors.Default} → {ChatColors.Gold}{target.TotalLevel}");
        target.Controller?.PrintToChat($"{Prefix} Вам выдано {ChatColors.Gold}{levels}{ChatColors.Default} общих уровней. Теперь {ChatColors.Gold}{target.TotalLevel}");
    }

    /// <summary>Добавить уровней текущей расе игрока. Опыт внутри уровня обнуляется.</summary>
    public void GrantRaceLevels(WarcraftPlayer admin, int slot, int levels)
    {
        if (Get(Utilities.GetPlayerFromSlot(slot)) is not { } target)
        {
            admin.Controller?.PrintToChat($"{Prefix} Этого игрока уже нет на сервере.");
            return;
        }

        if (target.Race is not { } race)
        {
            admin.Controller?.PrintToChat($"{Prefix} У игрока не выбрана раса — уровни некуда класть.");
            return;
        }

        var progress = target.Progress;
        var before = progress.Level;
        progress.Level = Math.Clamp(progress.Level + levels, 1, XpTable.MaxLevel);
        progress.Xp = 0;
        _store.MarkDirty();

        admin.Controller?.PrintToChat($"{Prefix} {target.Controller?.PlayerName}: {race.Name} {ChatColors.Gold}{before}{ChatColors.Default} → {ChatColors.Gold}{progress.Level}");
        target.Controller?.PrintToChat($"{Prefix} {ChatColors.Green}{race.Name}{ChatColors.Default}: уровень {ChatColors.Gold}{progress.Level}{ChatColors.Default}. Распределите очки — {ChatColors.Green}!wc");
    }

    /// <summary>
    /// Кого имел в виду админ: Steam-номер в любой из трёх записей либо часть ника
    /// того, кто сейчас на сервере.
    ///
    /// Три записи не для красоты. Прогресс хранится по SteamID64, а серверная консоль
    /// CS2 его нигде не показывает: `status` печатает <c>[U:1:N]</c>, старые списки —
    /// <c>STEAM_1:Y:Z</c>. Без перевода выдача с боевого сервера упиралась в то, что
    /// нужный номер попросту негде взять.
    /// </summary>
    private static ulong? ResolveSteamId(string argument)
    {
        if (SteamIdFromText(argument) is { } number) return number;

        var match = Utilities.GetPlayers().FirstOrDefault(player =>
            player is { IsValid: true, IsBot: false }
            && player.PlayerName.Contains(argument, StringComparison.OrdinalIgnoreCase));

        return match?.AuthorizedSteamID?.SteamId64;
    }

    /// <summary>Первый номер учётной записи Steam: к нему прибавляется номер аккаунта.</summary>
    private const ulong SteamIdBase = 76561197960265728;

    /// <summary>
    /// Разбор Steam-номера. Null — значит это не номер, и аргумент надо понимать как ник.
    /// </summary>
    private static ulong? SteamIdFromText(string text)
    {
        text = text.Trim();

        // Голое число принимаем только в диапазоне настоящих SteamID64. Слот, userid
        // и опечатка — тоже числа, а по ним завелась бы запись несуществующего игрока,
        // и выдача ушла бы в пустоту с бодрым ответом «выдана».
        if (ulong.TryParse(text, out var parsed))
            return parsed >= SteamIdBase ? parsed : null;

        var trimmed = text.Trim('[', ']');

        // [U:1:N] — то, что печатает `status` в консоли сервера.
        if (trimmed.StartsWith("U:1:", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(trimmed[4..], out var account))
            return SteamIdBase + account;

        // STEAM_X:Y:Z — старая запись, встречается в чужих админках и логах.
        if (trimmed.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(':');
            if (parts.Length == 3
                && uint.TryParse(parts[1], out var parity) && parity <= 1
                && uint.TryParse(parts[2], out var half))
                return SteamIdBase + (ulong)half * 2 + parity;
        }

        return null;
    }

    /// <summary>Ответ туда, откуда пришла команда: в чат игроку или в серверную консоль.</summary>
    private static void Reply(CCSPlayerController? controller, string message)
    {
        if (controller is { IsValid: true }) controller.PrintToChat($"{Prefix} {message}");
        else Console.WriteLine($"[WarcraftMod] {message}");
    }





    /// <summary>
    /// Пистолетный ли сейчас раунд. Считаем по счётчику раундов текущей половины:
    /// он обнуляется и после смены сторон, и в овертайме, поэтому первый раунд
    /// определяется одинаково во всех трёх случаях. Разминка пистолетной не считается.
    /// </summary>
    /// <summary>Идёт ли разминка. Отдельно от IsPistolRound: там это лишь одно из условий.</summary>
    private static bool IsWarmup() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;

    private static bool IsPistolRound()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;

        if (rules is null) return false;

        return !rules.WarmupPeriod && rules.RoundsPlayedThisPhase == 0;
    }


    [ConsoleCommand("css_wcbind", "Готовые бинды — вывести в консоль для копирования")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnBindCommand(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller is not { IsValid: true }) return;

        // Печатаем в консоль клиента: оттуда строку можно выделить и скопировать, из чата — нет.
        controller.PrintToConsole("");
        controller.PrintToConsole("=== Warcraft: бинды (скопируйте строку в консоль) ===");
        controller.PrintToConsole("");
        controller.PrintToConsole("bind f css_wc; bind mouse4 css_ability; bind mouse5 css_ult");
        controller.PrintToConsole("");
        controller.PrintToConsole("f - меню, mouse4 - активная способность, mouse5 - ультимейт");
        controller.PrintToConsole("");

        controller.PrintToChat($"{Prefix} Бинды выведены в консоль — откройте её клавишей {ChatColors.Green}~{ChatColors.Default} и скопируйте.");
    }

    [ConsoleCommand("css_wchelp", "Список команд мода")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnHelpCommand(CCSPlayerController? controller, CommandInfo command) =>
        WarcraftMenus.PrintHelp(this, controller);

    private void TryActivate(WarcraftPlayer warcraftPlayer, AbilityKind kind)
    {
        var controller = warcraftPlayer.Controller;

        if (warcraftPlayer.Race is not { } race)
        {
            controller?.PrintToChat($"{Prefix} Сначала выберите расу: {ChatColors.Green}!race");
            return;
        }

        var index = kind == AbilityKind.Ultimate ? race.UltimateIndex : race.ActiveIndex;
        if (index < 0)
        {
            controller?.PrintToChat($"{Prefix} У расы {race.Name} нет такой способности.");
            return;
        }

        var ability = race.Abilities[index];

        if (!warcraftPlayer.IsAlive)
        {
            controller?.PrintToChat($"{Prefix} Способности работают только при жизни.");
            return;
        }

        if (warcraftPlayer.RankOf(index) <= 0)
        {
            // Купить успел, а раунд ещё не сменился — это не «не изучена», и путать эти
            // два ответа нельзя: игрок пойдёт искать несуществующую ошибку в прокачке.
            controller?.PrintToChat(warcraftPlayer.BoughtRankOf(index) > 0
                ? $"{Prefix} «{ability.Name}» заработает со {ChatColors.Gold}следующего раунда{ChatColors.Default} — очко вложено посреди этого."
                : $"{Prefix} «{ability.Name}» ещё не изучена — {ChatColors.Green}!skills");
            return;
        }

        if (kind == AbilityKind.Ultimate && _config.DisableUltimatesInPistolRounds && IsPistolRound())
        {
            controller?.PrintToChat($"{Prefix} В пистолетном раунде ультимейты недоступны.");
            return;
        }

        // Ультимейт по умолчанию даётся раз за раунд. Проверяем до перезарядки:
        // «уже потрачен» — более точный ответ игроку, чем «перезаряжается».
        var oncePerRound = kind == AbilityKind.Ultimate && ability.OncePerRound;
        if (oncePerRound && warcraftPlayer.IsSpentThisRound(index))
        {
            controller?.PrintToChat($"{Prefix} «{ability.Name}» уже применён в этом раунде.");
            return;
        }

        if (warcraftPlayer.IsOnCooldown(index, out var secondsLeft))
        {
            controller?.PrintToChat($"{Prefix} «{ability.Name}» перезарядится через {ChatColors.Gold}{secondsLeft:0.0}{ChatColors.Default} с.");
            return;
        }

        var used = kind == AbilityKind.Ultimate
            ? race.OnActivateUltimate(warcraftPlayer)
            : race.OnActivateAbility(warcraftPlayer);

        if (!used) return;

        warcraftPlayer.StartCooldown(index, ability.Cooldown);
        if (oncePerRound) warcraftPlayer.MarkSpentThisRound(index);
    }
}
