using System.Linq;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// What the crucible will take: the same things a firepit's crucible takes, and no more.
    ///
    /// A vanilla firepit refuses an ingot, a work item and a broken tool head by size - the fired
    /// crucible declares a mouth of 0.125 x 0.25 x 0.125 and those three carry the collectible
    /// default of 0.5 - which is what keeps a whole ingot from coming back out of a tool that has
    /// been worn to nothing. The forge's charge slot once mirrored the crucible's smelting rules
    /// and not its size limit, so it took all three; a user of Smithing Plus, whose broken heads
    /// are plain work items, found a 100-unit ingot in every one. These pin the forge to what the
    /// firepit does, and pin the two switches that relax it to their own class and nothing more.
    /// </summary>
    public class ForgeChargeSize
    {
        [BeforeEach]
        public async Task KeepThePlayerAlive() => await TestLife.Alive();

        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        const string Crucible = "game:crucible-brown-fired";

        static async Task<BlockEntityCrucibulumForge> AForgeHoldingACrucible()
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.WorkItemSlot.Itemstack = World.Stack(Crucible);
            be.MarkDirty(true);
            await Ticks(1);
            return be;
        }

        static ItemStack Item(string code) => new ItemStack(Sapi.World.GetItem(new AssetLocation(code)));

        /// <summary>
        /// A copper pickaxe head the way Smithing Plus hands one back when the tool breaks. Their
        /// ItemDamagedPatches builds a plain vanilla work item with the recipe's voxels and id, and
        /// hangs the broken tool itself off it as repairedToolStack - it is the *tool* that carries
        /// brokenCount, and the head reads it through. Built here rather than borrowed, so this runs
        /// without their mod; CompatSmithingPlus breaks a real tool under their code and checks the
        /// two agree.
        /// </summary>
        public static ItemStack ABrokenToolHead()
        {
            var stack = AnUnfinishedWorkItem();
            var tool = Item("game:pickaxe-copper");
            tool.Attributes.SetInt("brokenCount", 1);
            stack.Attributes.SetItemstack("repairedToolStack", tool);
            return stack;
        }

        /// <summary>
        /// The same head with the count on the work item itself, which is how it reads once a
        /// repaired head has been through their crafting postfix.
        /// </summary>
        public static ItemStack ABrokenToolHeadCountedOnItself()
        {
            var stack = AnUnfinishedWorkItem();
            stack.Attributes.SetInt("brokenCount", 1);
            return stack;
        }

        /// <summary>A head before it ever broke: an unfinished work item off the anvil.</summary>
        public static ItemStack AnUnfinishedWorkItem()
        {
            var stack = Item("game:workitem-copper");
            var recipe = Sapi.GetSmithingRecipes()
                .First(r => r.Output.ResolvedItemstack?.Collectible.Code.Path.Contains("pickaxehead") == true
                            && r.Ingredient.Code.Path.Contains("copper"));
            bool[,,] v = recipe.Voxels;
            byte[,,] voxels = new byte[16, 6, 16];
            for (int x = 0; x < 16; x++) for (int y = 0; y < 6; y++) for (int z = 0; z < 16; z++)
                if (v[x, y, z]) voxels[x, y, z] = 1;
            stack.Attributes.SetBytes("voxels", BlockEntityAnvil.serializeVoxels(voxels));
            stack.Attributes.SetInt("selectedRecipeId", recipe.RecipeId);
            return stack;
        }

        /// <summary>
        /// Both ways into the charge: the window's drag asks the slot's CanHold, and a shift-click
        /// or a click on the block goes through TryPutInto, which asks CanTakeFrom. Returns what the
        /// slot said and how many actually arrived.
        /// </summary>
        static (bool canHold, int moved) Offer(BlockEntityCrucibulumForge be, ItemStack stack)
        {
            bool canHold = be.ChargeSlots[0].CanHold(new DummySlot(stack.Clone()));
            int moved = be.AddCharge(new DummySlot(stack.Clone()), 1);
            return (canHold, moved);
        }

        static void AssertRefused(BlockEntityCrucibulumForge be, ItemStack stack, string why)
        {
            var (canHold, moved) = Offer(be, stack);
            Assert.False(canHold, $"the window refuses {stack.Collectible.Code}: {why}");
            Assert.Equal(0, moved, $"and nothing of it lands in the crucible");
            Assert.True(be.ChargeEmpty, "the charge is still empty");
        }

        static void AssertTaken(BlockEntityCrucibulumForge be, ItemStack stack, string why)
        {
            var (canHold, moved) = Offer(be, stack);
            Assert.True(canHold, $"the window takes {stack.Collectible.Code}: {why}");
            Assert.Equal(1, moved, "and it lands in the crucible");
        }

        // -- parity with the firepit --------------------------------------------------------------

        [VsTest]
        public async Task ANuggetGoesIn()
        {
            var be = await AForgeHoldingACrucible();
            AssertTaken(be, Item("game:nugget-nativecopper"), "it is the ordinary charge");
        }

        [VsTest]
        public async Task AnIngotIsRefusedAsItIsAtAFirepit()
        {
            var be = await AForgeHoldingACrucible();
            AssertRefused(be, Item("game:ingot-copper"), "an ingot does not fit the crucible's mouth");
        }

        [VsTest]
        public async Task AnUnfinishedWorkItemIsRefused()
        {
            var be = await AForgeHoldingACrucible();
            AssertRefused(be, AnUnfinishedWorkItem(), "a work item is the same size as the ingot it came from");
        }

        [VsTest]
        public async Task ABrokenToolHeadIsRefused()
        {
            var be = await AForgeHoldingACrucible();
            AssertRefused(be, ABrokenToolHead(), "a worn-out head does not become a whole ingot again");
        }

        [VsTest]
        public async Task TheLimitIsTheCruciblesOwn()
        {
            // Read off the seated crucible's attribute, not a constant, so a modded crucible with a
            // wider mouth - or none declared - is measured the way vanilla would measure it.
            var crucible = World.Stack(Crucible);
            Assert.True(ItemSlotCrucibleCharge.Fits(Item("game:nugget-nativecopper"), crucible), "a nugget fits the mouth");
            Assert.False(ItemSlotCrucibleCharge.Fits(Item("game:ingot-copper"), crucible), "an ingot does not");
            Assert.True(ItemSlotCrucibleCharge.Fits(Item("game:ingot-copper"), null), "with no mouth to measure against, nothing is refused");
            await Task.CompletedTask;
        }

        // -- the switches -------------------------------------------------------------------------

        [AfterEach]
        public Task SwitchesOff()
        {
            CrucibulumModSystem.Config.MeltIngots = false;
            CrucibulumModSystem.Config.MeltBrokenToolHeads = false;
            return Task.CompletedTask;
        }

        [VsTest]
        public async Task TheShippedDefaultsAreOff()
        {
            var fresh = new CrucibulumConfig();
            Assert.False(fresh.MeltIngots, "a fresh config refuses ingots, as a firepit does");
            Assert.False(fresh.MeltBrokenToolHeads, "and broken tool heads");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task MeltIngotsAdmitsIngotsAndNothingElse()
        {
            CrucibulumModSystem.Config.MeltIngots = true;
            var be = await AForgeHoldingACrucible();

            AssertRefused(be, AnUnfinishedWorkItem(), "the ingot switch says nothing about work items");
            AssertRefused(be, ABrokenToolHead(), "nor about broken heads");
            AssertTaken(be, Item("game:ingot-copper"), "the switch is on");
        }

        [VsTest]
        public async Task MeltBrokenToolHeadsAdmitsHeadsAndNothingElse()
        {
            CrucibulumModSystem.Config.MeltBrokenToolHeads = true;
            var be = await AForgeHoldingACrucible();

            AssertRefused(be, Item("game:ingot-copper"), "the head switch says nothing about ingots");
            AssertRefused(be, AnUnfinishedWorkItem(), "an unbroken work item is not a broken head");
            AssertTaken(be, ABrokenToolHead(), "the switch is on");
        }

        [VsTest]
        public async Task BothPlacesTheCountCanLiveAreRead()
        {
            // Off the break it is on the tool hanging from the head; after their crafting postfix
            // it is on the head itself. Either is a broken head; a plain work item has neither.
            Assert.True(ItemSlotCrucibleCharge.IsBrokenToolHead(ABrokenToolHead()), "read through the broken tool");
            Assert.True(ItemSlotCrucibleCharge.IsBrokenToolHead(ABrokenToolHeadCountedOnItself()), "read off the head");
            Assert.False(ItemSlotCrucibleCharge.IsBrokenToolHead(AnUnfinishedWorkItem()), "a plain work item carries none");

            CrucibulumModSystem.Config.MeltBrokenToolHeads = true;
            var be = await AForgeHoldingACrucible();
            AssertTaken(be, ABrokenToolHeadCountedOnItself(), "the switch admits the head-counted form too");
        }
    }
}
