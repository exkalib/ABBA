# RIFT//CTRL

Windows-only external trainer for the current No Rest for the Wicked workflow. It does not install, load, or depend on a game mod.

This profile is locked to the supplied current `GameAssembly.dll` SHA-256 and requires exactly one match for every one of these entries before it permits writes:

- stackable item quantity: `89 3B 0F 94 C0 EB ??`
- player update: `PlayerControllerView.OnUpdate`
- item-detail lookup: `ItemsAPI.GetOldData`

Implemented current-build actions:

- captured stack quantity and optional “keep the last one” protection
- copper / silver / gold, plus Gloamseed through the game's own award API
- infinite health, stamina and focus; one-hit kill; native free-shop and ignore-requirements flags
- movement / experience multipliers, unspent attribute points, native level-up, native maximum stats and fast-travel unlock
- selected-item capture from its backpack detail view
- item rarity (Common / Magical / Plagued / Gold), add an enchantment, full repair, duplicate, set item level, and create a new item from the selected item's data

All item commands use the game's original APIs from its player-update thread. The hooks are temporary and restore the original instructions whenever the program disconnects or closes. This is intentionally an external tool: it does not install, load, or require a mod.

## First test sequence

1. Back up your save and use an offline character.
2. Connect, then click **检查已知定位**. It must report one match for all three entries.
3. On **角色**, enable and read the player context while the loaded character is standing still.
4. Test unlimited health and a small currency change first.
5. On **装备与词条**, enable item capture, click one unimportant backpack item in the game, return and read it. Test repair, duplicate, rarity, enchantment and template-create one at a time.

The UI intentionally does not expose free crafting, fall-damage prevention, or a global attack/damage multiplier. This build did not provide a safely verifiable shared entry for them, so they are kept out rather than risking an unknown write.

## Build on Windows

Install Visual Studio Community 2026 with the **.NET desktop development** workload (including the .NET 8 SDK), then open `NRftWManagerUI.csproj` and press `F5`. Or run:

```powershell
dotnet build
dotnet run
```

Do not test on a character you cannot restore. Re-run the three-entry check after every game update.
