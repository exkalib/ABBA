# NRftW Manager UI

Windows-only external trainer for the current No Rest for the Wicked workflow. It does not install, load, or depend on a game mod.

The first implemented features are:

- stackable item quantity: `89 3B 0F 94 C0 EB ??`
- automatic wallet context: `PlayerControllerView.OnUpdate` → `InventoryAPI.GetInventoryComponent` → `InventoryComponent.Gold` (`+0x4`)

The program requires exactly one match before enabling a feature. Material quantity uses a temporary external capture hook to remember the stack you deliberately change once in game. It can also keep a final stack of one from reaching zero while enabled.

The automatic wallet profile is locked to the `GameAssembly.dll` SHA-256 captured for the current build. It hooks the player's regular update only long enough to obtain the active inventory component; you do not need to gain or spend currency first. The hook is removed when the program disconnects. Copper, silver, and gold inputs are converted to the game's base units (`1`, `100`, and `10,000` respectively). Gloamseed is a separate dungeon currency and is deliberately not written by this profile.

For unverified fields (equipment rarity, enchantments, attributes), use the 4-byte value-change detector. Enter the visible value, run an initial scan, change that value once in game, enter the new value, filter, and copy the candidate report for follow-up analysis.

## Build on Windows

Install Visual Studio Community 2026 with the **.NET desktop development** workload (including the .NET 8 SDK), then open `NRftWManagerUI.csproj` and press `F5`. Or run:

```powershell
dotnet build
dotnet run
```

Do not write values until the in-game capture shows the expected current value. Back up your save before testing unverified fields.
