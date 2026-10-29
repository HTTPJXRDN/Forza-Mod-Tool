# Editing the tab help

Edit the matching `.txt` file in this folder to change a tab's help:

- `Crypto.txt`
- `CarEditor.txt`
- `AnimationSwap.txt`
- `SaveSwap.txt`
- `ProfileEditor.txt`
- `GarageViewer.txt`
- `DbBrowser.txt`

Use short steps and plain-text bullets. Save as UTF-8. Wording and line breaks appear as written; Markdown is not rendered.

For permanent changes, edit these source files and build normally. They copy to `Help/` beside the app and are included in publishing. No C# or XAML changes are needed.

To edit a built app directly, change its `Help/*.txt` beside the EXE, then close and reopen the help window. No app restart is needed. A later build can replace those output copies, so keep your permanent edits here.

Include the `Help` folder when distributing the app. Built-in copies provide a fallback if the external files are missing.
