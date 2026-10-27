# Versioned clean GameDB references

FH5 support adds `gamedbRC.stock.fh5-830738948.sqlite.gz`: verified clean PC/Xbox build `3.688.109.0`, 933 cars, 899 engines, 234 tables, database stamp `-830738948`. Uncompressed SHA-256: `51EE285919A1572E915E6F66671CC49C34BF15360ACD899C0959DFBA5C190545`. It was authenticated against the saved original encrypted container; it is not the modified all-cars-1-CR test or the Bugatti-only subset. FH2, FH5 and both FH6 references remain separate. GameDbIdentity identifies the game from schema; StockDatabaseCatalog requires both the same game and exact stamp. FM8 is recognized independently and never falls back to a Horizon stock reference.

FH2 support adds `gamedbRC.stock.fh2-2067577858.sqlite.gz`, from the user-supplied `fh2 gamedbRC.slt` (plain SQLite, 1,024-byte pages, 354 cars, 288 tables, stamp `-2067577858`). Uncompressed SHA-256: `4D50013DC7705DE05A074E4421679E16A3A1EDA0816841861B0B7139ECF45517`. The two supplied modded FH2 files have the same schema/stamp; they are comparison samples, not restore baselines. Selection remains exact-stamp, never a fallback to FH6. Unknown FH2 versions retain ordinary editing but cannot promise reference-based restore or reversible steering.

The verified October FH6 update is also embedded as `gamedbRC.stock.661353983.sqlite.gz`: 682 drivable models, build stamp `661353983`. It matches the previously inspected official update across all 207 tables. Garage Viewer defaults explicitly to this catalog; version stamps must never be sorted to guess the newest build. The original 671-model reference remains intact for older modded DB comparisons and update merges.

Keep the original `gamedbRC.stock.sqlite.gz` when adding a future update's clean baseline. The current verified reference has `VersionInfo.database_version = -1107070983`. This is an opaque database-build identifier, not the app's version or a sortable game version.

For a future verified, unmodified full GameDB, add a gzip-compressed SQLite file named `gamedbRC.stock.<build-label>.sqlite.gz` in this folder. The project embeds these files automatically. Do not include modified databases, single-car exports, duplicate references with the same stamp, or fabricated future versions. The catalog selects by the stamp inside the database, not the filename. More than two historical versions can be retained.

`Merge updated DB` must use the old clean reference matching the **modded input**. The updated top input is the clean destination; it need not already have an embedded reference. Missing/ambiguous matches require the user to supply the old clean `.slt` or `.sqlite`. The external reference must really be unmodified: matching `VersionInfo` alone cannot establish that.

Car Editor selects a matched embedded reference on every load. An unknown or ambiguous stamp disables stock comparison/restore and reference-dependent operations; ordinary editing/export remains available. Embed the newly verified reference to enable these features for the new game version.

The merged output keeps the new clean DB's version stamp. Consequently its next update merge uses that version's clean baseline, not the earlier one. Keep original clean databases and modded backups independently of the embedded references.
