# Changelog

## 0.2

### Added

- A visual Image Path Functions editor, available from supported image fields or as
  a standalone tool. Adjust images without changing the original files.
- Image effect layers with drag-and-drop ordering, visibility controls, crop, resize,
  rotation, color and transparency adjustments, palette tools, a color picker, zoom,
  and undo/redo. Compare the result with Wesnoth and export a rendered PNG.
- A campaign-opening notice explaining incomplete imports, with actions to open the
  WML Tester, reload the campaign, or continue using the available fields.
- Controls in Campaign Metadata to pin a campaign to the active Wesnoth installation,
  replace its existing pin, or unpin it to follow the active installation.

### Improved

- Library unit previews resolve more inherited values, races, descriptions and images,
  including content supplied by macros and linked add-ons.
- Library race groups and thumbnails load in the background and update incrementally,
  reducing flicker and scroll jumps. Cached summaries speed up later launches.
- Colored unit notices explain preprocessor preview contexts, unavailable inheritance
  and definitions that are inactive for the selected Wesnoth version.
- The WML Tester detects malformed assignment names and extra macro arguments, with
  clearer source locations. Problems that block campaign reading are marked as Errors.
- Portable update instructions explain how to retain an existing Library, settings
  and campaign metadata. Compatibility with v0.1.1 data has been verified.

### Fixed

- Unit saves in campaigns with bundled add-ons or custom unit loaders, including
  After the Storm/Naia and A Song of Fire.
- RAW editing and saving of local events in scenarios reopened by macros, including
  After the Storm events that previously offered only View WML.
- Difficulty-specific editing blocked by otherwise valid campaign WML, and supported
  singleplayer editing in campaigns that also contain multiplayer code.
- Image Editor access for supported campaign difficulty icons, including the default
  difficulty. Applied icon edits now appear immediately in pending changes.
- External campaign loading when matching Wesnoth content is available, and quoted
  Core unit references incorrectly appearing as unknown units.
- Ordinary unit saves being blocked by missing image or sound files. The Tester
  continues to report missing resources as warnings.

## 0.1.1 — First public release

- Create and edit singleplayer campaigns, scenarios, sides, unit types and stories.
- Build basic events visually and reuse campaign and Library presets.
- Inspect imported WML, dependencies and resources, with RAW editing for unsupported structures.
- Open campaigns directly from a configured Wesnoth installation, with their scenarios and images resolved.
- Edit combined WML assignments in arbitrary key order, including difficulty-specific values, while preserving companion fields.
- Run self-contained Windows x64 and Linux x64 packages with portable Library and settings storage.

WCC targets Wesnoth 1.20 and its 1.19 development line. Custom WML support is still
expanding; structured editing remains limited to source patterns WCC can safely verify.
