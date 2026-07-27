# Glamour Tracker+ Native

Fork of [Glamour Tracker+](../GlamourTrackerPlus) exploring **KamiToolKit** native FFXIV UI (same approach as DailyDuty / VanillaPlus).

## Commands

| Command | Opens |
| --- | --- |
| `/glamplus` | Native main menu |
| `/glamplus report` | Native Fashion Report |
| `/glamplus imgui` | Legacy ImGui UI |

## Build

```bash
cd GlamourTrackerPlus.Native
DOTNET_CLI_HOME="$HOME" NUGET_PACKAGES="$HOME/.nuget/packages" dotnet build -c Release --disable-build-servers
```

DLL: `GlamourTrackerNative/bin/Release/GlamourTrackerNative.dll`

Add that path as a Dalamud **dev plugin**. It can load beside Glamour Tracker+ (different InternalName).

## Submodules

- `KamiToolKit` — https://github.com/MidoriKami/KamiToolKit

## Author

Sin7000
