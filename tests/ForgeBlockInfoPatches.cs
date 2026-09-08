using System.Text;
using System.Threading.Tasks;
using Crucibulum;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// Other mods postfix BlockEntityForge.GetBlockInfo on the assumption that a forge's work item
    /// is metal, since in vanilla it always is. Smithing Plus's colours the temperature by asking
    /// the work item for its metal material, and for a crucible that lookup fails, is not cached,
    /// and walks every recipe in the game again on the next frame: 42ms a call, measured, for as
    /// long as the forge is looked at. The forge therefore writes the base's lines itself when a
    /// crucible is seated and calls the base only when it is not - so their patch still sees every
    /// forge it was written for and never sees the one it was not.
    ///
    /// Stands in for any such mod with a counting postfix of its own, installed for the test and
    /// removed after.
    /// </summary>
    public class ForgeBlockInfoPatches
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        const string PatchId = "com.dizzyd.crucibulum.tests.blockinfo";
        static Harmony harmony;
        public static int BaseCalls;

        /// <summary>
        /// The one block entity being asked. In singleplayer the client shares the process and
        /// asks its own copy of whatever it is looking at once a frame, through the same patched
        /// method, so a bare count would include those.
        /// </summary>
        public static BlockEntityForge Target;

        static void CountingPostfix(BlockEntityForge __instance)
        {
            if (ReferenceEquals(__instance, Target)) BaseCalls++;
        }

        [BeforeEach]
        public Task InstallTheCountingPostfix()
        {
            harmony = new Harmony(PatchId);
            harmony.Patch(AccessTools.Method(typeof(BlockEntityForge), nameof(BlockEntityForge.GetBlockInfo)),
                postfix: new HarmonyMethod(typeof(ForgeBlockInfoPatches), nameof(CountingPostfix)));
            BaseCalls = 0;
            return Task.CompletedTask;
        }

        [AfterEach]
        public Task RemoveIt()
        {
            harmony?.UnpatchAll(PatchId);
            harmony = null;
            return Task.CompletedTask;
        }

        static async Task<BlockEntityCrucibulumForge> AForgeHolding(string workItem)
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);
            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:coke", 2);
            if (workItem != null) be.WorkItemSlot.Itemstack = World.Stack(workItem);
            be.MarkDirty(true);
            return be;
        }

        static string InfoOf(BlockEntityCrucibulumForge be)
        {
            Target = be;
            BaseCalls = 0;
            var sb = new StringBuilder();
            be.GetBlockInfo(null, sb);
            Target = null;
            return sb.ToString();
        }

        [VsTest]
        public async Task ABareForgeStillGoesThroughTheBase()
        {
            var be = await AForgeHolding(null);
            string info = InfoOf(be);
            Assert.Equal(1, BaseCalls, "the vanilla method ran, so a postfix on it saw the forge");
            Assert.Contains(info, "Coke", "and wrote the fuel line");
        }

        [VsTest]
        public async Task AnIngotStillGoesThroughTheBase()
        {
            var be = await AForgeHolding("game:ingot-copper");
            string info = InfoOf(be);
            Assert.Equal(1, BaseCalls, "a metal work item is what their patches are for");
            Assert.Contains(info, "opper", "and the contents line names it");
        }

        [VsTest]
        public async Task ACrucibleDoesNot()
        {
            var be = await AForgeHolding("game:crucible-brown-fired");
            string info = InfoOf(be);
            Assert.Equal(0, BaseCalls, "the base is not called, so a postfix expecting metal never meets a crucible");
            Assert.Contains(info, "rucible", "but the contents line is still there");
            Assert.Contains(info, "Coke", "and so is the fuel line");
        }
    }
}
