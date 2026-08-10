namespace NRftWManagerUI.Core;

internal readonly record struct PlayerRuntimeContext(
    long Frame,
    long Hero,
    long InventoryComponent,
    long HeroComponent,
    long StatsComponent,
    long HealthComponent,
    long StaminaComponent,
    long AttributeComponent,
    long FocusComponent,
    long LevelComponent)
{
    public bool HasWallet => InventoryComponent != 0;
    public bool HasHero => HeroComponent != 0;
    public bool HasStats => StatsComponent != 0 && HealthComponent != 0 && StaminaComponent != 0 &&
                            AttributeComponent != 0 && FocusComponent != 0 && LevelComponent != 0;
}

internal enum PlayerCommand
{
    None = 0,
    LevelUp = 1,
    ChangeSelectedItemRarity = 2,
    AddSelectedItemEnchantment = 3,
    DuplicateSelectedItem = 4,
    RepairSelectedItem = 5,
    CreateSelectedItem = 6,
    SetSelectedItemLevel = 7,
    AwardGloamseed = 8,
    GiveMaxStats = 9,
    UnlockFastTravel = 10
}
