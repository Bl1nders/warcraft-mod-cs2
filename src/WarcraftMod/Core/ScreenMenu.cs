using System.Drawing;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace WarcraftMod.Core;

public sealed class ScreenMenuOption
{
    public required string Text { get; init; }
    public Action<WarcraftPlayer>? OnSelect { get; init; }
    public bool Disabled { get; init; }

    /// <summary>Подпись справа: ранг, уровень, стоимость. Может быть пустой.</summary>
    public string Hint { get; init; } = "";

    /// <summary>Пункт возврата. Рядом с ним показываем R — клавишу, делающую то же самое.</summary>
    public bool IsBack { get; init; }

    /// <summary>
    /// Свой цвет строки. Нужен страницам, собранным из недоступных пунктов: серый цвет
    /// «недоступного» там означал бы не запрет, а просто плохо читаемый текст.
    /// </summary>
    public Color? Color { get; init; }
}

/// <summary>
/// Меню, которое рисуется перед игроком и управляется клавишами движения.
/// Игроку ничего биндить не нужно — W, S, E и R сервер видит сам.
/// </summary>
public sealed class ScreenMenu
{
    public required string Title { get; init; }
    public List<ScreenMenuOption> Options { get; } = [];
    public int Cursor { get; set; }

    /// <summary>
    /// Сколько пунктов помещается на экране за раз. Остальные прокручиваются.
    ///
    /// Раньше здесь стояла пятёрка — от панели в центре экрана, которая обрезала всё ниже
    /// седьмой строки. Надписи в мире такого предела не знают, и окно расширено. Помнить
    /// про обрезку всё же надо: запасной способ показа рисует той самой панелью.
    ///
    /// Девять — это ещё и предел управления цифрами: выбор по номеру идёт через css_wc1..9,
    /// десятой клавиши у этого способа нет. Расширять окно дальше — значит делать нижние
    /// строки недоступными мёртвому, у которого номера единственный путь.
    /// </summary>
    public int PageSize { get; init; } = 9;

    /// <summary>Индекс первого видимого пункта — окно едет за курсором.</summary>
    private int _windowStart;

    /// <summary>Готовые строки и курсор, при котором они собраны.</summary>
    private List<MenuLine> _lines = [];

    private int _linesFor = -1;

    /// <summary>Мёртвому подвал другой — значит и готовые строки другие.</summary>
    private bool _linesForDead;

    /// <summary>Готовая разметка для центра экрана и курсор, при котором она собрана.</summary>
    private string _html = "";

    private int _htmlFor = -1;

    private bool _htmlForDead;

    /// <summary>То же самое, но без разметки.</summary>
    private string _plain = "";

    private int _plainFor = -1;

    private bool _plainForDead;

    /// <summary>Меню, куда вернуться по R. null — закрыть совсем.</summary>
    public Func<WarcraftPlayer, ScreenMenu?>? Parent { get; init; }

    // Цвета меню. Держим их здесь, а не в разметке: панель в мире красит строку целиком,
    // и один и тот же цвет должен доставаться обоим способам рисования.
    private static readonly Color TitleColor = Color.FromArgb(255, 196, 77);
    private static readonly Color SelectedColor = Color.FromArgb(255, 255, 255);
    private static readonly Color NormalColor = Color.FromArgb(176, 176, 176);
    private static readonly Color DisabledColor = Color.FromArgb(96, 96, 96);
    private static readonly Color FooterColor = Color.FromArgb(112, 112, 112);

    /// <summary>Читаемый текст страницы — светлее обычного пункта, его ведь не выбирают.</summary>
    public static Color ReadableColor { get; } = Color.FromArgb(214, 214, 214);

    /// <summary>Заголовок внутри страницы: название способности над её описанием.</summary>
    public static Color AccentColor { get; } = Color.FromArgb(255, 196, 77);

    /// <summary>Неразрывный пробел: годится и HTML, и надписи в мире, в отличие от &amp;nbsp;.</summary>
    private const string Space = " ";

