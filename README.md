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

## Installation

If you have never done this before, read straight through — the steps are in the order you need
to do them. The whole thing takes an hour or two, and most of that is downloading.

**What a modded server is made of.** Four layers, each sitting on top of the previous one:

```
CS2 Dedicated Server           the game server itself, from Valve
  └── Metamod:Source           lets third-party code hook into the engine
      └── CounterStrikeSharp   lets plugins be written in C#
          └── WarcraftMod      this mod
```

None of them is optional: without Metamod, CounterStrikeSharp will not start; without that, neither will the mod.

> ### Do not install any of this into your own game client
>
> The server is a **separate installation** that you are about to download through SteamCMD. It has
> nothing to do with the copy of CS2 you play on. Installing Metamod into your game client makes the
> patched `gameinfo.gi` load the mod on normal servers too — **that is a VAC ban risk.**

---

### Step 1. Install SteamCMD

SteamCMD is Valve's command-line downloader; the server itself is fetched with it.

1. Download the archive for your OS from [developer.valvesoftware.com/wiki/SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD).
2. Unpack it into its own folder, e.g. `C:\steamcmd`. **Not into Downloads or the Desktop** —
   SteamCMD unpacks a few hundred more megabytes next to itself.

### Step 2. Download the CS2 server

One command. No Steam account needed — the server is served anonymously:

```bash
C:\steamcmd\steamcmd.exe +force_install_dir C:\cs2server +login anonymous +app_update 730 validate +quit
```

That is about 40 GB — half an hour on a fast line, several hours otherwise.

**What not to trust here:** the size of `C:\cs2server` jumps to nearly full almost immediately,
because Steam reserves space for every file up front. The real sign of progress is the
`Update state ... progress:` lines in the SteamCMD window. If those keep coming, it is working — just wait.

When it finishes, `C:\cs2server\game\bin\win64\cs2.exe` must exist.

### Step 3. Install Metamod:Source

