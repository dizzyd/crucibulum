using System.Linq;
using Vintagestory.API.Common.Entities;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// The forge's original job, which this mod must leave completely alone.
    ///
    /// Heating an ingot to work on an anvil is what a forge is for, and every one of the changes
    /// here - a replaced block class, a widened inventory with slot filters, a new burn rate, a
    /// second renderer, a window on right click - is a chance to have broken it without noticing.
    /// The whole suite below is about a forge with no crucible anywhere near it.
    /// </summary>
    public class ForgeSmithing
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        static async Task<BlockEntityCrucibulumForge> ALitForge()
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:coke", 8);
            be.TryIgnite();
            be.MarkDirty(true);
            return be;
        }

        [VsTest]
        public async Task TheWorkItemSlotStillTakesEverythingAForgeAccepts()
        {
            // The slot filters exist for the window's sake, and they have to mirror
            // BEForge.OnPlayerInteract exactly or the vanilla shift-click paths start refusing
            // things they always took.
            Assert.True(ItemSlotForgeWorkItem.Accepts(World.Stack("game:ingot-copper")), "an ingot");
            Assert.True(ItemSlotForgeWorkItem.Accepts(World.Stack("game:metalplate-copper")), "a plate");
            Assert.True(ItemSlotForgeWorkItem.Accepts(World.Stack("game:crowbar-copper")),
                "a tool flagged forgable, which 1.22 lets you reheat in a forge");

            Assert.True(ItemSlotForgeFuel.Accepts(World.Stack("game:charcoal")), "charcoal");
            Assert.True(ItemSlotForgeFuel.Accepts(World.Stack("game:ore-bituminouscoal")), "coal ore");

            await Task.CompletedTask;
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task AnIngotBurnsFuelAtExactlyTheVanillaRate()
        {
            // The faster burn is for melting. A smith must not find their coke going three times
            // as fast as it used to.
            var be = await ALitForge();
            float bare = be.BurnRate;

            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper");
            be.MarkDirty(true);

            Assert.Equal(CrucibleWork.None, be.WorkState, "an ingot is not crucible work");
            Assert.Close(be.BurnRate, bare, 0.0001, "burn rate is untouched by an ingot");
            Assert.Close(be.BurnRate, 0.5, 0.0001, "and it is vanilla's own figure for coke");
        }

        [VsTest(TimeoutMs = 90000)]
        public async Task AnIngotStillHeatsUp()
        {
            // Heating an ingot is the vanilla forge tick's job and stays so - this mod's own
            // heating only ever touches a crucible.
            var be = await ALitForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper");
            be.WorkItemStack.Collectible.SetTemperature(Sapi.World, be.WorkItemStack, 20);
            be.MarkDirty(true);

            await World.TickNow(ForgePos);
            await Hours(1);
            await World.TickNow(ForgePos);

            float temp = Forge.WorkItemStack.Collectible.GetTemperature(Sapi.World, Forge.WorkItemStack);
            Assert.Greater(temp, 700f, "the ingot came up to forging heat");
        }

        [VsTest(TimeoutMs = 60000)]
        public async Task AnIngotIsNotHeldToTheCrucibleCeiling()
        {
            // The 1200 C crucible clamp must not become a clamp on smithing: a bellows-blown forge
            // still drives an ingot to the temperature vanilla allows.
            var be = await ALitForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper");
            be.extraOxygenRate = be.MaxExtraHeatRate;
            be.MarkDirty(true);

            await World.TickNow(ForgePos);
            await Hours(2);
            await World.TickNow(ForgePos);

            float temp = Forge.WorkItemStack.Collectible.GetTemperature(Sapi.World, Forge.WorkItemStack);
            Assert.Greater(temp, 1200f, "an ingot goes past the crucible's ceiling, as it does in vanilla");
        }

        [VsTest]
        public async Task IngotsStillStackInTheForge()
        {
            // Vanilla lets four ingots share the slot. The crucible refusal must not have caught
            // them: it is guarded on the stack being a crucible, not on the slot being occupied.
            var be = await ALitForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper", 3);
            be.MarkDirty(true);

            Assert.True(ItemSlotForgeWorkItem.Accepts(World.Stack("game:ingot-copper")),
                "the slot still takes another ingot");
            Assert.Equal(3, Forge.WorkItemStack.StackSize, "and holds the ones already in it");
        }

        [VsTest]
        public async Task BreakingASmithingForgeDropsTheIngot()
        {
            var be = await ALitForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper", 2);
            be.MarkDirty(true);

            Sapi.World.BlockAccessor.BreakBlock(ForgePos, null);

            await Until(() => World.Entities(ForgePos, 6)
                .OfType<EntityItem>()
                .Any(e => e.Itemstack?.Collectible.Code.Path == "ingot-copper"), 60,
                "the ingot dropped rather than being cleared away with the crucible slots");
        }

        [VsTest]
        public async Task TheBlockInfoSaysNothingAboutCrucibles()
        {
            // A smith should see the forge they have always seen.
            var be = await ALitForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper", 2);
            be.MarkDirty(true);

            var sb = new System.Text.StringBuilder();
            be.GetBlockInfo(null, sb);
            string text = sb.ToString();

            Assert.Contains(text, "ingot", "it says what is in there");
            Assert.False(text.Contains("crucible"), "and mentions no crucible");
            Assert.False(text.Contains("Melting"), "and no melt progress");
        }
    }
}
