using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// Real clicks, driven through the client the way a player's would be.
    ///
    /// The one these exist for: a crucible is a Block, and blocks in hand are placed on sneak. If
    /// the game reached the held item before Block.OnBlockInteractStart, the crucible would
    /// ground-store on top of the forge instead of going into it and the whole design would be
    /// wrong - and no server-side test would notice, because those call the block entity directly.
    /// </summary>
    public class ForgeClientSide
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        const string Crucible = "game:crucible-brown-fired";

        static async Task<BlockEntityCrucibulumForge> AForge()
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);
            await Player.StandNear(ForgePos);
            return Forge;
        }

        static async Task EmptyHand()
        {
            Player.Me.InventoryManager.ActiveHotbarSlot.Itemstack = null;
            Player.Me.InventoryManager.ActiveHotbarSlot.MarkDirty();
            await Ticks(3);
        }

        /// <summary>
        /// Tests share one player, so anything an earlier test handed them is still lying in the
        /// backpack. Counting what came out of a forge only means something against an empty one.
        /// </summary>
        static async Task EmptyPockets()
        {
            foreach (string cls in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
            {
                IInventory inv = Player.Me.InventoryManager.GetOwnInventory(cls);
                if (inv == null) continue;
                foreach (var slot in inv)
                {
                    if (slot.Empty) continue;
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
            await Ticks(3);
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ShiftRightClickSetsACrucibleInTheForge()
        {
            await AForge();
            await Player.Hold(Crucible, 2);
            await Ticks(2);

            await TestSneak.Click(ForgePos, BlockFacing.UP);

            Assert.NotNull(Forge.WorkItemStack,
                "the forge took the crucible - if this is null the held-item path won the click and " +
                "ground-stored it on top of the forge instead");
            Assert.Equal("crucible-brown-fired", Forge.WorkItemStack.Collectible.Code.Path, "what the forge holds");
            Assert.Equal(1, Forge.WorkItemStack.StackSize, "exactly one");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ShiftRightClickLoadsOreIntoTheCrucible()
        {
            var be = await AForge();
            be.WorkItemSlot.Itemstack = World.Stack(Crucible);
            be.MarkDirty(true);
            await Ticks(2);

            await Player.Hold("game:nugget-nativecopper", 8);
            await Ticks(2);

            await TestSneak.Click(ForgePos, BlockFacing.UP);

            Assert.Equal(1, Forge.ChargeSlots[0].StackSize, "one nugget went in per click");
            Assert.Equal(7, Player.Held.StackSize, "the rest stayed in hand");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ShiftRightClickStillFuelsTheForge()
        {
            // Vanilla's own path, through our block class. Coal ore is GroundStorable, so this is
            // also the proof that a ground-storable item in hand does not steal a sneak-click from
            // the block.
            await AForge();
            await Player.Hold("game:ore-bituminouscoal", 4);
            await Ticks(2);

            await TestSneak.Click(ForgePos, BlockFacing.UP);

            Assert.NotNull(Forge.FuelSlot.Itemstack, "coal reached the fuel slot rather than the ground");
            Assert.Equal("ore-bituminouscoal", Forge.FuelSlot.Itemstack.Collectible.Code.Path, "what is burning");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ASecondCrucibleIsRefusedRatherThanMerged()
        {
            var be = await AForge();
            be.WorkItemSlot.Itemstack = World.Stack(Crucible);
            be.MarkDirty(true);
            await Ticks(2);

            await Player.Hold(Crucible, 3);
            await Ticks(2);

            await TestSneak.Click(ForgePos, BlockFacing.UP);

            Assert.Equal(1, Forge.WorkItemStack.StackSize, "still exactly one crucible in the forge");
            Assert.Equal(3, Player.Held.StackSize, "and none taken from the hand");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ShiftRightClickTakesTheCrucibleBackOut()
        {
            var be = await AForge();
            be.WorkItemSlot.Itemstack = World.Stack(Crucible);
            be.MarkDirty(true);
            await Ticks(2);
            await EmptyPockets();

            await TestSneak.Click(ForgePos, BlockFacing.UP);

            Assert.Null(Forge.WorkItemStack, "the crucible left the forge");

            // TryGiveItemstack puts it wherever there is room, which is not necessarily the hand.
            Assert.Equal(1, OnPlayer("crucible-brown-fired"), "the player has it");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task APlainClickWithACrucibleInHandSetsItIn()
        {
            // A firepit takes a crucible on a plain click too (BlockFirepit does it with no shift
            // check), so both gestures work here rather than only the one.
            await AForge();
            await Player.Hold(Crucible, 2);
            await Ticks(2);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Ticks(6);

            Assert.NotNull(Forge.WorkItemStack, "the forge took it on a plain click");
            Assert.Equal(1, Forge.WorkItemStack.StackSize, "exactly one");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ShiftRightClickHandsBackALooseCharge()
        {
            var be = await AForge();
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 4)), 4);
            be.MarkDirty(true);
            await Ticks(2);
            await EmptyPockets();

            await TestSneak.Click(ForgePos, BlockFacing.UP);

            Assert.True(Forge.ChargeEmpty, "the charge came back out");
            Assert.Equal(4, OnPlayer("nugget-nativecopper"), "and is in the player's inventory");
        }

        /// <summary>
        /// Hotbar and backpack only: the creative inventory throws from its own Count getter when
        /// enumerated server-side, and TryGiveItemstack never puts anything there anyway.
        /// </summary>
        static int OnPlayer(string codePath)
        {
            int n = 0;
            foreach (string cls in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
            {
                IInventory inv = Player.Me.InventoryManager.GetOwnInventory(cls);
                if (inv == null) continue;
                foreach (var slot in inv)
                    if (slot.Itemstack?.Collectible.Code.Path == codePath) n += slot.StackSize;
            }
            return n;
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AMoltenCrucibleRendersWithoutThrowing()
        {
            // Not a pixel assertion - the renderer builds a mesh from a shape, an atlas lookup and a
            // model transform, any of which can be missing and would otherwise fail silently at a
            // warning. Also leaves a picture to look at.
            await World.SetCalendarTo(500 * 24 + 12);

            var be = await AForge();
            be.FuelSlot.Itemstack = World.Stack("game:coke", 4);

            var smelted = (BlockSmeltedContainer)Sapi.World.GetBlock(new AssetLocation("game:crucible-brown-smelted"));
            var stack = new ItemStack(smelted);
            smelted.SetContents(stack, World.Stack("game:ingot-copper"), 200);
            be.WorkItemSlot.Itemstack = stack;
            stack.Collectible.SetTemperature(Sapi.World, stack, 1150);
            be.TryIgnite();
            be.MarkDirty(true);

            await Player.StandNear(ForgePos, 2.5);
            await Interact.LookAt(ForgePos);
            await Frames.Wait(20);

            Assert.NotNull(Forge.WorkItemStack, "the molten crucible is still there after rendering");
            await Shot.Take("/tmp/crucibulum-molten.png");
        }
    }
}
