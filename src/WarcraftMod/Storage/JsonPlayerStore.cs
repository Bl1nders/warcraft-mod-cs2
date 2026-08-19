using System.Text.Json;

namespace WarcraftMod.Storage;

/// <summary>
/// Хранилище прогресса в одном JSON-файле. Всё держится в памяти, на диск пишется
/// пачкой в фоне — чтобы обращения к диску никогда не блокировали игровой поток.
/// </summary>
public sealed class JsonPlayerStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<ulong, PlayerRecord> _records = new();
    private readonly Lock _gate = new();
    private bool _dirty;

    public JsonPlayerStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<ulong, PlayerRecord>>(json);
            if (loaded is null) return;

            lock (_gate)
            {
                foreach (var (steamId, record) in loaded) _records[steamId] = record;
            }

            // Файл прочитался целиком — откладываем копию. Делаем это только после
            // успешного разбора: копировать битый файл поверх хорошей копии значит
            // потерять последнюю точку возврата ровно тогда, когда она нужна.
            TryBackup();
        }
        catch (Exception ex)
        {
            // Битый файл не должен ронять плагин — начинаем с чистого состояния,
            // а испорченный файл откладываем в сторону, чтобы его можно было изучить.
            Console.WriteLine($"[WarcraftMod] Не удалось прочитать {_path}: {ex.Message}");
            TryQuarantineCorruptFile();
        }
    }

    /// <summary>
    /// Копия прогресса на момент запуска сервера. Худшее, что можно потерять при порче
    /// файла, — одна сессия, а не вся история сервера.
    /// </summary>
    private void TryBackup()
    {
        try
        {
            File.Copy(_path, $"{_path}.backup", overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось сделать копию {_path}: {ex.Message}");
        }
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            File.Move(_path, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось отложить битый файл: {ex.Message}");
        }
    }

    public PlayerRecord Get(ulong steamId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(steamId, out var record))
            {
                record = new PlayerRecord();
                _records[steamId] = record;
            }

            return record;
        }
    }

    /// <summary>Снимок всех записей — для сводной статистики по серверу.</summary>
    public IReadOnlyList<(ulong SteamId, PlayerRecord Record)> All()
    {
        lock (_gate) return _records.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    /// <summary>Пометить, что данные изменились и их нужно сбросить на диск.</summary>
    public void MarkDirty()
    {
        lock (_gate) _dirty = true;
    }

    /// <summary>Записать на диск, если с прошлого раза что-то менялось.</summary>
    public void FlushIfDirty()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_records, SerializerOptions);
            _dirty = false;
        }

        try
        {
            // Пишем во временный файл и подменяем: обрыв записи не убьёт прогресс всего сервера.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось сохранить {_path}: {ex.Message}");
            lock (_gate) _dirty = true; // попробуем ещё раз на следующем тике сохранения
        }
    }
}
