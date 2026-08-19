using System.Drawing;
using System.Globalization;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace WarcraftMod.Core;

/// <summary>
/// Готовые "кирпичики" для способностей: лечение, отбрасывание, невидимость,
/// поиск целей. Расы должны пользоваться этими помощниками, а не лезть в схему напрямую.
/// </summary>
public static class Effects
{
    /// <summary>
    /// Вылечить игрока, не превышая его собственный потолок здоровья.
    /// Потолок берём у цели, а не у лекаря: у рас он разный — 50 у Коротышки,
    /// 150 у Бигфута, — и чужая цифра либо перелечила бы, либо недолечила.
    /// Возвращает, сколько HP реально добавили.
    /// </summary>
    public static int Heal(CCSPlayerPawn pawn, int amount)
    {
        if (amount <= 0 || pawn.Health <= 0) return 0;

        var maxHealth = pawn.MaxHealth > 0 ? pawn.MaxHealth : 100;
        var healed = Math.Min(amount, maxHealth - pawn.Health);
        if (healed <= 0) return 0;

        pawn.Health += healed;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        return healed;
    }

    /// <summary>Выставить здоровье и потолок здоровья (используется при спавне).</summary>
    public static void SetHealth(CCSPlayerPawn pawn, int health, int maxHealth)
    {
        pawn.MaxHealth = maxHealth;
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    /// <summary>1.0 — обычная гравитация, меньше — выше прыжок.</summary>
    public static void SetGravity(CCSPlayerPawn pawn, float scale) => pawn.GravityScale = scale;

    /// <summary>Выдать броню (не больше 100 — потолок движка).</summary>
    public static void SetArmor(CCSPlayerPawn pawn, int armor)
    {
        pawn.ArmorValue = Math.Clamp(armor, 0, 100);
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
    }

    /// <summary>
    /// Прозрачность модели: 255 — видно полностью, 0 — полностью невидим.
    /// Принимает любую модель, а не только игрока: оружие в руках — отдельная сущность,
    /// и его приходится прятать вторым вызовом.
    /// </summary>
    public static void SetRenderAlpha(CBaseModelEntity entity, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        entity.Render = Color.FromArgb(alpha, 255, 255, 255);
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_clrRender");
    }

    /// <summary>
    /// Убрать модель из отрисовки целиком или вернуть обратно.
    /// Оружию в руках одной прозрачности мало — движок продолжает его рисовать,
    /// поэтому вместе с альфой переключаем и режим отрисовки.
    /// </summary>
    public static void SetRenderVisible(CBaseModelEntity entity, bool visible)
    {
        entity.RenderMode = visible ? RenderMode_t.kRenderNormal : RenderMode_t.kRenderNone;
        Utilities.SetStateChanged(entity, "CBaseModelEntity", "m_nRenderMode");

        SetRenderAlpha(entity, visible ? 255 : 0);
    }

    /// <summary>
    /// Спрятать всё оружие игрока и вернуть спрятанное — по этому списку видимость и возвращают.
    /// Невидимость без этого не работает: убранные нож, пистолет и гранаты висят на теле
    /// собственными моделями, а активный ствол остаётся в руках.
    /// </summary>
    public static List<CBasePlayerWeapon> HideWeapons(CCSPlayerPawn pawn)
    {
        var hidden = new List<CBasePlayerWeapon>();
        if (pawn.WeaponServices is not { } services) return hidden;

        foreach (var handle in services.MyWeapons)
        {
            if (handle.Value is not { IsValid: true } weapon) continue;

            SetRenderVisible(weapon, false);
            hidden.Add(weapon);
        }

        return hidden;
    }

    /// <summary>
    /// Вернуть видимость спрятанному оружию. Возвращаем именно тому, что прятали:
    /// после смерти оно падает на пол, и невидимый ствол так и остался бы там лежать.
    /// </summary>
    public static void ShowWeapons(IEnumerable<CBasePlayerWeapon> weapons)
    {
        foreach (var weapon in weapons)
            if (weapon.IsValid) SetRenderVisible(weapon, true);
    }

    /// <summary>
    /// Стоит ли игрок на земле.
    /// Проверка идёт по хэндлу GroundEntity — на практике именно она работает.
    /// Вариант через флаг FL_ONGROUND был опробован и ломал прыжок в воздухе.
    /// </summary>
    public static bool IsOnGround(CCSPlayerPawn pawn)
    {
        try
        {
            return pawn.GroundEntity.Value is not null;
        }
        catch
        {
            // Невалидный хэндл означает, что опоры под ногами нет.
            return false;
        }
    }

    /// <summary>Ослепить игрока на заданное время (как от светошумовой).</summary>
    public static void Blind(CCSPlayerPawn pawn, float duration)
    {
        pawn.FlashDuration = duration;
        pawn.FlashMaxAlpha = 255f;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawnBase", "m_flFlashDuration");
    }

    /// <summary>
    /// Засветить игрока на радаре — родная система «обнаружен» из CS2.
    /// Пометка держится, пока её обновляют: движок сбрасывает её сам.
    /// </summary>
    public static void MarkSpotted(CCSPlayerPawn pawn, bool spotted)
    {
        var state = pawn.EntitySpottedState;
        state.Spotted = spotted;

        // Маска решает, кому видно. Все биты — видно всем, ноль — никому.
        for (var i = 0; i < state.SpottedByMask.Length; i++)
            state.SpottedByMask[i] = spotted ? uint.MaxValue : 0u;

        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
    }

    /// <summary>
    /// Засветить игрока только для перечисленных слотов — остальным он не виден.
    ///
    /// Отличается от <see cref="MarkSpotted"/> тем, что тот выставляет маску целиком
    /// и показывает цель всем подряд, включая её же союзников. Для меток вида «свои
    /// увидели чужого» нужна именно адресная маска, иначе способность работает и на
    /// противника тоже.
    ///
    /// Пометку движок сбрасывает сам, поэтому её надо подновлять примерно раз в 0.2 с,
    /// пока она должна держаться.
    /// </summary>
    public static void MarkSpottedFor(CCSPlayerPawn pawn, IReadOnlyCollection<int> viewerSlots)
    {
        var state = pawn.EntitySpottedState;
        state.Spotted = viewerSlots.Count > 0;

        for (var i = 0; i < state.SpottedByMask.Length; i++) state.SpottedByMask[i] = 0u;

        foreach (var slot in viewerSlots)
        {
            var word = slot / 32;
            if (word < 0 || word >= state.SpottedByMask.Length) continue;

            state.SpottedByMask[word] |= 1u << (slot % 32);
        }

        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
    }

    /// <summary>
    /// Видит ли игрок из слота <paramref name="viewerSlot"/> эту цель прямо сейчас.
    /// Спрашиваем у родной системы обнаружения CS2 — той самой, что рисует врагов
    /// на радаре: она считается по прямой видимости, поэтому цель за стеной в маске
    /// не появляется. Трассировку луча плагинам не выдают, это ближайшая замена.
    /// </summary>
    public static bool IsSpottedBy(CCSPlayerPawn targetPawn, int viewerSlot)
    {
        try
        {
            var mask = targetPawn.EntitySpottedState.SpottedByMask;

            var word = viewerSlot / 32;
            if (word < 0 || word >= mask.Length) return false;

            return (mask[word] & (1u << (viewerSlot % 32))) != 0;
        }
        catch
        {
            // Состояние недоступно — не мешаем способности сработать.
            return true;
        }
    }

    /// <summary>Обездвижить или вернуть управление.</summary>
    public static void SetFrozen(CCSPlayerPawn pawn, bool frozen)
    {
        var moveType = frozen ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    /// <summary>
    /// Запретить стрельбу на заданное число секунд.
    /// Решает не поле игрока, а тики следующей атаки у оружия в руках — запрет ставится
    /// на них. Запрет с конечным сроком выбран сознательно: при смене оружия отложенный
    /// ствол разблокируется сам, а не остаётся немым до конца раунда.
    /// Смена оружия сбрасывает запрет, поэтому длинный подновляют таймером.
    /// </summary>
    public static void BlockAttack(CCSPlayerPawn pawn, float seconds)
    {
        if (pawn.WeaponServices is not { } services) return;

        new CCSPlayer_WeaponServices(services.Handle).NextAttack = Server.CurrentTime + seconds;

        if (services.ActiveWeapon.Value is not { IsValid: true } weapon) return;

        var untilTick = Server.TickCount + (int)(seconds / Server.TickInterval) + 4;
        weapon.NextPrimaryAttackTick = untilTick;
        weapon.NextSecondaryAttackTick = untilTick;
        Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
        Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
    }

    /// <summary>
    /// Ножи в CS2 называются по-разному (bayonet, karambit и прочие), но все они
    /// либо содержат knife, либо bayonet.
    /// </summary>
    public static bool HoldsKnife(CCSPlayerPawn pawn)
    {
        var active = pawn.WeaponServices?.ActiveWeapon.Value;
        if (active is not { IsValid: true } || active.DesignerName is not { Length: > 0 } name) return false;

        return name.Contains("knife", StringComparison.OrdinalIgnoreCase)
               || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Запрет стрельбы, из которого нож вынут: с ножом в руках не пишем ничего.
    ///
    /// Вынимать нож нулём нельзя, хотя так и просится. <see cref="BlockAttack"/> не
    /// снимает запрет, а назначает время следующей атаки — и ноль назначает её на
    /// «сейчас», то есть стирает ножу его собственную задержку между взмахами. Оба
    /// поля, и левый удар, и правый. Подновление тиком давало учетверённый темп удара:
    /// так ломались Орк и Коротышка, обоим нож в запрете полагался рабочим.
    ///
    /// Отсюда второе правило, важнее первого: **нулём отсюда не зовут вовсе**. Писать
    /// надо остаток до конца способности — тогда запрет истекает вместе с ней, и снимать
    /// его руками не нужно ни по сроку, ни по смерти, ни по спавну. Остаток, доставшийся
    /// от прежнего ствола, снимет смена оружия.
    ///
    /// Снятие нулём выглядит безобидно ровно до того места, где оно случается на спавне:
    /// оружия в руках движок ещё не выдал, нож распознать не по чему, и ноль ложится
    /// поверх времени доставания — начало раунда достаётся игроку даром.
    /// </summary>
    public static void BlockGuns(CCSPlayerPawn pawn, float seconds)
    {
        if (HoldsKnife(pawn)) return;

        BlockAttack(pawn, seconds);
    }

    /// <summary>Пропускать сквозь стены (фазовый сдвиг).</summary>
    public static void SetNoclip(CCSPlayerPawn pawn, bool enabled)
    {
        var moveType = enabled ? MoveType_t.MOVETYPE_NOCLIP : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    /// <summary>Высота глаз стоящего игрока — к ней возвращаем обзор после телепорта.</summary>
    private const float StandingViewHeight = 64f;

    /// <summary>
    /// Вернуть игрока в нормальную стойку. После телепорта состояние приседа иногда
    /// остаётся полусогнутым: габариты у игрока стоячие, а камера сидит ниже, из-за чего
    /// она заглядывает внутрь стен. Если игрок и правда приседает, движок вернёт присед сам.
    /// </summary>
    public static void ResetStance(CCSPlayerPawn pawn)
    {
        try
        {
            if (pawn.MovementServices is { } services)
            {
                var movement = new CCSPlayer_MovementServices(services.Handle);
                movement.DuckAmount = 0f;
                movement.Ducked = false;
                movement.Ducking = false;
            }

            pawn.ViewOffset.Z = StandingViewHeight;
            Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_vecViewOffset");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось вернуть стойку: {ex.Message}");
        }
    }

    /// <summary>
    /// Переставить игрока в точку и выпрямить его. По горизонтали направление взгляда
    /// сохраняется, наклон и крен обнуляются.
    ///
    /// Разница с <see cref="TeleportTo"/> не косметическая: угол, отданный в Teleport,
    /// становится углом самой пешки, а не только взгляда. У смотрящего под ноги там
    /// pitch под девяносто, и после переноса модель лежит горизонтально. Пригодно всюду,
    /// где игрока переставляют не по его воле и наклон головы сохранять незачем.
    /// </summary>
    public static void TeleportUpright(CCSPlayerPawn pawn, Vector3 destination)
    {
        var position = new CounterStrikeSharp.API.Modules.Utils.Vector(destination.X, destination.Y, destination.Z);
        var angles = new QAngle(0f, pawn.V_angle.Y, 0f);
        pawn.Teleport(position, angles, new CounterStrikeSharp.API.Modules.Utils.Vector(0f, 0f, 0f));
    }

    /// <summary>Переставить игрока в точку, сохранив направление взгляда.</summary>
    public static void TeleportTo(CCSPlayerPawn pawn, Vector3 destination)
    {
        var position = new CounterStrikeSharp.API.Modules.Utils.Vector(destination.X, destination.Y, destination.Z);
        var angles = new QAngle(pawn.V_angle.X, pawn.V_angle.Y, pawn.V_angle.Z);
        pawn.Teleport(position, angles, new CounterStrikeSharp.API.Modules.Utils.Vector(0f, 0f, 0f));
    }

    /// <summary>Путь к модели, которую движок использует для игрока прямо сейчас.</summary>
    public static string? CurrentModelOf(CCSPlayerPawn pawn)
    {
        try
        {
            return pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState?.ModelName;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось прочитать модель: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Размер модели игрока: 1.0 — обычный. Масштабируется скелет, поэтому вместе
    /// с моделью растёт и то, по чему в неё попадают.
    /// </summary>
    public static void SetModelScale(CCSPlayerPawn pawn, float scale)
    {
        try
        {
            // Уже нужного размера — не трогаем. Проверка не для скорости: ниже мигает
            // sv_cheats, и делать это на каждом спавне каждой обычной расы незачем.
            if (Math.Abs(ModelScaleOf(pawn) - scale) < 0.01f) return;

            // Размер ставится движковой командой, а не записью полей, и вот почему.
            // Записать поля мод умеет — 18.08.2026 замерено, что после записи в схеме
            // стоит ровно то же самое, что после ent_scale: 1.5 и в m_flScale, и в
            // m_flAbsScale. Но растягивается при этом только картинка: тело, по которому
            // движок считает попадания, остаётся прежним, и пули проходят мимо груди.
            // Пробовали толкать телепортом и пересаживать модель заново — не помогает
            // ни то, ни другое. Движковая команда растягивает и хитбоксы, проверено
            // стрельбой по боту.
            //
            // Цена — читовый флаг: ent_scale помечена читовой, поэтому читы включаются
            // и гасятся вокруг неё же. Три команды уходят в один буфер и выполняются
            // подряд, окна для клиента между ними нет. Прежнее состояние флага
            // восстанавливается: на сервере разработки читы могут быть включены
            // намеренно, и гасить их за хозяином мод не вправе.
            var cheatsWereOn = ConVar.Find("sv_cheats")?.GetPrimitiveValue<bool>() ?? false;
            var value = scale.ToString(CultureInfo.InvariantCulture);

            if (!cheatsWereOn) Server.ExecuteCommand("sv_cheats 1");
            Server.ExecuteCommand($"ent_scale {value} {pawn.Index}");
            if (!cheatsWereOn) Server.ExecuteCommand("sv_cheats 0");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось изменить размер модели: {ex.Message}");
        }
    }

    /// <summary>
    /// Во сколько раз растянута модель. Единица — обычный размер.
    ///
    /// Читается у скелета, а не хранится отдельно: масштаб ставит раса на спавне, и
    /// второй записи о нём в моде быть не должно — разойдутся.
    /// </summary>
    public static float ModelScaleOf(CBaseEntity entity)
    {
        try
        {
            var scale = entity.CBodyComponent?.SceneNode?.GetSkeletonInstance().Scale ?? 1f;
            return scale > 0f ? scale : 1f;
        }
        catch
        {
            // Скелет ещё не готов — считаем размер обычным.
            return 1f;
        }
    }

    /// <summary>Сменить модель игрока. Модель должна быть предзагружена при старте карты.</summary>
    public static void SetModel(CCSPlayerPawn pawn, string modelPath)
    {
        try
        {
            pawn.SetModel(modelPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось сменить модель на '{modelPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Скорость, от которой считается потолок замедления в воздухе. Примерно столько
    /// бежит обычный игрок с оружием: настоящий потолок движка нам не виден, а привязка
    /// к постоянному числу даёт предсказуемое «замедлен вдвое — вдвое медленнее бегущего».
    /// </summary>
    private const float AirSlowReferenceSpeed = 250f;

    /// <summary>
    /// Какую долю разницы съедаем за один заход. Мгновенный срез с 450 до 125 читался бы
    /// как удар о стену и был бы вдобавок крупной поправкой к предсказанию клиента;
    /// постепенное сведение ощущается вязкостью, ради которой замедления и делались.
    /// </summary>
    private const float AirSlowRate = 0.35f;

    /// <summary>
    /// Довести замедление до цели, которая сейчас в воздухе.
    ///
    /// Нужно потому, что <c>VelocityModifier</c> движок применяет только на земле:
    /// в воздухе горизонтальную скорость считают <c>sv_airaccelerate</c> и воздушный
    /// предел, а потолок скорости там не значит ничего. Замерено 17.08.2026 на разгоне
    /// Кролика: множитель дорастал до 1.3 и честно лежал на пешке, а скорость не
    /// двигалась с 250 вовсе.
    ///
    /// Разгонов это не касается — на земле игрок ускоряется, а в воздухе Source
    /// сохраняет набранное, и оно едет с ним. А вот замедления без этого не работали
    /// совсем: достаточно было прыгать, чтобы Оцепенение, Ловушка охотника и Провокация
    /// прошли мимо. Поэтому здесь подрезается сама скорость, а не потолок.
    /// </summary>
    public static void EnforceAirSlow(CCSPlayerPawn pawn, float multiplier)
    {
        if (multiplier >= 1f) return;

        var velocity = pawn.AbsVelocity;
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);

        var target = AirSlowReferenceSpeed * multiplier;
        if (speed <= target) return;

        var slowed = MathF.Max(target, speed - (speed - target) * AirSlowRate);
        var scale = slowed / speed;

        // Обе оси одним множителем: замедляем, не отклоняя от курса.
        pawn.AbsVelocity.X = velocity.X * scale;
        pawn.AbsVelocity.Y = velocity.Y * scale;
    }

    /// <summary>Толкнуть игрока в заданном направлении (отбрасывание, рывок).</summary>
    public static void Push(CCSPlayerPawn pawn, Vector3 direction, float force)
    {
        if (direction.LengthSquared() < 0.0001f) return;

        var impulse = Vector3.Normalize(direction) * force;
        pawn.AbsVelocity.X += impulse.X;
        pawn.AbsVelocity.Y += impulse.Y;
        pawn.AbsVelocity.Z += impulse.Z;
    }

    /// <summary>
    /// Позиция глаз игрока. null означает, что модель ещё не в мире — способность,
    /// которой нужны координаты, должна в этом случае просто не сработать.
    /// </summary>
    public static Vector3? EyePosition(CCSPlayerPawn pawn)
    {
        if (pawn.AbsOrigin is not { } origin) return null;

        var offset = pawn.ViewOffset;
        return new Vector3(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
    }

    /// <summary>Позиция ног игрока, или null если модель не в мире.</summary>
    public static Vector3? Origin(CCSPlayerPawn pawn) =>
        pawn.AbsOrigin is { } origin ? new Vector3(origin.X, origin.Y, origin.Z) : null;

    /// <summary>Единичный вектор направления взгляда.</summary>
    public static Vector3 ForwardVector(CCSPlayerPawn pawn)
    {
        var pitch = float.DegreesToRadians(pawn.V_angle.X);
        var yaw = float.DegreesToRadians(pawn.V_angle.Y);
        var cosPitch = MathF.Cos(pitch);

        return new Vector3(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            -MathF.Sin(pitch));
    }

    /// <summary>Живые игроки в радиусе от точки. <paramref name="team"/> = null — любая команда.</summary>
    public static List<CCSPlayerController> PlayersInRadius(Vector3 center, float radius, CsTeam? team = null)
    {
        var result = new List<CCSPlayerController>();
        var radiusSquared = radius * radius;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.PlayerPawn.Value is not { } pawn || !pawn.IsValid) continue;
            if (pawn.Health <= 0) continue;
            if (team is not null && player.Team != team) continue;
            if (Origin(pawn) is not { } position) continue;
            if (Vector3.DistanceSquared(position, center) > radiusSquared) continue;

            result.Add(player);
        }

        return result;
    }

    /// <summary>
    /// Точка на земле, куда смотрит игрок, не дальше <paramref name="maxRange"/>.
    /// Трассировки лучей в API нет, поэтому считаем пересечение взгляда с горизонтальной
    /// плоскостью на уровне ног — на ровном месте это точно, на склонах приблизительно.
    /// null означает, что игрок смотрит недостаточно вниз.
    /// </summary>
    public static Vector3? AimPointOnGround(CCSPlayerPawn pawn, float maxRange)
    {
        // В прыжке уровень ног — это воздух, и «земля» получилась бы висящей в нём.
        if (!IsOnGround(pawn)) return null;

        if (Origin(pawn) is not { } feet || EyePosition(pawn) is not { } eye) return null;

        var forward = ForwardVector(pawn);
        if (forward.Z > -0.15f) return null;

        var distanceToPlane = (feet.Z - eye.Z) / forward.Z;
        var hit = eye + forward * distanceToPlane;

        var offset = new Vector3(hit.X - feet.X, hit.Y - feet.Y, 0f);
        if (offset.Length() > maxRange) offset = Vector3.Normalize(offset) * maxRange;

        return feet + offset;
    }

    /// <summary>
    /// Точка возрождения указанной команды, свободная от игроков.
    /// В начале раунда все точки заняты, и без этой проверки можно оказаться внутри чужой
    /// модели и застрять. Если свободных нет, берётся самая удалённая от людей.
    /// </summary>
    public static Vector3? FreeSpawnPoint(CsTeam team, float clearance = 70f)
    {
        var designerName = team == CsTeam.Terrorist ? "info_player_terrorist" : "info_player_counterterrorist";

        var points = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designerName)
            .Where(spawn => spawn.IsValid && spawn.AbsOrigin is not null)
            .Select(spawn => new Vector3(spawn.AbsOrigin!.X, spawn.AbsOrigin.Y, spawn.AbsOrigin.Z))
            .ToList();

        if (points.Count == 0) return null;

        var occupied = Utilities.GetPlayers()
            .Where(player => player.IsValid && player.PlayerPawn.Value is { IsValid: true } pawn && pawn.Health > 0)
            .Select(player => Origin(player.PlayerPawn.Value!))
            .OfType<Vector3>()
            .ToList();

        if (occupied.Count == 0) return points[Random.Shared.Next(points.Count)];

        float NearestPlayer(Vector3 point) => occupied.Min(body => Vector3.Distance(point, body));

        var free = points.Where(point => NearestPlayer(point) > clearance).ToList();

        return free.Count > 0
            ? free[Random.Shared.Next(free.Count)]
            : points.MaxBy(NearestPlayer);
    }

    /// <summary>
    /// Все точки возрождения на карте. По ним мод считает, где карта кончается:
    /// спавны стоят там, где игра начинается, и от них считается и дно, и края.
    /// </summary>
    public static List<Vector3> SpawnPoints()
    {
        var points = new List<Vector3>();

        foreach (var designerName in new[] { "info_player_terrorist", "info_player_counterterrorist" })
        {
            foreach (var spawn in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(designerName))
            {
                if (!spawn.IsValid || spawn.AbsOrigin is not { } origin) continue;
                points.Add(new Vector3(origin.X, origin.Y, origin.Z));
            }
        }

        return points;
    }

    /// <summary>Все живые игроки указанной команды, без ограничения по расстоянию.</summary>
    public static List<CCSPlayerController> AlivePlayersOfTeam(CsTeam team)
    {
        var result = new List<CCSPlayerController>();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.Team != team) continue;
            if (player.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Health <= 0) continue;

            result.Add(player);
        }

        return result;
    }

    /// <summary>
    /// Найти врага, на которого игрок смотрит: ближайшая к центру экрана цель внутри
    /// конуса взгляда, не дальше <paramref name="maxRange"/> и обязательно замеченная
    /// этим игроком — цель за стеной движок «замеченной» не считает, а значит
    /// и навестись на неё нельзя.
    /// </summary>
    public static CCSPlayerController? FindTargetInAim(
        CCSPlayerController source,
        float maxRange,
        float maxAngleDegrees,
        bool enemiesOnly = true)
    {
        if (source.PlayerPawn.Value is not { IsValid: true } sourcePawn) return null;
        if (EyePosition(sourcePawn) is not { } eye) return null;

        var forward = ForwardVector(sourcePawn);
        var minimumDot = MathF.Cos(float.DegreesToRadians(maxAngleDegrees));

        CCSPlayerController? best = null;
        var bestDot = minimumDot;

        foreach (var candidate in Utilities.GetPlayers())
        {
            if (candidate.Slot == source.Slot) continue;
            if (!candidate.IsValid) continue;
            if (enemiesOnly && candidate.Team == source.Team) continue;
            if (candidate.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Health <= 0) continue;
            if (!IsSpottedBy(pawn, source.Slot)) continue;
            if (EyePosition(pawn) is not { } targetEye) continue;

            var toTarget = targetEye - eye;
            var distance = toTarget.Length();
            if (distance > maxRange || distance < 0.001f) continue;

            var dot = Vector3.Dot(forward, toTarget / distance);
            if (dot <= bestDot) continue;

            bestDot = dot;
            best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Нанести урон с учётом брони — для случаев, когда мы подменяем обычный выстрел.
    /// Модель приближённая: точная формула движка зависит от бронепробития конкретного
    /// оружия, а здесь берётся усреднённая половина. Броня при этом расходуется.
    /// </summary>
    public static void ApplyDamageWithArmor(CCSPlayerPawn pawn, int damage)
    {
        if (damage <= 0 || pawn.Health <= 0) return;

        var armor = pawn.ArmorValue;
        if (armor > 0)
        {
            var toHealth = Math.Max(1, (int)(damage * 0.5f));
            var absorbed = Math.Min(armor, (int)((damage - toHealth) * 0.5f));

            SetArmor(pawn, armor - absorbed);
            damage = toHealth;
        }

        ApplyDirectDamage(pawn, damage);
    }

    /// <summary>Нанести урон игроку напрямую, в обход системы урона движка (для магических способностей).</summary>
    /// <remarks>
    /// Идёт мимо брони и мимо обработчиков урона — именно поэтому способности рас
    /// не могут бесконечно триггерить друг друга.
    /// </remarks>
    public static void ApplyDirectDamage(CCSPlayerPawn pawn, int damage)
    {
        if (damage <= 0 || pawn.Health <= 0) return;

        pawn.Health -= damage;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");

        if (pawn.Health > 0) return;

        // Смерть откладываем на следующий кадр: вызов изнутри обработки урона роняет сервер.
        Server.NextFrame(() =>
        {
            if (pawn.IsValid && pawn.Health <= 0) pawn.CommitSuicide(explode: false, force: true);
        });
    }
}
