using System.Reflection;

namespace WarcraftMod.Core;

/// <summary>
/// Находит все расы в сборке и раздаёт их по идентификатору.
/// Новая раса подхватывается сама — достаточно создать класс-наследник <see cref="Race"/>.
/// </summary>
public sealed class RaceRegistry
{
    private readonly Dictionary<string, Race> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Race> _ordered = [];

    public IReadOnlyList<Race> All => _ordered;

    public void DiscoverAndBind(WarcraftPlugin plugin)
    {
        _byId.Clear();
        _ordered.Clear();

        var raceTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Race)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name);

        foreach (var type in raceTypes)
        {
            if (Activator.CreateInstance(type) is not Race race) continue;

            if (_byId.ContainsKey(race.Id))
            {
                Console.WriteLine($"[WarcraftMod] Раса '{type.Name}' пропущена: идентификатор '{race.Id}' уже занят.");
                continue;
            }

            race.Bind(plugin);
            _byId[race.Id] = race;
            _ordered.Add(race);
        }
    }

    public Race? Find(string id) => _byId.GetValueOrDefault(id);

    public Race? Default => _ordered.FirstOrDefault();
}
