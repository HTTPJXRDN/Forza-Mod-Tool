# Bugatti engine import resources

`engines.json` contains five engine-only datasets from the owner's FH5 3.688.109.0 DB: EB110 SS (678), Veyron SS (788), Type 35 C (2194), Chiron (2753), and Divo (3300). Includes all associated engine-upgrade rows and every referenced full-throttle torque curve. FH6's two additional backfire scalar fields use 1.0. Donor cars are not imported.

`MediaPC.zip` is the export sidecar pack, not a normal ZIP to install unchanged. Car Editor extracts its install tree beside the chosen DB output only if a Bugatti engine has a new car link since that DB was loaded. It includes five modular engine XMLs, eleven FH6-authenticated granular audio ZIP/XML pairs, the encrypted manifest, eleven copy-paste XML lines in a separate TXT, and installation instructions. Nine shared native audio dependencies are reused without overwriting them. Native FH6 secondary-effect profiles are used. Only Chiron has been user-confirmed in game; the other four require in-game testing.

The full manifest is based on the owner's verified stock media manifest for FH6 DB version 661353983. It must not replace an existing custom manifest. The TXT is XML entries only; add them inside the existing decrypted `ZipFiles` root, then re-encrypt. Logical `game:\media\audio\...` paths are intentional even though every exported file installs under `MediaPC`.

Catalog imports and file exports reject collisions instead of overwriting unrelated data. Existing imported engine names/physics are retained when adding another link. Loading, removing, restoring, or rolling back a swap does not itself export files. No runtime process capture, FH5 installation, private cipher models, or research helpers are required by the application.