    public ScreenMenu Add(
        string text,
        Action<WarcraftPlayer>? onSelect = null,
        bool disabled = false,
        string hint = "",
        Color? color = null)
    {
        Options.Add(new ScreenMenuOption { Text = text, OnSelect = onSelect, Disabled = disabled, Hint = hint, Color = color });
        Invalidate();
        return this;
    }

    /// <summary>Пункт возврата или закрытия — тот, что дублирует клавишу R.</summary>
    public ScreenMenu AddBack(string text = "Назад", Action<WarcraftPlayer>? onSelect = null)
    {
        Options.Add(new ScreenMenuOption { Text = text, OnSelect = onSelect, IsBack = true });
        Invalidate();
        return this;
    }

    /// <summary>
    /// Забыть готовые строки. Вызывать после любой правки пунктов снаружи — иначе
    /// на экране останется прежний список.
    /// </summary>
    public void Invalidate()
    {
        _linesFor = -1;
        _htmlFor = -1;
        _plainFor = -1;
    }

    /// <summary>Сдвинуть курсор, пропуская недоступные пункты.</summary>
    public void MoveCursor(int direction)
    {
        if (Options.Count == 0) return;

        for (var step = 0; step < Options.Count; step++)
        {
            Cursor = (Cursor + direction + Options.Count) % Options.Count;
            if (!Options[Cursor].Disabled) return;
        }
    }

    /// <summary>
    /// Индекс пункта по его номеру на экране (1 — верхняя видимая строка). -1, если
    /// такой строки сейчас нет. Нужно для биндов на цифры: они работают там, где клавиши
    /// движения до меню не доходят — например, у мёртвого или зрителя.
    /// </summary>
    public int VisibleIndexOf(int number)
    {
        if (number < 1 || number > Math.Max(1, PageSize)) return -1;

        var index = _windowStart + number - 1;
        return index < Options.Count ? index : -1;
    }

    /// <summary>
    /// Пролистать список на страницу вперёд, с конца — обратно к началу. Нужно тому,
    /// кто не может двигать курсор клавишами: мёртвому и зрителю.
    /// </summary>
    public void PageDown()
    {
        var pageSize = Math.Max(1, PageSize);
        if (Options.Count <= pageSize) return;

        // Последняя страница почти всегда неполная, и просто прибавлять размер окна нельзя:
        // шаг перелетал за конец списка, окно сбрасывалось в начало, и нижние пункты
        // становились недостижимыми вовсе. Поэтому упираемся в конец, а с него — по кругу.
        var last = Options.Count - pageSize;
        var next = _windowStart + pageSize;

        _windowStart = next <= last ? next : _windowStart >= last ? 0 : last;

        // Курсор ведём за окном: он же задаёт окно при следующей сборке строк, и без этого
        // список тут же уехал бы обратно к прежнему пункту.
        Cursor = _windowStart;
        if (Options[Cursor].Disabled) MoveCursor(1);

        Invalidate();
    }

    /// <summary>Поставить курсор на первый доступный пункт.</summary>
    public void ResetCursor()
    {
        Cursor = 0;
        _windowStart = 0;
        if (Options.Count > 0 && Options[Cursor].Disabled) MoveCursor(1);
    }

