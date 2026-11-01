# Chained Beasts Hybrids 0.1.1

Pick a beast, hold ALT, and choose a fixed two-beast loadout from its IV, V, or VI row. It keeps the appearance of the beast you selected and combines two existing starting perks.

[Download Hybrids 0.1.1](https://github.com/Krrrrrrk/Chained-Beasts_Hybrids/releases/download/v0.1.1/Chained-Beasts-Hybrids-0.1.1.zip) for **Chained Beasts 1.0.2 / Steam build 26231777**, Windows x64. You'll need BepInEx 6 IL2CPP x64 build 791 and **Foundation 1.1.1 or newer** for the F1 panel hook. TC is optional. This is still experimental; the new selector and progression need more gameplay testing.

## Unlocking the new rows

Start a new campaign with this build installed, then win:

| New victory | Global unlock | Each selected beast also needs |
| --- | --- | --- |
| Difficulty 4, Primus | IV | Its normal I variant |
| Difficulty 5, Tribune | V | Its normal II variant |
| Difficulty 6, Legate | VI | Its normal III variant |

Each win unlocks that row for every beast on your local profile. A beast you haven't unlocked stays unavailable. You don't need the partner beast's unlocks, and there is no extra XP grind.

Old victories don't count. The mod starts with all three global unlocks locked. Each difficulty unlocks its own row; a Legate win doesn't automatically award IV and V. Campaigns created before this build don't have its receipt and won't grant a new unlock. A new campaign started with this build can be saved, resumed, and completed later.

## Choosing a hybrid

1. Use the normal character-selection grid, before starting the run.
2. Hold either ALT key. The existing I/II/III buttons show IV/V/VI, and hovering a choice shows both perks and any lock requirement.
3. Select the hybrid while holding ALT. You can release ALT after selecting it; the choice stays selected.
4. Confirm and start normally. To choose an ordinary variant instead, select it without ALT.

Mouse and native keyboard/controller navigation use the same loadout rules. ALT still comes from the keyboard in this build. Hybrid previews and committed choices display their tier beside the combined perk names.

Both skills are applied during native character setup, before starting health and trainer systems initialize. The native checkpoint keeps the skills when you save and resume. Changing a menu selection doesn't respec an already-running campaign.

## Loadouts

Each cell shows the added perk. IV keeps that beast's I perk, V keeps its II, and VI keeps its III.

| Beast | IV adds | V adds | VI adds |
| --- | --- | --- | --- |
| Lion | Ferocious Wolf | Bone Collector Wolf | Curious Monkey |
| Eagle | Clever Monkey | Pouncing Panther | Reckless Rhino |
| Bear | Armoured Rhino | Stomping Rhino | Binding Ibex |
| Panther | Agile Ibex | Silent Eagle | Ravenous Wolf |
| Ibex | Deadly Panther | Vicious Monkey | Vengeful Bear |
| Rhino | Hulking Bear | Mighty Bear | Patient Eagle |
| Monkey | Lethal Eagle | Desperate Ibex | Hoarding Lion |
| Wolf | Proud Lion | Regal Lion | Unrelenting Panther |

The combinations are mirrored, so Eagle VI and Rhino VI both provide Patient Eagle plus Reckless Rhino. Shared family bonuses apply once. Native drawbacks remain, including Panther's lower health, Bear's slower movement, Clever Monkey's affliction, and Curious Monkey's automatic training choices.

## F1 testing controls

In Solo, open Foundation's F1 menu and select **Progression**. The Hybrid beast levels panel has saved IV, V, and VI checkboxes, plus **Unlock all IV / V / VI** and **Lock all IV / V / VI** buttons. It also works in the character-selection lobby before a run exists; the ordinary game cheats stay unavailable until their normal run requirements are met.

These controls change only the hybrid tier flags for your profile. The selected beast still needs its matching normal I/II/III variant. Locking a selected hybrid in the lobby returns the queued loadout to its ordinary variant. It doesn't remove perks from an ongoing or saved run.

Manual overrides are saved separately from earned victories. Locking a tier preserves its earned history. A later fresh victory on that exact difficulty unlocks it normally again; replaying a previously recorded victory doesn't undo a manual lock.

## Install

1. Close the game and back up your plugins, configs and complete save folder.
2. Keep your working BepInEx 6 IL2CPP x64 build 791 setup, or install it using the [loader guide](https://github.com/Krrrrrrk/chained-beasts-mod-foundation/blob/main/docs/INSTALLATION.md).
3. Install [Foundation 1.1.1](https://github.com/Krrrrrrk/chained-beasts-mod-foundation/releases/download/v1.1.1/ChainedBeasts.ModFoundation-1.1.1.zip). If you use TC, get its matching [combined ZIP](https://github.com/Krrrrrrk/chained-beasts-mod-foundation/releases/download/v1.1.1/TermsAndContusions-with-Foundation-1.1.1.zip) instead. The public Foundation 1.1.0 DLL doesn't have the hook Hybrids needs.
4. Extract the Hybrids ZIP into the game folder, merging `BepInEx`. The plugin belongs at `BepInEx/plugins/Hybrids/ChainedBeasts.Hybrids.dll`. Keep one copy of each DLL.
5. Start Solo and use ALT on character selection. For shared games, everyone needs the same Hybrids and Foundation builds.

Move the old F8 `MulticlassLab` prototype outside plugins before using Hybrids. It has been replaced by this mod and can bypass the new unlock rules. The public download contains only this mod, its instructions and license. It doesn't include the loader, Foundation, game files or anyone's unlock data.

The config is `BepInEx\config\local.chainedbeasts.hybrids.cfg`:

```ini
[General]
Enabled = true
```

Unlocks and per-mode campaign receipts are kept under the game's normal local save folder in `ChainedBeasts.Hybrids`, scoped to the local Steam account (or the local save profile on other platforms). Keep that folder with your save backup. Unsupported or damaged mod data is preserved instead of being replaced with unlocked data.

## Multiplayer

Every player needs the matching Hybrids DLL and Foundation setup. Foundation's compatibility check includes the compiled Hybrids binary. Each player uses their own profile unlocks, and the host validates and synchronizes actual loadout choices. A selection stays pending until the host acknowledges it; confirm again once it has synced if your first confirmation was too early.

The host waits for a matching selection snapshot before starting. Player choices follow native network/local identity when the game renumbers player slots. New campaign victories are recorded on each participating local profile through the actual campaign-win event.

Online, couch co-op, controller navigation, and checkpoint behavior still need manual testing with this integrated build. The transport and native hooks compiling isn't a multiplayer playtest. Mid-run joining and reconnect behavior aren't added here.

## Testing status

The earlier Patient Eagle plus Reckless Rhino lab pairing was tried in-game. This integrated selector and progression build has not been launched by the agent.

Nongame validation covers the 24-slot roster, native target uniqueness and game fingerprints, unlock prerequisites, exact difficulty awards, replay protection, atomic writes, damaged/future data, and a managed fixture exercising the real run-hook code. Native UI rendering, complete campaigns, all combat pairings, and online synchronization remain manual checks.

## Remove it

Close the game and remove `BepInEx/plugins/Hybrids/ChainedBeasts.Hybrids.dll`. Keep your `ChainedBeasts.Hybrids` save-data folder if you might reinstall. Don't delete Foundation while another installed mod needs it. Back up the complete native save and matching mod data before changing versions.

## Build from source

Install a .NET SDK that supports `net6.0`, BepInEx's generated interop assemblies, and Foundation 1.1.1. From this repository:

```powershell
dotnet build ChainedBeasts.Hybrids.csproj -c Release
dotnet run --project tests/Hybrids.Core.Tests.csproj -c Release
dotnet run --project integration-tests/Hybrids.RunHooks.Integration.Tests.csproj -c Release
dotnet run --project menu-tests/Hybrids.Menu.Tests.csproj -c Release
python scripts/package.py
```

For another Steam library, set `CHAINED_BEASTS_GAME_DIR` before building. To reference an uninstalled Foundation source build, pass `-p:FoundationApiPath=C:\path\to\ChainedBeasts.ModFoundation.dll`. Building and packaging don't install or start the game. The included native-target resources are pinned to the supported build; `python build_inputs.py --game-dir C:\path\to\game` can recheck their committed method tokens and signatures against the pinned game binary. A changed game build needs a new audit before using its hooks.

The MIT license covers this project's original code. Chained Beasts and its assets keep their own ownership; no game assemblies, portraits or fonts are redistributed.
