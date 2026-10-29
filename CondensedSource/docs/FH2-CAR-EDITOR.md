# FH2 Car Editor compatibility

Open the FH2 `.slt` directly in **Car Editor**. Edit the private working copy, then **Export SLT** to a new filename. The supplied FH2 SLTs are already SQLite: export preserves that format and the 1,024-byte page size. Do not run them through FH6 encryption. Back up your installed FH2 DB before replacing it through your existing FH2 mod setup. Separate [FH2/Azimuth save support](FH2-SAVES.md) is available in Profile Editor and Garage Viewer; Save Swap remains FH6-only.

## Mapped options

| Existing control | FH2 behavior |
| --- | --- |
| Engine swaps / stock engine | Uses FH2's own engines, upgrade rows and donor specs; no FH6 engines or sound pack is inserted. |
| Motors / electric conversion | Uses the native FH2 motor and donor powertrain. |
| Power builder | Native turbo drop-off scale, camshaft redline (bounded by torque curve), engine mass, weight distribution and motor torque. Shared engines still affect all cars using their IDs. |
| Rims / tire width / wheelbase | Writes the matching FH2 upgrade/geometry fields. |
| Tire profile / track width | Controls remain visible but disabled for FH2. Direct track-offset edits showed no effect in-game; use the separate FH2 wheel mod. Existing DB values are preserved. |
| Race / Rally suspension | Clones missing upgrades from the selected car's stock physics. Keeps valid FH2 suspension-type IDs and native tuning bounds, not modern FH6 damping fields. |
| Custom drift | Adds a private **extra Race-tier** part; FH2 has no native Drift menu tier. Uses the target's stock geometry, FH2 Race tuning ranges and lowered limits. Applying again reuses/repairs the part. Uses an additional Race-menu option rather than a native Drift tier. |
| Steering angle | Optional, **whole-car** FH2 override. Enable Override steering angle and Apply. To undo after reopening, uncheck it and Apply; both original angle fields are restored and the custom kit remains. Export to keep either change. |
| Whitewalls | Copies FH2's own `whiteWall` / vintage donor set and matching tire brand. |
| Car bucket | Writes the selected native bucket ID. Adjacent FH2 string tables can resolve names; unknown text references remain explicitly untranslated rather than borrowing unrelated FH6 labels. |
| Engine labels / stock restore | Uses only the exact-stamp FH2 reference. Restore removes private added parts/links but preserves donor engines, engine names and records shared with other cars. |
| Prices | Changes native `Data_Car.BaseCost`; does not grant DLC or alter hidden-content flags. |

FH2 does not support the modern Autoshow/release-date, Forza Edition tire, enhanced-handling or preset-based widebody options. Tire-profile and track-width controls stay disabled; use the separate FH2 wheel mod.

## Native thumbnails

Car Editor and FH2 Garage Viewer read stock previews directly from `Mount\media\stripped\RC*.zip`, including the native Xbox texture layout. All 354 native model previews were decoded without extracting or changing the archives. No extra native decoder DLL is required. The images show stock models, not a save's paint or installed modifications.

Auto-find follows the loaded DB/save location and Azimuth's `data\azimuth.ini` title-folder setting. The emulator and game folders can have arbitrary names. Use **Folder / Thumbnail folder** to select a relocated game's outer folder, `Mount`, `media`, `stripped` or converted image folder. Car Editor's **Archive** button also accepts a resource ZIP. Manual sources are remembered separately for each game and shared between Car Editor and FH2 Garage Viewer.

## FH2 Extra Options compatibility

The supplied `Downloads\Azimuth` package contains FH2 Extra Options: the DLL reports v0.2.98, while the INI header still says v0.2.88. Its required Azimuth 0.9.10 executable SHA256 matches the user's installed `azimuth.exe` exactly.

The editor keeps FH2's native SQLite SLT format, 1,024-byte page size and original schema. Extra Options' virtual all-rims/stock-rim queries and front/rear aero lookups were checked against private original, modded and editor-exported DB copies. The expected tables/columns remain intact, including `Data_Car.StockWheelID`, `List_Wheels`, `WheelCategories`, `WheelAnnotations`, `UpgradeTypes` and the native aero records. No runtime-generated rim category or virtual wheel row is baked into the DB.

Extra Options declares file priority `mediaoverride > mediapc > original media`. Check higher-priority copies if an installed DB edit appears ignored. Save-checksum bypass is not required by the tool: save exports continue to write valid revision manifests. The supplied DLL/config and installed game files were inspected read-only. The user subsequently confirmed in-game compatibility with Extra Options enabled and an editor-exported DB.

## Reversible steering

FH2 stores `SteerMaxAngle` and `SteerMaxAngleFiltered` in `Data_Car`, not per suspension upgrade. The override therefore applies even with stock suspension installed. The matching embedded stock car supplies the reset values after reopening; no sidecar or extra database table is needed. Unknown/custom cars with no matching original donor cannot enable the reversible angle override. Drift suspension without a steering override remains available when its native physics are complete.

Custom drift uses part ID `carId × 1000 + 905` and private physics IDs `+905` / `+906`, stored at the verified FH2 Race tier (`Level=3`). Conflicting IDs or shared physics cause a refusal and rollback, not an overwrite. This avoids assuming that FH2 understands FH6's Drift level or suspension IDs 701/702. The game may label this extra choice as Race; this is not claimed to be a native FH2 Drift tier.

## Comparison samples

- `fh2 gamedbRC.slt`: supplied original reference, 354 cars / 355 engines.
- `fh2 modded.slt`: same car definitions; changed content flags, prices, bucket mappings and other mod settings.
- `fh2 modded 2 (extra cars etc).slt`: 356 cars, including two additional definitions with their own engine/body/upgrade rows.

The editor preserves imported/custom rows on load/export; it does not automatically copy those unrelated mod settings into other databases. All three use the same 288-table schema and version stamp `-2067577858`. An unknown/ambiguous stamp disables reference-dependent actions instead of restoring from a different game/version.

Offline checks cover all cars in all three inputs, edits, native physics IDs, duplicate/reapply handling, rollback on conflicts, export/reopen, original-angle reset with the kit retained, stock restore, schema preservation and unchanged source files. FH2 Car Editor and Extra Options compatibility have also been tested in-game. Keep backups when testing new edits.
