// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Crucibulum;

/// <summary>
/// Keeps crucibles out of the firepit when <see cref="CrucibulumConfig.CrucibleOnlyInForge"/> is
/// on.
///
/// The firepit accepts a crucible by its C# class, not by anything a JSON patch could reach:
/// BlockFirepit.OnBlockInteractStart tests <c>is BlockSmeltingContainer or BlockSmeltedContainer</c>
/// and InventorySmelting.CanContain says yes to its first three slots unconditionally. What every
/// entry path does share is the input slot itself, an <see cref="ItemSlotInput"/>: the click on
/// the block and a shift-click from the inventory both go through TryPutInto, which asks
/// <see cref="ItemSlotInput.CanTakeFrom"/>, and a drag in the window or a flip asks
/// <see cref="ItemSlotInput.CanHold"/>. Two postfixes on those cover the input slot. Taking a
/// crucible out is CanTake, which is left alone, so nothing already in a firepit is stranded by
/// the switch.
///
/// The fuel slot is a plain ItemSlotSurvival that takes anything, so with the input slot closed a
/// shift-click from the inventory would land the crucible there instead - in a firepit, doing
/// nothing, which is exactly what the switch says cannot happen. A third postfix, on the
/// firepit inventory's own <see cref="InventorySmelting.CanContain"/>, closes that: TryPutInto
/// and the base CanHold both consult it, for every slot of the inventory.
///
/// Vanilla creates ItemSlotInput only from InventorySmelting, so in practice all of this is the
/// firepit. A mod that reuses either class for a firepit-alike gets the same refusal, which is
/// what "only in the forge" ought to mean. The forge's own slots are ItemSlotSurvival subclasses
/// in an InventoryGeneric and are untouched.
///
/// Both sides are patched. The client predicts a drag against its own copy of the inventory, so a
/// server-only patch would show the crucible landing and then snap back. The postfix is a
/// boolean-and that reads the config on every call, so it is idempotent: in singleplayer, where
/// both sides resolve the same assembly, the patch is installed once and left in place for the
/// life of the process rather than registered per side and unpatched on dispose.
/// </summary>
[HarmonyPatch]   // bare: PatchAll only looks at classes carrying the attribute; the targets are on the methods
public static class FirepitCruciblePatch
{
    public const string HarmonyId = "com.dizzyd.crucibulum";

    private static Harmony harmony;

    public static void Install(ICoreAPI api)
    {
        if (harmony != null || Harmony.HasAnyPatches(HarmonyId)) return;
        harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(FirepitCruciblePatch).Assembly);
        api.Logger.Notification("[crucibulum] firepit input slot patched; CrucibleOnlyInForge is {0}",
            CrucibulumModSystem.Config.CrucibleOnlyInForge ? "on" : "off");
    }

    /// <summary>Whether the firepit input slot turns this stack away.</summary>
    public static bool Refuses(ItemStack stack) =>
        CrucibulumModSystem.Config.CrucibleOnlyInForge && BlockEntityCrucibulumForge.IsCrucible(stack);

    // Parameter names match the vanilla signatures; Harmony binds by name and throws at patch
    // time if they drift, which is the failure this wants - loud, not silent.

    [HarmonyPostfix, HarmonyPatch(typeof(ItemSlotInput), nameof(ItemSlotInput.CanTakeFrom))]
    private static void CanTakeFrom(ItemSlot sourceSlot, ref bool __result)
    {
        if (__result && Refuses(sourceSlot?.Itemstack)) __result = false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(ItemSlotInput), nameof(ItemSlotInput.CanHold))]
    private static void CanHold(ItemSlot slot, ref bool __result)
    {
        if (__result && Refuses(slot?.Itemstack)) __result = false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(InventorySmelting), nameof(InventorySmelting.CanContain))]
    private static void CanContain(ItemSlot sourceSlot, ref bool __result)
    {
        if (__result && Refuses(sourceSlot?.Itemstack)) __result = false;
    }
}
