using CounterStrikeSharp.API.Modules.Utils;

namespace WarcraftMod.Core;

/// <summary>
/// Модели агентов для маскировки. Пути извлечены из pak01_dir.vpk установленной игры,
/// поэтому дополнительный контент игрокам скачивать не нужно.
/// </summary>
/// <remarks>
/// Префикс именно agents/models — так модель называет сам движок. В архиве есть
/// и characters/models с теми же именами, но с ним SetModel даёт заглушку:
/// оранжевый квадрат вместо персонажа.
/// </remarks>
public static class Disguises
{
    private static readonly string[] CounterTerrorists =
    [
        "agents/models/ctm_fbi/ctm_fbi.vmdl",
        "agents/models/ctm_sas/ctm_sas.vmdl",
        "agents/models/ctm_st6/ctm_st6_variante.vmdl",
        "agents/models/ctm_swat/ctm_swat_variante.vmdl",
    ];

    private static readonly string[] Terrorists =
    [
        "agents/models/tm_leet/tm_leet_varianta.vmdl",
        "agents/models/tm_balkan/tm_balkan_variantf.vmdl",
        "agents/models/tm_jungle_raider/tm_jungle_raider_varianta.vmdl",
        "agents/models/tm_phoenix/tm_phoenix_varianta.vmdl",
    ];

    private static readonly Random Rng = new();

    public static IEnumerable<string> AllModels => CounterTerrorists.Concat(Terrorists);

    /// <summary>Случайная модель команды противника.</summary>
    public static string ForEnemyOf(CsTeam team)
    {
        var pool = team == CsTeam.Terrorist ? CounterTerrorists : Terrorists;
        return pool[Rng.Next(pool.Length)];
    }

    /// <summary>Случайная модель своей команды — чтобы вернуть нормальный вид.</summary>
    public static string ForOwnTeam(CsTeam team)
    {
        var pool = team == CsTeam.Terrorist ? Terrorists : CounterTerrorists;
        return pool[Rng.Next(pool.Length)];
    }
}
