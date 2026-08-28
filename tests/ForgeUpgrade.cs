using System.Linq;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// Forges that were already in the world when this mod was installed.
    ///
    /// Patching the blocktype only changes what gets built from now on. A saved chunk records the
    /// class its block entity was written with, so an existing forge comes back as a vanilla
    /// BlockEntityForge while its block is already ours - a forge that offers the crucible in its
    /// interaction help and then does nothing, until it is broken and replaced.
    ///
    /// The legacy state is built here directly rather than through a save and reload, which the
    /// harness cannot do across two different mod sets: spawn the vanilla block entity by name onto
    /// a block that is ours, which is exactly what loading such a chunk produces.
    /// </summary>
    public class ForgeUpgrade
    {
        static BlockPos ForgePos => P(8, 0, 8);

        /// <summary>A forge as it comes back in a world that predates this mod.</summary>
        static async Task<BlockEntityForge> ALegacyForge()
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            Sapi.World.BlockAccessor.RemoveBlockEntity(ForgePos);
            Sapi.World.BlockAccessor.SpawnBlockEntity("Forge", ForgePos);
            await Ticks(2);

            var be = Sapi.World.BlockAccessor.GetBlockEntity(ForgePos) as BlockEntityForge;
            Assert.NotNull(be, "the vanilla block entity spawned");
            Assert.False(be is BlockEntityCrucibulumForge, "and it is the old one, not ours");
            return be;
        }

        static void UpgradeChunkOf(BlockPos pos)
        {
            // What the chunk-load hook does, driven directly: the harness will not unload and
            // reload a chunk under a running test.
            var chunk = Sapi.WorldManager.GetChunk(pos);
            Assert.NotNull(chunk, "the chunk is loaded");
            CrucibulumModSystem.UpgradeForgesIn(Sapi, new[] { (IWorldChunk)chunk });
        }

        [VsTest]
        public async Task ALegacyForgeIsBroughtUpToDate()
        {
            var stale = await ALegacyForge();
            Assert.IsType<BlockCrucibulumForge>(stale.Block, "its block is already ours - only the entity is behind");

            UpgradeChunkOf(ForgePos);
            await Ticks(2);

            var fresh = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            Assert.NotNull(fresh, "the forge came back as ours");
            Assert.Equal(6, fresh.Inventory.Count, "with the charge slots it was missing");
        }

        [VsTest]
        public async Task TheUpgradeKeepsWhatWasInTheForge()
        {
            // Someone with a lit forge and a half-worked ingot in it should not notice this
            // happening, beyond the crucible starting to work.
            var stale = await ALegacyForge();
            stale.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper");
            stale.FuelSlot.Itemstack = World.Stack("game:coke", 3);
            stale.WorkItemStack.Collectible.SetTemperature(Sapi.World, stale.WorkItemStack, 900f);
            stale.TryIgnite();
            stale.MarkDirty(true);
            await Ticks(2);

            UpgradeChunkOf(ForgePos);
            await Ticks(2);

            var fresh = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            Assert.NotNull(fresh, "upgraded");
            Assert.Equal("ingot-copper", fresh.WorkItemStack?.Collectible.Code.Path, "the work item survived");
            Assert.Equal("coke", fresh.FuelSlot.Itemstack?.Collectible.Code.Path, "and the fuel");
            Assert.Equal(3, fresh.FuelSlot.StackSize, "all of it");
            Assert.True(fresh.IsBurning, "and it is still lit");

            float temp = fresh.WorkItemStack.Collectible.GetTemperature(Sapi.World, fresh.WorkItemStack);
            Log($"  ingot came through at {temp:0} degC, burning={fresh.IsBurning}");
            Assert.Greater(temp, 500f, "the ingot kept its heat");
        }

        [VsTest]
        public async Task AnUpgradedForgeActuallyMelts()
        {
            // The point of the exercise: the thing that did nothing until you broke and replaced it.
            var stale = await ALegacyForge();
            UpgradeChunkOf(ForgePos);
            await Ticks(2);

            var fresh = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            fresh.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            fresh.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            fresh.FuelSlot.Itemstack = World.Stack("game:coke", 4);
            fresh.TryIgnite();
            fresh.MarkDirty(true);
            await Ticks(2);

            var sb = new System.Text.StringBuilder();
            fresh.GetBlockInfo(null, sb);
            Log("  block info: " + sb.ToString().Replace("\n", " | ").Trim());
            Assert.Contains(sb.ToString(), "100 units", "it reads the charge");
            Assert.Contains(sb.ToString(), "1084", "and knows what it is waiting for");
        }

        [VsTest]
        public async Task AForgeThatIsAlreadyOursIsLeftAlone()
        {
            // The hook runs on every chunk load for the life of the world, so it has to be a no-op
            // once there is nothing to do - and must not quietly rebuild a forge someone is using.
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 7)), 7);
            be.MarkDirty(true);
            await Ticks(2);

            UpgradeChunkOf(ForgePos);
            await Ticks(2);

            var after = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            Assert.True(ReferenceEquals(be, after), "the same block entity, not a rebuilt one");
            Assert.Equal(7, after.ChargeSlots[0].StackSize, "and the charge untouched");
        }
    }
}
