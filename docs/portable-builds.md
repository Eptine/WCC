# Portable Windows and Linux builds

The release folders contain the application, .NET runtime and bundled resources.
Users extract the entire archive into a writable folder. Windows runs `WCC.exe`;
Linux runs `./WCC` in an X11 desktop session or through XWayland. Installing
Wesnoth is optional for opening WCC, and is necessary for its Core content and native
Wesnoth integration. Game installations and campaign folders remain external.

WCC supports these installation routes:

| Wesnoth installation | Background Wesnoth calls |
| --- | --- |
| Windows, Steam or standalone | Run the selected installation's `wesnoth.exe` directly. |
| Linux, package manager or self-built standard installation | Run the selected executable directly, using its host environment. |
| Linux, Steam | Use the selected game's installed Steam runtime when found. |

Native Linux detection also handles split installations: the game data can live
under `<prefix>/share/...` while the executable lives under `<prefix>/bin` or
`<prefix>/games`, including versioned executable names. Select the installed game
or data folder. Arbitrary uninstalled build-tree layouts are not automatically
discovered. WCC verifies the Linux executable's reported data path and exact version.

On Linux, WCC also uses the selected executable's adjacent `lib/` directory when
present. Steam installations can use their installed
`SteamLinuxRuntime_4`: WCC checks the game's Steam library first, then conventional
primary Steam locations. Keep that runtime installed through Steam. The same
launch setup serves version detection, preprocessing and image previews; each
container request shares only its game/input and requested output directories.
Installing Steam does not route unrelated native installations through its runtime.

Direct Linux launching and split-directory detection have regression coverage run
on Linux. Real binary checks also cover the supplied Linux game files and a Steam
layout with Runtime 4. Those checks do not establish end-to-end compatibility with
every distribution package or self-built installation; those remain manual test cases.

`Library/`, `Cache/` and `settings.json` belong beside the executable. Startup creates
portable data on first use.
Close WCC before moving its folder. Move the whole folder to retain its Library;
configure local Wesnoth/campaign paths again when moving between computers.

Creator campaigns retain their exact Core version, target and content fingerprint in
their sidecar. A rebuilt Library can reconnect that identical Core under new local index
IDs; it does not change the campaign's recorded Core pin or substitute another version.

The release root contains `WCC.exe` or `WCC`, `README.txt`, `ThirdPartyNotices/` and
`bin/`. The latter holds all managed assemblies, .NET runtime, Avalonia and native
support files together so normal dependency probing is retained. Runtime DLLs are
not divided into vendor subdirectories. The native WCC apphost directly hosts
`bin/Wcc.App.dll`; there is no shell wrapper or child launcher. The managed assembly
name remains stable for compiled resources and existing references. The Library,
cache and settings use the native executable's root, not the managed `bin/` folder.

`README.txt` includes portable storage, Linux requirements and saving behavior;
there is no separate `PORTABLE.txt`. Bundled filename and SHA-256 inventories live
in `ThirdPartyNotices/`. Its `SHA256SUMS` paths are relative to the release root.

Checked source routes must use ordinary real directories; symbolic-link components
are refused. Linux saving requires `statx` identity information and `renameat2`
exchange/no-replace support from the filesystem. After replacing a Linux file,
hidden `.wcc-stage-*.tmp` files retain displaced baselines and a successful save can
report a cleanup warning. Those files are recovery evidence, not automatic
transactional rollback. Close WCC and back up the campaign before manually
inspecting or removing retained stages.

Linux file locks are advisory; they do not stop another process from renaming
directory entries. Detected namespace interference retains recovery evidence.
Extended POSIX access ACLs are refused until exact preservation is supported.

## Build prerequisites

- Use the SDK pinned in [global.json](../global.json). Restore needs the existing
  project packages plus the .NET runtime/apphost packs for each requested RID.
- The current application targets `net10.0`; publish `win-x64` and `linux-x64`
  separately with self-contained enabled. Trimming, AOT and single-file publishing
  remain disabled so compiled XAML, reflection and native asset loading retain their
  ordinary behavior.
- Avalonia.Desktop already supplies its X11 backend and the Skia/HarfBuzz native
  assets. Do not add platform packages merely to cross-publish.
