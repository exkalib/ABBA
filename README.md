# NRftW Manager UI

Windows-only external trainer for the current No Rest for the Wicked workflow. It does not install, load, or depend on a game mod.

The first implemented features use the two signatures already validated in the prior test session:

- stackable item quantity: `89 3B 0F 94 C0 EB ??`
- currency update: `45 01 75 04 45 33 C9`

The program first requires exactly one match for each signature. It then uses a temporary external capture hook to remember the dynamic address of the material or currency you deliberately change once in game; only that captured address can be read or written. The item capture can also keep a final stack of one from reaching zero while it remains enabled. If a game update changes a signature, the program leaves writing locked.

For unverified fields (equipment rarity, enchantments, attributes), use the 4-byte value-change detector. Enter the visible value, run an initial scan, change that value once in game, enter the new value, filter, and copy the candidate report for follow-up analysis.

## Build on Windows

Install Visual Studio Community 2026 with the **.NET desktop development** workload (including the .NET 8 SDK), then open `NRftWManagerUI.csproj` and press `F5`. Or run:

```powershell
dotnet build
dotnet run
```

Do not write values until the in-game capture shows the expected current value. Back up your save before testing unverified fields.
