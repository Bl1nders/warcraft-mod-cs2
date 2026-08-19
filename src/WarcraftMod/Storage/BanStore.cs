using System.Text.Json;

namespace WarcraftMod.Storage;

/// <summary>Запись о бане. Ключ — SteamID64 забаненного.</summary>
public sealed class BanRecord
{
    /// <summary>Ник на момент бана — чтобы файл можно было читать глазами.</summary>
    public string Name { get; set; } = "";

    /// <summary>Unix-время окончания бана. 0 — навсегда.</summary>
    public long UntilUnix { get; set; }

    /// <summary>Кто выдал.</summary>
    public string By { get; set; } = "";
}

/// <summary>
/// Баны в отдельном файле рядом с плагином. В отличие от прогресса пишется сразу при
/// изменении, а не пачкой по таймеру: банов мало, зато потерять их при падении сервера
/// нельзя — забаненный вернётся в ту же минуту.
/// </summary>
public sealed class BanStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<ulong, BanRecord> _bans = new();
    private readonly Lock _gate = new();

    public BanStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<ulong, BanRecord>>(File.ReadAllText(_path));
            if (loaded is null) return;

            lock (_gate)
            {
                foreach (var (steamId, record) in loaded) _bans[steamId] = record;
            }
        }
        catch (Exception ex)
        {
            // Битый список банов не должен ронять плагин: хуже пустого бан-листа
            // только сервер, который вообще не поднялся.
            Console.WriteLine($"[WarcraftMod] Не удалось прочитать {_path}: {ex.Message}");
        }
    }

    /// <summary>Действует ли бан прямо сейчас. Истёкший снимается тут же и больше не мешает.</summary>
    public bool IsBanned(ulong steamId, out BanRecord? record)
    {
        lock (_gate)
        {
            if (!_bans.TryGetValue(steamId, out record)) return false;

            if (record.UntilUnix != 0 && record.UntilUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                _bans.Remove(steamId);
                record = null;
                Save();
                return false;
            }

            return true;
        }
    }

    public void Add(ulong steamId, BanRecord record)
    {
        lock (_gate)
        {
            _bans[steamId] = record;
            Save();
        }
    }

    public bool Remove(ulong steamId)
    {
        lock (_gate)
        {
            if (!_bans.Remove(steamId)) return false;

            Save();
            return true;
        }
    }

    public int Count
    {
        get { lock (_gate) return _bans.Count; }
    }

    /// <summary>
    /// Снимок действующих банов. Истёкшие не отдаём: они уже никого не держат,
    /// и показывать их в списке для снятия — только путать.
    /// </summary>
    public IReadOnlyList<(ulong SteamId, BanRecord Record)> Active()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_gate)
        {
            return _bans
                .Where(pair => pair.Value.UntilUnix == 0 || pair.Value.UntilUnix > now)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();
        }
    }

    /// <summary>Вызывается только под уже взятым замком.</summary>
    private void Save()
    {
        try
        {
            // Пишем во временный файл и подменяем — обрыв записи не оставит битый бан-лист.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_bans, SerializerOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось сохранить {_path}: {ex.Message}");
        }
    }
}
