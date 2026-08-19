using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vec = System.Numerics.Vector3;

namespace WarcraftMod.Core;

/// <summary>
/// Строка меню: текст, цвет и относительный размер шрифта. Одна и та же строка годится
/// и надписи в мире, и запасному тексту в центре экрана — поэтому текст здесь чистый,
/// без разметки, а вёрстка держится на неразрывных пробелах.
/// </summary>
public readonly record struct MenuLine(string Text, Color Color, float Scale = 1f);

/// <summary>Размер и место панели. Тюнится в конфиге: подобрать это можно только глазами.</summary>
public sealed record MenuLayout(
    string FontName,
    float FontSize,
    float ShiftRight,
    float ShiftUp,
    bool FollowView,
    bool Background,
    bool Shadow)
{
    /// <summary>Сколько единиц мира от глаз до панели.</summary>
    public const float Distance = 7f;

    /// <summary>Пересчёт размера шрифта в размер мира — так его понимает point_worldtext.</summary>
    public float WorldUnitsPerPx => 0.25f / 1050f * FontSize;

    /// <summary>
    /// Шаг между строками. Считается из размера шрифта, чтобы не заводить ещё одну ручку.
    /// Плотнее кегля примерно на четверть — так список читается блоком, а не россыпью.
    /// </summary>
    public float LineStep => FontSize * WorldUnitsPerPx * 1.28f;

    /// <summary>Насколько тень отступает от буквы. Доля от высоты строки, подобрана на глаз.</summary>
    public float ShadowOffset => LineStep * 0.07f;
}

/// <summary>
/// Панель меню, собранная из сущностей point_worldtext и висящая перед камерой игрока.
///
/// Зачем так, а не текстом в центре экрана. Способов писать в центр три, и все проверены
/// вживую 16.08.2026. Панель Danger Zone (PrintToCenterHtml) понимает разметку и цвета,
/// но в бою белеет примерно раз в секунду, и ни частота отправки, ни длительность показа,
/// ни вес разметки этого не лечат. Два других канала не мигают, но разметки не понимают
/// вовсе и рисуют шрифтом, который не прочитать. Надпись в мире свободна от обоих изъянов:
/// размер, цвет и место задаём мы, а мигать там нечему — это сущность, а не элемент интерфейса.
///
/// Родителя у неё нет. Задумывалось цеплять её к модели рук, как это делают чужие моды,
/// но в этой сборке CS2 её не существует: поля m_pViewModelServices нет в схеме ни под одним
/// именем, а перебор всех сущностей до 32768 не находит ни одной модели рук. Пробовали и
/// модель самого игрока: дрожи там нет, зато надпись идёт за телом, а тело доворачивается
/// за взглядом с задержкой и рывком. Поэтому панель переставляется перед камерой каждый кадр.
///
/// Чужим её не видно: посторонние сущности вырезаются в CheckTransmit.
/// </summary>
public sealed class WorldTextMenuPanel
{
    /// <summary>Цвет разделительных линий — приглушённое золото заголовка.</summary>
    private static readonly Color RuleColor = Color.FromArgb(120, 96, 40);

    /// <summary>Цвет тени. Не чистый чёрный: он даёт слишком жёсткий контур.</summary>
    private static readonly Color ShadowColor = Color.FromArgb(12, 10, 8);

    /// <summary>
    /// Почему панель не собралась. Немой отказ здесь недопустим: снаружи он выглядит как
    /// «меню осталось старым», и причину приходится искать в серверной консоли, которой
    /// на хостинге может не быть под рукой.
    /// </summary>
    public static string LastError { get; private set; } = "причина не записана";

    /// <summary>Всё созданное — по этому списку сущности вырезаются из передачи чужим.</summary>
    private readonly List<CPointWorldText> _entities = [];

    /// <summary>Цветные надписи и их тёмные двойники, по строке в каждом списке.</summary>
    private readonly List<CPointWorldText> _texts = [];

    private readonly List<CPointWorldText> _shadows = [];

    /// <summary>
    /// Левый край и верх списка относительно центра взгляда. Считаются при сборке панели
    /// и при смене текста, а не каждый кадр: от поворота головы они не меняются.
    /// </summary>
    private float _left;

    private float _top;

    public IReadOnlyList<CPointWorldText> Entities => _entities;

    /// <summary>Панель стоит и цела. false — её либо не собирали, либо она уже умерла.</summary>
    public bool IsAlive => _entities.Count > 0 && _entities.TrueForAll(entity => entity is { IsValid: true });