1. Open the [Metamod:Source downloads page](https://www.sourcemm.net/downloads.php).
2. Take the **`dev` branch, version 2.x**, for your platform (Windows or Linux).
   The stable 1.x branch does not work with CS2 — this is the most common mistake at this step.
3. Unpack so that the archive's `addons` folder lands in `C:\cs2server\game\csgo\`.
   You should end up with `C:\cs2server\game\csgo\addons\metamod\`.
4. Open `C:\cs2server\game\csgo\gameinfo.gi` in a text editor, find the `SearchPaths` block and add
   `Game csgo/addons/metamod` **above** the `Game csgo` line:

```
SearchPaths
{
    Game_LowViolence    csgo_lv
    Game                csgo/addons/metamod
    Game                csgo
    Game                csgo_imported
    ...
```

> **Careful how you save that file.** `gameinfo.gi` must stay UTF-8 **without a BOM**. Notepad and
> PowerShell 5.1's `Set-Content -Encoding utf8` both add one, after which the engine can no longer
> parse the file and the server will not start. Safe: Notepad++, VS Code, or any editor with an
> explicit "UTF-8 without BOM" option.

### Step 4. Install CounterStrikeSharp

1. Open the [CounterStrikeSharp releases](https://github.com/roflmuffin/CounterStrikeSharp/releases).
2. Take the **`with-runtime`** build — it ships .NET inside, so you do not have to install it separately.
3. **Pick the right platform carefully.** The archive names look alike and grabbing the Linux build on
   Windows is easy to do. It breaks the install silently, and the only fix is unpacking the correct
   build over it — see "Troubleshooting" below.
4. Unpack it the same way as Metamod: the archive's `addons` folder goes into `C:\cs2server\game\csgo\`.
   You should end up with `C:\cs2server\game\csgo\addons\counterstrikesharp\`.

On Windows, steps 3 and 4 are scripted in this repository — it unpacks both archives and registers
Metamod in `gameinfo.gi` without a BOM:

```bash
.\tools\install-server-addons.ps1 -ServerRoot C:\cs2server -MetamodZip .\metamod.zip -CssZip .\css.zip
```

The script is idempotent: running it again will not create duplicate entries in `gameinfo.gi`.

### Step 5. Install the mod

You need a compiled `WarcraftMod.dll`. To build from source you need the
[.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
dotnet build -c Release
```

The output lands in `src\WarcraftMod\bin\Release\net10.0\`. Copy **everything** from that folder into:

```
C:\cs2server\game\csgo\addons\counterstrikesharp\plugins\WarcraftMod\
```

Create the `WarcraftMod` folder yourself if it is not there. On Windows the script does both:

```bash
.\tools\deploy-plugin.ps1 -ServerRoot C:\cs2server
```

It builds the mod and copies the files while leaving `warcraft_config.json` and
`warcraft_players.json` alone — your settings and player progress survive an update.

### Step 6. Start it and verify

Start locally first, without exposing anything to the internet:

```bash
cd C:\cs2server\game\bin\win64
cs2.exe -dedicated -console -condebug -port 27015 +sv_lan 1 +map de_dust2 +maxplayers 12
```

**Three checks, in order.** If the first one fails, the rest tell you nothing:

| Check | Where to look | What you want |
|---|---|---|
| 1. Did CounterStrikeSharp load | `game\csgo\addons\counterstrikesharp\logs\` | A file dated **today**. It writes one on every start — no file means CSSharp did not come up |
| 2. Does it see the mod | Server console: `css_plugins list` | `WarcraftMod` is in the list |
| 3. Does the mod work in game | Join the server, type `!race` | The race selection menu opens |

To join your own server: open the console in game (`~`) and type `connect 127.0.0.1:27015`.

### Step 7. Put the server on the internet

While `+sv_lan 1` is in the command line, only you can see the server. To get it into the server browser:

1. Get a **GSLT token** at [steamcommunity.com/dev/managegameservers](https://steamcommunity.com/dev/managegameservers),
   app id `730`.
2. Drop `+sv_lan 1` and add `+sv_setsteamaccount YOUR_TOKEN`.
3. Forward port `27015` (TCP and UDP) on your router if the server is at home.

**The token is not only about visibility.** Without it the server cannot ask Steam who just connected,
so the mod never receives a SteamID and can neither load nor save progress. From the outside this
looks like "the mod does not work for players", even though it loaded fine.

Treat the token as a password. Keep it out of the repository and out of screenshots — pass it through
an environment variable or your hosting panel's settings.

---

### Troubleshooting

Collected from things that actually went wrong on a live server.

| Symptom | What happened | What to do |
|---|---|---|
| Server starts, but `css_plugins list` says "unknown command" | CounterStrikeSharp did not load. **It fails silently** — no console error, no line in the Metamod log | Check whether there is a file dated today in `addons\counterstrikesharp\logs\`. If not, see the next row |
| No CSSharp log for today | You unpacked the build for the wrong platform: `addons\metamod\counterstrikesharp.vdf` points at `bin\linuxsteamrt64\` instead of `bin\win64\`, and the `dotnet\` folder now mixes two runtimes | Unpack the correct `with-runtime` build over it. Leftover `.so` files next to the `.dll` files are harmless — Windows ignores them |
| Server crashes immediately on start | Non-ASCII characters in `server.cfg`, e.g. in `hostname` | Keep server configs ASCII-only |
| The mod worked, then vanished after a server update | `app_update 730 validate` **does not repair, it reverts**: it restores the stock `gameinfo.gi` and `cfg\server.cfg`, wiping the Metamod line | Redo step 3, or run `install-server-addons.ps1` again |
| Mod loads, but player progress does not work | No GSLT token, so the server gets no SteamIDs | Step 7 |
| Everyone in chat sees the mod commands | A CounterStrikeSharp setting, not a mod one | In `addons\counterstrikesharp\configs\core.json` move the `!` prefix from `PublicChatTrigger` to `SilentChatTrigger`. The file is read at startup only — restart the server; `css_plugins reload` will not pick it up |

**Where the logs are:**

- Mod and CounterStrikeSharp errors — `game\csgo\addons\counterstrikesharp\logs\log-cssharp<date>.txt`
- With `-condebug`, the server console goes **not** to `game\csgo\console.log` as you would expect,
  but to `game\csgo\addons\metamod\console.log`
- If the CSSharp log cuts off and restarts a few seconds later with "CounterStrikeSharp is starting up",
  that is a native crash, not a C# exception

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