    /// <summary>
    /// Строки меню сверху вниз: заголовок, видимые пункты, подвал.
    ///
    /// Про скрытые пункты говорит счётчик в заголовке, а не строки «выше/ниже»: каждая такая
    /// строка съедала место под пункт. Заодно высота меню постоянна — список не прыгает,
    /// когда окно едет за курсором.
    /// </summary>
    public IReadOnlyList<MenuLine> Lines(bool dead = false)
    {
        if (_linesFor == Cursor && _linesForDead == dead && _lines.Count > 0) return _lines;

        var pageSize = Math.Max(1, PageSize);
        var scrolls = Options.Count > pageSize;

        if (scrolls)
        {
            // Двигаем окно ровно настолько, чтобы курсор оставался видимым.
            if (Cursor < _windowStart) _windowStart = Cursor;
            else if (Cursor >= _windowStart + pageSize) _windowStart = Cursor - pageSize + 1;

            _windowStart = Math.Clamp(_windowStart, 0, Options.Count - pageSize);
        }
        else
        {
            _windowStart = 0;
        }

        var lines = new List<MenuLine>(pageSize + 2);

        var counter = scrolls ? $"{Space}{Cursor + 1}/{Options.Count}" : "";
        lines.Add(new MenuLine($"{Title}{counter}", TitleColor, 1.15f));

        var end = Math.Min(_windowStart + pageSize, Options.Count);
        for (var i = _windowStart; i < end; i++)
        {
            var option = Options[i];
            var selected = i == Cursor;

            // Выбранный пункт — белый со стрелкой, недоступный — серый, обычный — приглушённый.
            var color = option.Color ?? (option.Disabled ? DisabledColor : selected ? SelectedColor : NormalColor);

            // Номер строки на экране. Он же аргумент для «!wc N» — единственного способа
            // выбрать пункт, когда клавиши до сервера не доходят: мёртвому и зрителю их
            // забирает камера наблюдателя, проверено замером 16.08.2026.
            //
            // Недоступной строке номер не пишем: нажатие по нему всё равно ничего не сделает,
            // а на страницах описания, собранных из таких строк, номера были бы шумом.
            var number = option.Disabled ? "" : $"{i - _windowStart + 1}.{Space}";
            var marker = selected ? "▸ " : $"{Space}{Space}";
            // Подпись отделяем двойным пробелом: вплотную к названию она читается его хвостом.
            var hint = option.Hint.Length > 0 ? $"{Space}{Space}{option.Hint}" : "";

            // Клавишу показываем у самого пункта: подвал внизу читают не все, а нажать надо здесь.
            // У возврата это R, и он остаётся на месте даже под курсором: E там тоже сработает,
            // но менять подсказку на неё — значит прятать R ровно тогда, когда на пункт смотрят.
            var key = option.IsBack ? $"{Space}[R]"
                : selected && !option.Disabled ? $"{Space}[E]"
                : "";

            lines.Add(new MenuLine($"{marker}{number}{option.Text}{hint}{key}", color));
        }

        // Подвал в тех же скобках, что и подсказки у пунктов, — чтобы связь читалась сразу.
        // В главном меню возвращаться некуда, R его закрывает — так и пишем: пункт «Закрыть»
        // лежит в самом низу списка и на глаза попадается редко, а подвал виден всегда.
        // Строка перелистывания — только тому, кто не может двигать курсор клавишами,
        // и только когда листать есть куда. Живому она не нужна: у него W и S.
        if (dead && scrolls)
            lines.Add(new MenuLine($"{Space}{Space}0.{Space}Дальше ▾", NormalColor));

        var back = Parent is null ? "закрыть" : "назад";

        // Мёртвому подсказываем то, что у него работает. Клавиши ему не доходят вовсе —
        // их забирает камера наблюдателя, — а команды доходят всегда, поэтому выбор
        // остаётся по номеру строки: бинды на цифры или !wc1 прямо в чат.
        lines.Add(dead
            ? new MenuLine($"!wc 1{Space}выбор{Space}·{Space}!wc 0{Space}дальше{Space}·{Space}!wcc{Space}закрыть", FooterColor, 0.85f)
            : new MenuLine($"[W/S] выбор{Space}[E] принять{Space}[R] {back}", FooterColor, 0.85f));

        _lines = lines;
        _linesFor = Cursor;
        _linesForDead = dead;
        return _lines;
    }

    /// <summary>
    /// Те же строки разметкой для центра экрана. Запасной способ: он уходит клиенту каждый
    /// тик и от этого мигает, поэтому разметка здесь скупая — на строку по одному тегу.
    /// </summary>
    public string RenderHtml(bool dead = false)
    {
        var lines = Lines(dead);

        // Разметка уходит клиенту десятки раз в секунду, а меняется только по нажатию.
        // Одна и та же ссылка на строку заодно служит признаком «содержимое не менялось».
        if (_htmlFor == Cursor && _htmlForDead == dead && _html.Length > 0) return _html;

        var sb = new StringBuilder();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var size = line.Scale > 1f ? "class='fontSize-l' " : line.Scale < 1f ? "class='fontSize-s' " : "";
            var color = $"#{line.Color.R:x2}{line.Color.G:x2}{line.Color.B:x2}";
            var end = i < lines.Count - 1 ? "<br>" : "";

            sb.Append($"<font {size}color='{color}'>{line.Text}</font>{end}");
        }

