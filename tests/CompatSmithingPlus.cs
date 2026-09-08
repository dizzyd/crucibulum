using System.Linq;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// The forge against a broken tool head Smithing Plus actually produced, not one built to
    /// their shape. A copper pickaxe is worn to its last point and broken under their
    /// ItemDamagedPatches, which hands the player a work item; that work item is then offered to
    /// the forge, and the answer must follow the config file the game booted with.
    ///
    /// These do not toggle the switches themselves. They read what is in effect and assert against
    /// it, so a boot with the shipped file exercises the refusal and a boot with the switches on
    /// exercises the admission - and both confirm that what the file says is what the forge does,
    /// which the in-process toggles in ForgeChargeSize cannot show.
    ///
    /// They no-op when Smithing Plus is not installed. Put it in the path first:
    ///
    ///   bash scripts/run.sh mods/crucibulum/tests --mod mods/crucibulum/crucibulum \
    ///        --mods ~/mods/sp-compat --filter CompatSmithingPlus
    /// </summary>
    public class CompatSmithingPlus
    {
        [BeforeEach]
        public async Task KeepThePlayerAlive() => await TestLife.Alive();

        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        static bool Installed => Sapi.ModLoader.IsModEnabled("smithingplus");

        static bool Absent(string what)
        {
            if (Installed) return false;
            Log($"  Smithing Plus not installed - {what} not checked");
            return true;
        }

        static IServerPlayer ThePlayer => (IServerPlayer)Sapi.World.AllOnlinePlayers.First();

        static async Task<BlockEntityCrucibulumForge> AForgeHoldingACrucible()
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(1);
            return be;
        }

        /// <summary>
        /// Wears a copper pickaxe out in the player's hand and returns the head Smithing Plus gave
        /// back for it. Their patch is a prefix on CollectibleObject.DamageItem that fires when the
        /// blow would take the last point of durability, server side, and puts the work item into
        /// the player's inventory.
        /// </summary>
        static async Task<ItemStack> ABrokenHeadOffARealTool()
        {
            var player = ThePlayer;
            var inv = player.InventoryManager;

            foreach (ItemSlot s in inv.GetHotbarInventory()) { s.Itemstack = null; s.MarkDirty(); }

            var pickaxe = World.Stack("game:pickaxe-copper");
            pickaxe.Attributes.SetInt("durability", 1);
            ItemSlot hand = inv.ActiveHotbarSlot;
            hand.Itemstack = pickaxe;
            hand.MarkDirty();
            await Ticks(1);

            pickaxe.Collectible.DamageItem(Sapi.World, player.Entity, hand, 1);
            await Ticks(2);

            var head = inv.GetHotbarInventory().FirstOrDefault(s => s.Itemstack?.Collectible.Code.Path.StartsWith("workitem-") == true)?.Itemstack
                    ?? World.Entities(ForgePos, 64).OfType<EntityItem>().Select(e => e.Itemstack)
                            .FirstOrDefault(s => s?.Collectible.Code.Path.StartsWith("workitem-") == true);

            Assert.NotNull(head, "Smithing Plus handed a work item back for the broken pickaxe");
            Log($"  their head: {head.Collectible.Code}, attributes {head.Attributes.ToJsonToken()}");
            return head.Clone();
        }

        static (bool canHold, int moved) Offer(BlockEntityCrucibulumForge be, ItemStack stack)
        {
            bool canHold = be.ChargeSlots[0].CanHold(new DummySlot(stack.Clone()));
            int moved = be.AddCharge(new DummySlot(stack.Clone()), 1);
            return (canHold, moved);
        }

        static CrucibulumConfig TheFile => Sapi.LoadModConfig<CrucibulumConfig>(CrucibulumModSystem.ConfigFile);

        [VsTest, RequiresClient]   // a real break needs a player holding the tool
        public async Task TheirHeadIsTheShapeWeRecognise()
        {
            if (Absent("their broken head's shape")) return;

            var head = await ABrokenHeadOffARealTool();

            Assert.True(head.Collectible is ItemWorkItem, "it is a vanilla work item");
            Assert.True(ItemSlotCrucibleCharge.Accepts(head), "which the crucible would melt");
            // The raw geometry, not Fits - that answer depends on the switch this boot was given.
            Size3f mouth = World.Stack("game:crucible-brown-fired").ItemAttributes["maxContentDimensions"].AsObject<Size3f>(null);
            Assert.False(mouth.CanContain(head.Collectible.Dimensions), "but it does not fit the crucible's mouth");
            Assert.True(ItemSlotCrucibleCharge.IsBrokenToolHead(head), "and we read it as a broken head");
            Assert.NotNull(head.Attributes.GetItemstack("repairedToolStack"), "the broken tool hangs off it");
            Assert.Equal(0, head.Attributes.GetInt("brokenCount"), "the count is not on the head itself off the break");
        }

        [VsTest, RequiresClient]   // a real break needs a player holding the tool
        public async Task TheirHeadFollowsTheConfigFile()
        {
            if (Absent("a real broken head at the forge")) return;

            bool on = CrucibulumModSystem.Config.MeltBrokenToolHeads;
            Assert.Equal(TheFile.MeltBrokenToolHeads, on, "the setting in effect is the one in the file");
            Log($"  MeltBrokenToolHeads is {(on ? "on" : "off")} in this boot");

            var head = await ABrokenHeadOffARealTool();
            var be = await AForgeHoldingACrucible();
            var (canHold, moved) = Offer(be, head);

            Assert.Equal(on, canHold, on ? "the window takes their head with the switch on" : "the window refuses their head with the switch off");
            Assert.Equal(on ? 1 : 0, moved, on ? "and it lands in the crucible" : "and nothing of it lands in the crucible");
        }

        [VsTest]
        public async Task AnIngotFollowsTheConfigFile()
        {
            if (Absent("an ingot at the forge with their patches loaded")) return;

            bool on = CrucibulumModSystem.Config.MeltIngots;
            Assert.Equal(TheFile.MeltIngots, on, "the setting in effect is the one in the file");
            Log($"  MeltIngots is {(on ? "on" : "off")} in this boot");

            var be = await AForgeHoldingACrucible();
            var (canHold, moved) = Offer(be, World.Stack("game:ingot-copper"));

            Assert.Equal(on, canHold, on ? "the window takes an ingot with the switch on" : "the window refuses an ingot with the switch off");
            Assert.Equal(on ? 1 : 0, moved, "and the crucible agrees");
        }

        [VsTest, RequiresClient]
        public async Task TheirRecoveryPathIsUntouched()
        {
            // Not ours to break: chiselling a head into bits is the balance the refusal protects.
            if (Absent("their chisel recipe")) return;

            var head = await ABrokenHeadOffARealTool();
            bool recipe = Sapi.World.GridRecipes.Any(r => r.Output?.ResolvedItemStack?.Collectible.Code.Path.StartsWith("metalbit-") == true
                && r.RecipeIngredients.Any(i => i?.ResolvedItemStack?.Collectible is ItemWorkItem));
            Assert.True(recipe, "their chisel-a-work-item recipe is registered");
            Assert.True(head.Collectible.Code.Path.StartsWith("workitem-"), "and the head is the work item it takes");
        }
    }
}
