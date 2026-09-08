// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
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
/// storageType 4 (Metallurgy) plus something that melts and needs a container to do it -- and
/// the crucible's own mouth, which is the part that is easy to miss.
///
/// A firepit's crucible refuses an ingot, and with it a work item and a broken tool head. Not
/// for anything to do with smelting - all three carry combustible props that melt them back into
/// a whole ingot - but because the inventory compares an item's <c>size</c> against the
/// container's <c>maxContentDimensions</c>, and the fired crucible declares a mouth of
/// 0.125 x 0.25 x 0.125 that a nugget fits and the collectible default of 0.5 does not. That is
/// the only thing standing between a tool that has been worn to nothing and a full 100 units of
/// metal back; Smithing Plus, whose broken heads are plain work items, leans on it. This slot once
/// applied the smelting rules and not the size, so the forge took all three.
/// </summary>
public class ItemSlotCrucibleCharge : ItemSlotSurvival
{
    public ItemSlotCrucibleCharge(InventoryBase inventory) : base(inventory)
    {
        StorageType = EnumItemStorageFlags.Metallurgy;
    }

    /// <summary>The crucible seated in the forge, whose mouth sets the limit. Slot 0 is the work item.</summary>
    private ItemStack CrucibleInForge => inventory[0]?.Itemstack;

    public override bool CanHold(ItemSlot sourceSlot)
    {
        return base.CanHold(sourceSlot) && Accepts(sourceSlot.Itemstack) && Fits(sourceSlot.Itemstack, CrucibleInForge);
    }

    public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
    {
        return base.CanTakeFrom(sourceSlot, priority) && Accepts(sourceSlot.Itemstack) && Fits(sourceSlot.Itemstack, CrucibleInForge);
    }

    /// <summary>Whether this is something a crucible melts at all. Size is a separate question.</summary>
    public static bool Accepts(ItemStack stack)
    {
        if (stack == null) return false;

        CombustibleProperties props = stack.Collectible.GetCombustibleProperties(null, stack, null);
        if (props is { BurnTemperature: > 1000 }) return false;   // that is fuel, not a charge

        return props?.SmeltedStack != null && props.MeltingPoint > 0 && props.RequiresContainer;
    }

    /// <summary>
    /// Whether the stack fits through the crucible's mouth, decided the way a firepit decides it:
    /// the item's dimensions against the container's <c>maxContentDimensions</c>. A crucible that
    /// declares no limit takes anything, as vanilla's inventory does when the attribute is absent,
    /// and so does an empty forge, where there is no mouth to measure against.
    ///
    /// The two config switches are exemptions from this and nothing else: each admits exactly its
    /// own class of item and leaves the limit in place for everything else.
    /// </summary>
    public static bool Fits(ItemStack stack, ItemStack crucible)
    {
        if (stack == null) return false;

        Size3f mouth = crucible?.ItemAttributes?["maxContentDimensions"].AsObject<Size3f>(null);
        if (mouth == null || mouth.CanContain(stack.Collectible.Dimensions)) return true;

        CrucibulumConfig config = CrucibulumModSystem.Config;
        return (config.MeltIngots && IsIngot(stack))
            || (config.MeltBrokenToolHeads && IsBrokenToolHead(stack));
    }

    public static bool IsIngot(ItemStack stack) => stack?.Collectible.FirstCodePart() == "ingot";

    /// <summary>
    /// A tool head that came off a broken tool, as Smithing Plus marks one: a work item carrying
    /// <c>brokenCount</c>, either on itself or on the tool stack it remembers having repaired. Read
    /// by attribute name so nothing of theirs is referenced; without their mod nothing carries it
    /// and this is simply false. An unfinished work item straight off the anvil has no count and
    /// is not a broken head, and no switch here admits it.
    /// </summary>
    public static bool IsBrokenToolHead(ItemStack stack)
    {
        ITreeAttribute attributes = stack?.Attributes;
        if (attributes == null) return false;
        if (attributes.GetInt("brokenCount") > 0) return true;

        return attributes.GetItemstack("repairedToolStack")?.Attributes?.GetInt("brokenCount") > 0;
    }
}
