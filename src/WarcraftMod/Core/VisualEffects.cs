using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
// В обоих пространствах имён есть Vector: у движка — сетевой объект, у System.Numerics — структура.
// Математику ведём в Vector3, а в движок отдаём engine-версию, поэтому фиксируем алиас явно.
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace WarcraftMod.Core;

/// <summary>
/// Частицы, звуки и тряска экрана для способностей.
/// Всё обёрнуто в try/catch: неверное имя эффекта или неудачное создание сущности
/// не должно ронять сервер посреди раунда — способность просто отработает беззвучно.
/// </summary>
public static class VisualEffects
{
    /// <summary>
    /// Пути к частицам, извлечённые из pak01_dir.vpk установленной игры — не выдуманные.
    /// Указываем исходный .vpcf, движок сам подставит скомпилированный .vpcf_c.
    /// </summary>
    public static class Fx
    {
        public const string FireBurst = "particles/inferno_fx/explosion_molotov_air_core.vpcf";
        public const string FireMedium = "particles/burning_fx/env_fire_medium.vpcf";
        public const string BurningBody = "particles/burning_fx/burning_character.vpcf";
        public const string Embers = "particles/burning_fx/env_embers_medium.vpcf";

        public const string ExplosionBig = "particles/explosions_fx/explosion_c4_500.vpcf";
        public const string GroundBlast = "particles/explosions_fx/explosion_c4_500_groundbase.vpcf";
        public const string Ring = "particles/explosions_fx/bumpmine_detonate_ring.vpcf";

        public const string BloodHeavy = "particles/blood_impact/blood_impact_heavy.vpcf";
        public const string BloodSpray = "particles/blood_impact/blood_impact_high_vis_spray.vpcf";

        public const string SmokeBurst = "particles/explosions_fx/explosion_smokegrenade_init.vpcf";
        public const string Sparks = "particles/ambient_fx/ambient_sparks_glow.vpcf";

        /// <summary>
        /// Всё, что нужно объявить движку при загрузке карты.
        /// Без предзагрузки EffectName молча игнорируется и частиц не видно вообще.
        /// </summary>
        public static readonly string[] All =
        [
            FireBurst, FireMedium, BurningBody, Embers,
            ExplosionBig, GroundBlast, Ring,
            BloodHeavy, BloodSpray,
            SmokeBurst, Sparks,
        ];
    }

    /// <summary>
    /// Модели, которые мод ставит в мир сам. Их, как и частицы, надо объявлять движку
    /// при загрузке карты, иначе предмет создастся, но останется невидимым.
    /// </summary>
    /// <remarks>
    /// Модели агентов сюда добавлять нельзя: сервер падал с access violation сразу после
    /// загрузки карты. Их игра грузит сама, объявлять не требуется.
    /// </remarks>
    /// <remarks>
    /// Модели оружия сюда не годятся: жёстким предметом они не отображаются вовсе —
    /// проверено и на гранате-обманке, и на мировой модели АК. Берём предметные модели.
    /// </remarks>
    public static class Models
    {
        /// <summary>Полоса шипов — метка Ловушки охотника у Ночных Эльфов.</summary>
        public const string Spikes = "models/generic/bird_spikes_01/bird_spikes_01_a.vmdl";

        /// <summary>Металлическая скоба — мелкая железка на полу.</summary>
        public const string Clamp = "models/generic/crate_plastic_01/crate_plastic_01_clamp_01.vmdl";

        /// <summary>Замок от ящика — самое маленькое, что нашлось.</summary>
        public const string Padlock = "models/generic/crate_plastic_01/crate_plastic_01_lock_01.vmdl";

        /// <summary>Коробка с кабелями. Отложена под будущую способность «мина».</summary>
        public const string CableBox = "models/generic/cable_kit_01/ck01_cablebox_01_a.vmdl";

        /// <summary>Небольшой ящик с боеприпасами — оружейный ящик Альянса.</summary>
        public const string AmmoBoxSmall = "models/generic/ammo_box_01/ammo_box_01_small.vmdl";

        public static readonly string[] All = [Spikes, Clamp, Padlock, CableBox, AmmoBoxSmall];
    }