    /// <summary>Собрать панель заново. false — не из чего или игрока нет в мире.</summary>
    public bool Show(CBasePlayerPawn pawn, IReadOnlyList<MenuLine> lines, MenuLayout layout)
    {
        Hide();

        if (lines.Count == 0)
        {
            LastError = "меню пустое";
            return false;
        }

        if (ViewFrame(pawn) is not { } frame)
        {
            LastError = "не удалось узнать точку взгляда: у пешки нет положения в мире";
            return false;
        }

        lines = Decorate(lines);
        Measure(lines, layout);

        for (var i = 0; i < lines.Count; i++)
        {
            var point = LinePosition(frame, layout, i);

            // Тень создаётся первой и стоит чуть дальше от глаз — иначе она перекрыла бы
            // саму надпись, а не легла под неё.
            if (layout.Shadow)
            {
                var shadow = CreateLine(lines[i] with { Color = ShadowColor }, layout,
                    ShadowPosition(frame, layout, point), frame.Angles);

                if (shadow is null) return Failed();

                _shadows.Add(shadow);
                _entities.Add(shadow);
            }

            var text = CreateLine(lines[i], layout, point, frame.Angles);
            if (text is null) return Failed();

            _texts.Add(text);
            _entities.Add(text);
        }

        return true;

        bool Failed()
        {
            Hide();
            return false;
        }
    }

    /// <summary>
    /// Переставить панель перед камерой. Зовётся каждый кадр: родителя у надписи нет,
    /// иначе она осталась бы висеть там, где её создали.
    /// </summary>
    public void Follow(CBasePlayerPawn pawn, MenuLayout layout)
    {
        if (_texts.Count == 0) return;
        if (ViewFrame(pawn) is not { } frame) return;

        for (var i = 0; i < _texts.Count; i++)
        {
            if (_texts[i] is not { IsValid: true } text) return;

            var point = LinePosition(frame, layout, i);
            Place(text, point, frame.Angles);

            if (i < _shadows.Count && _shadows[i] is { IsValid: true } shadow)
                Place(shadow, ShadowPosition(frame, layout, point), frame.Angles);
        }
    }

    /// <summary>
    /// Поменять текст на месте, не пересобирая сущности. false — состав строк изменился
    /// или сущности уже мертвы, надо звать Show.
    /// </summary>
    public bool Update(IReadOnlyList<MenuLine> lines, MenuLayout layout)
    {
        lines = Decorate(lines);

        if (_texts.Count == 0 || _texts.Count != lines.Count) return false;

        for (var i = 0; i < lines.Count; i++)
        {
            if (_texts[i] is not { IsValid: true } text) return false;

            text.MessageText = lines[i].Text;
            text.Color = lines[i].Color;
            Utilities.SetStateChanged(text, "CPointWorldText", "m_messageText");
            Utilities.SetStateChanged(text, "CPointWorldText", "m_Color");

            // Тень повторяет текст, но не цвет — иначе перестанет быть тенью.
            if (i >= _shadows.Count || _shadows[i] is not { IsValid: true } shadow) continue;

            shadow.MessageText = lines[i].Text;
            Utilities.SetStateChanged(shadow, "CPointWorldText", "m_messageText");
        }

        Measure(lines, layout);
        return true;
    }

    public void Hide()
    {
        foreach (var entity in _entities) VisualEffects.RemoveEntity(entity);

        _entities.Clear();
        _texts.Clear();
        _shadows.Clear();
    }

    /// <summary>
    /// Обвести список линиями: одна под заголовком, вторая над подвалом. Длину берём по
    /// самому длинному пункту, чтобы рамка сходилась с текстом при любом меню.
    ///
    /// Украшение живёт здесь, а не в самом меню, нарочно: запасной способ показа рисует
    /// панелью, которая обрезает всё ниже седьмой строки, и две лишние строки съели бы
    /// там подвал с подсказкой клавиш.
    /// </summary>
    private static List<MenuLine> Decorate(IReadOnlyList<MenuLine> lines)
    {
        if (lines.Count < 3) return [.. lines];

        var longest = 0;
        foreach (var line in lines) longest = Math.Max(longest, line.Text.Length);

        var rule = new MenuLine(new string('─', Math.Clamp(longest, 10, 46)), RuleColor, 0.8f);

        var result = new List<MenuLine>(lines.Count + 2) { lines[0], rule };

        for (var i = 1; i < lines.Count - 1; i++) result.Add(lines[i]);

        result.Add(rule);
        result.Add(lines[^1]);

        return result;
    }

    /// <summary>Посчитать, где у списка левый край и верх, чтобы он встал ровно перед игроком.</summary>
    private void Measure(IReadOnlyList<MenuLine> lines, MenuLayout layout)
    {
        var width = 0f;
        foreach (var line in lines) width = MathF.Max(width, LineWidth(line, layout));

        _left = layout.ShiftRight - width / 2f;
        _top = layout.ShiftUp + lines.Count * layout.LineStep / 2f;
    }

    /// <summary>Куда смотрит игрок: точка глаз и тройка направлений по правилам движка.</summary>
    private readonly record struct Frame(Vec Eye, Vec Forward, Vec Right, Vec Up, QAngle Angles);

