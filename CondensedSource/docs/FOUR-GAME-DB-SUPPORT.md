# GameDB support: FH2, FH5, FH6 and FM8

The game is detected automatically. No manual key/game dropdown is needed. Game identity uses independent schema signatures; embedded stock references additionally require the exact database stamp. A reference from another game is never used as a fallback.

| Game | Crypto tab | Car Editor |
| --- | --- | --- |
| FH2 | Supplied SLTs are already plain SQLite; no decryption needed. | Native mapped options, original-format export, matching embedded stock reference. See [FH2 notes](FH2-CAR-EDITOR.md). |
| FH5 | Authenticated PC GameDB decrypt/re-encrypt. Original matching encrypted SLT required for encryption. | Direct SLT or SQLite loading; encrypted SLT export, native FH5 mappings, matching embedded stock reference. |
| FH6 | Existing AES GameDB decrypt/re-encrypt. | Existing features and historical/current FH6 references retained. |
| FM8 | Existing authenticated TransformIT GameDB codec. | Existing decrypted-SQLite editor workflow retained. No Horizon reference is used for FM8 restore; direct FM8 SLT editor integration is still disabled. |

## Merging databases

All four games use the same Crypto workflows: regular merge, Merge updated DB, selected-car export/import/rebuild and focused body-kit import. Encrypted FH5, FH6 and FM8 inputs are materialized privately; FH2 outputs retain their plain-SQLite SLT format. Known cross-game inputs are refused.

- Regular merging carries selected edits, added rows and new custom tables. Add-only merging keeps existing base rows.
- Merge updated DB starts from the new clean update and applies changes relative to the old modded DB's matching clean baseline. The old baseline must match the old DB's version; the new update is expected to have a different version. New engines and custom additions can carry over, but incompatible schema changes still stop the merge.
- Car donors carry a game identifier even for sparse electric-car exports without engine rows. Existing donor files remain readable.
- Body-kit imports use each game's native tables. Optional newer aspect-ratio, track-spacing and preset tables are not invented for games that lack them. Core body tables must still be present and compatible.

For encrypted output, use a template from the output database's game and version. Merge previews and offline checks cannot guarantee that every custom part or engine is compatible in-game; test exports with backups before installing them.

## FH5 use

1. Close the game before installing changes. Back up the clean and any existing modded DB.
2. Open the FH5 `gamedbRC.slt` in **Car Editor**, or decrypt it in **Crypto** first and load the SQLite.
3. Select a car, set the desired options and Apply. Engine/motor operations have their own buttons.
4. **Export SLT** creates an authenticated FH5 output. If you loaded SQLite, choose its original FH5 encrypted SLT when asked. A different game/build template is refused.
5. Install the encrypted output as `MediaPC\stripped\gamedbRC.slt` through your existing FH5 mod setup. Keep the clean `media\stripped` copy untouched. Do not install plaintext SQLite as the encrypted game file.

FH5 uses native engines/motors, power fields, rims/width/profile/track upgrades, buckets, body parts, Race/Rally/Drift suspension and per-kit steering fields. Native whitewall/vintage sets and Horizon Edition tire models are used instead of FH6-only names; Edition availability covers FH5's HE as well as FE names. The verified clean reference enables stock engine labels and ownership-aware car restore. Shared engine/name/part behavior is unchanged.

Engine swaps list every engine already in the open DB, including custom/imported engines. Bundled FH5-to-FH6 Bugatti injection and automatic audio sidecars have been removed; existing DB records stay unchanged. The FH6 enhanced-Lotus package is not enabled for FH5. Car Editor reads native FH5 RC thumbnail archives in memory, prefers HiRes packs and auto-finds registered/common installations. Manual folder/ZIP sources are supported and remembered separately per game. Unknown bucket text is explicitly untranslated rather than guessed from another game.

FH5 profile saves have separate authenticated **decryption-only** support. **Crypto → Decrypt** exports the complete `.fh5-profile.decrypted` section stream to a selected folder, retaining all four save sections rather than extracting SQLite. Header/all chunk tags and database integrity are checked; the source is never written. FH5 save viewing in Profile Editor is disabled for now, including normal load/drop routes and manually decrypted profile streams. The unavailable FH5 save button has been removed. FH5 save editing, re-encryption, Garage Viewer and Save Swap are not enabled. This does not disable FH5 GameDB, asset or audio-ZIP support.

## FH5 General/File text assets

Crypto also supports `PhysicsSettings.ini` and other text assets using the same authenticated FH5 General/File profile. Drop the encrypted asset and click **Decrypt**; edit the new `.decrypted` file, then **Re-encrypt**. The session remembers its original template; after restarting, select that original encrypted asset if prompted. Exported files use `.modded` names and never replace inputs or previous outputs.

These assets use different data/MAC contexts from GameDB: a 36-byte authenticated header, 512-byte data / 16-byte encrypted-tag slots, continuous chaining and declared zero padding, with no DB CRC scrambling. The authenticated header selects FH5 automatically, including slot counts whose lengths also resemble GameDB files. Every chunk tag and padding byte is checked. Encryption authenticates the complete template, recalculates length/padding and all tags, then authenticates/decrypts the output to verify exact edited bytes. Line endings are preserved, not reformatted as classic INI.

