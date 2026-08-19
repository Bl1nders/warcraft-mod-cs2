using CounterStrikeSharp.API.Core;

namespace WarcraftMod.Core;

/// <summary>
/// Надписи способностей в центре экрана.
///
/// Отдельный помощник, а не прямой вызов у контроллера, по двум причинам. Во-первых,
/// проверка на живого игрока собрана в одном месте, а не повторяется в двух десятках рас.
/// Во-вторых, это единственная точка, где такие надписи можно будет придержать: они идут
/// не тем каналом, которым рисуется меню, и перебивают его — если понадобится молчать,
/// пока у игрока открыто меню, менять надо будет только здесь.
/// </summary>
public static class CenterText
{
    /// <summary>
    /// Выключатель всех надписей мода в центре экрана. Заведён ради разбора мигания меню:
    /// это единственное, что мод пишет туда помимо самого меню, и проверить его можно
    /// только полным молчанием. Задаётся из конфига полем ShowCenterMessages.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    public static void Print(CCSPlayerController? controller, string text)
    {
        if (!Enabled) return;
        if (controller is not { IsValid: true }) return;

        controller.PrintToCenter(text);
    }
}
