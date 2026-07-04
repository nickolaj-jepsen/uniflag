# SimHub plugin API notes

SimHub's plugin API is undocumented; this file is the project's working
reference. **Reference SimHub version: 9.11.21** (pins the CI DLL cache
and the dev-box install; bump deliberately).

Sources: reflection over the installed `SimHub.Plugins.dll` (assembly
1.0.9679.31701 — SimHub strips exe/dll version resources to 1.0.0.0, the
real version lives in the uninstall registry key), the official plugin
SDK demo that **ships inside the install** at
`C:\Program Files (x86)\SimHub\PluginSdk\User.PluginSdkDemo`, and IL
inspection of the loader/persistence paths. Everything below was
verified against 9.11.21.

## The contract SimHub loads

A plugin is a .NET Framework 4.8 class library dropped into the SimHub
install directory (`C:\Program Files (x86)\SimHub`). Discovery: on
startup `PluginFinder` scans `*.dll` in its own directory (results
cached in `PluginsData\Common\ResolveCache.json`) for public,
non-abstract classes implementing `SimHub.Plugins.IPlugin`, shows a
"New plugins have been detected !" dialog, and persists the per-class
enable flag in `PluginsData\PluginsActivation.json`. Assemblies are not
strong-named.

Interfaces (all in `SimHub.Plugins`):

```csharp
interface IPlugin
{
    PluginManager PluginManager { set; }   // a public { get; set; } auto-property satisfies this
    void Init(PluginManager pluginManager);
    void End(PluginManager pluginManager);
}

interface IDataPlugin : IPlugin
{
    void DataUpdate(PluginManager pluginManager, ref GameData data);
}

interface IWPFSettings
{
    Control GetWPFSettingsControl(PluginManager pluginManager);   // null allowed
}

interface IWPFSettingsV2 : IWPFSettings
{
    ImageSource PictureIcon { get; }   // 24x24, black/white-friendly; helper: this.ToIcon(Bitmap)
    string LeftMenuTitle { get; }      // null falls back to [PluginName]
}
```

Note the exact name `GetWPFSettingsControl` — plural "Settings". Related
optional interfaces exist (`IPluginV2.PluginManagerLoaded`,
`IWPFSettingsNoTitle`, `IRTDataPlugin`) but the trio above is the whole
v2 surface we need.

Type metadata attributes (constructor args are positional strings):

```csharp
[PluginName("Uniflag")]
[PluginDescription("...")]
[PluginAuthor("Nickolaj Jepsen")]
```

(`PluginDescritionAttribute` — sic — also exists as a legacy typo'd
duplicate; use the correctly spelled one. `AutoEnablebyDefaultPlugin`,
`NoAutoEnablePlugin`, `BetaPlugin`, `PluginDependsOn(Type)` exist as
behaviour modifiers.)

## Settings persistence

Extension methods on `IPlugin` in `SimHub.Plugins.IPluginExtensions`:

```csharp
Settings = this.ReadCommonSettings("GeneralSettings", () => new UniflagSettings());
this.SaveCommonSettings("GeneralSettings", Settings);
```

Serialized with JSON.net to
`<install>\PluginsData\Common\<PluginTypeName>.<settingsName>.json`
(via `PluginManager.GetCommonStoragePath`, with file versioning through
`WoteverCommon.JsonExtensions`). Settings classes must be plain
JSON.net-serializable POCOs.

## Lifecycle facts

- Plugins are constructed with `Activator.CreateInstance` — a public
  **parameterless constructor** is required. After construction SimHub
  sets `IPlugin.PluginManager`, then calls `Init` once all plugins are
  created.
- **Plugins are rebuilt (`End` + new instance + `Init`) at game change**
  (SDK demo comment) — never assume `Init` runs exactly once per SimHub
  session. Save settings in `End`.
- Enabling a plugin from the UI without restart triggers a "late
  activation" `Init`; `PluginManager.PluginActivationRequiresReload`
  exists for plugins that can't do that.
