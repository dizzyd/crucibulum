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

        /// <summary>
        /// A forge whose work slot holds exactly what is asked for - including nothing, which is
        /// what the slot reads while the crucible is riding the mouse cursor.
        /// </summary>
        static async Task<BlockEntityCrucibulumForge> AForgeWhoseWorkSlotHolds(ItemStack stack)
        {
            var be = await AForgeHoldingACrucible();
            be.WorkItemSlot.Itemstack = stack;
            be.MarkDirty(true);
            await Ticks(1);
            return be;
        }

        static ItemStack Item(string code) => new ItemStack(Sapi.World.GetItem(new AssetLocation(code)));

        /// <summary>A crucible that has been run molten: the state a forge is left in after a melt.</summary>
        static ItemStack AMoltenCrucible()
        {
            var smelted = (BlockSmeltedContainer)Sapi.World.GetBlock(new AssetLocation("game:crucible-brown-smelted"));
            var stack = new ItemStack(smelted);
            smelted.SetContents(stack, World.Stack("game:ingot-copper"), 100);
            return stack;
        }

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
            Assert.True(ItemSlotCrucibleCharge.FitsMouth(Item("game:nugget-nativecopper"), crucible), "a nugget fits the mouth");
            Assert.False(ItemSlotCrucibleCharge.FitsMouth(Item("game:ingot-copper"), crucible), "an ingot does not");

            // The size helper answers only the size question, and with no crucible to measure
            // against there is nothing for it to refuse. That is not admission: reading the two as
            // the same thing is what let an ingot into a forge with no fired crucible in it.
            Assert.True(ItemSlotCrucibleCharge.FitsMouth(Item("game:ingot-copper"), null), "nothing to measure against");
            Assert.False(ItemSlotCrucibleCharge.Admits(Item("game:ingot-copper"), null), "but nothing to put it in either");
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

        // -- there has to be a crucible ------------------------------------------------------------

        [VsTest]
        public async Task NoFiredCrucibleMeansNothingGoesIn()
        {
            // Reported as "MeltIngots: false no longer stops ingots melting". The size check reads
            // the mouth of whatever is in the work slot, and an absent or molten crucible declares
            // none - so the check, and both switches with it, stopped running rather than refusing.
            //
            // Both switches are *on* here on purpose: the rule being pinned is that there has to be
            // a fired crucible to charge, not that an ingot is forbidden. Ordinary ore is refused in
            // these states too.
            CrucibulumModSystem.Config.MeltIngots = true;
            CrucibulumModSystem.Config.MeltBrokenToolHeads = true;

            foreach (var (what, inSlotZero) in new[]
            {
                ("nothing - the crucible is riding the cursor", (ItemStack)null),
                ("an ingot being smithed", World.Stack("game:ingot-copper")),
                ("a crucible already run molten", AMoltenCrucible()),
            })
            {
                var be = await AForgeWhoseWorkSlotHolds(inSlotZero);
                Log($"  work slot holds {what}");

                Assert.False(be.CanAcceptCharge, $"the forge accepts no charge with {what} in it");
                AssertRefused(be, Item("game:ingot-copper"), "there is no fired crucible to melt it in");
                AssertRefused(be, Item("game:nugget-nativecopper"), "and ordinary ore is no different");
            }
        }

        [VsTest]
        public async Task LiftingTheCrucibleIsNotAWayIn()
        {
            // The window deliberately stays open while the crucible is on the cursor, so that a drag
            // is not cut off half way. That is the moment the work slot reads empty.
            var be = await AForgeHoldingACrucible();

            ItemStack onTheCursor = be.WorkItemSlot.TakeOutWhole();
            be.MarkDirty(true);
            await Ticks(1);

            AssertRefused(be, Item("game:ingot-copper"), "the crucible is on the cursor, not in the forge");

            be.WorkItemSlot.Itemstack = onTheCursor;
            be.MarkDirty(true);
            await Ticks(1);

            Assert.True(Forge.ChargeEmpty, "so the crucible comes back to an empty charge");

            // The end the report was about: not just an empty slot, but nothing for the forge to
            // turn into metal once the crucible is back in and the fire is up.
            Assert.False(
                Forge.CrucibleStack.Collectible.CanSmelt(Sapi.World, Forge.ChargeProvider, Forge.CrucibleStack, null),
                "and the forge has nothing to melt");
        }

        [VsTest]
        public async Task AMoltenCrucibleCannotBeLoadedAndSwappedOut()
        {
            // The other half of the same hole, and the one that needed no gesture at all: a molten
            // crucible declares no mouth either. Flipping a fired crucible straight onto the work
            // slot is one click, so the slot never reads empty and the handback never fires - a
            // charge loaded here would simply still be sitting there, ready to melt.
            var be = await AForgeWhoseWorkSlotHolds(AMoltenCrucible());

            AssertRefused(be, Item("game:ingot-copper"), "a crucible that has already run has nothing to melt");

            // A real flip, not an assignment: one operation, hand for slot, so the work slot never
            // reads empty in between and the charge handback never gets a chance to fire.
            //
            // The hand is a slot in an inventory of its own rather than a bare DummySlot, because
            // TryFlipWith asks the other side's CanHold and ItemSlot.CanHold dereferences that
            // slot's inventory. A detached one has none, and this runs headless, where there is no
            // player to borrow a real hotbar slot from.
            ItemSlot hand = new InventoryGeneric(1, "crucibulum-thehand", Sapi)[0];
            hand.Itemstack = World.Stack(Crucible);

            Assert.True(be.WorkItemSlot.TryFlipWith(hand), "a fired crucible swaps straight in for the molten one");
            Assert.Equal("crucible-brown-smelted", hand.Itemstack?.Collectible.Code.Path, "the molten one came out to the hand");
            be.MarkDirty(true);
            await Ticks(1);

            Assert.Equal("crucible-brown-fired", Forge.WorkItemStack?.Collectible.Code.Path, "the fired one is seated");
            Assert.True(Forge.ChargeEmpty, "and it inherits nothing");
            Assert.False(
                Forge.CrucibleStack.Collectible.CanSmelt(Sapi.World, Forge.ChargeProvider, Forge.CrucibleStack, null),
                "so there is nothing for it to melt");
            Assert.True(Forge.CanAcceptCharge, "though it can be charged from here as usual");
        }

        [VsTest]
        public async Task AModdedCrucibleWithNoMouthStillTakesWhatItLikes()
        {
            // "No mouth declared" has to keep meaning "no limit" for a container that genuinely
            // declares none - that is how vanilla's inventory reads a missing attribute, and a
            // modded crucible is entitled to it. What changed is that there must *be* a container.
            //
            // Built by taking the mouth off one colour of the vanilla crucible, since vanilla ships
            // no mouthless smelting container to borrow.
            var block = Sapi.World.GetBlock(new AssetLocation("game:crucible-red-fired"));
            var mouthed = block.Attributes;
            try
            {
                // Its own attributes with the one key lifted out, rather than a hand-written stand-in:
                // everything else the crucible declares - glow, shelvable, firepit props, transforms -
                // stays exactly as it was for the moments this is in place.
                var mouthless = mouthed.Token.DeepClone();
                ((Newtonsoft.Json.Linq.JObject)mouthless).Remove("maxContentDimensions");
                block.Attributes = new Vintagestory.API.Datastructures.JsonObject(mouthless);

                Assert.Null(block.Attributes["maxContentDimensions"].AsObject<Size3f>(null), "the mouth is gone");
                Assert.Equal(4, block.Attributes["cookingContainerSlots"].AsInt(0), "everything else it declares is still there");

                var be = await AForgeWhoseWorkSlotHolds(World.Stack("game:crucible-red-fired"));
                Assert.True(be.CanAcceptCharge, "it is a fired smelting container like any other");
                AssertTaken(be, Item("game:ingot-copper"), "and it declares no mouth to refuse an ingot");
            }
            finally
            {
                block.Attributes = mouthed;
            }
        }
    }
}
