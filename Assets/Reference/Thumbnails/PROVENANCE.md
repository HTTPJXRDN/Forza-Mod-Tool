# FH2 native thumbnail layouts

The two gzip resources contain unsigned 16-bit BC7-block source indices for FH2's native 816x459 and 192x108, one-mip, tile-mode-14 thumbnails. They contain no image pixels or executable code.

Both mappings were computed using XGCreateTexture2DComputer/CopyFromSubresource as an offline reference and checked against actual FH2 RC-archive images. Every output block exactly matched the native detiler. Runtime decoding uses these fixed maps and the existing BCnEncoder library; no XG DLL is bundled or required.

The native texture-header layout was checked against the MIT-licensed ForzaTools/ForzaTechStudio DurangoTextureContentHeader format definitions: https://github.com/D3FEKT/ForzaTechStudio/blob/Main/ForzaTools.Bundles/Metadata/TextureContentHeaders/DurangoTextureContentHeader.cs

Unsupported dimensions, layouts, mip counts and incomplete payloads are refused rather than guessed. PC thumbnail decoding retains its existing path.
