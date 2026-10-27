# Garage Viewer

Garage Viewer supports FH6 C_ProfileData and FH2/Azimuth revision saves. Back up the whole save folder and work on a copy.

## FH6

1. Load C_ProfileData. The built-in stock catalog loads automatically.
2. Search by name, manufacturer, year or ID. Ctrl/Shift-click selects several cars.
3. Use the right-hand list to add stock cars, or select owned cars to remove, duplicate or set the current car.
4. Edit Original Owner, Distance driven or Top speed, then click **Apply car fields**. In a batch, check only the fields to replace. An empty checked owner field clears that label.
5. Click **Export save**. With FH6 closed, install an encrypted copy as C_ProfileData.

Original Owner is a label, not account identity. Distance and speed use raw DB units. Current/pending cars, barn finds and other protected models cannot be removed.

Adding or duplicating supported non-DLC cars also completes their matching Car Collection Journal entries. DLC cars can still be added; their Journal entries are excluded from this sync. Removing a car does not undo Journal completion. No DLC license or reward claim is granted.

If Add is disabled, read the status message. An unresolved current-car reference can block other garage actions while supported additions remain available. Revert all edits returns to the loaded snapshot.

### Previews

FH6 reads cached per-car images from `%LOCALAPPDATA%\ForzaHorizon6\LocalStorage_Cache\CacheThumbnails`. Choose another cache folder if needed. Cached images can show paint and installed parts; stock fallback images are labelled. Missing or invalid images do not block editing.

Use Auto-find or Stock thumbnails folder to choose stock previews. Drag the dividers to resize the lists, fields and larger selected-car preview. Sources are remembered per game.

## FH2 / Azimuth

Click **FH2 save…** and choose a revision or the revisions folder. Add, duplicate or remove supported stock cars, or edit their owner/history fields. Batch Original Owner is supported; distance and top speed are single-car edits. FH2 uses its own stock catalog and native previews.

**Export save** writes a complete revision. Close FH2 and Azimuth, then back up the whole User save folder. Copy the same exported commit and entire data folder into **both numbered folders inside revisions**, keeping their existing names. Replacing only one can stop the edited save loading.

Each tab keeps its own working copy. Export and reload the result in the other tab to combine edits. See the [FH2 save guide](../docs/FH2-SAVES.md) or click **?** for help.
