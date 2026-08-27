using System.Linq;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// Loading and unloading the crucible.
    ///
    /// The charge deliberately lives in the forge rather than in the crucible stack, the way a
    /// firepit holds it: a crucible carried off with ore in it would be invisible to the firepit,
    /// which reads its ingredients from whatever heat source is holding it.
    ///
    /// These drive AddCharge/TakeCharge directly rather than through a click, so they run headless.
    /// ForgeClientSide covers the click itself, which is where the interesting routing question is.
    /// </summary>
    public class ForgeCharging
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        const string Crucible = "game:crucible-brown-fired";
        const string CopperNugget = "game:nugget-nativecopper";

        static async Task<BlockEntityCrucibulumForge> PlaceForge(string crucible = Crucible)
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            if (crucible != null) be.WorkItemSlot.Itemstack = World.Stack(crucible);
            be.MarkDirty(true);
            return be;
        }

        static DummySlot Holding(string code, int quantity) => new DummySlot(World.Stack(code, quantity));

        /// <summary>
        /// World.Entities' code filter matches the *entity* code, which for a dropped stack is
        /// always "game:item" - so the itemstack has to be looked at directly.
        /// </summary>
        static int DroppedNuggets() =>
            World.Entities(ForgePos, 6)
                .OfType<EntityItem>()
                .Where(e => e.Itemstack?.Collectible.Code.Path == "nugget-nativecopper")
                .Sum(e => e.Itemstack.StackSize);


        [VsTest]
        public async Task NuggetsGoIntoTheCrucibleOneAtATime()
        {
            var be = await PlaceForge();
            var hand = Holding(CopperNugget, 8);

            Assert.Equal(1, be.AddCharge(hand), "nuggets moved by a plain shift-click");
            Assert.Equal(1, Forge.ChargeSlots[0].StackSize, "what ended up in the crucible");
            Assert.Equal(7, hand.StackSize, "what stayed in hand");
        }

        [VsTest]
        public async Task CtrlMovesTheWholeStack()
        {
            var be = await PlaceForge();
            var hand = Holding(CopperNugget, 8);

            Assert.Equal(8, be.AddCharge(hand, hand.StackSize), "nuggets moved by shift+ctrl");
            Assert.True(hand.Empty, "the hand emptied");
        }

        [VsTest]
        public async Task NoCrucibleMeansNoCharge()
        {
            var be = await PlaceForge(crucible: null);
            Assert.False(be.CanAcceptCharge, "an empty forge accepts no charge");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AMoltenCrucibleTakesNoMoreOre()
        {
            var be = await PlaceForge("game:crucible-brown-smelted");
            Assert.False(be.CanAcceptCharge, "a crucible that has already gone molten");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ACrucibleGoesIntoAnEmptyForge()
        {
            var be = await PlaceForge(crucible: null);
            var hand = Holding(Crucible, 3);

            Assert.True(be.TrySetCrucible(hand), "setting a crucible in an empty forge");
            Assert.Equal("crucible-brown-fired", Forge.WorkItemStack?.Collectible.Code.Path, "what the forge holds");
            Assert.Equal(1, Forge.WorkItemStack.StackSize, "exactly one");
            Assert.Equal(2, hand.StackSize, "the other two stayed in hand");
        }

        [VsTest]
        public async Task ASecondCrucibleIsRefusedRatherThanMerged()
        {
            // BEForge's "merge heatable item" branch is guarded by !forgable, and the crucible
            // deliberately is not - so a refused crucible must not reach it, or it stacks a second
            // one into the slot and DoSmelt silently eats it.
            var be = await PlaceForge();
            var hand = Holding(Crucible, 3);

            Assert.False(be.TrySetCrucible(hand), "a forge that already holds a crucible");
            Assert.Equal(1, Forge.WorkItemStack.StackSize, "still exactly one crucible in the forge");
            Assert.Equal(3, hand.StackSize, "nothing was taken from the hand");
        }

        [VsTest]
        public async Task AWarmCrucibleCanStillBeToppedUp()
        {
            // Two stacks at different temperatures will not merge, so once the charge had warmed up
            // at all, adding more of the same ore was silently refused - the click did nothing and
            // looked like the forge rejecting it.
            var be = await PlaceForge();
            be.AddCharge(Holding(CopperNugget, 20), 20);

            be.ChargeSlots[0].Itemstack.Collectible.SetTemperature(Sapi.World, be.ChargeSlots[0].Itemstack, 600f);
            be.WorkItemStack.Collectible.SetTemperature(Sapi.World, be.WorkItemStack, 600f);

            Assert.Equal(20, be.AddCharge(Holding(CopperNugget, 20), 20), "a warm crucible still takes ore");
            Assert.Equal(40, Forge.ChargeSlots[0].StackSize, "and it lands in the slot already holding some");
        }

        [VsTest]
        public async Task ColdOreCoolsTheMelt()
        {
            // And it costs you: cold metal tipped into a hot crucible drags the whole lot down,
            // weighted by how much was already in there.
            var be = await PlaceForge();
            be.AddCharge(Holding(CopperNugget, 20), 20);
            be.WorkItemStack.Collectible.SetTemperature(Sapi.World, be.WorkItemStack, 1000f);
            be.ChargeSlots[0].Itemstack.Collectible.SetTemperature(Sapi.World, be.ChargeSlots[0].Itemstack, 1000f);

            be.AddCharge(Holding(CopperNugget, 60), 60);

            float after = Forge.WorkItemStack.Collectible.GetTemperature(Sapi.World, Forge.WorkItemStack);
            Assert.Less(after, 1000f, "the crucible cooled");
            Assert.Greater(after, 20f, "but not all the way to the ore's temperature");
        }

        [VsTest]
        public async Task FuelStillGoesToTheFuelSlot()
        {
            // The charge filter runs before the vanilla forge's, so it has to refuse anything the
            // forge would rather burn. Coal ore carries a burn temperature above 1000 and would
            // otherwise vanish into the crucible instead of feeding the fire.
            var be = await PlaceForge();

            Assert.False(be.IsCrucibleCharge(World.Stack("game:ore-bituminouscoal")), "coal ore is not a charge");
            Assert.False(be.IsCrucibleCharge(World.Stack("game:coke")), "coke is not a charge");
            Assert.False(be.IsCrucibleCharge(World.Stack("game:charcoal")), "charcoal is not a charge");
            Assert.True(be.IsCrucibleCharge(World.Stack(CopperNugget)), "a copper nugget is a charge");
            Assert.True(be.IsCrucibleCharge(World.Stack("game:ingot-copper")), "a copper ingot is a charge");

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task FourDifferentIngredientsFitForAnAlloy()
        {
            // Bronze needs two, and the vanilla crucible offers four slots. Same-item clicks merge,
            // different items take a fresh slot.
            var be = await PlaceForge();

            var copper = Holding(CopperNugget, 4);
            be.AddCharge(copper);
            be.AddCharge(copper);

            var tin = Holding("game:nugget-cassiterite", 4);
            be.AddCharge(tin);

            Assert.Equal(2, Forge.ChargeSlots[0].StackSize, "copper merged into one slot");
            Assert.Equal("nugget-cassiterite", Forge.ChargeSlots[1].Itemstack?.Collectible.Code.Path,
                "tin took the next slot");
        }

        [VsTest]
        public async Task TakingTheCrucibleLeavesTheChargeBehind()
        {
            // Same as a firepit: the ore belongs to the heat source. Pulling the crucible out mid-melt
            // must not delete it.
            var be = await PlaceForge();
            be.AddCharge(Holding(CopperNugget, 4), 4);

            be.WorkItemSlot.Itemstack = null;
            be.MarkDirty(true);
            await Ticks(2);

            Assert.False(Forge.ChargeEmpty, "the charge survived the crucible leaving");

            Assert.True(Forge.TakeCharge(null), "the charge can be pulled back out");
            Assert.True(Forge.ChargeEmpty, "the forge is empty afterwards");

            await Until(() => DroppedNuggets() == 4, 60,
                "with nobody to hand it to, the charge lands on the ground rather than vanishing");
        }

        [VsTest]
        public async Task BreakingTheForgeDropsTheCharge()
        {
            var be = await PlaceForge();
            be.AddCharge(Holding(CopperNugget, 4), 4);

            Sapi.World.BlockAccessor.BreakBlock(ForgePos, null);

            await Until(() => DroppedNuggets() == 4, 60,
                "the charge dropped as an item rather than being cleared away");
        }

        [VsTest]
        public async Task TheChargeSurvivesASaveAndReload()
        {
            // The charge lives in an inventory of our own, next to the forge's, so it is only
            // persisted because ToTreeAttributes says so. Silence here means ore disappearing on
            // chunk unload.
            var be = await PlaceForge();
            be.AddCharge(Holding(CopperNugget, 3), 3);

            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            be.ToTreeAttributes(tree);

            be.TakeCharge(null);
            Assert.True(Forge.ChargeEmpty, "cleared before reloading");

            be.FromTreeAttributes(tree, Sapi.World);

            Assert.Equal(3, Forge.ChargeSlots[0].StackSize, "the charge came back off the tree");
            await Task.CompletedTask;
        }
    }
}
