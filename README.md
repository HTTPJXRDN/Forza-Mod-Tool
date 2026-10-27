# Forza Mod Tool — 1.3.0

Edit supported files from **FH2, FH5, FH6 and Forza Motorsport (FM8)**. Profile Editor and Garage Viewer support FH6 and FH2/Azimuth saves. Save Swap is FH6-only; FH5 saves can be decrypted in Crypto.

| Game | GameDB / Car Editor | Profile / Garage | Save Swap |
|---|---|---|---|
| FH2 | Open plain SLT or SQLite; supported options only | Azimuth revision saves | No |
| FH5 | Open SLT directly, or decrypt to SQLite | Decryption only in Crypto | No |
| FH6 | Open SLT directly, or decrypt to SQLite | Yes | Yes |
| FM8 | Decrypt in Crypto, edit SQLite, then re-encrypt | No | No |

**New in 1.3.0:** FH2/FH5 support, FH2 save editing, track widths and size labels in mm per side, stock engine names, car buckets, expanded stock restore, stock thumbnails, a DB Browser scratch pad and editable **?** help. See [CHANGELOG.md](CHANGELOG.md) for the full list.

## Table of contents

- [Start here](#start-here)
- [Back up first](#back-up-first)
- [Where to find your files](#where-to-find-your-files)
  - [FH6 save location](#fh6-save-location)
  - [FH2/Azimuth save location](#fh2azimuth-save-location)
- [Choose a tab](#choose-a-tab)
  - [Car Editor](#car-editor-change-a-car)
  - [Profile Editor](#profile-editor-balances-unlocks-and-journal)
  - [Garage Viewer](#garage-viewer-manage-owned-cars)
  - [FH2 save editing](#fh2-save-editing)
  - [Animation Swap](#animation-swap-change-animations)
  - [Save Swap](#save-swap-use-another-saves-progress)
  - [Crypto](#crypto-convert-or-merge-files)
  - [Merge an updated DB with your modded DB](#merge-updated-db-keep-your-mods-after-a-game-update)
  - [DB Browser](#db-browser-manual-database-editing)
- [Install an edited save](#install-an-edited-save)
- [Motorsport differences](#motorsport-the-important-differences)
- [Troubleshooting](#if-something-goes-wrong)
- [Disclaimer](#disclaimer)
- [Credits](#credits)

## Start here

1. Download the **app ZIP** from Releases, not a Source or GitHub upload ZIP.
2. Extract the whole ZIP into a normal folder.
3. Run **FH6ModStudio.exe**. Keep the included DLLs, animation helpers, `Help` and `licenses` folders beside it.

The app EXE runs on Windows 10/11 x64 without a separate .NET installation. The ZIP is a normal archive, not password-encrypted.

## Back up first

- **Back up the whole save folder**, plus every game file you plan to replace.
- Keep untouched backups outside the game folder. Never overwrite your only backup.
- Work on copies. Close the game before installing edited files.
- Test a small change first. If the game crashes or rejects it, restore the backup.

The tool edits a private working copy. **Apply does not install a mod, and exporting does not replace your live save or files.** You install the exported file yourself.

## Where to find your files

`<FH6>` and `<FM>` mean your game's installation folder. FH5's content folder may be the game folder or its `Content` subfolder; FH2 files are under the unpacked game's `Mount` folder. In Steam, right-click the game → **Manage → Browse local files**. Other launchers have their own install-folder option.

| File | Location | Used for |
|---|---|---|
| Original FH6 GameDB | `<FH6>\media\stripped\gamedbRC.slt` | Copy from here; leave this original untouched |
| Working FH6 GameDB | `<FH6>\MediaPC\stripped\gamedbRC.slt` | Car Editor, Crypto and DB Browser |
| FH6 car ZIPs | `<FH6>\media\Cars\<car>.zip` | Animation Swap |
| FH6 profile save | See the save path below | Profile Editor, Garage Viewer and Save Swap |
| FH5 GameDB | `<FH5 content>\media\stripped\gamedbRC.slt` | Copy to `MediaPC\stripped` before editing |
| FH2 GameDB | `<FH2 Mount>\media\stripped\gamedbRC.slt` | Plain SQLite SLT; open directly |
| FH2 save | Azimuth's `data\titles\...\revisions` folder | Profile Editor and Garage Viewer |
| Motorsport GameDB | `<FM>\Content\media\base\gamedb.slt` | Decrypt in Crypto before editing |
| Motorsport camera ZIP | `<FM>\Content\media\base\camera.zip` | Crypto extraction/rebuilding |
| Other Motorsport assets | Their original subfolders under `<FM>\Content\media\` | Supported Crypto workflows |
| Tool temporary files | `%TEMP%\ForzaModTool\Work` | Scratch files, not files to install |

### FH6 save location

Find your FH6 profile here:

```text
C:\XboxGames\GameSave\pgs\u_<account>_<suffix>\current\ContainersRoot\User_<account>\C_ProfileData
```

Start in `C:\XboxGames\GameSave\pgs`, open your `u_` folder, then `current\ContainersRoot\User_...`.

Use the active file named exactly **C_ProfileData**, not `C_ProfileData_SCopy`, `C_ProfileBackup` or a `User_..._Backup` folder. Back up `C_ProfileData`, then copy `C_ProfileData` into a separate working folder.

### FH2/Azimuth save location

```text
<Azimuth>\data\titles\1A96545B\saves\player-1\User_<account>\revisions\<revision>\
```

Click **FH2 save…** in Profile Editor or Garage Viewer. Choose a revision, its `data` folder, or the `revisions` folder to load the latest revision. Back up the entire `User_<account>` folder first. This supports Azimuth revision saves, not Xbox 360/STFS containers.

Keep the existing numbered folders inside `revisions`; their numbers change as the game saves. Install the same exported save contents in both folders, as described under [FH2 save editing](#fh2-save-editing).

## Choose a tab

| Tab | What it does | What to load |
|---|---|---|
| **Car Editor** | Change availability, upgrades, engines, buckets, power and fitment | FH2/FH5/FH6 SLT or SQLite; FM8 uses decrypted SQLite |
| **Profile Editor** | Edit balances, XP, unlocks and supported progress | FH6 `C_ProfileData` or FH2/Azimuth revision |
| **Garage Viewer** | Add/remove cars and edit owned-car fields | FH6 `C_ProfileData` or FH2/Azimuth revision |
| **Animation Swap** | Swap door animations and adjust supported channels | A copied car ZIP or `carclips_<ID>.clipd` |
| **Save Swap** | Copy another save's progress into your account's container | Two copied, encrypted FH6 profiles |
| **Crypto** | Decrypt/re-encrypt files, extract/rebuild supported ZIPs, and merge databases | The relevant copied game file |
| **DB Browser** | Browse tables, edit cells, run SQL and keep scratch-pad notes | FH2/FH5/FH6 SLT or SQLite; decrypt FM8 first |

**Each tab has its own working copy.** To combine edits from different tabs, export from the first tab and load that exported file into the next one.

Click **?** in a tab for a short guide. Help stays open while you use the tool. Edit the matching `Help/*.txt` beside the app to change its wording; reopen the help window to reload it.

### Car Editor: change a car

1. Before editing, switch to an unrelated car in-game, let the game save, then close it.
2. Copy the original FH6 GameDB from `media\stripped` into `MediaPC\stripped`. Back up any existing override first.
3. Open **Car Editor → Load SLT / DB** and select the copy in `MediaPC\stripped`. No manual decrypt step is needed for FH2, FH5 or FH6; decrypt FM8 first.
4. Search for a car and make your changes. Checkbox-based fitment, drivetrain, tire and stance options need **APPLY changes to this car**. Engine/motor, price, Autoshow and handling action buttons apply immediately to the working copy.
5. Click **Export SLT** or **Export DB** and save a new file.

To install an FH6 GameDB edit, put the exported **encrypted SLT** in `<FH6>\MediaPC\stripped\`, named exactly `gamedbRC.slt`. Leave `media\stripped` untouched. If you exported SQLite, re-encrypt it in Crypto first.

FH5 uses the same `MediaPC\stripped` installation pattern through your existing FH5 mod setup. FH2 exports stay plain; use your FH2/Azimuth mod setup. FM8 uses its original paths, as explained below.

- **Track width (FH5/FH6):** enter mm per side. `20` means 20 mm each side; the DB stores `0.040` metres total. The wider step defaults to 5 mm, and **Step (mm)** changes the increment. Reapply Track offset without new steps to repair old size names, including batches. Install any exported `stringtables` folder under the game's `MediaPC\stripped`; correct existing tables are reused.
- **Car bucket:** choose the category name, then **Set bucket**.
- **Engine names:** **Label stock engines** uses car name - engine name. Generic engines keep their names. Labels are shared by every car using that engine.
- **Restore selected car:** restores the car's own DB records, including DB Browser edits. Added swap links are removed; engine names and shared parts stay intact. Check the log for shared items kept.
- **Thumbnails:** Auto-find supports FH2/FH5/FH6, or choose a folder/ZIP. Sources are remembered per game. FM8 needs extracted supported images.
- FH2 tire profile and track width remain visible but disabled; use the separate FH2 wheel mod. Added-widebody base tracks use metres.

Test with a fresh Autoshow copy of the edited car. An old garage instance can retain incompatible upgrade references.

### Profile Editor: balances, unlocks and Journal

1. Click **Load C_ProfileData** and load your working copy.
2. Use **Overview**. Enter a balance or XP value, then click its **Apply** button. XP shows a level/prestige preview; it does not grant level-up rewards.
3. For clothes, horns, emotes, phrases or badges, open the list under the button. Search, select items, then click **Unlock selected**. **Ctrl+A selects visible items only.** Review the confirmation.
4. For Collection Journal, select **rows**, then click **Complete selected challenges**. The tickboxes show status; they are not buttons. Clear filters before selecting the whole Journal.
5. Click **Export save** and save a separate output.

Driver XP retention was tested after earning XP and restarting. Online XP editing shows its own level and linked Driver XP previews; Online XP persistence remains unconfirmed. **Complete playlist** completes the selected series, its seasons and activities, and updates Playlist History points while leaving rewards unclaimed.

Journal completion also updates supported race, PR stunt, job and world progress. Better personal bests are kept. Linked horns and phrases complete their required Journal progress. To discover regions, complete all ten region rows together. The advanced guide covers result values and limits.

Badge earned dates use **UTC**. Preserve real dates, follow the displayed playlist window, and check the actual availability before entering a date for an unverified series.

Missing or changed definitions stay read-only. Unlocks do not grant DLC licenses; Journal entries do not add actual cars, photos or online results.

See the [Profile Editor screenshots and detailed help](docs/ADVANCED_GUIDE.md#section-5-profile-editor) in the advanced guide.

### Garage Viewer: manage owned cars

1. Click **Load C_ProfileData…** and choose a working copy of your save.
2. To add cars, use the right-hand list. **Missing cars only** hides models you already own; traffic cars have a separate experimental list.
3. Select owned cars to remove/duplicate them, change the current car, or edit owner/history fields. Field edits need **Apply car fields**.
4. Click **Export save** for a separate output.

The built-in stock catalog loads automatically; there is no manual GameDB-catalog loader. Adding or duplicating supported non-DLC cars also completes their matching Car Collection Journal entries. DLC cars remain addable, but those Journal entries are excluded from automatic completion. Adding a car does not grant its DLC license.

### FH2 save editing

Load an Azimuth revision using **FH2 save…** in Profile Editor or Garage Viewer.

- **Profile Editor:** credits, spendable skill points, wheelspins and Driver XP. XP has a read-only level/wristband preview.
- **Progress:** mainland and Storm Island boards/roads, perks, owned-car photography, camera discovery and championships. **All mainland + finales** also fixes post-story progression. Storm Island covers 29 championships and 88 races; its final gauntlet/story are excluded. Camera discovery does not create scores, and completion buttons do not award XP, credits or reward cars.
- **Garage Viewer:** add, duplicate or remove supported cars and edit owner/distance/top speed. Original Owner supports batches; distance and top speed are single-car edits.
- **Export save:** writes a complete revision. Close FH2 and Azimuth and back up the entire `User_<account>` save folder. Copy the same exported `commit` and entire `data` folder into **both folders inside `revisions`**, replacing their contents. Keep both existing folder names. Replacing only one can stop the edited save loading.

See the [FH2 save guide](docs/FH2-SAVES.md) for installation and supported fields. FH2/FH5 save swapping is unavailable.

### Animation Swap: change animations

1. Browse to a copied complete car ZIP, or a raw `carclips_<ID>.clipd`.
2. Choose the donor and front/rear door channels, then apply the swap. Other channels and endpoint adjustments are advanced options.
3. Use **Save As** to write a new file. Keep the input untouched.

For FH6 car-ZIP overrides, use `<FH6>\MediaPC\Cars\` with the original car ZIP's filename; keep the original under `media\Cars` untouched. A raw CLIPD is not a complete installable car ZIP. See the [detailed guide](docs/ADVANCED_GUIDE.md#section-3-animation-swap) for raw-file and stance workflows.

### Save Swap: use another save's progress

1. **Donor** = the encrypted save containing the progress you want.
2. **Target** = a copy of your own encrypted save.
3. Check the paths, then click **Build Swapped Save**.
4. The new `.swapped` file is written beside the copied target. Neither input is overwritten.

Do not reverse donor and target. Save Swap does not grant DLC ownership or guarantee compatibility with every save/build.

### Crypto: convert or merge files

- **Decrypt:** drop a copied encrypted file into the main area, then click **Decrypt**. Edit the resulting SQLite/text file or extracted folder.
- **Re-encrypt:** load the edited output and choose its matching original **template** when asked. A template is the untouched encrypted file used to rebuild the container.
- **ZIP rebuild:** only use supported round-trip workspaces. Keep their original/template metadata; do not add/remove entries. Track/CMS extractions are not normal rebuildable ZIPs.
- **Share cars:** put a full modified GameDB in the top area → **Export Car Related DB** → select cars. To import, put the receiving full GameDB on top, the car donor in the lower merge area, then **Import car DB**. A car donor is not a complete GameDB to install.
- **Profile saves:** Decrypt writes the complete `.decrypted` profile, without extracting a SQLite file. FH6 can re-encrypt it with its original profile template. FH5 is decrypt-only; viewing, editing and Save Swap are disabled.
- **After a game update:** use **Merge updated DB** to keep your mods with the new game content. Follow the steps below.

Leave **Drop & rebuild** off unless you intend to replace the donor cars' existing mods. General table/row merging is advanced; follow the [detailed merge guide](docs/ADVANCED_GUIDE.md#merge-another-database).

### Merge updated DB: keep your mods after a game update

Close the game and back up your modded GameDB first.

1. In **Crypto**, drop the **new clean** `<FH6>\media\stripped\gamedbRC.slt` into the **top** drop zone. Drop your **current modded** `<FH6>\MediaPC\stripped\gamedbRC.slt` into the lower **Optional** drop zone. Click **Merge updated DB**.
2. Review the preview, then click **Create updated DB**. If asked for a clean reference, use an untouched full GameDB from the **same old version as your modded DB**.
3. In `MediaPC\stripped`, rename your old modded file to `gamedbRC.slt.bak` (keep any existing backup; use a different backup name if needed). Copy the new `.updatedmerge.<timestamp>.slt` from the output folder into `MediaPC\stripped` and rename that copy to **gamedbRC.slt**. Launch the game and check your mods and the new content.

The same merge actions support FH2, FH5, FH6 and FM8 DBs; use two DBs from the same game. Unchanged old tables absent from an update are skipped and reported; edited missing tables or incompatible layouts still stop the update merge.

The result saves beside the top input unless you choose an **Output folder**. Do not replace the clean original in `media\stripped`. See the [three-step guide with photos](docs/ADVANCED_GUIDE.md#merge-after-a-game-update) for examples.

### DB Browser: manual database editing

1. Open a database and choose a table.
2. Edit cells or run SQL only if you know what those fields do.
3. **Write changes** keeps your edits in the working copy. **Export as** writes the separate output you actually keep.

The lower-right **Scratch pad** holds notes and IDs. Import/export notes as TXT; they clear when the tool closes.

For FH5/FH6/FM8, an exported `.sqlite` must be re-encrypted before installation. FH2 plain SLTs are the exception. DB Browser changes are not automatically shared with the other tabs.

## Install an edited save

For FH2/Azimuth, back up first, then install the same exported `commit` and whole `data` folder in **both revision folders**, keeping their existing names. See [FH2 save editing](#fh2-save-editing). The following instructions are for FH6.

With the game closed, back up the whole active save folder. Copy your **encrypted** exported/swapped profile into that folder and name the copy exactly **C_ProfileData**. Keep the backup untouched.

A `.decrypted` profile cannot be installed directly. Older standalone profile SQLite exports also require re-encryption. Re-encrypt it with its matching original profile template first. Cloud sync can replace local changes, so verify the result after saving/reloading.

## Motorsport: the important differences

Decrypt the supported `gamedb.slt` in **Crypto**, edit the SQLite in **Car Editor** or **DB Browser**, export it, then re-encrypt with the original Motorsport template.

FM has **no MediaPC override folder**. Back up `media\base\gamedb.slt` before replacing it with the re-encrypted DB. Put other finished encrypted files/rebuilt ZIPs at their original paths under `<FM>\Content\media\`, with its original filename and subfolder. Do not install decrypted SQLite.

FH2 save editing/export and supported FM8 LZX camera/car ZIP operations need **[x64 xcompress64.dll](https://github.com/D3FEKT/ForzaTechStudio/blob/Main/ForzaTechStudio/Native/xcompress64.dll)** beside the app EXE. Click **Download raw file** on GitHub, put it beside `FH6ModStudio.exe`, then restart the tool. It is not included in the release. See [setup instructions](NATIVE_RUNTIME.md). Track archives are extract-only. Profile/garage/save swapping are not supported for FM.

## If something goes wrong

- **Change missing?** Check Apply → Export → install the exported file, not the input. Check that you loaded the previous tab's export when combining edits.
- **Template requested?** Use the matching original encrypted file from the same game/build.
- **Save option disabled?** Read the status/compatibility message. Do not force unsupported fields through SQL.
- **Game hangs or crashes?** Close it, restore the backup, then retry one small edit. When testing a different DB, make sure it includes the current car and all house display cars. For GameDB car changes, switch away first and test a fresh car instance.

Known limits: Online XP persistence is unconfirmed. The reported Car Collection display of **4,500 / 4,480** is unresolved. Extreme power/handling edits can raise PI, including X999.

More help: [detailed guide](docs/ADVANCED_GUIDE.md) · [changelog](CHANGELOG.md).

## Disclaimer

Unofficial tool; not affiliated with Microsoft, Xbox, Playground Games or Turn 10. Back up your files and use edits at your own risk.

## Credits

- JXRDN — Project creator, feature direction, UI design, research, and extensive in-game testing.
- Draff — Original Botan-based cryptography work, technical research, reference material, save-swap guidance, and now the newly added save editor.
- The Botan Project — Legacy crypto research/reference credit; this release uses managed .NET cryptography, not a bundled Botan binary.
- Smidge — Provided the reference database and schema examples that helped make additional upgrade options possible.
- Stalin — Identified and helped correct text-asset round-trip requirements
- Codex — Development assistance, debugging, automated round-trip testing, UI refinement, and documentation.
- DB Browser for SQLite (https://github.com/sqlitebrowser/sqlitebrowser) UI/workflow reference
- ForzaTechStudio (https://github.com/D3FEKT/ForzaTechStudio)
