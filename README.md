# UnpackStation

A portable Windows tool that **extracts** installer packages into a chosen directory
without ever running the installer.

- `.exe` packages: Inno Setup installers are detected by their `Inno Setup Setup Data`
  signature and extracted with the bundled `engines/innoextract/x64/innoextract.exe`
  (1.13.0, zlib/libpng license; never runs the setup). Other exe packages dynamically load
  the bundled `engines/7z/x64/7z.dll` (7-Zip 26.01 x64) and probe NSIS / CAB SFX / generic
  SFX / 7z / zip by content. No COM registration and no installed 7-Zip are required.
- `.msi` packages: runs the system `msiexec /a` administrative install so embedded and
  external CAB files are handled correctly.
- Unsupported formats fail with a clear reason instead of guessing; InstallShield still
  reports that it is not supported.

## Usage

Drop an `.exe` / `.msi` onto the window or click the drop zone to pick a file, confirm
the output directory, then click **Start extraction**.

The default output directory is derived from the package's own install location and
points to **the directory that actually contains the extracted result**. Files that
already exist in the output directory are skipped and never overwritten.

Progress: `.exe` reports the number of bytes actually written. `.msi` uses the MSI
`File` table row count as the denominator and the files already written to the staging
directory as the numerator; if the `File` table cannot be read, the bar stays
indeterminate (Marquee) instead of inventing a percentage.

NSIS installers skip the installer's runtime plugins and temporary files
(`$PLUGINSDIR`, `$TEMP`), so only the files a real installation would produce are
extracted, with the same directory layout. Inno Setup installers use `innoextract -m`
to exclude temporary files and expose the `app\` payload directly in the chosen output
directory. The same read-only listing is used when a file is selected, so Inno Setup packages
also get an automatically derived default output directory instead of falling back to the
source file's directory.

Safety: path traversal protection (including symlinks and directory junctions),
decompression-bomb protection (aborts above 20 GB or 100,000 files), Mark of the Web
on extracted executables, standard user privileges (`asInvoker`, no elevation),
PerMonitorV2 DPI awareness and long-path support. The tool never runs anything it
extracts.

On failure, use **Copy diagnostic information** at the top right of the log to get
the source path, engine, exit code / HRESULT, exceptions and warnings. Unhandled
exceptions are also written to `%LOCALAPPDATA%\UnpackStation\crash.log`.

## Prerequisites: the .NET 10 SDK

This project targets `net10.0-windows` (WPF), so the SDK is needed on the
development machine for every command in the next section. It is not needed on
the target machine: the published app is self-contained.

Check whether it is already available:

```bash
dotnet --info
```

If `dotnet` is not found, install the Windows x64 .NET 10 SDK (it includes the
Windows Desktop targeting pack required by WPF) with the official installer, or
unpack the SDK archive into a directory of your choice, for example
`%USERPROFILE%\.dotnet`. For the unpacked SDK, add that directory to the user
`PATH` so `dotnet` works from any terminal:

```powershell
$sdk = Join-Path $env:USERPROFILE '.dotnet'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $sdk) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $sdk) -join ';'), 'User')
}
```

Open a **new** terminal afterwards -- a running terminal keeps the old `PATH` --
and run `dotnet --info` again. If you prefer not to change `PATH`, call the SDK
by its full path in every command below, for example
`& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project src/UnpackStation.csproj`.

## Build, run, test and publish

All commands below are run from the repository root and assume the .NET 10 SDK is
available as `dotnet` (see **Prerequisites** above). Every build copies `engines/`
into the output directory, so both `7z.dll` and `innoextract.exe` resolve from
`AppContext.BaseDirectory` in the Debug and published layouts.

### `dotnet run` -- develop and launch

```bash
dotnet run --project src/UnpackStation.csproj
```

Restores, builds the Debug configuration and starts the WPF window in one step.
Use it while changing code: it is the quickest way to see an edit on screen, and
it needs no publish step -- the SDK supplies the runtime. Add `-c Release` for
the Release configuration; press `Ctrl+C` in the terminal or close the window to
stop. The built `src/bin/Debug/net10.0-windows/UnpackStation.exe` can also be
launched directly. Because `dotnet run` rebuilds before starting, close an
already running instance first: the running process locks the exe and its dlls
and the build fails.

### `dotnet build` -- compile without starting

```bash
dotnet build UnpackStation.sln -c Debug
```

Compiles both projects of the solution and reports compile errors without
launching the app. Use it for a fast check that the whole solution still builds,
or when you only need the binaries under `src/bin/`.

### `dotnet test` -- run the test suite

```bash
dotnet test UnpackStation.sln -c Debug
```

Builds the solution and runs the xUnit suite in `tests/`. Use it after changing
extraction, MSI or path-handling code, and before committing. Test fixtures are
described under **Tests** below.

### `dotnet publish` -- produce the release

```bash
dotnet publish src/UnpackStation.csproj -c Release -r win-x64 --self-contained true -o publish
```

Produces the self-contained win-x64 build in `publish/`; the target machine needs
no .NET runtime. This is the only supported release route -- use `run`, `build`
and `test` during development instead. `publish/` is the final runnable directory.
Copy the whole folder, not just the exe:

```text
publish/
├─ UnpackStation.exe                  # double-click entry point
├─ hostfxr.dll / coreclr.dll / ...    # .NET runtime, next to the exe
├─ UnpackStation.dll / *.dll          # app and WPF assemblies
├─ <language resource folders>/       # satellite resource assemblies
├─ engines/7z/
│  ├─ License.txt                     # 7-Zip LGPL license text
│  └─ x64/7z.dll                      # 7-Zip engine
└─ engines/innoextract/
   ├─ LICENSE.txt                     # innoextract zlib/libpng license
   └─ x64/innoextract.exe             # Inno Setup extraction engine
