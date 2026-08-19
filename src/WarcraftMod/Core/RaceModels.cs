using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;

namespace WarcraftMod.Core;

/// <summary>Пара моделей расы: одна для контр-террористов, одна для террористов.</summary>
public sealed class RaceModelPair
{
    /// <summary>Модель для контр-террористов. Пустая строка — оставить обычный облик.</summary>
    [JsonPropertyName("CT")]
    public string CounterTerrorist { get; set; } = "";

    /// <summary>Модель для террористов. Пустая строка — оставить обычный облик.</summary>
    [JsonPropertyName("T")]
    public string Terrorist { get; set; } = "";

    public RaceModelPair() { }

    public RaceModelPair(string counterTerrorist, string terrorist)
    {
        CounterTerrorist = counterTerrorist;
        Terrorist = terrorist;
    }
}

/// <summary>
/// Какой моделью выглядит раса. Пути извлечены из pak01_dir.vpk установленной игры,
/// поэтому скачивать игрокам нечего и предзагрузка не нужна — движок грузит агентов сам.
/// </summary>
/// <remarks>
/// <para>
/// Моделей у расы две, и это не украшательство: облик — единственное, по чему в бою
/// отличают своего от чужого. Одна модель на обе команды превратила бы Орка в чужого
/// для собственной команды, поэтому каждой расе подобрана пара — агент CT и агент T,
/// похожие по виду и разные по цвету формы.
/// </para>
/// <para>
/// Фэнтезийных моделей — орков, эльфов, скелетов — в CS2 нет вовсе: все 92 модели
/// игроков это люди в снаряжении. Подобрано ближайшее по смыслу: врачи Альянсу,
/// заросшие джунглями партизаны Ночным Эльфам, самые громоздкие агенты Орде.
/// Настоящие фэнтезийные модели требуют воркшоп-аддона и докачки на входе —
/// когда он появится, менять надо будет только пути, а не код.
/// </para>
/// <para>
/// Оборотня в таблице намеренно нет: вся раса про то, чтобы не выделяться, и своя
/// модель выдавала бы его раньше первого выстрела — ровно как подпись в табло,
/// от которой он уже отказался через <see cref="Race.HiddenInScoreboard"/>.
/// </para>
/// </remarks>
public static class RaceModels
{
    /// <summary>
    /// Облик по умолчанию. Подбирается только глазами, поэтому любую строку можно
    /// перебить из warcraft_config.json, не пересобирая плагин.
    /// </summary>
    private static readonly Dictionary<string, RaceModelPair> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // Альянс Людей — целительная аура: медик Жандармерии и «Доктор» Романов.
        ["human"] = new(
            "agents/models/ctm_gendarmerie/ctm_gendarmerie_varianta.vmdl",
            "agents/models/tm_balkan/tm_balkan_varianth.vmdl"),

        // Нежить — зараза и черепа: биозащита SWAT и Дэррил с черепом вместо лица.
        ["undead"] = new(
            "agents/models/ctm_swat/ctm_swat_varianth.vmdl",
            "agents/models/tm_professional/tm_professional_varf2.vmdl"),

        // Ночные Эльфы — лес: «Обнимающий деревья» Фарлоу и заросший Арно.
        ["nightelf"] = new(
            "agents/models/ctm_swat/ctm_swat_variantk.vmdl",
            "agents/models/tm_jungle_raider/tm_jungle_raider_variantc.vmdl"),

        // Орда Орков — громила: сапёр в защитном костюме и Максимус в тяжёлой броне.
        // Это два самых массивных силуэта в игре — ближе к орку в CS2 ничего нет.
        ["orc"] = new(
            "agents/models/ctm_swat/ctm_swat_varianti.vmdl",
            "agents/models/tm_balkan/tm_balkan_varianti.vmdl"),

        // Кузнечик — прыгун: лёгкое снаряжение авианаводчика и «Рогатка».
        ["grasshopper"] = new(
            "agents/models/ctm_st6/ctm_st6_variantm.vmdl",
            "agents/models/tm_phoenix/tm_phoenix_variantg.vmdl"),

        // Кролик — бег и прыжки: агент без шлема и уличный боец в лёгком.
        ["rabbit"] = new(
            "agents/models/ctm_fbi/ctm_fbi_variantb.vmdl",
            "agents/models/tm_phoenix/tm_phoenix_varianti.vmdl"),

        // Соник — скорость: обтекаемый гидрокостюм и Салли, которая уходит от погони.
        ["sonic"] = new(
            "agents/models/ctm_diver/ctm_diver_variantc.vmdl",
            "agents/models/tm_professional/tm_professional_varj.vmdl"),

        // Разведчик — чутьё и наблюдение: снайпер ФБР и элитный следопыт.
        ["illusionist"] = new(
            "agents/models/ctm_fbi/ctm_fbi_varianth.vmdl",
            "agents/models/tm_jungle_raider/tm_jungle_raider_variantb.vmdl"),

        // Бигфут — дикарь: командир в тяжёлой броне и Крассуотер из джунглей.
        // Раса ещё и растягивает модель в полтора раза, так что важна массивность.
        ["bigfoot"] = new(
            "agents/models/ctm_gendarmerie/ctm_gendarmerie_variantc.vmdl",
            "agents/models/tm_jungle_raider/tm_jungle_raider_varianta.vmdl"),

        // Коротышка — самый мелкий: стажёр Жандармерии и «Малыш Кев».
        ["shorty"] = new(
            "agents/models/ctm_gendarmerie/ctm_gendarmerie_variantd.vmdl",
            "agents/models/tm_professional/tm_professional_varh.vmdl"),

        // Иллюзионист — донатная, и видно это должно быть издалека: жёлтый химзащитный
        // костюм и самый пёстрый костюм в игре.
        ["mirage"] = new(
            "agents/models/ctm_gendarmerie/ctm_gendarmerie_variantb.vmdl",
            "agents/models/tm_professional/tm_professional_varf.vmdl"),

        // Повелитель времени — донатная: безликий боец SAS в противогазе и профессор Шахмат.
        ["chronos"] = new(
            "agents/models/ctm_sas/ctm_sas_variantf.vmdl",
            "agents/models/tm_leet/tm_leet_varianti.vmdl"),
    };

    private static Dictionary<string, RaceModelPair> _table = Defaults;

    /// <summary>Выдавать ли расам их облик вовсе.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>
    /// Наложить настройки поверх таблицы по умолчанию. Зовётся один раз при загрузке плагина.
    /// Ключ — идентификатор расы, пустая строка в паре означает «оставить обычный облик».
    /// </summary>
    public static void Configure(bool enabled, IReadOnlyDictionary<string, RaceModelPair>? overrides)
    {
        Enabled = enabled;

        var merged = new Dictionary<string, RaceModelPair>(Defaults, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
            foreach (var (raceId, pair) in overrides)
                merged[raceId] = pair;

        _table = merged;
    }

    /// <summary>Облик расы для этой команды, или null — если расе положен обычный вид.</summary>
    public static string? For(string raceId, CsTeam team)
    {
        if (!Enabled) return null;
        if (!_table.TryGetValue(raceId, out var pair)) return null;

        var model = team switch
        {
            CsTeam.CounterTerrorist => pair.CounterTerrorist,
            CsTeam.Terrorist => pair.Terrorist,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(model) ? null : model;
    }
}
