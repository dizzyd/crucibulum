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
    /// Heating and smelting a crucible in the forge.
    ///
    /// All server-side, so they run headless. The forge's own heating works from
    /// Calendar.TotalHours deltas, so it needs one tick to take a baseline and another after the
    /// clock has moved; melt progress on the other hand accrues in real seconds, exactly as it does
    /// in a firepit, so it is waited out rather than fast-forwarded.
    /// </summary>
    public class ForgeMelting
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        const string Crucible = "game:crucible-brown-fired";
        const string CopperNugget = "game:nugget-nativecopper";

        static float TempOf(ItemStack stack) => stack.Collectible.GetTemperature(Sapi.World, stack);

        /// <summary>A lit forge with a crucible in it and the given charge in the crucible.</summary>
        static async Task<BlockEntityCrucibulumForge> LitForge(string fuel, string charge = null, int chargeQty = 0)
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack(fuel, 8);
            be.WorkItemSlot.Itemstack = World.Stack(Crucible);
            if (charge != null) be.ChargeSlots[0].Itemstack = World.Stack(charge, chargeQty);
            be.TryIgnite();
            be.MarkDirty(true);

            return be;
        }

        /// <summary>Baseline firing, move the clock, fire again: the forge heats on calendar deltas.</summary>
        static async Task SoakFor(double hours)
        {
            await World.TickNow(ForgePos);
            await Hours(hours);
            await World.TickNow(ForgePos);
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task ACokeForgeDrivesACrucibleToTwelveHundred()
        {
            // 700 base + coke's tempGainDeg of 100 + the mod's 400 crucible bonus is 1200, which is
            // also the crucible's own maxHeatableTemp, so this is the ceiling from both directions.
            var be = await LitForge("game:coke");
            await SoakFor(1);

            Assert.Close(TempOf(be.WorkItemStack), 1200, 1, "crucible temperature in a lit coke forge");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task EvenABellowsWillNotReachIron()
        {
            // The bonus is multiplied by the bellows oxygen boost, so without the maxHeatableTemp
            // clamp a blown coke forge would sit at ~1972 and melt iron. That clamp is the gate.
            var be = await LitForge("game:coke");
            be.extraOxygenRate = be.MaxExtraHeatRate;

            Assert.LessOrEqual(be.CrucibleMaxTemperature(), 1200f, "crucible ceiling with a bellows running");
            Assert.Less(be.CrucibleMaxTemperature(), 1482f, "crucible ceiling against limonite's melting point");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 90000)]
        public async Task EveryFuelHitsTheCeilingTheReadmeClaims()
        {
            // The README's fuel table is the mod's balance in one place; if these drift apart the
            // table is a lie, and it is the only thing a player reads before choosing a fuel.
            var expected = new (string fuel, int ceiling)[]
            {
                ("game:coke", 1200),              // 700 + 100 + 400, then clamped by maxHeatableTemp
                ("game:charcoal", 1150),          // 700 +  50 + 400
                ("game:ore-bituminouscoal", 1100),// 700 +   0 + 400
                ("game:ore-anthracite", 1100),
                ("game:ore-lignite", 1000),       // 700 - 100 + 400
                ("game:coal-contaminated", 900),  // 700 - 200 + 400
            };

            foreach (var (fuel, ceiling) in expected)
            {
                var be = await LitForge(fuel);
                Assert.Equal((float)ceiling, be.CrucibleMaxTemperature(), "unaided ceiling on " + fuel);
            }
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task LigniteCannotReachCopper()
        {
            // 700 - 100 + 400 = 1000, short of copper's 1084. Fuel choice still decides.
            var be = await LitForge("game:ore-lignite");
            Assert.Less(be.CrucibleMaxTemperature(), 1084f, "crucible ceiling on lignite");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task CopperNuggetsMeltIntoAMoltenCrucible()
        {
            // Four nuggets is 30s * 4 / 20 = 6 seconds of melt time, which the block entity's own
            // 200ms listener accrues in real time exactly as a firepit would.
            var be = await LitForge("game:coke", CopperNugget, 4);
            await SoakFor(1);

            Assert.Greater(TempOf(be.WorkItemStack), 1084f, "crucible temperature before melting");
            Assert.Greater(TempOf(be.ChargeSlots[0].Itemstack), 1084f,
                "charge temperature - the ore has to follow the crucible or nothing ever melts");

            await Until(() => Forge.WorkItemStack?.Collectible is BlockSmeltedContainer, 900,
                "the crucible turning molten");

            var molten = Forge.WorkItemStack;
            Assert.True(molten.Collectible is BlockSmeltedContainer, "crucible is now the smelted variant");
            Assert.True(Forge.ChargeEmpty, "the charge was consumed");

            var contents = ((BlockSmeltedContainer)molten.Collectible).GetContents(Sapi.World, molten);
            Assert.NotNull(contents.Key, "the molten crucible holds metal");
            Assert.Equal("ingot-copper", contents.Key.Collectible.Code.Path, "metal poured from native copper");
            Assert.Equal(20, contents.Value, "units of copper from 4 nuggets at a 20:1 ratio");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task AnAlloyMeltsIntoTheRightMetal()
        {
            // Copper alone only exercises GetSingleSmeltableStack. An alloy goes down the other
            // branch entirely - AlloyRecipe.Matches and GetTotalOutputQuantity - which is most of
            // why putting the charge in the forge rather than the crucible stack was worth doing.
            var be = await LitForge("game:coke");
            be.ChargeSlots[0].Itemstack = World.Stack(CopperNugget, 18);
            be.ChargeSlots[1].Itemstack = World.Stack("game:nugget-cassiterite", 2);
            be.MarkDirty(true);

            await SoakFor(1);
            await Until(() => Forge.WorkItemStack?.Collectible is BlockSmeltedContainer, 900,
                "the crucible turning molten");

            var molten = Forge.WorkItemStack;
            var contents = ((BlockSmeltedContainer)molten.Collectible).GetContents(Sapi.World, molten);

            Assert.Equal("ingot-tinbronze", contents.Key.Collectible.Code.Path, "90:10 copper to tin is tin bronze");
            Assert.Equal(100, contents.Value, "one ingot's worth of it");
            Assert.True(Forge.ChargeEmpty, "both ingredients were consumed");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task AMeltInProgressSurvivesAReload()
        {
            // meltProgress lives on the block entity rather than in the inventory, so it only
            // persists because ToTreeAttributes says so. Losing it would silently restart a melt
            // every time the chunk unloaded.
            var be = await LitForge("game:coke", CopperNugget, 20);
            await SoakFor(1);
            await Until(() => Forge.MeltProgress > 0, 300, "the melt starting");

            float progress = Forge.MeltProgress;

            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            be.ToTreeAttributes(tree);
            be.FromTreeAttributes(tree, Sapi.World);

            Assert.Close(Forge.MeltProgress, progress, 0.001, "melt progress came back off the tree");
            Assert.NotNull(Forge.WorkItemStack, "and so did the crucible");
            Assert.Equal("crucible-brown-fired", Forge.WorkItemStack.Collectible.Code.Path, "the same crucible");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task TheMoltenChargeComesOutHotEnoughToPour()
        {
            // BlockSmeltingContainer stamps the output with the *lowest* ingredient temperature. If
            // the charge were never heated alongside the crucible that would be ambient, and the
            // metal would arrive already solidified - melted and useless in the same instant.
            var be = await LitForge("game:coke", CopperNugget, 4);
            await SoakFor(1);
            await Until(() => Forge.WorkItemStack?.Collectible is BlockSmeltedContainer, 900,
                "the crucible turning molten");

            var molten = Forge.WorkItemStack;
            var smelted = (BlockSmeltedContainer)molten.Collectible;
            var contents = smelted.GetContents(Sapi.World, molten);

            Assert.False(smelted.HasSolidifed(molten, contents.Key, Sapi.World),
                "the metal is still liquid when the melt finishes");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task TheMetalActuallyPoursIntoAMold()
        {
            // The end of the whole loop. Everything upstream can be right and still produce a stack
            // a mold will not take - the contents have to be a resolved, pourable metal at a
            // temperature the mold accepts.
            var be = await LitForge("game:coke", CopperNugget, 20);
            await SoakFor(1);
            await Until(() => Forge.WorkItemStack?.Collectible is BlockSmeltedContainer, 900,
                "the crucible turning molten");

            var molten = Forge.WorkItemStack;
            var smelted = (BlockSmeltedContainer)molten.Collectible;
            var contents = smelted.GetContents(Sapi.World, molten);

            BlockPos moldPos = P(10, 0, 8);
            World.SetBlock("game:ingotmold-brown-fired", moldPos);
            await Ticks(2);

            // 1.22's ingot mold is a rack that holds one or two mold halves, so a bare placed block
            // takes nothing until one is in it.
            var moldRack = World.BE<BlockEntityIngotMold>(moldPos);
            moldRack.MoldLeft = World.Stack("game:ingotmold-brown-fired");
            moldRack.MarkDirty(true);
            await Ticks(2);

            var mold = Sapi.World.BlockAccessor.GetBlockEntity(moldPos) as ILiquidMetalSink;
            Assert.NotNull(mold, "a mold to pour into");
            Assert.True(mold.CanReceiveAny, "an empty mold takes metal");
            Assert.True(mold.CanReceive(contents.Key), "and it takes this metal in particular");

            float temp = molten.Collectible.GetTemperature(Sapi.World, molten);
            int amount = contents.Value;
            mold.BeginFill(new Vintagestory.API.MathTools.Vec3d(0.5, 0.5, 0.5));
            mold.ReceiveLiquidMetal(contents.Key, ref amount, temp);

            Assert.Less(amount, contents.Value, "the mold took some of it");

            var moldBe = World.BE<BlockEntityIngotMold>(moldPos);
            ItemStack poured = moldBe.ContentsLeft ?? moldBe.ContentsRight;
            Assert.NotNull(poured, "and is now holding metal");
            Assert.Equal("ingot-copper", poured.Collectible.Code.Path, "copper, as poured");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task AnUnlitForgeMeltsNothing()
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:coke", 8);
            be.WorkItemSlot.Itemstack = World.Stack(Crucible);
            be.ChargeSlots[0].Itemstack = World.Stack(CopperNugget, 4);
            be.MarkDirty(true);

            await SoakFor(2);
            await Ticks(60);

            Assert.False(Forge.WorkItemStack?.Collectible is BlockSmeltedContainer,
                "an unlit forge left the charge alone");
            Assert.Equal(0f, Forge.MeltProgress, "no melt progress without fire");
        }

        [VsTest(TimeoutMs = 90000)]
        public async Task CopperDoesNotMeltOnLignite()
        {
            var be = await LitForge("game:ore-lignite", CopperNugget, 4);
            await SoakFor(2);
            await Ticks(120);

            Assert.Less(TempOf(Forge.WorkItemStack), 1084f, "lignite cannot reach copper's melting point");
            Assert.False(Forge.WorkItemStack?.Collectible is BlockSmeltedContainer, "nothing melted");
        }
    }
}