- `DataUpdate(PluginManager, ref GameData)` runs once per game-data
  frame (~60 Hz max) on a dedicated update thread
  (`PluginManager.UpdateThread`) with a cloned `GameData` snapshot,
  including while no game runs (`data.GameRunning == false`).
  Exceptions are caught by SimHub and **throttle the plugin** — don't
  rely on them for control flow; keep the method allocation-light.
- WPF settings controls live on the UI thread; anything updated from
  `DataUpdate` must marshal via `Dispatcher`.
- `GameData` (in `GameReaderCommon.dll`) exposes `OldData`/`NewData` as
  public **fields** (not properties) of type
  `GameReaderCommon.StatusDataBase` (~257 properties — the normalized
  telemetry M4 consumes), plus properties `GameRunning`, `GamePaused`,
  `GameInMenu`, `GameReplay`, `GameName`, `Spectating`, `FrameTime`,
  `SessionId`.
- Custom properties/actions/events: `this.AttachDelegate(name, func)`,
  `this.AddAction(...)`, `this.AddEvent(...)` — same extensions class.

## Game-specific raw data via `GetRawDataObject()` (M10 research)

Everything verified against SimHub 9.11.21 by reflection + IL inspection.

- `StatusDataBase.GetRawDataObject()` is the abstract seam to a game's raw
  data. The concrete `StatusData<T>` implements it as a plain field read of
  `Raw` (`T`) plus a `box` that is a no-op for class-typed raws — **no
  allocation, trivially cheap per tick**.
- Raw types live in per-game reader assemblies. There is **no
  `iRacingReader.dll`** — the iRacing reader lives in **`ICarsReader.dll`**
  under the `IRacingReader` namespace (assembly name and namespace differ).
  `IRacingReader.IRacingManager` extends
  `GameManagerBase<DataSampleEx, IDisposable, DataSampleStorage>`, so for
  iRacing `GetRawDataObject()` returns an **`IRacingReader.DataSampleEx`**.
- `DataSampleEx` exposes (public properties): `SessionData`
  (`iRacingSDK.SessionData`, the parsed session YAML), `CurrentSessionInfo`,
  `AllSessionData`, `Telemetry` (`iRacingSDK.Telemetry`), `GearRatios`,
  `SessionDataDict`.
- **`iRacingSDK.Telemetry` derives from `Dictionary<string, object>`** with
  typed convenience getters on top. The typed `SessionFlags` getter is
  IL-verified as `(SessionFlags)(int)this["SessionFlags"]` — i.e. the
  dictionary entry is **boxed `Int32`** (the top start-light bits make it
  negative; reinterpret unchecked to `uint`). Dictionary keys are whatever
  telemetry variables the sim exports that session — names like
  `PlayerCarTowTime` exist at runtime even though no typed getter covers
  them.
- `Telemetry.UnderPaceCar` getter (IL-verified): `CarIdxTrackSurface[0] == 3`
  — car index 0 is the pace car; 3 = on track.
- The uniflag extractor (`GameDataExtractor.ExtractIRacingRaw`) therefore
  reads raw data **reflectively**: one cached `PropertyInfo` for
  `DataSampleEx.Telemetry` (cache keyed on the raw object's exact `Type`,
  re-resolved on change), then casts the value to
  `IDictionary<string, object>` (BCL interface — no proprietary reference
  needed) and `TryGetValue("SessionFlags")`. Per-tick cost at 60 Hz: one
  field read, one `PropertyInfo.GetValue` invocation (~hundreds of ns), one
  dictionary lookup; zero per-tick allocation. Null-safe at every layer —
  shape drift in a future SimHub degrades to "no raw data", never a throw
  (SimHub throttles plugins whose `DataUpdate` throws). Note the one
  reflective call whose failure mode is a throw rather than a null:
  `Type.GetProperty("Telemetry")` raises `AmbiguousMatchException` if a
  future raw type shadows `Telemetry` with a different property type, so
  the extractor guards it and caches null like every other drift shape.