    private static Vector ToEngine(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Создать эффект частиц в точке мира и убрать его через <paramref name="lifetime"/> секунд.
    /// </summary>
    public static void SpawnParticle(
        BasePlugin plugin,
        string effectPath,
        Vector3 position,
        float lifetime = 3f,
        Color? tint = null)
    {
        if (string.IsNullOrWhiteSpace(effectPath)) return;

        try
        {
            var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
            if (particle is null || !particle.IsValid) return;

            particle.EffectName = effectPath;
            particle.StartActive = true;
            if (tint is { } color) particle.Tint = color;

            particle.Teleport(ToEngine(position), QAngle.Zero, Vector.Zero);
            particle.DispatchSpawn();

            // Сущности частиц не исчезают сами. Remove() на них не срабатывал — эффекты
            // висели до конца раунда, — поэтому сначала гасим вход Stop, затем Kill.
            plugin.AddTimer(lifetime, () =>
            {
                try
                {
                    if (!particle.IsValid) return;

                    particle.AcceptInput("Stop", particle, particle, "", 0);
                    particle.AcceptInput("Kill", particle, particle, "", 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WarcraftMod] Не удалось убрать частицы: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Частицы '{effectPath}' не создались: {ex.Message}");
        }
    }

    /// <summary>Эффект в точке игрока, приподнятый над землёй на <paramref name="heightOffset"/>.</summary>
    public static void SpawnParticleAt(
        BasePlugin plugin,
        string effectPath,
        CCSPlayerPawn pawn,
        float heightOffset = 40f,
        float lifetime = 3f,
        Color? tint = null)
    {
        if (Effects.Origin(pawn) is not { } origin) return;

        SpawnParticle(plugin, effectPath, origin with { Z = origin.Z + heightOffset }, lifetime, tint);
    }

    /// <summary>
    /// Поставить в мире неподвижный предмет — метку способности на земле.
    /// Возвращает его, чтобы вызывающий мог убрать метку, когда она отслужит.
    /// </summary>
    public static CDynamicProp? SpawnProp(BasePlugin plugin, string model, Vector3 position, float yaw = 0f)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        try
        {
            var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
            if (prop is null || !prop.IsValid) return null;

            prop.SetModel(model);
            prop.Teleport(ToEngine(position), new QAngle(0f, yaw, 0f), Vector.Zero);
            prop.DispatchSpawn();

            // Сквозь метку проходят насквозь: она обозначает место, а не перекрывает его.
            prop.Collision.SolidType = SolidType_t.SOLID_NONE;
            Utilities.SetStateChanged(prop, "CCollisionProperty", "m_nSolidType");

            return prop;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Предмет '{model}' не создался: {ex.Message}");
            return null;
        }
    }

    /// <summary>Убрать сущность. Remove() на созданных нами объектах не срабатывал — только вход Kill.</summary>
    public static void RemoveEntity(CBaseEntity? entity)
    {
        try
        {
            if (entity is not { IsValid: true }) return;

            entity.AcceptInput("Kill", entity, entity, "", 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Не удалось убрать сущность: {ex.Message}");
        }
    }

    /// <summary>Проиграть звук от сущности — слышен всем, с привязкой к позиции источника.</summary>
    public static void PlaySound(CBaseEntity source, string soundEvent, float volume = 1f, float pitch = 1f)
    {
        if (string.IsNullOrWhiteSpace(soundEvent)) return;

        try
        {
            if (!source.IsValid) return;

            var recipients = new RecipientFilter();
            recipients.AddAllPlayers();
            source.EmitSound(soundEvent, recipients, volume, pitch);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Звук '{soundEvent}' не проигрался: {ex.Message}");
        }
    }

    /// <summary>
    /// Проиграть звук одному игроку. Нужен для того, что адресовано лично ему:
    /// приветствие при входе слышит вошедший, а не весь сервер — иначе на живом
    /// сервере с постоянной ротацией входов бубнило бы у всех каждые полминуты.
    ///
    /// Источником берём его собственную оболочку: звук от чужой сущности приходит
    /// из точки на карте и глохнет с расстоянием, а обращение к игроку должно звучать
    /// ровно, где бы он ни стоял.
    /// </summary>
    public static void PlaySoundTo(CCSPlayerController listener, string soundEvent, float volume = 1f, float pitch = 1f)
    {
        if (string.IsNullOrWhiteSpace(soundEvent)) return;

        try
        {
            if (listener is not { IsValid: true }) return;

            // Живому звук идёт от его оболочки, мёртвому и зрителю — от наблюдателя:
            // у трупа оболочка ещё есть, но она лежит на земле в стороне от камеры.
            CBaseEntity? source = listener.PlayerPawn.Value;
            if (source is not { IsValid: true }) source = listener.Pawn.Value;
            if (source is not { IsValid: true }) return;

            var recipients = new RecipientFilter();
            recipients.Add(listener);
            source.EmitSound(soundEvent, recipients, volume, pitch);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Звук '{soundEvent}' не проигрался игроку {listener.PlayerName}: {ex.Message}");
        }
    }

    /// <summary>Тряхнуть экран у всех в радиусе — для взрывов и ударных волн.</summary>
    public static void ScreenShake(
        BasePlugin plugin,
        Vector3 position,
        float amplitude = 8f,
        float radius = 600f,
        float duration = 1f)
    {
        try
        {
            var shake = Utilities.CreateEntityByName<CEnvShake>("env_shake");
            if (shake is null || !shake.IsValid) return;

            shake.Amplitude = amplitude;
            shake.Frequency = 40f;
            shake.Duration = duration;
            shake.Radius = radius;

            shake.Teleport(ToEngine(position), QAngle.Zero, Vector.Zero);
            shake.DispatchSpawn();
            shake.AcceptInput("StartShake", shake, shake, "", 0);

            plugin.AddTimer(duration + 0.5f, () =>
            {
                try
                {
                    if (shake.IsValid) shake.AcceptInput("Kill", shake, shake, "", 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WarcraftMod] Не удалось убрать env_shake: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WarcraftMod] Тряска экрана не создалась: {ex.Message}");
        }
    }
}
