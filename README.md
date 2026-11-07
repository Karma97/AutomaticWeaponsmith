# Automatic Weaponsmith

Builds the best attachment setup for your weapon with one click.

Automatic Weaponsmith adds an **AUTO BUILD** button to the weapon builds screen. Pick a mode and the mod finds the best parts from your stash, the traders and the flea market. It shows you the result first. Nothing is bought unless you buy it yourself.

## Requirements

- SPT 4.1.6

## Installation

1. Download the ZIP file.
2. Open your SPT folder (the folder with `EscapeFromTarkov.exe`).
3. Extract the ZIP into this folder. You should now have this file:
   `BepInEx\plugins\AutomaticWeaponsmith\AutomaticWeaponsmith.dll`
4. Start the game.

## How to use

1. Open **BUILDS** at the bottom of the screen and choose a weapon.
2. Click **AUTO BUILD** (top left, below the save and assemble icons).
3. Choose a mode:
   - **RECOIL**: as little recoil as possible, but ergonomics stays above a minimum (40 by default).
   - **ERGONOMICS**: ergonomics up to 100, then as little recoil as possible.
   - **BALANCED**: a mix of both.
4. Wait a few seconds while the button shows **CALCULATING ...**. The game does not freeze.
5. The new build appears on your weapon, together with a window that shows:
   - ergonomics and recoil before and after
   - which parts come from your stash, from traders and from the flea market
   - the total cost
6. Click **YES** to keep the build or **NO** to undo it.
7. To buy the missing parts, use the game's own **FIND PARTS** or **ASSEMBLE** button as usual.

## What the mod pays attention to

- **Real offers only**: trader parts must be unlocked for your loyalty level and in stock. Flea market parts are checked live. If nobody sells a part right now, the mod picks a different one.
- **Stash first**: parts you already own are free, so they are preferred.
- **Always an optic**: the build always gets a red dot, holo or scope. If you already have one on your weapon, it stays. Iron sights are never used.
- **Always a flashlight**: the build always gets a real flashlight (a lamp with white light, not just a laser).
- **Usable magazine**: the magazine is at least as big as the one on your weapon.
- **No useless parts**: no empty mounts, and ergonomics above 100 is not wasted.
- **Fitting parts only**: parts that do not work together are never combined.

## Settings

Press **F12** in the game to open the settings (this needs the BepInEx Configuration Manager, which is included in SPT). You can also edit `BepInEx\config\com.automaticweaponsmith.cfg`.

### Optimizer

| Setting | Default | What it does |
| --- | --- | --- |
| Always use an optic (no iron sights) | On | Always add a red dot, holo or scope. |
| Always add a flashlight | On | Always add a flashlight. |
| Sight | Keep | **Keep**: your current sight stays. **SameType**: any sight of the same type. **Any**: no rule. |
| Minimum magazine size | 30 | Only used if your weapon has no magazine. |
| Minimum ergonomics (Recoil mode) | 40 | Recoil mode will not go below this. 0 = off. |
| Recoil weight (Balanced mode) | 1.0 | Higher = Balanced mode cares more about recoil. |
| Allow grenade launchers | Off | Allow underbarrel grenade launchers. |
| Time limit (ms) | 1500 | After this time, the mod finishes faster but may find a slightly worse build. |

### Prices

| Setting | Default | What it does |
| --- | --- | --- |
| Allow barter trades | Off | Also use trader offers that you pay with items (and GP coins). |
| Use budget limit | Off | Keep the build below a maximum price. |
| Budget (roubles) | 100,000 | The maximum price. Parts from your stash are free. |

## Good to know

- The mod never buys anything by itself. It only changes the build on the builds screen.
- The game's FIND PARTS window shows the cheapest offer for each part, which might be a GP coin or barter offer. Click the small arrow on the right side of a row to choose a different offer.
- Prices are loaded when you click a mode and are kept for 5 minutes.
- If something goes wrong, check `BepInEx\LogOutput.log`. The mod writes the full build and all prices there.

## Uninstall

Delete the folder `BepInEx\plugins\AutomaticWeaponsmith`.

## Building from source

Source code: https://github.com/Karma97/AutomaticWeaponsmith

1. Install the .NET SDK (8 or newer).
2. Build with the path to your SPT folder:
   `dotnet build -c Release -p:SptDir="C:\path\to\SPT"`
3. The DLL is copied to `BepInEx\plugins\AutomaticWeaponsmith` in that SPT folder.

## License

MIT, made by Dizzy. See `LICENSE`.
