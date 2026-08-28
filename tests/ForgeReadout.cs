using System.Text;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.API.Datastructures;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// The block info readout.
    ///
    /// This is the readout you get without opening anything, and it has to stand on its own: the
    /// common case is meant not to need the window at all. It carries what the firepit's dialog
    /// carries - contents and yield - and one thing that dialog does not: vanilla's GetOutputText returns null for a mix that matches no alloy, which
    /// on its own is indistinguishable from a mix that does. A player staring at a silent crucible
    /// has no way to tell "wrong ratio" from "correct, just cold".
    /// </summary>
    public class ForgeReadout
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        static async Task<string> Readout(params (string code, int qty)[] charge)
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:coke", 4);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            foreach (var (code, qty) in charge)
            {
                be.AddCharge(new DummySlot(World.Stack(code, qty)), qty);
            }
            be.MarkDirty(true);

            var sb = new StringBuilder();
            be.GetBlockInfo(null, sb);
            return sb.ToString();
        }

        [VsTest]
        public async Task ASingleMetalReportsItsYield()
        {
            string text = await Readout(("game:nugget-nativecopper", 20));

            Assert.Contains(text, "20x", "the nugget count");
            Assert.Contains(text, "100 units", "20 nuggets at a 20:1 ratio is one ingot's worth");
            Assert.Contains(text, "Copper", "the metal it will make");
        }

        [VsTest]
        public async Task AValidAlloyReportsSharesAndYield()
        {
            // Tin bronze wants 8-12% tin; 18 nuggets to 2 is 90:10.
            string text = await Readout(("game:nugget-nativecopper", 18), ("game:nugget-cassiterite", 2));

            Assert.Contains(text, "90%", "copper's share of the melt");
            Assert.Contains(text, "10%", "tin's share of the melt");
            Assert.Contains(text, "Tin bronze", "what it will make");
            Assert.Contains(text, "100 units", "how much");
        }

        [VsTest]
        public async Task AWrongRatioSaysSoAndQuotesTheRatioItWants()
        {
            // The case the readout exists for. Vanilla would print nothing at all here.
            string text = await Readout(("game:nugget-nativecopper", 8), ("game:nugget-cassiterite", 12));

            Assert.Contains(text, "40%", "copper's share");
            Assert.Contains(text, "60%", "tin's share");
            Assert.Contains(text, "will not combine", "an explicit refusal rather than silence");
            Assert.Contains(text, "8-12%", "the tin ratio tin bronze actually wants");
            Assert.Contains(text, "88-92%", "the copper ratio");
        }

        [VsTest]
        public async Task MetalsThatMakeNoAlloyQuoteNoRatio()
        {
            // Nothing alloys copper with gold, so there is no ratio to aim for and quoting one
            // would send the player off chasing a recipe that does not exist.
            string text = await Readout(("game:nugget-nativecopper", 10), ("game:nugget-nativegold", 10));

            Assert.Contains(text, "will not combine", "still says it will not work");
            Assert.False(text.Contains("needs"), "but offers no ratio to aim for");
        }

        [VsTest]
        public async Task ALoneMetalGetsNoPercentage()
        {
            // A percentage on a single ingredient is noise - it is always 100%.
            string text = await Readout(("game:nugget-nativecopper", 20));
            Assert.False(text.Contains("%"), "no share shown when there is nothing to blend");
        }

        [VsTest]
        public async Task AFuelTooCoolToMeltSaysWhatItNeeds()
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:ore-lignite", 4);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            be.MarkDirty(true);

            var sb = new StringBuilder();
            be.GetBlockInfo(null, sb);
            string text = sb.ToString();

            Assert.Contains(text, "1084", "copper's melting point");
            Assert.Contains(text, "1000", "what lignite can actually reach");
        }

        [VsTest]
        public async Task TheReadoutSaysWhatTemperatureItIsWaitingFor()
        {
            // Watching a number climb with no idea what it is climbing towards was the complaint.
            // Copper melts at 1084, so that is what both readouts should quote.
            string info = await Readout(("game:nugget-nativecopper", 20));
            Log("  block info: " + info.Replace("\n", " | ").Trim());
            Assert.Contains(info, "1084", "the block info names the melting point");

            var tree = new TreeAttribute();
            Forge.SetDialogValues(tree);
            Assert.Close(1084f, tree.GetFloat("meltingPoint"), 1f, "and the window is told it too");
        }

        [VsTest]
        public async Task TheTargetGoesAwayOnceTheMetalIsMolten()
        {
            // Once it is liquid the bar says how far along it is and the threshold has stopped
            // being the question, so the window is told zero rather than a number to display.
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            var smelted = (BlockSmeltedContainer)Sapi.World.GetBlock(new AssetLocation("game:crucible-brown-smelted"));
            var stack = new ItemStack(smelted);
            smelted.SetContents(stack, World.Stack("game:ingot-copper"), 200);
            be.WorkItemSlot.Itemstack = stack;
            stack.Collectible.SetTemperature(Sapi.World, stack, 1150);
            be.MarkDirty(true);
            await Ticks(2);

            var tree = new TreeAttribute();
            be.SetDialogValues(tree);
            Assert.Close(0f, tree.GetFloat("meltingPoint"), 0.01f, "no target once it is molten");
        }
    }
}