    /// <summary>
    /// Точка глаз. Своя, а не из Effects: та работает с пешкой живого, а меню рисуется
    /// и мёртвому — у него камера отдельной сущностью, общей у них только основа.
    /// </summary>
    /// <summary>
    /// Где у игрока глаза. Отступ от ног умножается на масштаб модели, и это не
    /// украшение: у растянутой расы камера поднимается вместе с моделью, а
    /// <c>ViewOffset</c> остаётся прежним. Меню висит в семи юнитах перед этой точкой,
    /// поэтому промах даже в треть роста уводит его целиком за край экрана — у Бигфута
    /// меню не было видно вовсе, у Коротышки оно уезжало вверх.
    /// </summary>
    private static Vec? EyePosition(CBasePlayerPawn pawn)
    {
        if (pawn.AbsOrigin is not { } origin) return null;

        var offset = pawn.ViewOffset;
        var scale = Effects.ModelScaleOf(pawn);

        return new Vec(
            origin.X + offset.X * scale,
            origin.Y + offset.Y * scale,
            origin.Z + offset.Z * scale);
    }

    private static Frame? ViewFrame(CBasePlayerPawn pawn)
    {
        if (EyePosition(pawn) is not { } eye) return null;

        var pitch = float.DegreesToRadians(pawn.V_angle.X);
        var yaw = float.DegreesToRadians(pawn.V_angle.Y);

        var sinPitch = MathF.Sin(pitch);
        var cosPitch = MathF.Cos(pitch);
        var sinYaw = MathF.Sin(yaw);
        var cosYaw = MathF.Cos(yaw);

        // Разворот текста лицом к игроку. Числа взяты из работающего кода, а не выведены:
        // point_worldtext рисует надпись в своей плоскости, и только такой поворот ставит
        // её перед камерой не зеркальной и не вверх ногами.
        var angles = new QAngle(pawn.V_angle.X, pawn.V_angle.Y + 270f, 90f);

        return new Frame(
            eye,
            new Vec(cosPitch * cosYaw, cosPitch * sinYaw, -sinPitch),
            new Vec(sinYaw, -cosYaw, 0f),
            new Vec(sinPitch * cosYaw, sinPitch * sinYaw, cosPitch),
            angles);
    }

    private Vec LinePosition(Frame frame, MenuLayout layout, int line) =>
        frame.Eye
        + frame.Forward * MenuLayout.Distance
        + frame.Right * _left
        + frame.Up * (_top - line * layout.LineStep);

    /// <summary>Тень: вправо, вниз и на волос дальше от глаз, чтобы лечь под надпись.</summary>
    private static Vec ShadowPosition(Frame frame, MenuLayout layout, Vec point) =>
        point
        + frame.Right * layout.ShadowOffset
        - frame.Up * layout.ShadowOffset
        + frame.Forward * 0.03f;

    private static void Place(CPointWorldText entity, Vec point, QAngle angles) =>
        entity.Teleport(new Vector(point.X, point.Y, point.Z), angles, null);

    /// <summary>
    /// Ширина строки в единицах мира. Точной её не узнать — шрифт неравноширинный, а мерить
    /// его сервером нечем, — но для выравнивания по центру хватает и приблизительной:
    /// в среднем знак занимает около половины кегля.
    /// </summary>
    private static float LineWidth(MenuLine line, MenuLayout layout)
    {
        var size = layout.FontSize * line.Scale;
        return line.Text.Length * size * 0.5f * (0.25f / 1050f * size);
    }

    private static CPointWorldText? CreateLine(MenuLine line, MenuLayout layout, Vec point, QAngle angles)
    {
        try
        {
            var entity = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
            if (entity is not { IsValid: true })
            {
                LastError = "движок не создал сущность point_worldtext";
                return null;
            }

            var size = layout.FontSize * line.Scale;

            entity.MessageText = line.Text;
            entity.Enabled = true;

            // Без этого надпись красит освещение карты, и на тёмном этаже меню не прочитать.
            entity.Fullbright = true;
            entity.FontName = layout.FontName;
            entity.FontSize = size;
            entity.Color = line.Color;
            entity.WorldUnitsPerPx = 0.25f / 1050f * size;
            entity.DepthOffset = 0f;
            entity.DrawBackground = layout.Background;
            entity.BackgroundBorderWidth = 0.12f;
            entity.BackgroundBorderHeight = 0.14f;
            entity.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT;
            entity.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_TOP;
            entity.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;

            Place(entity, point, angles);
            entity.DispatchSpawn();

            // Подложку подтверждаем и после создания: выставленная только до спавна, она
            // до клиента может не доехать, и тогда строки обрастают тёмными пятнами.
            entity.DrawBackground = layout.Background;
            Utilities.SetStateChanged(entity, "CPointWorldText", "m_bDrawBackground");

            return entity;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[WarcraftMod] Строка меню не создалась: {LastError}");
            return null;
        }
    }
}
