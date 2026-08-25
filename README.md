# Ascension GM (local, free)

Local toolkit for a **private Ascension / 3.3.5 client** you own:

- **ExtProxy** — 32-bit proxy DLL: unlocked Lua, Object Manager, teleport/nav helpers, and the other native GM functions the client exposes to addons.
- **AscensionBoot** — separate 32-bit boot loader: patches `Extensions.dll` → `ExtProxy64.dll` and starts a runtime copy. Source lives in `AscensionBoot/`.
- **GmToolbox** — optional local injector / instance manager. No login, no launcher, no store.
- **HeadlessClient** — optional console client that logs into **your** realm using **your** config file.

GitHub Actions builds a **portable zip on every push**. Unzip, run `GMToolBox.exe`, set the path to your `Ascension.exe`, click **Launch**. ExtProxy unlocks Lua (`GmTeleport`, `GmFace`, …) and the zip ships the base addons (map teleport, `/tpface`, `/gmteleport`).

## Requirements

- Windows x64
- .NET 8 SDK (GmToolbox + HeadlessClient)
- i686 llvm-mingw / clang (ExtProxy) — put `i686-w64-mingw32-clang.exe` on PATH or under `ExtProxy/llvm-mingw-i686/`
- Your own `Ascension.exe` (or equivalent 3.3.5 client)
- Navmesh folders only if **you** want pathing (`*.mmap` / `*.mmtile`) — not shipped

## 1. Configure (required)

Copy the examples and fill them yourself. **Never commit the copies.**

```text
config/headless.example.json  →  HeadlessClient/src/HeadlessClient.Host/appsettings.Local.json
config/gmtoolbox.example.json →  GmToolbox/Config/settings.json
```

Set at least:

| Field | Meaning |
| --- | --- |
| `AuthHost` / `AuthPort` | Your realm auth server |
| `Account` / `Password` | Your account (local file only) |
| `PreferredRealm` / `PreferredCharacter` | Who to enter world as |
| `ascensionExe` | Full path to your client exe |
| `mapsDir` / `mmapsDir` | Empty unless you have meshes |

## 2. Build ExtProxy + AscensionBoot

```powershell
cd ExtProxy
.\build.ps1 -SkipToolbox
```

That builds `ExtProxy64.dll` and invokes `AscensionBoot/build.ps1` (copies the exe beside ExtProxy).

To build only the boot loader:

```powershell
cd AscensionBoot
.\build.ps1
```

## 3. Build GmToolbox (optional)

```powershell
cd GmToolbox
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Or unpack a GitHub Release zip and run `GMToolBox.exe`. Set `ascensionExe` (Settings or `Config/settings.json`). **Launch** injects ExtProxy and copies the base addons into the client.

## 4. Run HeadlessClient (optional)

```powershell
cd HeadlessClient
dotnet run --project src/HeadlessClient.Host
```

Local `/health` only. Credentials stay in `appsettings.Local.json`.

## GitHub Releases

Every push to `main` / `master` (and every `v*` tag) runs `.github/workflows/release.yml`:

1. Builds ExtProxy + AscensionBoot (llvm-mingw)
2. Publishes self-contained `GMToolBox.exe`
3. Packs `AddOns` (GmShared, GmUI, GmTooltipFix, GmTeleport, GmMapTeleport, GmCmds)
4. Uploads `ascension-gm-dist.zip` and publishes/updates the **latest** GitHub Release

You only need your stock client path. Maps are optional. Credentials are never packed.

## Discord

Community (optional): **https://discord.gg/3K24chnRKm**

## What was removed

Launcher, store, Discord login gate, paid entitlements, bundled maps, baked install paths, and hosted download catalog.

GMToolBox shows the Discord invite on the top bar. Membership is not required.