        _html = sb.ToString();
        _htmlFor = Cursor;
        _htmlForDead = dead;
        return _html;
    }

    /// <summary>
    /// Те же строки без всякой разметки. Для способов показа, которые идут не панелью,
    /// а прямой командой движка: цветов там нет, но и белеть нечему.
    /// </summary>
    public string RenderPlain(bool dead = false)
    {
        var lines = Lines(dead);

        if (_plainFor == Cursor && _plainForDead == dead && _plain.Length > 0) return _plain;

        _plain = string.Join('\n', lines.Select(line => line.Text));
        _plainFor = Cursor;
        _plainForDead = dead;
        return _plain;
    }
}

/// <summary>
/// Чем писать меню в центр экрана. Это три разных механизма, а не три вида одного:
/// панель — игровое событие, остальные два — прямая команда движка.
/// </summary>
public enum CenterStyle
{
    /// <summary>Панель возрождения Danger Zone. Понимает разметку, но в бою белеет.</summary>
    Panel = 0,

    /// <summary>Обычный текст в центре экрана.</summary>
    Plain = 1,

    /// <summary>Он же, другим стилем.</summary>
    Alert = 2
}

/// <summary>
/// Держит открытые меню и разбирает нажатия.
/// Пока меню открыто, игрок заморожен — иначе W и S двигали бы его вместе с курсором.
/// </summary>
public sealed class ScreenMenuManager
{
    private sealed class OpenMenu
    {
        public required ScreenMenu Menu { get; set; }
        public MoveType_t RestoreMoveType { get; init; }

        /// <summary>Панель в мире. Пустая, пока меню рисуется запасным способом.</summary>
        public WorldTextMenuPanel Panel { get; } = new();

        /// <summary>
        /// Привязаться к модели рук не вышло (или так велит конфиг, или игрок мёртв) —
        /// рисуем текстом в центре экрана каждый тик, как раньше.
        /// </summary>
        public bool InCenterScreen { get; set; }

        /// <summary>Когда можно пробовать собрать сорванную панель снова.</summary>
        public float NextRebuildAt { get; set; }


        /// <summary>Сколько раз панель уже пересобирали. Ограничитель против вечного цикла.</summary>
        public int Rebuilds { get; set; }

        /// <summary>Про откат на запасной способ уже сказали — второй раз не повторяем.</summary>
        public bool FallbackReported { get; set; }
    }

    /// <summary>Сколько раз пробуем поднять сорванную панель, прежде чем уйти в центр экрана.</summary>
    private const int RebuildLimit = 3;

    private readonly Dictionary<int, OpenMenu> _open = new();
    private readonly MenuLayout _layout;
    private readonly bool _useWorldText;
    private readonly CenterStyle _centerStyle;
    private readonly bool _centerMarkup;

    public ScreenMenuManager(MenuLayout layout, bool useWorldText, CenterStyle centerStyle, bool centerMarkup)
    {
        _layout = layout;
        _useWorldText = useWorldText;
        _centerStyle = centerStyle;
        _centerMarkup = centerMarkup;
    }

    public bool IsOpen(int slot) => _open.ContainsKey(slot);