- **Do NOT add compile-time references** to `ICarsReader.dll` or
  `iRacingSDK.dll`: CI stages only the four pinned reference DLLs
  (SimHub.Plugins, GameReaderCommon, log4net, SimHub.Logging), and the raw
  shapes are per-game anyway. Reflection + BCL interfaces is the supported
  pattern.
- **Game identity**: `GameData.GameName` for iRacing is `"IRacing"` — the
  same per-game code that names the `PluginsData\IRacing` settings folder
  on a live install. NCalc's `SessionFlagsDetails.Is<bit>` layer is a
  SimHub-side `EnumExposer<SessionFlags>` attached by the reader under
  `Telemetry.SessionFlagsDetails`; it exists for formulas only — typed
  plugins read the mask directly (see
  `docs/simhub-flag-properties.md`, iRacing section, for the verified bit
  table and the unified `GD_Flag_*` derivation).

## Referencing SimHub DLLs

The DLLs are proprietary: never committed, never shipped. Projects
reference them through the MSBuild property `$(SimHubDir)` (defaults to
`C:\Program Files (x86)\SimHub`; override with `-p:SimHubDir=...` or the
`UNIFLAG_SIMHUB_DIR` env var consumed by `just plugin-build` /
`just plugin-test`).

Minimal reference set: `SimHub.Plugins.dll`, `GameReaderCommon.dll`,
`log4net.dll` (2.0.15 at 9.11.21 — reference without a version pin; the
SDK demo's 2.0.8 pin is stale and non-binding). CI also stages
`SimHub.Logging.dll` beside them to avoid resolve warnings.

- `plugin/src`: references use `Private=false` so the build output
  contains only `UniflagPlugin.dll` (the release-zip audit relies on
  this).
- `plugin/tests`: `Private=true` copies the DLLs beside the test runner
  so reflection over plugin metadata resolves. `bin/` is gitignored.

## CI acquisition of reference DLLs

SimHub installers are GitHub release assets:
`https://github.com/SHWotever/SimHub/releases/download/<VER>/SimHub.<VER>.zip`
(one file inside: `SimHubSetup_<VER>.exe`, Inno Setup 6.4.3, ~218 MB).

**innoextract cannot unpack it** (newest release 1.9 supports Inno Setup
≤ 6.0.5; verified to fail on 9.11.21 with "Unexpected setup data
version: 6.4.3"). The working path — proven by other plugin repos on
`windows-latest` — is a headless silent install:
`/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR=...`
(launch via `Start-Process -Wait`; the setup exe is a GUI-subsystem
app). .NET Framework 4.8 is preinstalled on `windows-latest`, so no
prerequisite dialog fires. CI caches only the four staged reference
DLLs (~10 MB) keyed by the pinned version — see the `plugin` job in
`.github/workflows/ci.yml`.

## Build notes (SDK-style csproj targeting net48)

- `Microsoft.NET.Sdk` + `<UseWPF>true</UseWPF>` + `net48` builds with
  the plain `dotnet` CLI (no Visual Studio / MSBuild needed — verified
  locally).
- `Microsoft.NETFramework.ReferenceAssemblies` (PrivateAssets=all)
  supplies the targeting pack, so CI runners and dev boxes need no
  .NET Framework developer pack.
- `LangVersion` pinned to 9.0 — fine on net48 as long as we avoid
  features needing runtime support (default interface members) or add
  the `IsExternalInit` shim (records/init-only).

## Dev-box install/verify loop

Copying `UniflagPlugin.dll` into `C:\Program Files (x86)\SimHub`
requires elevation; then restart SimHub, accept the "New plugins have
been detected !" prompt, and the plugin appears in the left menu under
its `LeftMenuTitle`.
