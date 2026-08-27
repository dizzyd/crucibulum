using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// What a melt costs in fuel.
    ///
    /// The two devices burn on different clocks. A firepit spends <c>BurnDuration</c> *real*
    /// seconds per lump; the forge spends <c>1/BurnRate</c> *in-game hours*. At the shipped
    /// calendar an in-game hour is 120 real seconds, so one coke is 40 seconds in a firepit and 240
    /// in a forge - and since melt time is counted in real seconds on both, melting over a forge
    /// came out six times cheaper. A single lump saw off a full 700-unit crucible.
    /// </summary>
    public class ForgeFuelCost
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        /// <summary>Real seconds one lump of this fuel buys in a firepit.</summary>
        static float FirepitSecondsPer(string fuel)
        {
            ItemStack stack = World.Stack(fuel);
            return stack.Collectible.GetCombustibleProperties(Sapi.World, stack, null).BurnDuration;
        }

        /// <summary>Real seconds one lump buys in the forge, at the shipped calendar.</summary>
        static float ForgeSecondsPer(BlockEntityCrucibulumForge be)
        {
            float realSecondsPerHour = 3600f
                / (BlockEntityCrucibulumForge.DefaultSpeedOfTime * BlockEntityCrucibulumForge.DefaultCalendarSpeedMul);
            return realSecondsPerHour / be.BurnRate;
        }

        static async Task<BlockEntityCrucibulumForge> Forge_(string fuel, bool withCharge)
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack(fuel, 8);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            if (withCharge) be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            be.MarkDirty(true);
            return be;
        }

        [VsTest]
        public async Task AnIdleForgeBurnsAtTheVanillaRate()
        {
            // Nothing should change for someone using the forge to smith. Fuel only goes faster
            // when the crucible has metal in it.
            var be = await Forge_("game:coke", withCharge: false);

            Assert.False(be.CrucibleDrawsHeat, "an empty crucible is not work");
            Assert.Close(be.BurnRate, 0.5, 0.001, "coke burn rate with nothing to melt");
        }

        [VsTest]
        public async Task AChargedCrucibleCostsRoughlyWhatAFirepitCosts()
        {
            var be = await Forge_("game:coke", withCharge: true);

            Assert.True(be.CrucibleDrawsHeat, "ore in the crucible is work");

            float forge = ForgeSecondsPer(be);
            float firepit = FirepitSecondsPer("game:coke");

            // The configured share: the forge should cost 0.8 of a firepit, so a lump lasts 1/0.8
            // as long.
            float expected = firepit / CrucibulumModSystem.Config.CrucibleFuelUseVsFirepit;
            Assert.Close(forge, expected, 1.0, "real seconds one coke buys while melting");
        }

        [VsTest]
        public async Task OneLumpNoLongerMeltsAWholeCrucible()
        {
            // The reported problem, as a number. 128 copper + 12 tin is 700 units and 210 real
            // seconds of melting; before this it drew under one lump of coke.
            var be = await Forge_("game:coke", withCharge: false);
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 128)), 128);
            be.AddCharge(new DummySlot(World.Stack("game:nugget-cassiterite", 12)), 12);
            be.MarkDirty(true);

            float meltSeconds = be.WorkItemStack.Collectible.GetMeltingDuration(Sapi.World, be.ChargeProvider, be.WorkItemSlot);
            Assert.Close(meltSeconds, 210, 1, "melt time for 140 nuggets");

            float lumps = meltSeconds / ForgeSecondsPer(be);
            Assert.Greater(lumps, 3f, "a full crucible costs several lumps of coke, not one");

            float firepitLumps = meltSeconds / FirepitSecondsPer("game:coke");
            Assert.Less(lumps, firepitLumps, "but still a little cheaper than a firepit");
        }

        [VsTest]
        public async Task PuttingACrucibleInNeverMakesFuelLastLonger()
        {
            // The guard against the arithmetic going the wrong way: whatever the calendar or the
            // config says, a crucible must never be a fuel saving.
            foreach (string fuel in new[] { "game:coke", "game:charcoal", "game:ore-bituminouscoal", "game:ore-lignite" })
            {
                var idle = await Forge_(fuel, withCharge: false);
                float vanilla = idle.BurnRate;

                var busy = await Forge_(fuel, withCharge: true);
                Assert.GreaterOrEqual(busy.BurnRate, vanilla, fuel + " burns at least as fast with a charge in");
            }
        }
    }
}
