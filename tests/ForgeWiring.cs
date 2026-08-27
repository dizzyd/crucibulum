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
    /// The mod reaches the forge entirely through two JSON patches -- one repointing the blocktype's
    /// class and entityClass, one flagging the crucible <c>forgable</c> so the 1.22 forge will accept
    /// and render it. Both compile to nothing and fail silently in-world if a path or a class name is
    /// wrong, which is exactly the failure mode these guard.
    /// </summary>
    public class ForgeWiring
    {
        static BlockPos ForgePos => P(8, 0, 8);

        [VsTest]
        public async Task TheForgeBlocktypeUsesOurClasses()
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            Block forge = World.GetBlock(ForgePos);
            Assert.True(forge is BlockCrucibulumForge,
                $"forge block class is {forge.GetType().Name}, expected BlockCrucibulumForge - " +
                "the /class patch did not land, or another mod replaced it after us");

            BlockEntity be = Sapi.World.BlockAccessor.GetBlockEntity(ForgePos);
            Assert.True(be is BlockEntityCrucibulumForge,
                $"forge block entity is {be?.GetType().Name}, expected BlockEntityCrucibulumForge");
        }

        [VsTest]
        public async Task TheCrucibleCarriesAnInForgeTransform()
        {
            // Both variants need it -- reheating a solidified charge is why the smelted one matters.
            // attributesByType merges over attributes rather than replacing it, so both inherit the
            // one patch.
            foreach (string code in new[] { "game:crucible-brown-fired", "game:crucible-brown-smelted" })
            {
                Block crucible = Sapi.World.GetBlock(new AssetLocation(code));
                Assert.NotNull(crucible, code);

                ModelTransform tf = crucible.Attributes?["inForgeTransform"].AsObject<ModelTransform>();
                Assert.NotNull(tf, code + " carries an inForgeTransform - the renderer places the " +
                                   "crucible with it, and without one it sits in the coals");
            }

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheCrucibleIsDeliberatelyNotForgable()
        {
            // If it were, BEForge would draw it with the glow curve it uses for a lump of iron under
            // the hammer, which saturates a crucible to a featureless white lump well below its
            // working temperature -- and BEForge's merge branch, guarded by !forgable, would stack
            // four crucibles into the slot and smelt one. Both are why we own the whole path.
            Block crucible = Sapi.World.GetBlock(new AssetLocation("game:crucible-brown-fired"));
            Assert.False(crucible.Attributes?.IsTrue("forgable") == true,
                "crucible-brown-fired must NOT be forgable");

            await Task.CompletedTask;
        }

        [VsTest, RequiresClient]
        public async Task TheMeltSurfaceShapeExists()
        {
            // Shapes are a client-only asset category, so this can only be asked of a real client.
            // The renderer logs a warning and draws nothing if it is missing, which looks exactly
            // like "the melt never finished".
            IAsset asset = Capi.Assets.TryGet(new AssetLocation("crucibulum:shapes/block/meltsurface.json"));
            Assert.NotNull(asset, "crucibulum:shapes/block/meltsurface.json ships with the mod");

            await Task.CompletedTask;
        }
    }
}
