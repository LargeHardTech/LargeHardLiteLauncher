# LargeHardLiteLauncher

[简体中文](README.md) | English | [Français](README.fr.md) | [Русский](README.ru.md) | [微软式中文](README.zh-ms.md)

A Minecraft launcher that runs in the terminal. Written in C# with
[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui), compiled by NativeAOT into a single
exe with no .NET runtime dependency and no graphical interface.

Layout follows [PCL2](https://github.com/Hex-Dragon/PCL2); the multiplayer part follows
[HMCL](https://github.com/HMCL-dev/HMCL)'s Terracotta integration.

## Features

**Launching**

- Multiple game directories, version list sorted by numeric segments
- Per-version settings: memory mode, Java, JVM arguments, game arguments, window size,
  isolation mode, pre-launch command
- Missing library files are downloaded automatically before launch (with mirror fallback);
  a missing library no longer blocks startup
- Java is picked as the lowest major version that satisfies the requirement; 32-bit Java
  lowers the heap and prints a notice
- The game process is started with `DETACHED_PROCESS`, so closing the terminal window does
  not terminate the game
- Session written to `running-game.json`; reopening the launcher recognises a game that is
  still running
- Crash analysis maps log signatures to causes and suggestions (14 rules)
- Custom home page: preset text, local file, or remote URL, with `${date}` `${player}`
  `${version}` and other placeholders

**Downloading and installing**

- Vanilla installation, source selectable (official / BMCLAPI mirror / automatic)
- Loader installation: Forge, NeoForge, Fabric, Quilt
- Community search and install: mods, modpacks, datapacks, resource packs, shaders
  (Modrinth needs no key; CurseForge needs your own)
- Resumable downloads: a network failure keeps the `.tmp` file for the next attempt, only a
  checksum failure discards it
- Required dependencies are downloaded together with a mod
- Modpack installation (`.mrpack`): installs the matching loader and extracts overrides
- Export a version as `.mrpack`, with optional online matching to reduce size
  (measured: a 62-mod pack went from 518 MB to 186 MB)

**Version management**

- Mod manager: scan the mods directory, batch enable/disable (rename to `.disabled`),
  delete, check for updates
- Mod update check: look up the latest version on Modrinth by jar SHA1, update in one step
- Resource pack / shader management: reads and writes the real enable state in `options.txt`
  and `optionsshaders.txt`
- Verify files: checks the client jar, libraries, natives and assets against the version json
- Delete a version (including its saves and configuration)

**Multiplayer**

- Terracotta is embedded; create or join a room without downloading anything separately
- Public relay node list is fetched automatically

**JAR launching**

- Run any jar independently (server, client, or any other Java program)
- Graphical configuration for memory, GC presets (G1 / ZGC / Parallel / Serial), extra JVM
  and program arguments, server mode
- A command box sends input straight to the process stdin
- Pass a jar path on the command line or drop a jar onto the exe to open this page directly

## Interface

Six pages, switched with F1 to F6:

```
Launch | Download | Multiplayer | JAR | Settings | More
```

- **Launch**: account, version list, launch button, home area
- **Download**: vanilla, loaders, community resources
- **Multiplayer**: create and join rooms
- **JAR**: standalone jar launching
- **Settings**: account, Java, memory, isolation, download source, appearance, performance
- **More**: guides, credits, small tools

## Requirements

- Windows 10 or later (process creation and window enumeration use Win32 APIs)
- Java is required to launch the game, or download one from Settings → Java
- 100 columns or wider recommended for the terminal, 80 columns minimum

## Building

Requires the .NET 10 SDK and an MSVC toolchain (used by the NativeAOT linker).

```bash
python scripts/build.py build      # Debug build, keeps every self-test entry point
python scripts/build.py publish    # Release publish, output in bin/Release/net10.0/win-x64/publish/
```

`scripts/build.py` handles three things: skip restore, clear MSYS environment variables, and add
`IlcUseEnvironmentalTools` (so ILCompiler uses the toolchain from the environment instead of
probing Visual Studio through the registry).

Debug builds include the self-test entry points, Release builds do not:

```bash
LargeHardLiteLauncher.exe --uitest        # construct all six pages
LargeHardLiteLauncher.exe --selftest      # 31 assertion blocks (must not run on the AOT binary)
LargeHardLiteLauncher.exe --vmtest        # version manager navigation
LargeHardLiteLauncher.exe --packcfgtest   # resource pack / shader config read-write
LargeHardLiteLauncher.exe --updatetest <version dir>   # mod update check
LargeHardLiteLauncher.exe --launchtest <version dir>   # launch the game and follow the log
```

## Layout

```
Core/            Logic with no UI dependency
  Config.cs          configuration (SourceGenerator serialization)
  VersionScanner.cs  version json parsing, rules evaluation
  GameLauncher.cs    launch pipeline: arguments, natives, classpath, crash analysis
  GameDownloader.cs  download plan and resume
  LoaderApi.cs       loader version manifests
  LoaderInstaller.cs Forge/NeoForge/Fabric/Quilt installation
  ModScanner.cs      mods directory scan and enable/disable
  ModUpdateChecker.cs mod update check
  ModpackExporter.cs export .mrpack
  ModpackInstaller.cs install .mrpack
  PackConfig.cs      real enable state for resource packs and shaders
  TerracottaService.cs multiplayer process and REST interface
  JarLauncher.cs     standalone jar launching
  DetachedProcess.cs DETACHED_PROCESS process creation
  GameSession.cs     record of the running game session
  GameWindowFinder.cs find the game window by class name
  HomePageLoader.cs  custom home page and placeholders
  CrashAnalyzer.cs   crash log analysis
  Localization.cs    UI strings
UI/              Interface
  MainWindow.cs      main window, page switching
  PageBase.cs        page base class and PageId
  LaunchPage.cs      launch page
  VersionManager*.cs version manager dialog (overview / settings / mods / packs / shaders / verify)
  DownloadPage.cs    download page
  DownloadCommunity.cs community search and details
  DownloadLoaderInstall.cs loader installation page
  TerracottaPage.cs  multiplayer page
  JarLaunchPage.cs   JAR page
  SettingsPage.cs    settings page
  MorePage.cs        more page
  TaskPanel.cs       task bar
  AccountManagerUi.cs account manager dialog
Tests/           self-test code (excluded from Release builds)
  SelfTest.cs        31 assertion blocks
  InheritTest.cs     version inheritance merging
  LaunchTest.cs      real launch with log tracking
  LoaderTest.cs      loader installation
  ModTest.cs         mods directory scan
  MsAuthTest.cs      Microsoft login request bodies
Resources/       embedded resources (Terracotta kernel zip, AGPL text)
scripts/         build and run scripts
  build.py           build entry point, handles local environment quirks
  publish.bat        double-click publish on Windows, strips pdb
  run.bat            plain dotnet run
Program.cs       entry point and self-test entry points
```

## Data and configuration

```
%USERPROFILE%\.largehardlitelauncher\
  config.json         global configuration
  logs\               launcher logs
  java\               Java downloaded by the launcher
  terracotta\         extracted multiplayer kernel
  cache\              home page cache
  running-game.json   the game currently running
```

Game files and versions stay in the `.minecraft` directory and are not touched.

## Known limitations

- Windows only, no cross-platform plan (process creation and window lookup rely on Win32)
- Mod update checks only cover Modrinth; the CurseForge fingerprint API needs a key and
  rate-limits aggressively
- The launcher has no self-update; upgrading means replacing the exe
- Single instance only; a second instance just shows a notice
- Modpack import supports `.mrpack` only, CurseForge zip modpacks are not supported

## FAQ

**Why a terminal interface?**

A Minecraft launcher mostly spends its time downloading, extracting and assembling arguments;
the interface only presents the result. A graphical interface also has to handle DPI scaling,
dark mode, high contrast, screen readers and font fallback. The terminal skips that layer and
also works over ssh.

**Why 24 MB when PCL is 5 MB?**

The comparison is not equivalent. PCL depends on a system-installed .NET Desktop Runtime
(hundreds of MB) and ships only application code. This project is self-contained: the whole
runtime is inside, plus 7.75 MB of embedded multiplayer kernel. On equal terms, the
application code itself is about 158 KB.

**Will it delete my saves?**

No. The only feature that deletes files is "Delete version", which asks for confirmation and
states that the operation cannot be undone. Disabling a mod renames the file; disabling a
resource pack or shader only changes one line in `options.txt`.

## Third-party components

| Component | Purpose | License |
|---|---|---|
| [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) | terminal UI framework | MIT |
| [Terracotta](https://github.com/burningtnt/Terracotta) | multiplayer kernel, distributed embedded | AGPL-3.0 |
| [EasyTier](https://easytier.cn) | P2P networking underneath Terracotta | Apache-2.0 |

The Terracotta binary is unmodified; its source lives in the upstream repository. The full
license text is available in the program under More → About.

Download and resource data come from [BMCLAPI](https://bmclapi2.bangbang93.com),
[Modrinth](https://modrinth.com), [CurseForge](https://www.curseforge.com) and
[Adoptium](https://adoptium.net); Chinese mod names reference
[MCMod](https://www.mcmod.cn).

## License

The code of this project is licensed under [WTFPL v2](LICENSE), with no warranty.

Exception: the embedded Terracotta binary is distributed unmodified and remains under the
upstream AGPL-3.0. This project's license covers its own code only and does not change the
terms of that binary.

## References

- [PCL2](https://github.com/Hex-Dragon/PCL2): layout, version management, modpack export
- [HMCL](https://github.com/HMCL-dev/HMCL): argument expansion rules, loader installation,
  Terracotta integration