The saved `PhysicsSettings.ini` reference decrypts to 64,937 bytes with SHA-256 `2B2E3A3B1A0650F80DCFB22EF88488021D87A5BD995B7E95CE49A7AB4E4D1803`; its original encrypted SHA-256 is `92EC16A51D619142A40BEC0918C6DA4EDB0229CBE0E4DDE997965FED98A2F329`. Offline round trips do not verify an asset override installation path or its edited behavior in-game. Do not replace a GameDB, ZIP or differently encrypted asset with this format.

## FH5 and ordinary ZIP workspaces

Drop an original ZIP onto Crypto and click **Decrypt**. This creates a new `.extracted` workspace, containing its original files plus `fh5-zip-roundtrip.json` and `.__original.zip`. Edit the extracted files without changing their paths, then drop the entire folder or the workspace JSON onto Crypto and click **Re-encrypt**. A new `.modded.zip` is created beside the workspace, or in the selected output folder. Keep both workspace metadata files; do not add/remove entries. An older research `Extracted` folder without workspace metadata must be extracted again from its original ZIP first.

Supported formats are single-disk ZIP32, ordinary stored/DEFLATE entries (methods 0/8) and FH5 General/File encrypted-DEFLATE entries (method 22). Every encrypted header and chunk authenticates with the separate FH5 File profile. Standard ZIPs have no encryption or game identity and are labelled accordingly. Each file's recovered length and CRC32 are checked; binary files and originally imperfect XML are preserved, not reformatted or repaired.

Rebuilding retains original entry order, names, compression/encryption methods, metadata, Forza data-offset/alignment fields and untouched packed payloads. An unchanged workspace produces a byte-for-byte original ZIP. Edited encrypted entries are recompressed and authenticated with fresh padding/header/chunk tags. The complete rebuilt ZIP is decoded and checked against the edited workspace before export. Rules.zip's four empty directory records retain their original stored-local / method-22-central metadata; they have no encrypted payload.

Large ordinary archives, including the root cinematics ZIPs larger than 2 GiB, are streamed rather than loaded into a single byte array. Limits: ZIP32 archives below 4 GiB, up to 65,534 entries / 32 MiB of central metadata, 2 GiB per ordinary file, 128 MiB per encrypted packed/plain entry, and 16 GiB total plaintext. ZIP64, descriptors, split archives, unknown crypto/methods, unsafe paths, collisions and workspace links are refused. Template fingerprints and new-only outputs protect originals and previous exports.

The folder's verified research covers all 31 root archives / 21,375 entries, including 1,553 encrypted file payloads in Camera, CMS, GameTunableSettings, ProfileSchema, Rendering, RenderScenarios, Rules and stateflow. The remaining 23 archives are ordinary ZIPs. This does not imply every nested FH5 format, key family or save container is supported; ProfileSchema.zip is schema data, not profile-save encryption. Research reports, README files and partial donor databases are not new encrypted asset types. Partial DBs must be merged into a complete matching GameDB, not installed as standalone databases.

Production-code offline checks also extracted and rebuilt every one of those 31 archives: unchanged outputs matched the complete original SHA-256, a one-entry test edit survived rebuilding, and every untouched compressed/encrypted payload remained byte-exact. Before/after original archive SHA-256 checks passed. An independent decoder authenticated all 322 entries of an edited Camera export and verified its DEFLATE boundaries, lengths and CRCs. Safety tests cover corrupted ciphertext/metadata, unsafe/colliding paths, missing/extra workspace files, template changes, output conflicts, links and edits made during export. FH6 ZIP and FH5/FH6/FM8 loose-asset regressions passed. Large disposable test workspaces/exports were removed after successful checks; game originals were never installed over or edited.

Exports do not install files, repair game syntax, or update an external `zipmanifest.xml`. A structurally valid, authenticated archive does not prove that its edits or deployment metadata will work in-game. Back up originals and test installation through the appropriate game/mod workflow separately.

## FH5 GameDB codec and reference validation

- Reference build: PC/Xbox `3.688.109.0`; `VersionInfo.database_version=-830738948`; 933 cars / 899 engines / 234 tables.
- Clean SQLite SHA-256: `51EE285919A1572E915E6F66671CC49C34BF15360ACD899C0959DFBA5C190545`.
- Trusted encrypted original SHA-256: `D9783671D2EAE2F38DA633A18B0A871CE7AA60D5930FD2C29D1B7269251265E1`.
- Saved data-only cipher contexts pass 32 independent vectors each. No live game/process access or captured native code execution is required.
- FH5 and FH6 share 36-byte framing. A valid FH5 header MAC selects the FH5 codec; the codec then checks every encrypted chunk tag and SQLite/header length. Encryption regenerates/authenticates all tags and verifies the recovered SQLite.
- Unchanged FH5 encryption reproduces the original byte-for-byte. The implementation also reproduces the independently generated all-cars-1-CR file exactly (`35D56A12CC926673F34F3B931588AD0F9315C283C84C279F787F8A9C361747B1`), which was previously reported working via the MediaPC override. That price screenshot did not prove every purchase or every new editor feature.
- Header, ciphertext-data and encrypted-tag tampering are rejected. Source files are read-only and exports use new filenames.

FH5 and FH2 Car Editor support were confirmed in-game by the user. This does not establish compatibility for every custom car/edit; test changes on a backup. No release package or installed game file is changed by this implementation.
