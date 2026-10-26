# WCC — Wesnoth Campaign Creator

WCC is a desktop editor for singleplayer Battle for Wesnoth campaigns. Create
campaigns, scenarios, unit types, stories and basic events, or edit imported WML.
Unsupported structures remain accessible through the RAW editor.

## Build and run

Install the .NET SDK specified in [global.json](global.json), then run:

```sh
dotnet build src/Wcc.App/Wcc.App.csproj
dotnet run --project src/Wcc.App/Wcc.App.csproj --no-build
```

WCC uses Avalonia and supports Windows and Linux. Configure a Wesnoth installation
in the app to load Core resources and campaigns. Portable builds start with
`WCC.exe` or `./WCC` and keep `Library/`, `Cache/` and `settings.json` beside the
executable. See [portable builds](docs/portable-builds.md) for publishing and Linux
requirements.

[src/Wcc.App](src/Wcc.App) contains the desktop application;
[src/Wcc.Core](src/Wcc.Core) contains the WML, Library and checked editing logic.
The [architecture guide](docs/architecture.md) describes their connections.

WCC's source is [LGPL-3.0-or-later](LICENSE). Bundled components and artwork retain their own
licenses; see [ThirdPartyNotices/WCC.txt](ThirdPartyNotices/WCC.txt).