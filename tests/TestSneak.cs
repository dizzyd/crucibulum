using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// Holding the sneak key down.
    ///
    /// Writing <c>Controls.ShiftKey</c> directly does not work: the client recomputes controls from
    /// the real key state every frame and overwrites it within a tick or two, so the click that
    /// follows is never a sneak-click. It has to go through the input layer.
    /// </summary>
    public static class TestSneak
    {
        public static async Task Down()
        {
            await Input.KeyDown(GlKeys.ShiftLeft);
            await Frames.Wait(3);
        }

        public static async Task Up()
        {
            await Input.KeyUp(GlKeys.ShiftLeft);
            await Frames.Wait(3);
        }

        /// <summary>Sneak-click a block, releasing the key whatever happens.</summary>
        public static async Task Click(BlockPos pos, BlockFacing face = null)
        {
            await Down();
            try
            {
                await Interact.UseBlock(pos, face);
                await Ticks(6);
            }
            finally
            {
                await Up();
            }
        }
    }
}
