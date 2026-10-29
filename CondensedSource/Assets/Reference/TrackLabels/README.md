These small gzip files are clean native Upgrades.str fallback tables for track-width labels: FH6's 24 shipped locales (v8 UTF-8), and FH5 CZ/EN/GB/HU (v4 UTF-16LE). They come from the locally installed games' native language ZIPs. Original table keys, strings, sections and encodings are preserved.

Exports prefer detected installed native tables and MediaPC overrides. The fallback allows offline exports when an installation is unavailable. Generated files go beside the exported full GameDB, never into the installed game. Car-related DB exports do not collect string files or other assets.

In Car Editor, **Export string tables** is on by default for FH5/FH6. Uncheck it for DB-only output, or use **String tables: Auto…** to select a stringtables folder manually. **Auto** restores install detection.

For each language, loose MediaPC overrides take priority over MediaPC ZIPs, then loose media tables and media ZIPs. Valid language ZIPs such as EN.zip are supported. A missing MediaPC stringtables folder is normal: stock media tables can supply the baseline. Unused ZIPs are skipped, and unreadable required files are reported by path.

Generated overrides use mediapc/stripped/stringtables/<language>/Upgrades.str. Install the stringtables folder under the game's MediaPC/stripped after backing up existing overrides. Correct labels are reused across cars; DB-only output still needs matching installed labels for new sizes.
