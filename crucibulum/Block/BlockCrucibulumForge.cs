using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Crucibulum;

/// <summary>
/// The vanilla forge block, repointed at <see cref="BlockEntityCrucibulumForge"/> and given first
/// refusal on the interactions vanilla does not handle. BlockEntityForge.OnPlayerInteract is not
/// virtual, so the hook has to happen here rather than in the block entity.
/// </summary>
public class BlockCrucibulumForge : BlockForge
{
    private WorldInteraction[] crucibleInteractions;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        if (api.Side != EnumAppSide.Client) return;
        ICoreClientAPI capi = (ICoreClientAPI)api;

        crucibleInteractions = ObjectCacheUtil.GetOrCreate(api, "crucibulumForgeInteractions", () =>
        {
            List<ItemStack> crucibleStacks = new();
            List<ItemStack> chargeStacks = new();

            InteractionStacksDelegate chargeStacksFor = (wi, bs, es) =>
            {
                var be = api.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityCrucibulumForge;
                if (be?.CrucibleStack == null || BlockEntityCrucibulumForge.IsMoltenCrucible(be.CrucibleStack)) return null;
                return wi.Itemstacks.Where(be.IsCrucibleCharge).ToArray();
            };

            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj is BlockSmeltingContainer)
                {
                    List<ItemStack> stacks = obj.GetHandBookStacks(capi);
                    if (stacks != null) crucibleStacks.AddRange(stacks);
                    continue;
                }

                CombustibleProperties props = obj.CombustibleProps;
                if (props?.SmeltedStack == null || props.MeltingPoint <= 0 || !props.RequiresContainer) continue;
                if (props.BurnTemperature > 1000) continue;
                if ((obj.StorageFlags & EnumItemStorageFlags.Metallurgy) == 0) continue;

                List<ItemStack> chargeable = obj.GetHandBookStacks(capi);
                if (chargeable != null) chargeStacks.AddRange(chargeable);
            }

            return new[]
            {
                new WorldInteraction
                {
                    // No hotkey: a crucible goes in on a plain click, the way it does at a firepit.
                    ActionLangCode = "crucibulum:blockhelp-forge-addcrucible",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = crucibleStacks.ToArray(),
                    GetMatchingStacks = (wi, bs, es) =>
                        api.World.BlockAccessor.GetBlockEntity(bs.Position) is BlockEntityCrucibulumForge { WorkItemStack: null }
                            ? wi.Itemstacks
                            : null
                },
                new WorldInteraction
                {
                    ActionLangCode = "crucibulum:blockhelp-forge-takecrucible",
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right,
                    GetMatchingStacks = (wi, bs, es) =>
                    {
                        var be = api.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityCrucibulumForge;
                        return be?.CrucibleStack != null ? System.Array.Empty<ItemStack>() : null;
                    }
                },
                new WorldInteraction
                {
                    ActionLangCode = "crucibulum:blockhelp-forge-addcharge",
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = chargeStacks.ToArray(),
                    GetMatchingStacks = chargeStacksFor
                },
                new WorldInteraction
                {
                    ActionLangCode = "crucibulum:blockhelp-forge-addchargestack",
                    HotKeyCodes = new[] { "shift", "ctrl" },
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = chargeStacks.ToArray(),
                    GetMatchingStacks = chargeStacksFor
                },
                new WorldInteraction
                {
                    ActionLangCode = "crucibulum:blockhelp-forge-opencrucible",
                    MouseButton = EnumMouseButton.Right,
                    GetMatchingStacks = (wi, bs, es) =>
                    {
                        // Only where a plain click actually opens it: a forge holding an ingot hands
                        // the ingot over instead, and must keep saying so.
                        var be = api.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityCrucibulumForge;
                        if (be == null) return null;
                        return be.WorkItemStack == null || be.CrucibleStack != null
                            ? System.Array.Empty<ItemStack>()
                            : null;
                    }
                },
                new WorldInteraction
                {
                    ActionLangCode = "crucibulum:blockhelp-forge-takecharge",
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right,
                    GetMatchingStacks = (wi, bs, es) =>
                    {
                        var be = api.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityCrucibulumForge;
                        return be != null && be.WorkItemStack == null && !be.ChargeEmpty ? System.Array.Empty<ItemStack>() : null;
                    }
                }
            };
        });
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityCrucibulumForge be
            && be.OnPlayerInteractCrucible(world, byPlayer, blockSel))
        {
            return true;
        }

        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        WorldInteraction[] vanilla = base.GetPlacedBlockInteractionHelp(world, selection, forPlayer);
        return crucibleInteractions == null ? vanilla : crucibleInteractions.Append(vanilla);
    }
}
