using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>What a crucible does once the fire under it goes out.</summary>
    public class ForgeCooling
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        static async Task<BlockEntityCrucibulumForge> UnfuelledForgeHoldingCrucibleAt(float temp)
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = null;                       // the fire has gone out
            var crucible = World.Stack("game:crucible-brown-fired");
            be.WorkItemSlot.Itemstack = crucible;
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            crucible.Collectible.SetTemperature(Sapi.World, crucible, temp);
            be.MarkDirty(true);
            return be;
        }

        static float TempOf(BlockEntityCrucibulumForge be) =>
            be.WorkItemStack.Collectible.GetTemperature(Sapi.World, be.WorkItemStack);

        /// <summary>
        /// Steps the world the way cooling actually experiences it: the calendar moves, and the
        /// forge ticks see a non-zero hoursPassed. Ticking alone will not do - vstestkit freezes
        /// the calendar, so a real-time loop leaves every temperature exactly where it started.
        /// </summary>
        static async Task Step(double hours)
        {
            await Hours(hours);
            await Ticks(2);
        }

        static async Task<float> Curve(float startTemp, string label)
        {
            var be = await UnfuelledForgeHoldingCrucibleAt(startTemp);
            int relights = 0;
            bool wasBurning = false;

            Log($"  --- {label}: unfuelled forge, crucible at {TempOf(be):0} degC ---");
            for (int i = 1; i <= 30; i++)
            {
                await Step(0.05);
                if (be.IsBurning && !wasBurning) relights++;
                wasBurning = be.IsBurning;
                if (i % 3 == 0 || i <= 12)
                    Log($"  +{i * 0.05:0.00}h ({i * 0.05 * 120:0}s real)  {TempOf(be):0} degC  burning {be.IsBurning}");
            }
            Log($"  {label}: ended at {TempOf(be):0} degC after {relights} self-relights on zero fuel");
            return TempOf(be);
        }

        [VsTest(TimeoutMs = 180000)]
        public async Task AMoltenCrucibleCoolsWhenTheFuelRunsOut()
        {
            float end = await Curve(1100, "molten (1100)");
            Assert.Less(end, 1100f, "an unfuelled forge must not keep the crucible hot");
        }

        [VsTest(TimeoutMs = 180000)]
        public async Task AControlBelowTheReigniteThreshold()
        {
            float end = await Curve(850, "below reignite (850)");
            Assert.Less(end, 850f);
        }

        [VsTest(TimeoutMs = 180000)]
        public async Task AnEmptyForgeDoesNotRelightItself()
        {
            // Vanilla's idle branch calls TryIgnite() whenever the work item is over 900 degC, and
            // TryIgnite() checks only "am I already burning" - not CanIgnite, which is the property
            // that knows about fuel. So a forge with nothing in it to burn lights anyway, and each
            // relit tick runs the burning branch: it heats the contents and re-arms the half-hour
            // cooldown delay. A molten crucible sits well above 900, so it holds its own melt for
            // free forever.
            var be = await UnfuelledForgeHoldingCrucibleAt(1100);
            Assert.Equal(0f, be.FuelLevel, "no fuel at all");

            be.TryIgnite();
            Log($"  straight after TryIgnite on an empty forge: burning {be.IsBurning}, fuel {be.FuelLevel:0.00}");

            // One listener cycle is all it takes for the fire to be put back out - and it must
            // happen before the next vanilla tick, which is what would spend the relight on heat
            // the forge has no fuel for. Both listeners run at 200ms, which is about seven game
            // ticks, so a two-tick wait is not one cycle.
            await Ticks(10);
            Assert.False(be.IsBurning, "a forge with no fuel must not stay lit");

            float before = TempOf(be);
            for (int i = 1; i <= 20; i++)
            {
                await Step(0.05);
                if (i % 4 == 0) Log($"  +{i * 0.05:0.00}h  {TempOf(be):0} degC  burning {be.IsBurning}");
                Assert.False(be.IsBurning, $"still alight on zero fuel at +{i * 0.05:0.00}h");
            }

            Assert.Less(TempOf(be), before - 100, "and must cool once the delay is up, not hold");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task AnIngotOnAnEmptyForgeKeepsTheVanillaBehaviour()
        {
            // The relight is only suppressed for a crucible that is drawing heat, so a forge being
            // used to warm an ingot is left exactly as vanilla has it - someone smithing should not
            // find their forge behaving differently because this mod is installed.
            //
            // Note what vanilla actually does, which is not "stays lit": the relit forge burns for
            // a single tick, and the next tick sees FuelLevel <= 0 and puts it out again - after
            // running the heating block. The gain is small, but SetTemperature pushes the next
            // cooling half an in-game hour out, so a work item over 900 degC never gets to cool.
            // That flicker is what this mod suppresses, and only under a working crucible.
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = null;
            var ingot = World.Stack("game:ingot-copper");
            be.WorkItemSlot.Itemstack = ingot;
            ingot.Collectible.SetTemperature(Sapi.World, ingot, 1100);
            be.MarkDirty(true);

            Assert.False(be.CrucibleDrawsHeat, "no crucible drawing heat, so the suppression is out of scope");

            be.TryIgnite();
            Assert.True(be.IsBurning, "vanilla lights it and this mod must not intercept that");
            Log("  ingot on an unfuelled forge: vanilla relight left alone");
        }
    }
}