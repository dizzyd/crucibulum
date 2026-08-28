using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Crucibulum;

/// <summary>
/// The forge's work item slot, widened to take a crucible.
///
/// The filters exist because there is now a dialog: without them a player could drop a boot in
/// the fuel slot. They have to mirror BEForge.OnPlayerInteract exactly, or the vanilla
/// shift-click paths start silently refusing things they used to accept.
/// </summary>
public class ItemSlotForgeWorkItem : ItemSlotSurvival
{
    public ItemSlotForgeWorkItem(InventoryBase inventory) : base(inventory) { }

    public override bool CanHold(ItemSlot sourceSlot)
    {
        return base.CanHold(sourceSlot) && Accepts(sourceSlot.Itemstack);
    }

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
    {
        return base.CanTakeFrom(sourceSlot, priority) && Accepts(sourceSlot.Itemstack);
    }

    public static bool Accepts(ItemStack stack)
    {
        if (stack == null) return false;
        if (BlockEntityCrucibulumForge.IsCrucible(stack)) return true;

        string first = stack.Collectible.FirstCodePart();
        return first == "ingot" || first == "metalplate" || first == "workitem"
               || stack.Collectible.Attributes?.IsTrue("forgable") == true;
    }
}

/// <summary>Fuel the forge will actually burn: vanilla's rule is a burn temperature over 1000.</summary>
public class ItemSlotForgeFuel : ItemSlotSurvival
{
    /// <summary>
    /// How deep the forge's coal bed goes.
    ///
    /// The forge draws its coal, and everything sitting on it, at
    /// <c>y + (fuelLevel - 1) / 64</c> blocks - the bed rises as it fills. Vanilla keeps that in
    /// range by refusing fuel once the level is over 4.5, but that guard lives in the shift-click
    /// path, not in the slot, so a window that exposes the slot lets a whole stack of 64 in and
    /// lifts the coals and the crucible a full block into the air above the forge.
    ///
    /// Six is what vanilla can actually reach: it will take one more whenever the level has burnt
    /// down to 4.5 or less.
    /// </summary>
    public const int MaxFuel = 6;

    public ItemSlotForgeFuel(InventoryBase inventory) : base(inventory)
    {
        MaxSlotStackSize = MaxFuel;
    }

    public override bool CanHold(ItemSlot sourceSlot)
    {
        return base.CanHold(sourceSlot) && Accepts(sourceSlot.Itemstack);
    }

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
    {
        return base.CanTakeFrom(sourceSlot, priority) && Accepts(sourceSlot.Itemstack);
    }

    public static bool Accepts(ItemStack stack) =>
        stack?.Collectible.GetCombustibleProperties(null, stack, null) is { BurnTemperature: > 1000 };
}

/// <summary>
/// One of the crucible's four ingredient slots. Mirrors the vanilla crucible's own filter --
/// storageType 4 (Metallurgy) plus something that melts and needs a container to do it.
/// </summary>
public class ItemSlotCrucibleCharge : ItemSlotSurvival
{
    public ItemSlotCrucibleCharge(InventoryBase inventory) : base(inventory)
    {
        StorageType = EnumItemStorageFlags.Metallurgy;
    }

    public override bool CanHold(ItemSlot sourceSlot)
    {
        return base.CanHold(sourceSlot) && Accepts(sourceSlot.Itemstack);
    }

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
    {
        return base.CanTakeFrom(sourceSlot, priority) && Accepts(sourceSlot.Itemstack);
    }

    public static bool Accepts(ItemStack stack)
    {
        if (stack == null) return false;

        CombustibleProperties props = stack.Collectible.GetCombustibleProperties(null, stack, null);
        if (props is { BurnTemperature: > 1000 }) return false;   // that is fuel, not a charge

        return props?.SmeltedStack != null && props.MeltingPoint > 0 && props.RequiresContainer;
    }
}
