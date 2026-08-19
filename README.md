# Warcraft Mod for CS2

A Warcraft-style RPG mod for Counter-Strike 2: pick a race, earn XP from kills, level up and
spend skill points on passive, active and ultimate abilities.

Written in C# for **CounterStrikeSharp** (on top of Metamod:Source 2). 13 races, 16 levels,
per-race progress saved by SteamID.

[Русская версия README](README.ru.md)

**Try it on a live server:** `connect 45.95.31.2:27015` — the mod runs there in production.

> **Heads up:** all in-game text (menus, ability names, chat) is currently **Russian only**.
> The code, config and documentation are language-neutral, but there is no localization layer
> yet. If you need English strings, open an issue — it is the first thing on the list.

---

## Features

- **Race system with reflection-based registration.** A new class deriving from `Race` is picked
  up automatically — no registry to edit, no DI wiring.
- **16 levels, one skill point each.** Every race has exactly 4 abilities (2 passive, 1 active,
  1 ultimate) with 4 ranks apiece — the points always add up.
- **Progress is stored per race**, keyed by SteamID. Switching races does not wipe the old build.
- **Auto-distribution of skill points** for players who do not want to think about builds.
- **Damage is modified before it lands** — crits, dodge, block, damage reduction, lifesteal.
- **In-game menus** for race selection, skill spending and admin actions.
- **Map voting** at round end from a configurable pool.
- **Donor-only races** gated behind a grant command, if you run a paid server.

## Races

| Race | Passives | Active | Ultimate |
|---|---|---|---|
| Orcish Horde | Momentum, Strong Arms | Battle Cry | Blood on the Fists |
| Undead Scourge | Vampirism, Undead Aura | Petrify | Life Drain |
| Human Alliance | Last Sight, Veteran | Smoke Grenade | Weapon Crate |
| Night Elf | Evasion, Lightness | Shadow | Hunter's Trap |
| Bigfoot | Vitality, Thick Hide | Taunt | Alpha Hide |
| Grasshopper | Multi-jump, Springiness | Leap | Trampoline |
| Scout | Instinct, Blink | Flash Burst | Summon |
| Shapeshifter | Infiltration, Regeneration | Vanish | Infection |
| Rabbit | Acceleration, Bunny Hop | Backflip | Stomp |
| Shorty | Nimbleness, Squeeze | Flea | Commotion |
| Sonic | Speed, Adrenaline | Drift | Rings |
| Chronos ★ | Head Start, Time Out | Rewind | Global Rollback |
| Mirage ★ | Mirror Glare, Afterimage | Double | Phantom Shots |

★ — donor-only, granted with `css_wcgrant`. Remove the `DonorOnly` override to make them free.

Ultimates unlock at level 6.

## Commands

| Command | What it does |
|---|---|
| `!race` | Race selection menu |
| `!skills` | Spend skill points |
| `!ability` | Use the active ability |
| `!ult` | Use the ultimate |
| `!wcinfo` | Your race, ranks and what they do |
| `!resetskills` | Reset the point distribution |
| `!wchelp` | Command list |

Handy client binds:

```bash
bind mouse4 css_ability; bind mouse5 css_ult
```

Admin commands (`css_wcgrant`, bans, resets) are listed by `css_wchelp` in the server console.

---

## Requirements

| Component | Version it was built and tested against |
|---|---|
| CounterStrikeSharp | 1.0.371 — **needs .NET 10**, the docs on the website are out of date |
| Metamod:Source | 2.0.0-git1410 (dev build) |
| .NET SDK | 10.0 |

## Install

1. Install a CS2 dedicated server via SteamCMD (`app_update 730`).
2. Install **Metamod:Source 2.x** on top and register it in `game/csgo/gameinfo.gi`.
3. Install **CounterStrikeSharp** (`with-runtime` build if .NET is not on the machine).
4. Copy the build output into
   `game/csgo/addons/counterstrikesharp/plugins/WarcraftMod/`.
5. Restart the server and check `css_plugins list`.

Steps 2–4 are scripted for Windows:

```bash
.\tools\install-server-addons.ps1 -ServerRoot C:\cs2server -MetamodZip .\metamod.zip -CssZip .\css.zip
```

```bash
.\tools\deploy-plugin.ps1 -ServerRoot C:\cs2server
```

> **Do not install Metamod into your own game client.** A patched `gameinfo.gi` loads the mod on
> normal servers too, which is a VAC ban risk. The server must be a separate SteamCMD install.

## Build

```bash
dotnet build -c Release
```

Output lands in `src/WarcraftMod/bin/Release/net10.0/`.

## Configuration

On first start the plugin creates two files next to itself:

- `warcraft_config.json` — XP rates, save interval, admin list, map fences, welcome sounds.
  See [`warcraft_config.example.json`](warcraft_config.example.json) for every key with sane values.
- `warcraft_players.json` — player progress. Back it up; do not commit it.

Put your own SteamID64 into `Admins` to get access to the admin menu.

---

## Adding your own race

Drop a file into `src/WarcraftMod/Races/`, derive from `Race`, describe the abilities.
Nothing to register — the registry finds the class by reflection.

```csharp
public sealed class MyRace : Race
{
    public override string Id => "myrace";        // never change after release: progress is keyed by it
    public override string Name => "My Race";
    public override string Description => "What it does";

    public override IReadOnlyList<Ability> Abilities { get; } =
    [
        new Ability { Name = "Passive 1", Description = "...", Kind = AbilityKind.Passive },
        new Ability { Name = "Passive 2", Description = "...", Kind = AbilityKind.Passive },
        new Ability { Name = "Active",    Description = "...", Kind = AbilityKind.Active,   Cooldown = 25f },
        new Ability { Name = "Ultimate",  Description = "...", Kind = AbilityKind.Ultimate, RequiredLevel = 6, Cooldown = 60f },
    ];

    public override void OnSpawn(WarcraftPlayer player) { }
    public override bool OnActivateAbility(WarcraftPlayer player) => false;
}
```

Rules that are easy to forget:

- **Never reorder abilities** — ranks are stored by index. Append new ones at the end.
- The sum of all `MaxRank` values must equal `XpTable.MaxLevel` (16), or the points will not add up.
- Exactly one `Kind = Active` and one `Kind = Ultimate` — `!ability` and `!ult` are bound to them.
- Use `Effects` (healing, knockback, invisibility, target search) instead of touching engine
  schema fields directly.
- Permanent speed bonuses go into `BaseSpeedMultiplier`, temporary ones into `TempSpeedMultiplier`.

## Project layout

```
src/WarcraftMod/
  WarcraftPlugin.cs      entry point: events, XP, commands
  Core/
    Race.cs              race base class
    Ability.cs           ability description
    WarcraftPlayer.cs    per-session player state
    RaceRegistry.cs      reflection-based race discovery
    XpTable.cs           level curve
    Effects.cs           helpers for abilities
  Races/                 the races themselves
  Menus/                 race selection, skill and admin menus
  Storage/               progress persistence
  Config/                settings
tools/                   build and install scripts (PowerShell)
```

Source comments are in Russian — the project was written in it and is being opened up as is.

## License

GPL-3.0. CounterStrikeSharp is GPL-3.0, and this plugin links against it.