- Linux still needs its operating-system libraries. Avalonia lists X11, ICE, SM and
  fontconfig; .NET also needs the distribution's glibc, GCC/stdc++, ICU, SSL, zlib,
  Brotli, Kerberos, timezone and certificate libraries. Versions depend on the
  chosen distribution; use its package names rather than copying random libraries
  into the release. See [Avalonia Linux deployment](https://docs.avaloniaui.net/docs/deployment/linux)
  and [Microsoft's Ubuntu dependencies](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install#dependencies).

## Publish

From PowerShell at the repository root:

```powershell
./scripts/Publish-Portable.ps1 -Version 0.1.0-preview
```

The script publishes sequentially into fresh staging folders under
`artifacts/portable`, checks for accidental user state, gathers bundled notices and
restored package license/notice files and metadata, and creates `README.txt` and
`ThirdPartyNotices/SHA256SUMS`. Completed folders have the form
`WCC-<version>-<rid>`. Existing output or archive names cause refusal;
choose a new output root instead of deleting user
data. Interrupted staging folders remain available for inspection.

The publish invocation imports `scripts/PortableAppHost.targets` only for this
operation. It uses the pinned SDK's `CreateAppHost` task and pristine RID apphost
to generate the native root entry point, embedding `bin/Wcc.App.dll` and selecting
app-local .NET lookup. The SDK stock host is retained outside the release in its
unique staging parent as build evidence. The Windows GUI subsystem and managed PE
resources are retained. Normal development builds do not import this target.

The [.NET HostWriter contract](https://github.com/dotnet/runtime/blob/v10.0.3/src/installer/managed/Microsoft.NET.HostModel/AppHost/HostWriter.cs)
supports relative managed paths and a different host filename. The native
[corehost implementation](https://github.com/dotnet/runtime/blob/v10.0.3/src/native/corehost/corehost.cpp)
resolves that path relative to the executable and uses its directory as the app
root. The [hostfxr resolver](https://github.com/dotnet/runtime/blob/v10.0.3/src/native/corehost/apphost/standalone/hostfxr_resolver.cpp)
receives that managed app root for local runtime discovery. Actual extracted-host
checks remain required on each target OS; source inspection alone does not prove
that a release has its complete runtime and native dependencies.

`-Version` labels the archive and README; it does not override assembly metadata
after testing. `-NuGetConfig <path>` supplies an explicit restore configuration;
`NUGET_PACKAGES` can point at an owned package cache. The script uses the calling
environment and does not install SDKs or native system libraries. Restore may
download the existing project dependencies and required runtime/apphost packs.

Select one runtime or a different fresh destination when needed:

```powershell
./scripts/Publish-Portable.ps1 -RuntimeIdentifiers win-x64 -Version 0.1.0-preview -OutputRoot artifacts/release-review
./scripts/Publish-Portable.ps1 -RuntimeIdentifiers linux-x64 -Version 0.1.0-preview -OutputRoot artifacts/linux-review -NoArchive
```

Windows output becomes a ZIP. Linux output becomes a tar.gz when the script runs
on Linux. A Windows cross-publish deliberately leaves the Linux folder unarchived
because ZIP/Windows file modes do not establish the apphost's Unix executable bit.
On Linux, from the directory containing the completed Linux folder:

```sh
chmod 755 WCC-0.1.0-preview-linux-x64/WCC
if [ -f WCC-0.1.0-preview-linux-x64/bin/createdump ]; then
  chmod 755 WCC-0.1.0-preview-linux-x64/bin/createdump
fi
tar --sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner \
  -czf WCC-0.1.0-preview-linux-x64.tar.gz WCC-0.1.0-preview-linux-x64
```

The resulting archive retains executable mode without requiring .NET on the test
machine. GNU tar's fixed ordering/timestamps make archive metadata repeatable;
this is not a claim that unpinned package feeds or SDK versions produce identical
binaries. Keep the exact source/build/test receipts for the release being shared.

## Check before sharing

Test the extracted archives, not only the build directory. Verify startup from a
different working directory, a writable folder with spaces, fresh Library
initialization without importing existing legacy data, relocation with owned Library
entries, and a fresh profile pointing to an actual matching Wesnoth installation.
Check one campaign create/save/reopen,
images/fonts, pickers and native preprocessing. On Linux use a case-sensitive local
filesystem and verify both text and binary-resource writes. Retain original fixtures.

The source tests and packaging commands alone do not establish native Linux GUI or
Wesnoth gameplay compatibility. Record the OS/runtime/desktop actually tested and
any missing native dependencies. WSL/X11 smoke checks should be identified as such.

`ThirdPartyNotices/WCC.txt` is the central license and attribution index in source
and distributions. WCC's source uses LGPL-3.0-or-later (see the root LICENSE and
COPYING texts). Original component and
font licenses and the Wesnoth image inventory are kept beside the index.
Add future components/assets to the index and retain
their required notices in `ThirdPartyNotices/`; the App project copies that folder
into development and publish outputs.

Portable publishing also gathers restored package license/notice files and
`.nuspec` version metadata under `ThirdPartyNotices/Packages/`, and the exact
self-contained runtime's license/notices under `ThirdPartyNotices/DotNet/`.
The explicit MicroCom and Tmds notices cover upstream MIT texts absent from
those NuGet packages. No WCC license is selected by the publishing process.
