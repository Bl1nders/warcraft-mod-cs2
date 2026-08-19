using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftMod.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace WarcraftMod.Races;

/// <summary>
/// Кузнечик — вертикаль: живёт на крышах, забирается туда многопрыжком и рывком,
/// а батутом закидывает наверх всякого, кто на него наступит.
/// </summary>
public sealed class Grasshopper : Race
{
    private const int MultiJump = 0;
    private const int Springiness = 1;
    private const int LeapForward = 2;
    private const int Trampoline = 3;

    /// <summary>
    /// Прибавка к силе обычного прыжка за ранг. Считается в скорости, а не в высоте:
    /// движок прыгает своими 301, и прибавка ложится поверх них одним кадром.
    /// </summary>
    private const float GroundJumpPerRank = 25f;

    /// <summary>Первая раса в очереди после стартовой четвёрки.</summary>
    public override int UnlockTotalLevel => Unlocks.Tier(1);

    public override string Id => "grasshopper";
    public override string Name => "Кузнечик";
    public override string Description => "Живёт на верхнем этаже: прыгает выше всех и дважды подряд, бросается вперёд рывком и ставит батут, подбрасывающий всех подряд.";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability
        {
            Name = "Многопрыжок",
            Description = "Прыжки в воздухе: один на рангах 1-2, два на 3-4; с рангом выше",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Прыгучесть",
            Description = "Прыжок с земли по нажатию выше: +25 к силе за ранг, до +100. В распрыжке с зажатым пробелом не работает",
            Kind = AbilityKind.Passive,
        },
        new Ability
        {
            Name = "Скачок",
            Description = "Мощный бросок в сторону взгляда, сила растёт с рангом",
            Kind = AbilityKind.Active,
            Cooldown = 10f,
        },
        new Ability
        {
            Name = "Батут",
            Description = "Зона на 20 с: подбрасывает всех, кто в неё войдёт — и своих, и чужих",
            Kind = AbilityKind.Ultimate,
            RequiredLevel = 6,
            Cooldown = 60f,
        },
    ];

    public override void OnSpawn(WarcraftPlayer player)
    {
        var jump = player.RankOf(MultiJump);
        if (jump > 0)
        {
            // Ранги 1-2 дают один прыжок в воздухе, 3-4 — два.
            player.ExtraJumps = Math.Max(1, jump / 2);
            player.ExtraJumpPower = 260f + jump * 25f;
        }

        // Здесь по очереди стояли две пассивки, и обе не пережили проверки игрой.
        // Сперва иммунитет к падению — он обессмыслился, когда урон от падения выключили
        // на сервере целиком. Потом замедленное падение с пониженной гравитацией: оно
        // подрезало скорость снижения каждый тик с сервера, а клиент считает своё падение
        // сам — выходила та же рваная задержка, что и у прежнего банни-хопа Кролика.
        //
        // Прыгучесть выбрана как замена, но с оговоркой, и её надо помнить. Прибавка
        // ложится на Server.NextFrame поверх прыжка, который клиент уже предсказал своей
        // силой, — то есть клиенту приходит поправка. У Многопрыжка её нет: прыжок в
        // воздухе клиент не предсказывает вовсе, там мод не спорит, а задаёт. Насколько
        // заметна поправка вверх, знает только игра; если дёргает — менять на пассивку,
        // которая не трогает вертикаль (разгон с приземления через TempSpeedMultiplier).
        //
        // Второе следствие того же порядка: срабатывает прибавка только на свежем нажатии
        // пробела. В распрыжке с зажатой клавишей нового нажатия нет, и высоту она не
        // поднимает — это записано в HANDOFF как «высоту автопрыжка модом не поднять».
        var springiness = player.RankOf(Springiness);
        if (springiness > 0) player.GroundJumpBonus = springiness * GroundJumpPerRank;
    }

    public override bool OnActivateAbility(WarcraftPlayer player)
    {
        var rank = player.RankOf(LeapForward);
        if (rank <= 0 || player.Pawn is not { } pawn) return false;

        var direction = Effects.ForwardVector(pawn);
        direction.Z = MathF.Max(direction.Z, 0.35f); // всегда с подъёмом, даже если смотреть в пол

        Effects.Push(pawn, direction, 500f + rank * 90f);

        player.Controller?.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Lime}Скачок!");
        return true;
    }

    public override bool OnActivateUltimate(WarcraftPlayer player)
    {
        var rank = player.RankOf(Trampoline);
        if (rank <= 0 || player.Pawn is not { } pawn || player.Controller is not { } controller) return false;

        // Ставим туда, куда смотрим, но недалеко. Не смотрит вниз — кладём под ноги.
        var placement = Effects.AimPointOnGround(pawn, maxRange: 250f) ?? Effects.Origin(pawn);
        if (placement is not { } zone) return false;

        const float duration = 20f;
        const float radius = 130f;
        const float tick = 0.25f;

        var launchPower = 700f + rank * 60f;
        var ticksLeft = (int)(duration / tick);
        var markerCounter = 0;

        // Один и тот же игрок не должен подлетать каждые 0.25 с, пока стоит в зоне.
        var lastLaunch = new Dictionary<int, float>();

        controller.PrintToChat($"{WarcraftPlugin.Prefix} {ChatColors.Lime}Батут установлен{ChatColors.Default} на {duration:0} с — подбрасывает всех подряд");
        DrawZone(zone, radius);

        Timer? zoneTimer = null;
        zoneTimer = Plugin.AddRoundTimer(tick, () =>
        {
            if (--ticksLeft <= 0)
            {
                zoneTimer?.Kill();
                return;
            }

            // Перезапускаем каждый тик: искра живёт 0.6 с, так мерцание идёт без провалов.
            markerCounter++;
            DrawZone(zone, radius);

            foreach (var candidate in Utilities.GetPlayers())
            {
                if (!candidate.IsValid || candidate.PlayerPawn.Value is not { IsValid: true } candidatePawn) continue;
                if (candidatePawn.Health <= 0) continue;
                if (Effects.Origin(candidatePawn) is not { } position) continue;

                // По горизонтали — радиус зоны, по высоте — только рядом с полом батута.
                var flat = new Vector3(position.X - zone.X, position.Y - zone.Y, 0f);
                if (flat.LengthSquared() > radius * radius) continue;
                if (MathF.Abs(position.Z - zone.Z) > 90f) continue;

                if (lastLaunch.TryGetValue(candidate.Slot, out var last) && Server.CurrentTime - last < 1f) continue;

                lastLaunch[candidate.Slot] = Server.CurrentTime;
                candidatePawn.AbsVelocity.Z = launchPower;

                // Подбросили — значит и за приземление отвечаем мы, а не игрок.
                Plugin.Get(candidate)?.GrantLaunchFallImmunity();

                VisualEffects.PlaySound(candidatePawn, "SmokeGrenade.Bounce");
                CenterText.Print(candidate, "БАТУТ!");
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }

    /// <summary>
    /// Обозначить границу зоны шестью точками по окружности.
    /// Берём искры — единственный эффект из набора, который реально отображается;
    /// кольцо не рисуется вовсе. Искра короткая, поэтому её перезапускают каждый тик.
    /// </summary>
    private void DrawZone(Vector3 center, float radius)
    {
        var tint = Color.FromArgb(255, 255, 40, 40);

        const int markers = 6;
        for (var i = 0; i < markers; i++)
        {
            var angle = MathF.Tau * i / markers;
            var point = center + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 8f);

            VisualEffects.SpawnParticle(Plugin, VisualEffects.Fx.Sparks, point, lifetime: 0.6f, tint: tint);
        }
    }
}
