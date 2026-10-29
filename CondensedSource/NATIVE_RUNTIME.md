# Required DLL for FH2 saves and FM8 LZX

**x64 xcompress64.dll** is needed for:

- FH2/Azimuth save loading, editing and export.
- FM8 method-21 LZX camera/car ZIP rebuilding and track extraction.

The DLL is not included in the release. Download it separately:

1. Open [xcompress64.dll on ForzaTechStudio's GitHub](https://github.com/D3FEKT/ForzaTechStudio/blob/Main/ForzaTechStudio/Native/xcompress64.dll).
2. Click **Download raw file** on GitHub.
3. Put `xcompress64.dll` in the same folder as `FH6ModStudio.exe`, then restart the tool.

Use the 64-bit file at that link. You do not need to install or run ForzaTechStudio.

Without the DLL, FH2 save and FM8 LZX operations are unavailable. Car Editor, DB Browser, database/asset crypto, FH6 ZIPs and the included animation helpers still work.

For a source build, put the DLL beside the built EXE. The public source project does not copy or bundle it.
