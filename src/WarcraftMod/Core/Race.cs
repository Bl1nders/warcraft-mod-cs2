using CounterStrikeSharp.API.Core;

namespace WarcraftMod.Core;

/// <summary>
/// Базовый класс расы. Чтобы добавить свою расу, унаследуйтесь от него,
/// опишите способности в <see cref="Abilities"/> и переопределите нужные обработчики.
/// Реестр находит все наследники автоматически — регистрировать вручную не нужно.
/// </summary>
public abstract class Race
{
    /// <summary>Короткий стабильный идентификатор — по нему сохраняется прогресс. Не меняйте после релиза.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>
    /// Способности расы. Порядок важен: по индексу хранятся ранги в сейве.
    /// Дописывайте новые способности в конец, иначе прогресс игроков перепутается.
    /// </summary>
    public abstract IReadOnlyList<Ability> Abilities { get; }

    /// <summary>
    /// Какой общий уровень нужен, чтобы раса открылась. Общий уровень — сумма уровней
    /// по всем расам игрока. Ноль означает стартовую расу, доступную сразу.
    /// </summary>
    public virtual int UnlockTotalLevel => 0;

    /// <summary>
    /// Раса выдаётся лично, а не открывается прокачкой. Уровень для неё не имеет значения:
    /// доступ раздаёт владелец сервера командой и может его отобрать.
    /// </summary>
    public virtual bool DonorOnly => false;

    /// <summary>
    /// Прятать расу из табло. Нужно тем, чья игра держится на скрытности: подпись в списке
    /// игроков выдала бы Оборотня раньше, чем он успел бы кого-то обмануть.
    /// </summary>
    public virtual bool HiddenInScoreboard => false;

    /// <summary>
    /// Во сколько раз раса крупнее обычного игрока. 1 — обычный размер.
    ///
    /// Объявляется здесь, а не ставится расой на спавне, потому что размер обязан
    /// назначаться ровно один раз: движковый ent_scale выполняется из очереди команд,
    /// и два вызова подряд — сброс и установка — читают друг у друга ещё не обновлённое
    /// значение. Плагин надевает этот множитель сам, после облика расы.
    ///
    /// Увеличивать выше 1.2 нельзя без новой проверки стрельбой: хитбоксы следуют за
    /// масштабом не полностью, и на 1.5 у растянутой модели выпадает голова. Сжатие
    /// работает честно — проверено вплоть до 0.5.
    /// </summary>
    public virtual float BodyScale => 1f;

    protected WarcraftPlugin Plugin { get; private set; } = null!;

    internal void Bind(WarcraftPlugin plugin) => Plugin = plugin;

    /// <summary>Индекс активной способности, или -1 если у расы её нет.</summary>
    public int ActiveIndex => IndexOfKind(AbilityKind.Active);

    /// <summary>Индекс ультимейта, или -1 если у расы его нет.</summary>
    public int UltimateIndex => IndexOfKind(AbilityKind.Ultimate);

    private int IndexOfKind(AbilityKind kind)
    {
        for (var i = 0; i < Abilities.Count; i++)
            if (Abilities[i].Kind == kind) return i;

        return -1;
    }

    // --- Обработчики событий. Переопределяйте только те, что нужны расе. ---

    /// <summary>Игрок возродился. Здесь выдают стартовые бонусы: HP, броню, скорость, гравитацию.</summary>
    public virtual void OnSpawn(WarcraftPlayer player) { }

    /// <summary>Игрок погиб.</summary>
    public virtual void OnDeath(WarcraftPlayer player) { }

    /// <summary>Игрок кого-то убил.</summary>
    public virtual void OnKill(WarcraftPlayer killer, CCSPlayerController victim, EventPlayerDeath ev) { }

    /// <summary>
    /// Игрок наносит урон — вызывается ДО применения. Можно менять <c>info.Damage</c>
    /// (крит, доп. урон) и лечить атакующего (вампиризм).
    /// </summary>
    public virtual void OnDealDamage(WarcraftPlayer attacker, CCSPlayerController victim, CTakeDamageInfo info) { }

    /// <summary>
    /// По игроку наносят урон — вызывается ДО применения. Можно уменьшить или обнулить
    /// <c>info.Damage</c> (уклонение, блок, снижение урона).
    /// </summary>
    public virtual void OnTakeDamage(WarcraftPlayer victim, CCSPlayerController? attacker, CTakeDamageInfo info) { }

    /// <summary>Игрок ввёл !ability. Вернуть true, если способность сработала (тогда уйдёт в кулдаун).</summary>
    public virtual bool OnActivateAbility(WarcraftPlayer player) => false;

    /// <summary>Игрок ввёл !ult. Вернуть true, если ульта сработала (тогда уйдёт в кулдаун).</summary>
    public virtual bool OnActivateUltimate(WarcraftPlayer player) => false;

    /// <summary>Начало раунда — до спавна.</summary>
    public virtual void OnRoundStart(WarcraftPlayer player) { }

    /// <summary>
    /// Игрок с включённым банни-хопом только что подпрыгнул сам.
    /// Здесь набирают разгон: обычный прыжок скорости не добавляет.
    /// </summary>
    public virtual void OnAutoHop(WarcraftPlayer player) { }
}
