namespace WarcraftMod.Core;

/// <summary>Кривая уровней и размеры наград за опыт.</summary>
public static class XpTable
{
    public const int MaxLevel = 16;

    /// <summary>Сколько опыта нужно, чтобы уйти с <paramref name="level"/> на следующий.</summary>
    public static int XpToNextLevel(int level)
    {
        if (level >= MaxLevel) return int.MaxValue;
        // Мягко растущая кривая: 1→2 стоит 80, дальше +45 за уровень.
        return 80 + (level - 1) * 45;
    }

    /// <summary>Сколько очков навыков доступно игроку на данном уровне.</summary>
    /// <remarks>По очку за уровень: на 16 уровне ровно 16 очков = 4 способности по 4 ранга.</remarks>
    public static int SkillPointsAtLevel(int level) => level;

    /// <summary>
    /// Раса целиком, с первого уровня до потолка. Точка отсчёта для разговоров о балансе.
    /// </summary>
    public static int XpForFullRace()
    {
        var total = 0;
        for (var level = 1; level < MaxLevel; level++) total += XpToNextLevel(level);
        return total;
    }

    // ------------------------------------------------------------------
    // Общий уровень
    // ------------------------------------------------------------------

    /// <summary>
    /// Потолок общего уровня. Упереться в него всерьёз нельзя, он стоит только затем,
    /// чтобы подсчёт уровня из опыта не мог зациклиться на битых данных.
    /// </summary>
    public const int AccountMaxLevel = 999;

    private const int AccountXpBase = 60;
    private const int AccountXpStep = 6;

    /// <summary>
    /// Потолок стоимости общего уровня. Без него кривая квадратичная, и на дистанции
    /// в сотни уровней — а именно туда мод и приедет, когда рас станет много —
    /// последние волны стоили бы сотни часов.
    /// </summary>
    private const int AccountXpCap = 300;

    /// <summary>Опыт для перехода с общего уровня <paramref name="level"/> на следующий.</summary>
    public static int AccountXpToNextLevel(int level) =>
        Math.Min(AccountXpBase + Math.Max(0, level - 1) * AccountXpStep, AccountXpCap);

    /// <summary>Общий уровень по накопленному общему опыту. Уровни считаются с единицы.</summary>
    public static int AccountLevelFromXp(long xp)
    {
        var level = 1;
        while (level < AccountMaxLevel)
        {
            var cost = AccountXpToNextLevel(level);
            if (xp < cost) break;

            xp -= cost;
            level++;
        }

        return level;
    }

    /// <summary>Сколько общего опыта соответствует ровно этому уровню.</summary>
    public static long AccountXpForLevel(int level)
    {
        long total = 0;
        for (var i = 1; i < Math.Min(level, AccountMaxLevel); i++) total += AccountXpToNextLevel(i);
        return total;
    }

    /// <summary>Опыт, набранный внутри текущего общего уровня, и сколько нужно до следующего.</summary>
    public static (long Into, int Needed) AccountProgress(long xp)
    {
        var level = AccountLevelFromXp(xp);
        return (xp - AccountXpForLevel(level), AccountXpToNextLevel(level));
    }

    // ------------------------------------------------------------------
    // Ускорение прокачки для тех, кто уже освоил расы
    // ------------------------------------------------------------------

    /// <summary>За каждую доведённую до потолка расу новые качаются быстрее на столько.</summary>
    private const float CatchUpPerMaxedRace = 0.2f;

    /// <summary>Дальше этого ускорение не растёт — иначе раса пролетала бы за пару раундов.</summary>
    private const float CatchUpCap = 5f;

    /// <summary>
    /// Множитель опыта расы. Первая раса — полный путь, двадцатая — короткий: когда рас
    /// в моде десятки, одинаковая цена за каждую превращает знакомство с новой расой
    /// в повинность на несколько дней.
    ///
    /// Ускоряет он только расы. Общий опыт идёт ровным темпом и остаётся честным
    /// мерилом времени — иначе открытия посыпались бы лавиной.
    /// </summary>
    public static float RaceXpMultiplier(int maxedRaces) =>
        Math.Min(1f + Math.Max(0, maxedRaces) * CatchUpPerMaxedRace, CatchUpCap);
}