    public void Open(WarcraftPlayer warcraftPlayer, ScreenMenu menu)
    {
        menu.ResetCursor();

        if (_open.TryGetValue(warcraftPlayer.Slot, out var existing))
        {
            // Переход между разделами: заморозку не трогаем, меняем только содержимое.
            existing.Menu = menu;
            Draw(warcraftPlayer.Slot, ViewPawnOf(warcraftPlayer), existing);
            return;
        }

        var restore = MoveType_t.MOVETYPE_WALK;
        if (warcraftPlayer.Pawn is { } pawn && pawn.Health > 0)
        {
            // Чужую заморозку (например, от ложной смерти) не запоминаем: её снимет свой
            // таймер, а меню, вернув её при закрытии, оставило бы игрока обездвиженным навсегда.
            restore = pawn.ActualMoveType == MoveType_t.MOVETYPE_NONE ? MoveType_t.MOVETYPE_WALK : pawn.ActualMoveType;
            Freeze(pawn, true);
        }

        var open = new OpenMenu { Menu = menu, RestoreMoveType = restore };
        _open[warcraftPlayer.Slot] = open;
        Draw(warcraftPlayer.Slot, ViewPawnOf(warcraftPlayer), open);
    }

    public void Close(WarcraftPlayer warcraftPlayer)
    {
        if (!_open.Remove(warcraftPlayer.Slot, out var open)) return;

        open.Panel.Hide();

        if (warcraftPlayer.Pawn is { } pawn && pawn.Health > 0)
        {
            pawn.MoveType = open.RestoreMoveType;
            pawn.ActualMoveType = open.RestoreMoveType;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        }

        // Стирать надпись нужно только за собой: панель в мире исчезает вместе с сущностями.
        // И стирать обязательно тем же каналом, которым рисовали: чужой канал чужую надпись
        // не трогает, а оставшийся на экране список выглядит как «меню не закрывается».
        if (open.InCenterScreen) ClearCenter(warcraftPlayer.Controller);
    }

    /// <summary>Закрыть без возврата движения — когда pawn уже недоступен (спавн, выход).</summary>
    public void ForceForget(int slot)
    {
        if (_open.Remove(slot, out var open)) open.Panel.Hide();
    }

    private static void Freeze(CCSPlayerPawn pawn, bool frozen)
    {
        var moveType = frozen ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    /// <summary>
    /// Показать текущее состояние меню. Панель в мире меняется только здесь — по нажатию,
    /// а не по таймеру: в этом вся разница с прежним способом.
    /// </summary>
    private void Draw(int slot, CBasePlayerPawn? pawn, OpenMenu open)
    {
        if (open.InCenterScreen) return;

        if (!_useWorldText)
        {
            open.InCenterScreen = true;
            ReportFallback(slot, open, "выключено в конфиге, ключ MenuInWorldText");
            return;
        }

        var lines = open.Menu.Lines(IsDead(pawn));

        // Строк столько же — меняем текст на месте, сущности пересобирать незачем.
        if (open.Panel.Update(lines, _layout)) return;

        if (pawn is not null && open.Panel.Show(pawn, lines, _layout)) return;

        open.InCenterScreen = true;
        ReportFallback(slot, open, WorldTextMenuPanel.LastError);
    }

    /// <summary>
    /// Сказать игроку, что меню рисуется запасным способом, и почему.
    ///
    /// Пишем в его собственную консоль, а не в серверную: на хостинге серверная консоль
    /// не всегда под рукой, а клиентская открывается клавишей ~ у каждого. Молча
    /// откатываться нельзя — снаружи это выглядит как «обновление не приехало».
    /// </summary>
    private static void ReportFallback(int slot, OpenMenu open, string reason)
    {
        if (open.FallbackReported) return;
        open.FallbackReported = true;

        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } controller) return;