```

To produce the distributable zip, run the packaging script:

```bash
dotnet run PackRelease.cs
```

If `publish/` does not exist yet, the script first runs the `dotnet publish` command above
automatically; if it exists, the current output is reused. `PackRelease.cs` is a single-file
app run by the .NET 10 SDK already needed to build this project. It uses only BCL APIs, so
there is no dependency on 7-Zip, a PowerShell execution policy or a machine-specific install
path. The script checks that `publish/` is complete, writes explicit directory entries,
replaces any previous zip and verifies that the archive root contains `UnpackStation.exe`,
both licenses and both engines with no `publish/` prefix. Use
`dotnet run PackRelease.cs -- <publishDir> <outputZip>` to override the paths; a custom
publish directory must already exist and is never auto-generated.

`dist/UnpackStation-win-x64.zip` is the resulting artifact (about 66 MiB with both
extraction engines; exact file/folder counts depend on the publish output); both
`publish/` and `dist/` are build output and are ignored by git, and the zip name
carries no version number.

`7z.dll` must stay at `engines/7z/x64/7z.dll` and `innoextract.exe` at
`engines/innoextract/x64/innoextract.exe` relative to the exe (the app resolves
both from `AppContext.BaseDirectory`). Running the published app needs no COM
registration and no installed 7-Zip -- the engines are bundled and can be replaced
as their licenses allow. Only x64 engines are shipped, so the project is pinned to
`<PlatformTarget>x64</PlatformTarget>`.

## Tests

Integration samples are generated at runtime by `tests/TestFixtures.cs`, so no binary
test fixtures are tracked in the repository. The few deliberately non-ASCII string
literals in the test suite are input data for Unicode path handling, not UI text.

## License notices

The same notices are shown in the application's About window. This tool does not use
innounp (GPLv3), does not copy UniExtract2 code, and has no GPL/AGPL dependencies.

### 7-Zip (LGPL)

1. **This software uses parts of 7-Zip** (`7z.dll`) through dynamic loading only.
2. **7-Zip is licensed under the GNU LGPL** (some parts carry the unRAR restriction or
   use a BSD license).
3. **7-Zip source code is available at [www.7-zip.org](https://www.7-zip.org).**

Releases ship `engines/7z/License.txt`; that file is authoritative for binary
redistribution of `7z.dll`.

### innoextract (zlib/libpng)

1. **Inno Setup packages are extracted by the bundled `innoextract` 1.13.0** through a
   child process; the setup executable is never run.
2. **innoextract is licensed under the zlib/libpng license**, a permissive license that
   is not GPL/AGPL.
3. **Source code:** [github.com/crazy-max/innoextract](https://github.com/crazy-max/innoextract),
   a fork of Daniel Scharrer's innoextract.

Releases ship `engines/innoextract/LICENSE.txt`; that file is authoritative for binary
redistribution of `innoextract.exe`.
