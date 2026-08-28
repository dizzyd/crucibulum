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

            foreach (CollectibleObject obj in api.World.Collectibles)
            {
                if (obj is not BlockSmeltingContainer) continue;
                List<ItemStack> stacks = obj.GetHandBookStacks(capi);
                if (stacks != null) crucibleStacks.AddRange(stacks);
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
                    ActionLangCode = "crucibulum:blockhelp-forge-opencrucible",
                    MouseButton = EnumMouseButton.Right,
                    GetMatchingStacks = (wi, bs, es) =>
                    {
                        // Only where a plain click actually opens it, which is only a forge with a
                        // crucible in it. A bare forge, or one holding an ingot, does something else.
                        var be = api.World.BlockAccessor.GetBlockEntity(bs.Position) as BlockEntityCrucibulumForge;
                        return be?.CrucibleStack != null ? System.Array.Empty<ItemStack>() : null;
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
