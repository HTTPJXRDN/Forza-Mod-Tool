# FH2 / Azimuth saves

This supports the inspected Azimuth **complete save revision v2** layout with `VersionFlags = 6`. It is not a general Xbox 360/STFS or encrypted-console save converter, and does not use FH6 profile encryption.

**Latest confirmation — 2026-10-26:** the user confirmed bulk Storm Island boards, spendable skill points, inserting missing perks and the remaining XP/wristband checks in-game. These join the previously confirmed mainland career repair, championships, both road maps, cameras and owned-car Promo. Earlier captured-save and validation notes below describe the evidence available at those times; their pending-test statements for these fields are superseded by this confirmation. Unsupported Storm Island finale/story actions remain excluded.

## Load

FH2 saves require [x64 xcompress64.dll](https://github.com/D3FEKT/ForzaTechStudio/blob/Main/ForzaTechStudio/Native/xcompress64.dll). Click **Download raw file** on GitHub, put the DLL beside `FH6ModStudio.exe`, then restart the tool. It is not included in the release.

1. Back up the entire `User_<account>` folder, including all revisions. Keep the backup outside Azimuth's live saves.
2. In **Profile Editor** or **Garage Viewer**, click **FH2 save…**.
3. Choose a numbered revision folder, its `data` folder, or the containing `revisions` folder. The numbers change as the game saves; there are no fixed folder numbers to use. The parent chooses the highest revision number. A corrupt newest revision is refused instead of silently loading an older one.
4. A revision's `commit`, `ProfileData` or `PlayerDatabase` file can also be selected using the normal load dialog; Garage Viewer accepts a dropped revision folder/file.

The inspected path layout is:

```text
<Azimuth>\data\titles\<title>\saves\player-1\User_<account>\revisions\<revision>\
    commit
    data\BinarySaveState
    data\PlayerDatabase
    data\ProfileData
    data\SaveState
    data\VersionFlags
```

All five sizes and SHA256 hashes must match `commit`. Missing/extra blobs, unsupported versions, malformed wrappers/padding, unsafe linked paths, invalid XML or an invalid SQLite database are refused. The source is kept read-only; garage edits use a separate temporary SQLite copy.

## Supported edits

- **Profile Editor:** credits, available skill points, wheelspins, Driver XP with a level preview, base/island board groups, separate mainland/island roads and championship completion/mainland career repair. Click Apply for each numeric value, then Export save.
- **Available skill points:** edits `SkillsState.CurrentSkillTokensTotal`, the spendable Perks balance. The earlier implementation edited `/Main/NumSkillTokens`, a different statistic; that lifetime counter is now preserved. Raw earned/unspent skill-chain scores and perk unlocks are unchanged. Allowed balance: 0–2,147,483,647.
- **Driver XP:** enter XP and use its single Apply button. Level/wristband is a read-only preview; Apply preserves exact XP instead of rounding to a level boundary. Both `/Main/XP` and `/CompareStats/WristbandLevel` are synchronized with `/Main/Level`, the zero-based index **within the current wristband**, not the absolute HUD index. Native level 5 matched saved index 4 at 65,503 XP; the later native 10,071,953 XP / HUD 311 sample matched Gold index 161. Uses stock `PlayerXPLevels.xml` wristband intervals, not `CareerPlayerLevels.xml` or FH6 progression. Preview supports displayed levels 1–1000. Recognized earlier-tool absolute indices can be repaired explicitly by applying XP; load/no-edit export never repairs them silently. Unknown mismatches, higher unsupported XP and pending XP disable editing. The latest game-written capture retains 10,079,562 XP with the matching Gold index 161 / displayed-level preview 311. The remaining XP/wristband checks were subsequently confirmed in-game. No level-up rewards are granted.
- **Bonus boards:** choose all base-game boards, a base group (Fast travel / Small XP / Medium XP / High XP), or Storm Island upgrade boards. Base-game definitions contain 150 boards (50 / 50 / 35 / 15); Storm Island adds 25 canonical upgrade-board entries after visiting it. Updates each selected native State to Collected, its group counters and the combined overall total (up to 175). Mainland completion preserves island progress and vice versa; runtime island alias objects stay unchanged. Unknown IDs/states, incomplete lists or inconsistent counters disable completion. Existing collected boards are retained and repeating an action does nothing. No XP, credits or leaderboard results are fabricated.
- **Roads:** separate buttons fill the recognized mainland bitmap plus `/CompareStats/RoadsDiscovered = 315`, or island bitmap plus `/Island/RoadsDiscovered = 50`. Supports the native five-record mainland layout and six-record layout after visiting Storm Island; an absent island record is not invented. Requires known map identities, exact payload sizes/trailing words and valid counters. Each action preserves the other map, framing and all other binary records. A bitmap has 32,768 sample bits, not that many roads. Both road maps were confirmed working in-game; the subsequent game-written revision retained 315 / 50 discovered roads. Back up the entire User folder and verify both maps/statistics after export/restart. No XP, credits or career completion are awarded by these buttons.
- **Mainland championships:** one destination completes its 28 championships/races without changing story progression. **All mainland + finales / career repair** completes 168 championships / 672 races, both mainland finales and post-story road-trip mode. It synchronizes the selected active championship even if its lifetime wins already exist, unlocks the six mainland destinations and championship/finale events, and clears pending mainland-finale state. Run it again on an already-168/168 save to repair the earlier scripted-career dead-end. Uses native `Campaigns`, `AnthemEvents` and `AnthemEventRaces`; the two mainland finale stages share event/race 201. Database, counters and the supported XML fields change atomically. Existing history is preserved, including native pruned history; paired first-place results with target 0 or 100 retain their finish counts. New history reuses the save's recognized offline 12-driver identities. Unknown schemas/rosters, inconsistent counters, unknown career modes or blocking non-finale events are refused. Single-destination edits still refuse pending finales. Showcases, Storm Island, XP, balances and reward cars stay unchanged; no donor account or cars are copied. Post-story career repair was confirmed in-game: the game no longer requests already-finished championships, and the subsequent live revision retains both finale flags and native post-story mode.
- **Storm Island championships:** after its introduction, a separate action covers the 29 pre-finale championships/gauntlets and their 88 races. Uses island-specific records/counters and shared race/event results with native island IDs. The two legitimate captured island finishes left `IslandCareer_ChampionshipHistory` empty, so the editor preserves it rather than using mainland history logic. Existing unrecognized history, online rosters, changed schemas and pending island finales are refused. The single final-gauntlet campaign is excluded; finale/story/reward-car flags, finale-progress percentages, XP, credits, spins and leaderboard results are not awarded. This is not a full story/finale-completion button. Editor-generated pre-finale championship completion was confirmed in-game; the subsequent revision retains all 29 wins.
- **Local unlocks:** Unlock all perks inserts missing native `PerkUnlock` IDs 0–24 and synchronizes `/CompareStats/PerksUnlocked`. It does not deduct skill points or rewrite skill-chain scores. Mark owned cars photographed adds missing `PhotoCaptures` records for supported models already in the garage and synchronizes `/CompareStats/PhotoChallengesCompleted`. Existing captures are preserved; PG bus/truck, the null placeholder and unknown garage models are skipped. The 351-model mapping/count was compared against the reference saves; no actual images, cars, XP or reward payouts are created. Unknown IDs, schemas, linked foreign keys/triggers or inconsistent photo counts disable the relevant action.
- **Speed-camera discovery:** separate Reveal buttons set only existing `CameraState.Discovered` flags for the 30 mainland traps / 15 zones or five island traps / five zones. Island objects must already exist; definitions are not invented. `Completed`, `PersonalBest`, triggered counters, profile/database data and the other region are preserved. These are discovery actions, not completion or score-generation buttons. Unknown object lists, booleans or score formats are refused.
- **Garage Viewer:** add supported stock FH2 cars, duplicate selected cars with new IDs/VINs and copied purchased parts, remove selected unprotected cars, and edit one car's owner label/distance/top speed. Values use native stored units. The owner label is not account identity.
- **Batch Original Owner:** Ctrl/Shift-select owned cars, enter an Original Owner label, then click **Apply Original Owner to N cars**. Mixed owners display an empty field; entering an empty value deliberately clears the selected labels. Only the labels change, in one transaction: distance, top speed, other cars and account identity stay unchanged. Distance/top-speed controls remain single-car only. Export save afterward.
- Current/pending garage references, barn-find cars and non-removable models are protected. Unknown models cannot be removed through the UI. Keep at least one garage car.
- Adding cars uses the embedded FH2 DB only. Two reference models with ambiguous stock weight-reduction parts are excluded from Add (IDs 326 and 1271); owned copies remain identifiable. DLC content still requires its matching installed content/license.
- FH2's `LastUsedGarageId` allocator advances with additions/duplicates and does not rewind after removals. This prevents later in-game purchases reusing editor-issued IDs.
- No FH6 Collection Journal completion, online unlocks, account swapping or current-car switching is performed.

**Validation:** preserved native captures cover base-board collection, a race/early level-up, a mainland championship finish, Storm Island's introduction and normal championship finish, and an island upgrade board. The later gold sample plus the user's HUD-311 confirmation established the wristband-relative saved index; earlier absolute-index behavior was not correct. Four supplied progressed saves and native definitions informed the new all-mainland finale/post-story repair and local unlock mappings. On 2026-10-26 the user confirmed mainland career repair, championship records, both road maps, cameras and owned-car Promo in-game. A stable, hash-verified capture of live revision 0136 retains 168 mainland wins, 29 island wins, 348 photographed models and all 55 discovered cameras; career flags match the progressed reference saves. The capture already had 25 perks before these edits, so it does not independently verify inserting missing perk rows. The remaining XP/wristband checks were subsequently confirmed in-game. Island story/finales, showcases, bucket-list/barn-find completion, raw skill-chain state and arbitrary save records remain unsupported.

Profile Editor and Garage Viewer retain independent working copies. Export and reload the exported revision in the other tab to combine their changes. Back to FH6 returns to that tab's existing FH6 interface; it does not convert FH2 data into an FH6 save.

## Export and install

1. Click **Export save** and choose an existing output parent folder outside the live save area.
   The picker starts in Documents/Desktop. A blocked location displays a message without starting export; choose a normal output folder, not the live User/revisions tree.
2. The tool creates a new `FH2-save-edited-...` folder. It contains `commit` and the complete `data` folder, not one standalone edited blob.
3. Exports preserve `VersionFlags` and all unedited blobs, retain `ssmc` wrapper capacities, regenerate manifest sizes/hashes and reopen the complete result for verification. `BinarySaveState` stays byte-exact unless a road action was used; each changes only its recognized map bitmap. A no-edit export reproduces all original file bytes, including `commit`. Changed source files or existing/live output targets stop export.
4. Close the game **and Azimuth**. Back up the entire live User save folder again before installing anything.
5. Copy the same exported **`commit` and entire `data` folder** into **both numbered folders inside `revisions`**, replacing their contents. Replacing only one can stop the edited save loading. Keep both existing folder names and the account/title directories. The tool does not install files automatically.
6. Launch and verify a small change first. If rejected, close Azimuth and restore the complete backed-up User save folder. Never mix an old manifest with edited blobs.

FH2 save editing has been tested in-game. Keep the full backup when testing new edits or a different Azimuth build.

## Development test history

These dated notes record earlier builds. Pending tests listed here have been superseded by the latest confirmation at the top.

### Offline validation — 2026-10-24

- 888 checks passed against both supplied revisions. No-edit exports matched every original file byte, including the manifest.
- Balance edits, maximum unsigned values, wheelspin XML isolation, native wrapper capacities and exported manifest reloads passed.
- Garage add/duplicate/remove/detail edits passed, including a batch of all 352 supported stock configurations. Unrelated tables and all non-database blobs remained unchanged. Native ID allocation continued above removed editor-added IDs.
- Corrupt hashes, missing/extra files, invalid wrapper lengths/padding, unsupported versions, XML DTDs, linked inputs, stale sources and live/existing output targets were refused. Unknown garage links, foreign keys, triggers and ID allocators became read-only; unknown stock fields were not guessed.
- Both tab hosts and FH2 surfaces loaded/rendered. An actual FH6 profile still parsed/exported on a private fixture, its catalog remained FH6-specific, and cross-game catalog additions were refused.
- The async path used by the Export button passed edited profile/garage exports and reloads. Live-folder rejection returned normal UI feedback, stale-source worker errors were contained, and unrelated revisions/commit names no longer blocked valid destinations.
- Debug and Release builds completed without warnings or errors. The live FH2 revision files remained byte-for-byte unchanged throughout the tests.

### Batch owner follow-up — 2026-10-24

- 1,335 checks passed using preserved revision fixtures, without editing live FH2 saves. Debug and Release builds remained clean.
- The actual multi-select UI action and exported reload retained the requested owner labels, kept the selection, and preserved every other garage field, unselected car, unrelated table and non-database blob.
- Empty labels, Unicode/apostrophes, duplicate selections and the 256-character limit were checked. Invalid labels, missing cars and read-only layouts were rejected without partial edits; UI errors returned normal feedback and retained entered text.

### XP, boards and spendable skill points — 2026-10-24

- 5,117 offline checks passed, including original no-edit exports, all 1–1000 level start/end boundaries, duplicate XP counters, combined/staged edits, and export/reload.
- The actual XP, level, skill-point and board-group UI buttons were exercised. Invalid level input returned normal feedback; board selection/counts refreshed and survived export. The new dark/pink board picker was visually checked.
- Skill-point changes preserved every ProfileData byte and all unrelated XML bytes. Board completion preserved unrelated properties/objects and did not award XP, credits or spins; repeating it was a no-op. XP-only edits preserved the XML, database and opaque blobs exactly.
- Mismatched XP counters, pending XP, unknown board IDs/states, incomplete board definitions and inconsistent counters were refused without partial changes. Native level 5 and spendable skill points 1 matched the preserved screenshot-time save.
- These are offline consistency checks and live observations, not confirmation that the newly edited XP/boards/skill-point balance persist in-game.

### XP-only Apply and gold-preview follow-up — 2026-10-24

- 5,147 checks passed after removing the editable level row. The actual UI retained exactly 10,000,000 XP across Apply, board edits, export and reload; its read-only preview displayed gold level 310.
- All level boundaries were rechecked with the corrected first gold +300 increment. Known earlier-preview counters were recognized without changing a no-edit export; explicit XP Apply repaired the staged level index. Unrecognized mismatches remained disabled.
- Verified live snapshots 00d8 → 00d9 increased both XP counters from 9,994,000 to 9,994,024 while the saved level stayed 310. The live files were copied/read only. This confirms that sample retained edited XP after earning 24 XP; it does not verify persistence of the newly corrected level index.
- Debug and Release builds completed without warnings/errors. The simplified dark/pink XP UI was rendered and visually checked.

### Experimental mainland roads — 2026-10-24

- 5,724 offline checks passed, including the existing profile/garage/FH6 regression checks and the new road tests. Debug and Release builds completed with no warnings/errors; the road controls were rendered and visually checked.
- Three preserved native captures matched the supported binary/map layout. Road completion changed only the 4,096-byte bitmap and its 315-road statistic; other records, map ID, endpoints, footer, XML, database and unrelated properties were preserved.
- Export/reload, repeat completion, exact no-edit export and combined XP/boards/skill-point/credit edits passed. The actual Discover all roads button refreshed its status and exported the staged result.
- Unsupported framing, names, UTF-8, markers, endpoints, map IDs, footer, payload sizes, trailers and out-of-range counters were refused without partial edits; unrelated supported edits remained available. An empty required blob was refused on load.
- These checks validate isolation and file consistency, not in-game acceptance or unused-bitmap-bit behavior. Live saves were not edited. Championship completion awaits a legitimate finish-transition comparison and remains unsupported.

### Mainland / Storm Island completion and native gold index — 2026-10-24

- 7,143 offline checks passed, including actual mainland/island UI buttons, selections/status refresh, exported reloads and repeat-action no-ops. Native captures and preserved fixtures remained unchanged; tests never wrote live FH2 saves.
- Mainland completion reached 168 championships / 672 races. Storm Island completion reached 29 pre-finale championships/gauntlets / 88 races, explicitly excluding its final-gauntlet campaign. Native participant identities were retained and unrelated tables/maps, XML story/reward flags, XP and balances were isolated.
- A failed mid-transaction write was injected for each region. Database, XML and profile changes rolled back together while retaining earlier staged edits. Unknown schemas, linked data, online rosters, malformed history, inconsistent counters and overflow were rejected.
- Independent mainland / island road edits, five-/six-record framing, map/counter isolation, combined staged changes and absent/unknown-map refusal passed. New game-written captures later retained 315 mainland and 50 island road counts; rendered maps/unused bits remain unverified.
- A legitimate island board changed canonical `island_discount_board_upgrades_010` to Collected, the island upgrade count to 1 and the combined board total to 151. Completion preserved runtime aliases and base groups; the completed combined total was verified as 175, not 150 or 200.
- Native 10,062,269 → 10,071,953 XP retained saved Gold index 161; the user confirmed HUD level 311. Preview/Apply now uses the wristband-relative saved index, recognizing earlier absolute-index patterns for explicit repair without auto-mutating no-edit exports. All displayed-level boundaries 1–1000 were rechecked offline; other native wristband transitions and newly applied corrected XP still need game tests.
- Older validation sections above describe the implementation at those times. The current supported behavior and remaining limits are listed at the top of this document.
- Final Debug and Release builds both completed with zero warnings/errors. Updated mainland/island UI renders were visually checked; new release files were not generated.

### Mainland finale / post-story repair — 2026-10-25

- 5,579 targeted offline checks passed in addition to the 7,143-check FH2 regression suite. The captured 168/168 dead-end now receives active-event/standings repair, both mainland finale results and the supported post-story state; repeat completion is a no-op after export/reload.
- Four supplied progressed references agree on user-selected road trips, playthrough 5, four scripted playthroughs completed and the road-trip creator unlock. The inspected native state-flow distinguishes these gates from lifetime championship records. Reference profiles with compatible offline rosters passed exact no-edit exports and repair; no donor identity, roster, cars or whole save state was copied into the user's fixture.
- Existing finale finish counts, pruned history, paired target-zero first-place results and the reference's second-finale progress above 100 were preserved. One-destination edits left story XML and finale results unchanged. Island tables/state, unrelated profile values and opaque blobs stayed unchanged.
- Injected failures during destination/finale writes rolled back database, XML and properties together while retaining earlier staged edits. Changed story schemas, invalid playthrough lists, mismatched/overflowing finale results, unknown target values, unknown modes/booleans and blocking non-finale events were refused without partial edits. The actual UI action/export/reload passed and its layout was visually checked.
- No live save or supplied archive was modified. Game acceptance and removal of the stuck championship prompt still require the user's export/install/restart test.

### Local unlock mapping — 2026-10-25

- 1,827 targeted checks passed for perks, owned-car Promo flags and separate mainland/island camera discovery. All four supplied references passed the supported mapping checks. The earlier 7,143-check FH2 suite and 5,579-check mainland repair suite also passed after these additions.
- Missing perk rows were restored to 25 without changing skill points. Owned-photo completion preserved existing captures, excluded the three PG/null models, skipped unknown cars and kept the count within the 351-model reference. Camera discovery preserved completion flags, personal bests, triggered statistics and the other region; absent island objects were not invented.
- Actual UI actions, status refresh, export/reload and repeated-action no-ops passed. Unrelated properties, tables, XML objects and opaque blobs stayed unchanged; injected perk/photo write failures rolled back while retaining earlier staged credits. Unknown IDs and malformed camera values were refused. The new controls were rendered and visually checked.
- These checks establish file consistency and isolation, not in-game acceptance. No live save, original reference archive or installed game file was changed. Bucket-list and barn-find completion remain deferred pending their linked-state mapping.