        controller.PrintToConsole($"[WarcraftMod] Меню в мире не создалось: {reason}");
        controller.PrintToChat($"{WarcraftPlugin.Prefix} Меню рисуется запасным способом. Причина — в вашей консоли, клавиша ~");
    }

    /// <summary>
    /// Чья камера сейчас у игрока: своя пешка, пока он жив, и наблюдательная после смерти.
    /// Меню рисуется перед ней в обоих случаях — мёртвому оно нужно даже больше, ему как раз
    /// есть время посмотреть расы. Клавиши до него доходят: проверено замером 16.08.2026,
    /// мёртвый в команде шлёт и W с S, и E с R. Не доходят они только из команды зрителей.
    /// </summary>
    /// <summary>Камера принадлежит мёртвому — значит клавиши до мода не дойдут.</summary>
    private static bool IsDead(CBasePlayerPawn? pawn) => pawn is not CCSPlayerPawn { Health: > 0 };

    private static CBasePlayerPawn? ViewPawnOf(WarcraftPlayer warcraftPlayer) =>
        warcraftPlayer.Controller is { IsValid: true } controller ? ViewPawn(controller) : null;

    private static CBasePlayerPawn? ViewPawn(CCSPlayerController controller)
    {
        if (controller.PlayerPawn.Value is { IsValid: true, Health: > 0 } alive) return alive;

        return controller.ObserverPawn.Value is { IsValid: true } observer ? observer : null;
    }

    /// <summary>
    /// Перерисовать меню, оставшиеся в центре экрана. Панель в мире сюда не попадает:
    /// она уходит клиенту один раз и дальше живёт сама.
    /// </summary>
    public void RenderAll()
    {
        if (_open.Count == 0) return;

        foreach (var (slot, open) in _open)
        {
            var controller = Utilities.GetPlayerFromSlot(slot);
            if (controller is not { IsValid: true }) continue;

            var pawn = ViewPawn(controller);

            if (open.InCenterScreen)
            {
                SendToCenter(controller, open, IsDead(pawn));
                continue;
            }

            if (open.Panel.IsAlive)
            {
                // Родителя у надписи нет, поэтому за взглядом её ведём сами.
                if (_layout.FollowView && pawn is not null) open.Panel.Follow(pawn, _layout);

                continue;
            }

            // Сорвало вместе с моделью рук. Пробуем поднять, но не чаще раза в секунду
            // и не бесконечно: замкнувшийся круг «создали — умерло» плодил бы сущности
            // шестьдесят раз в секунду, а игрок так и сидел бы замороженным перед пустотой.
            if (Server.CurrentTime < open.NextRebuildAt) continue;

            open.NextRebuildAt = Server.CurrentTime + 1f;

            if (++open.Rebuilds > RebuildLimit) open.InCenterScreen = true;
            else Draw(slot, pawn, open);
        }
    }

    /// <summary>
    /// Отправить меню надписью в центр экрана — прежний способ.
    ///
    /// Панель, которой это рисуется, на самом деле счётчик обратного отсчёта: ей передают,
    /// сколько секунд показывать, и она сама перерисовывается каждую секунду. Похоже, именно
    /// эта перерисовка и белеет. Поэтому длительность и частота отправки вынесены в конфиг:
    /// подобрать их можно только глазами, а каждая проверка иначе стоила бы пересборки.
    /// </summary>
    private void SendToCenter(CCSPlayerController controller, OpenMenu open, bool dead)
    {
        // Каждый тик, и реже нельзя: любой из этих способов держится ровно до следующего
        // кадра. Подбор частоты и длительности проверен и на побеление панели не влияет,
        // так что крутить тут больше нечего.
        var text = _centerMarkup ? open.Menu.RenderHtml(dead) : open.Menu.RenderPlain(dead);

        switch (_centerStyle)
        {
            case CenterStyle.Plain: controller.PrintToCenter(text); break;
            case CenterStyle.Alert: controller.PrintToCenterAlert(text); break;
            default: controller.PrintToCenterHtml(text); break;
        }
    }

    /// <summary>
    /// Убрать чужие панели из того, что уходит игроку. Надпись в мире — обычная сущность,
    /// и без этого меню одного игрока висело бы посреди карты на виду у всех.
    /// </summary>
    public void HideForeignPanels(CCheckTransmitInfo info, int viewerSlot)
    {
        foreach (var (slot, open) in _open)
        {
            if (slot == viewerSlot) continue;

            foreach (var entity in open.Panel.Entities)
                if (entity is { IsValid: true })
                    info.TransmitEntities.Remove(entity);
        }
    }

    /// <summary>Есть ли хоть одна панель в мире — чтобы не ходить по игрокам зря каждый кадр.</summary>
    public bool HasWorldPanels
    {
        get
        {
            foreach (var open in _open.Values)
                if (open.Panel.Entities.Count > 0) return true;

            return false;
        }
    }

    /// <summary>Стереть надпись меню тем же каналом, которым она рисовалась.</summary>
    private void ClearCenter(CCSPlayerController? controller)
    {
        if (controller is not { IsValid: true }) return;

        switch (_centerStyle)
        {
            case CenterStyle.Plain: controller.PrintToCenter(" "); break;
            case CenterStyle.Alert: controller.PrintToCenterAlert(" "); break;
            default: controller.PrintToCenterHtml(" "); break;
        }
    }

    /// <summary>Разобрать нажатие. Возвращает true, если кнопку забрало меню.</summary>
    public bool HandleButtons(WarcraftPlayer warcraftPlayer, PlayerButtons pressed)
    {
        if (!_open.TryGetValue(warcraftPlayer.Slot, out var open)) return false;

        var menu = open.Menu;

        if (pressed.HasFlag(PlayerButtons.Forward)) { menu.MoveCursor(-1); Draw(warcraftPlayer.Slot, ViewPawnOf(warcraftPlayer), open); return true; }
        if (pressed.HasFlag(PlayerButtons.Back)) { menu.MoveCursor(1); Draw(warcraftPlayer.Slot, ViewPawnOf(warcraftPlayer), open); return true; }

        if (pressed.HasFlag(PlayerButtons.Reload))
        {
            var parent = menu.Parent?.Invoke(warcraftPlayer);

            if (parent is not null)
            {
                Open(warcraftPlayer, parent);
                return true;
            }

            // Пустой ответ означает две разные вещи, и путать их нельзя. Родителя может
            // не быть вовсе — тогда R закрывает меню, и это верно для главной страницы.
            // Но построитель родителя может и сам открыть его, вернув пустоту: так устроены
            // все возвраты в моде. Отличаем по тому, сменилось ли активное меню, — иначе
            // R из любого подраздела закрывал бы только что открытый родительский список.
            if (_open.TryGetValue(warcraftPlayer.Slot, out var current) && !ReferenceEquals(current.Menu, menu))
                return true;

            Close(warcraftPlayer);
            return true;
        }

        if (!pressed.HasFlag(PlayerButtons.Use)) return false;

        Activate(warcraftPlayer, menu);
        return true;
    }

    /// <summary>
    /// Выбрать пункт по его номеру на экране. Второй путь управления, не зависящий от
    /// клавиш движения: у мёртвого и зрителя W и S забирает камера наблюдателя.
    /// Возвращает false, если меню не открыто или такой строки на экране нет.
    /// </summary>
    public bool SelectByNumber(WarcraftPlayer warcraftPlayer, int number)
    {
        if (!_open.TryGetValue(warcraftPlayer.Slot, out var open)) return false;

        // Ноль — не пункт, а перелистывание: единственный способ добраться до нижней части
        // длинного списка тому, кому не доходят клавиши.
        if (number == 0)
        {
            open.Menu.PageDown();
            Draw(warcraftPlayer.Slot, ViewPawnOf(warcraftPlayer), open);
            return true;
        }

        var index = open.Menu.VisibleIndexOf(number);
        if (index < 0) return false;

        open.Menu.Cursor = index;
        Activate(warcraftPlayer, open.Menu);
        return true;
    }

    /// <summary>Принять текущий пункт. Общий путь для E и для биндов на цифры.</summary>
    private void Activate(WarcraftPlayer warcraftPlayer, ScreenMenu menu)
    {
        if (menu.Cursor < 0 || menu.Cursor >= menu.Options.Count) return;

        var option = menu.Options[menu.Cursor];
        if (option.Disabled) return;

        // Обработчик может открыть новое меню — поэтому закрываем до вызова,
        // а не после, иначе свежее меню тут же схлопнется.
        Close(warcraftPlayer);
        option.OnSelect?.Invoke(warcraftPlayer);
    }
}
