<p align="center">
  <img src="src/Wcc.App/Assets/Wcc/Icons/WCC.png" alt="WCC shield icon" width="64" height="64">
</p>

# WCC — Wesnoth Campaign Creator

**Create or edit Wesnoth packages with visual editors or WML.**

### [⬇ Download Build here](https://github.com/Eptine/WCC/releases/tag/Build)

**Windows & Linux x64 · Preview · Singleplayer**

[Features](#features) · [Planned features](#planned-features) · [Getting started](#getting-started) · [Build from source](#build-from-source) · [Feedback](#feedback)

WCC helps you create campaigns, scenarios, unit types, stories and basic events for
[The Battle for Wesnoth](https://www.wesnoth.org/). You can also work with existing
campaigns and add-ons. Supported structures have visual controls; custom or
unsupported WML remains accessible through the RAW editor.

WCC is actively developed. Preview builds are still evolving, so keep backups of
important campaigns and Library content while testing.

## Features

| Area | What you can do |
| --- | --- |
| Campaigns | Create campaigns and chapters, manage dependencies and open existing campaign packages. |
| Scenarios | Edit settings, difficulties, sides, leaders, placed units, music, schedules and story slides. |
| Events | Build supported event graphs with dialogue, unit spawning/movement, gold, camera actions and victory/defeat behavior. |
| Presets | Reuse Side, Leader, Unit and Event presets across a package or within a chapter. |
| Units & Library | Browse Core/add-on content; create and edit unit types, attacks and basic animations with previews. |
| Maps & WML | Preview maps, pick coordinates, inspect source and use WML checks and diagnostic reports. |
| WML Editor | Texteditor designed for WML Syntax, autodetects and resolves macros if context is provided
| WML Test | Test tools designed as alternative to WMLlint, -indent, -scope

Changes remain pending until you save. Structured editing depends on the source;
complex imported content may need RAW editing. Map previews are available, while
a full terrain/map editor is planned.

## Planned features

- Project-wide Undo/Redo and more convenient editing workflows.
- More event actions and conditions.
- Richer unit, animation and image-modifier tools.
- Decoration management and custom terrain/map authoring.
- Faster validation, saving and refresh for large campaigns.
- Share functionality for WCC library entrys
- Automatic Upgrade Script for Wesnoth Campaigns (backporting possible)
- Post 1.0: Support for popular user solutions, like AMLA/Item System from LotI or Multihex-Maps/Complex-Animations like TLU.

This is a direction for development, not a promised release schedule. Features may
be adjusted as testing and feedback shape the project.

## Getting started

Download a **compiled build** using the link above. Prebuilt releases include .NET;
GitHub's automaticallygenerated [source archives](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases) are for building the application yourself.

| Platform | Package | Start |
| --- | --- | --- |
| Windows x64 | ZIP | Extract the entire archive, then run `WCC.exe`. |
| Linux x64 | tar.gz | Extract the entire archive, then run `./WCC` in X11 or XWayland. |

1. Extract WCC into a writable folder and keep all included files together.
2. Open **Settings** and select your local Wesnoth installation.
3. Optionally select the add-ons folder for automatic discovery, or attach required
   dependency folders when prompted. Manual attachments last for the current campaign session.
4. Choose **New campaign**, or open an existing campaign's `_main.cfg`.
5. Edit, save explicitly, and reopen your campaign to check the result.

The current target Version is **Wesnoth 1.20**; its 1.19 development line maps to
that target. Testing currently focuses on **1.19.X**. Older Wesnoth Versions are not supported - yet.

WCC keeps `Library/`, `Cache/` and `settings.json` beside its executable. Close WCC
before moving it, and keep its Library and settings with it. Wesnoth installations
and campaign folders remain separate. For Linux system requirements, installation
routes and portable storage details, see [portable builds](docs/portable-builds.md).

## Build from source

Install the .NET SDK pinned in [global.json](global.json), then run:

```sh
dotnet build src/Wcc.App/Wcc.App.csproj
dotnet run --project src/Wcc.App/Wcc.App.csproj --no-build
```

[Wcc.App](src/Wcc.App) contains the Avalonia desktop application;
[Wcc.Core](src/Wcc.Core) contains the WML, Library and editing logic.
See the [architecture guide](docs/architecture.md) for more detail, and the
[portable build guide](docs/portable-builds.md) to create standalone packages.

## Feedback

Bug reports and feature suggestions are welcome through the [official Wesnoth Forum Thread](https://forums.wesnoth.org/viewtopic.php?t=60997) or this repository's Issues.
For a bug, include the WCC version, operating system, Wesnoth version, campaign and
dependencies, steps to reproduce, and what you expected versus what happened.
A screenshot, small example or exported WML Tester report can help when relevant.

## License & acknowledgements

WCC's source is [LGPL-3.0-or-later](LICENSE). Bundled libraries, artwork and fonts
retain their own licenses; see [ThirdPartyNotices/WCC.txt](ThirdPartyNotices/WCC.txt).

WCC uses [Avalonia](https://avaloniaui.net/) and resources from The Battle for Wesnoth.
Thanks to their contributors and to everyone testing WCC and sharing feedback :)
