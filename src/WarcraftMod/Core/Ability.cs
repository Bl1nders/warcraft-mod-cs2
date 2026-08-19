namespace WarcraftMod.Core;

/// <summary>Тип способности. Определяет, как игрок её использует.</summary>
public enum AbilityKind
{
    /// <summary>Работает сама, без нажатия клавиш.</summary>
    Passive,

    /// <summary>Активируется командой !ability (обычно с коротким кулдауном).</summary>
    Active,

    /// <summary>Ультимейт, активируется командой !ult. Открывается не с 1 уровня.</summary>
    Ultimate,
}

/// <summary>
/// Описание одной способности расы. Сама логика живёт в классе расы —
/// здесь только метаданные для меню, кулдаунов и правил прокачки.
/// </summary>
public sealed class Ability
{
    public required string Name { get; init; }

    /// <summary>Текст в меню прокачки. {0} подставляется силой на текущем ранге.</summary>
    public required string Description { get; init; }

    public AbilityKind Kind { get; init; } = AbilityKind.Passive;

    /// <summary>Сколько очков можно вложить. По умолчанию 4, как в классическом WC3-моде.</summary>
    public int MaxRank { get; init; } = 4;

    /// <summary>Уровень игрока, начиная с которого можно вложить первое очко.</summary>
    public int RequiredLevel { get; init; } = 1;

    /// <summary>Перезарядка в секундах. Игнорируется для пассивок.</summary>
    public float Cooldown { get; init; } = 30f;

    /// <summary>
    /// Одно применение за раунд. Правило мода для ультимейтов, поэтому по умолчанию true;
    /// у остальных способностей не проверяется вовсе. Ставьте false тому ультимейту,
    /// которому осознанно разрешено срабатывать несколько раз за раунд.
    /// </summary>
    public bool OncePerRound { get; init; } = true;
}
