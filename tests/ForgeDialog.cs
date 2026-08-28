using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// The crucible window.
    ///
    /// The forge is a BlockEntityContainer rather than an openable one, so every piece of the
    /// open/close/sync protocol is hand-written here rather than inherited - which is exactly the
    /// kind of thing that compiles and then does nothing in-world.
    /// </summary>
    public class ForgeDialog
    {
        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        static async Task<BlockEntityCrucibulumForge> EmptyHandedAtAForge()
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            await Player.StandNear(ForgePos);
            Player.Me.InventoryManager.ActiveHotbarSlot.Itemstack = null;
            Player.Me.InventoryManager.ActiveHotbarSlot.MarkDirty();
            await Ticks(3);

            return Forge;
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task APlainClickOpensTheCrucible()
        {
            // Firepit convention: a bare right click on the block opens its window.
            var be = await EmptyHandedAtAForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(2);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);

            await Gui.WaitFor<GuiDialogCrucibleForge>(120);
            Assert.NotNull(Forge.WorkItemStack, "opening the window did not also take the crucible");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ABareForgeOpensNothing()
        {
            // The window belongs to the crucible. A forge with nothing in it stays a bare forge,
            // and clicking it does what clicking a bare forge has always done: nothing.
            await EmptyHandedAtAForge();

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Ticks(10);

            Assert.False(await Gui.IsOpen<GuiDialogCrucibleForge>(), "no window on an empty forge");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task ABareForgeWithSomethingInHandStaysInert()
        {
            // A smith holding an ingot who clicks a bare forge should get what vanilla gives them:
            // nothing. Putting a window in their face on the way to shift-clicking the ingot in
            // would be a small but constant irritation.
            await EmptyHandedAtAForge();
            await Player.Hold("game:ingot-copper", 4);
            await Ticks(2);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Ticks(6);

            Assert.False(await Gui.IsOpen<GuiDialogCrucibleForge>(), "no window opened");
            Assert.Null(Forge.WorkItemStack, "and nothing was put in - shift does that, as in vanilla");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task AForgeHoldingAnIngotStillHandsItOverOnAPlainClick()
        {
            // The regression that would matter most. Smithing is: heat ingot, right click to grab
            // it, hit it on the anvil. A window in the way of that would ruin the forge for its
            // original purpose.
            var be = await EmptyHandedAtAForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:ingot-copper");
            be.MarkDirty(true);
            await Ticks(2);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Ticks(6);

            Assert.Null(Forge.WorkItemStack, "the ingot came out");
            Assert.False(await Gui.IsOpen<GuiDialogCrucibleForge>(), "and no window opened");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task OpeningTheWindowOpensTheForgeInventoryOnTheServer()
        {
            // The proof that the hand-written open/close protocol actually reaches the server. The
            // forge is a plain BlockEntityContainer, so none of this is inherited: if the packets
            // went nowhere the window would still appear, and every slot in it would be inert.
            var be = await EmptyHandedAtAForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(2);

            Assert.False(Forge.Inventory.HasOpened(Player.Me), "nobody has it open to begin with");

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Gui.WaitFor<GuiDialogCrucibleForge>(120);
            await Until(() => Forge.Inventory.HasOpened(Player.Me), 60,
                "the server opened the forge inventory for this player");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
            await Until(() => !Forge.Inventory.HasOpened(Player.Me), 60,
                "and closed it again when the window went away");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task TheWindowShowsTheForgesOwnSlots()
        {
            // The dialog is handed the block entity's inventory rather than a copy, so what the
            // player drags is the real thing. Six slots: work item, fuel, and four for the charge.
            var be = await EmptyHandedAtAForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 5)), 5);
            be.MarkDirty(true);
            await Ticks(2);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            var dlg = await Gui.WaitFor<GuiDialogCrucibleForge>(120);

            await OnClient();
            var clientBe = (BlockEntityCrucibulumForge)Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            int count = clientBe.Inventory.Count;
            string charge = clientBe.ChargeSlots[0].Itemstack?.Collectible.Code.Path;
            int chargeQty = clientBe.ChargeSlots[0].StackSize;
            await OnServer();

            Assert.Equal(6, count, "work item, fuel and four charge slots in one inventory");
            Assert.Equal("nugget-nativecopper", charge, "the client sees the charge the server has");
            Assert.Equal(5, chargeQty, "and the right amount of it");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
        }

        [VsTest]
        public async Task TheWindowIsToldTheMeltProgressAndTheVerdict()
        {
            // What SetDialogValues puts on the wire. Runs headless because it is the block entity's
            // half of the contract - the dialog only draws what it is handed here.
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:coke", 4);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 18)), 18);
            be.AddCharge(new DummySlot(World.Stack("game:nugget-cassiterite", 2)), 2);
            be.MarkDirty(true);

            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            be.SetDialogValues(tree);

            Assert.Equal(1200f, tree.GetFloat("maxTemp"), "the ceiling coke can drive a crucible to");
            Assert.Equal(1, tree.GetInt("haveCrucible"), "the dialog knows there is a crucible");
            Assert.Greater(tree.GetFloat("maxMeltTime"), 0f, "a melt duration to scale the arrow against");

            string status = tree.GetString("statusText");
            Assert.Contains(status, "90%", "copper's share");
            Assert.Contains(status, "Tin bronze", "what it will make");
        }

        [VsTest]
        public async Task AnEmptyForgeTellsTheWindowToSaySo()
        {
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            Forge.SetDialogValues(tree);

            Assert.Equal(0, tree.GetInt("haveCrucible"), "no crucible in it");
            Assert.Contains(tree.GetString("statusText"), "crucible", "the window prompts for one");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task DraggingOreIntoTheWindowReachesTheServer()
        {
            // The whole point of the window. The forge is a plain BlockEntityContainer, so the slot
            // packet handling is hand-written; if it went nowhere the grid would look right and
            // move nothing. This sends exactly the packet the slot grid sends.
            var be = await EmptyHandedAtAForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(2);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Gui.WaitFor<GuiDialogCrucibleForge>(120);

            // Ore on the cursor. It has to be on both sides: the client's inventory is its own copy,
            // and the server resolves the move against its own mouse slot, so setting only the
            // client's would send a packet that moves nothing and prove the opposite of the point.
            await OnServer();
            Player.Me.InventoryManager.MouseItemSlot.Itemstack = World.Stack("game:nugget-nativecopper", 6);
            Player.Me.InventoryManager.MouseItemSlot.MarkDirty();
            await Ticks(2);

            await OnClient();
            var clientBe = (BlockEntityCrucibulumForge)Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            var mouse = Capi.World.Player.InventoryManager.MouseItemSlot;
            mouse.Itemstack = new ItemStack(Capi.World.GetItem(new AssetLocation("game:nugget-nativecopper")), 6);
            mouse.MarkDirty();

            // Exactly the packet the slot grid sends when you click a stack into a slot.
            var op = new ItemStackMoveOperation(Capi.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, 6)
            {
                ActingPlayer = Capi.World.Player
            };
            object packet = clientBe.Inventory.InvNetworkUtil.GetActivateSlotPacket(
                BlockEntityCrucibulumForge.FirstChargeSlot, op);
            Capi.Network.SendBlockEntityPacket(ForgePos.X, ForgePos.Y, ForgePos.Z, packet);

            await OnServer();

            await Until(() => Forge.ChargeSlots[0].StackSize > 0, 120,
                "the ore reached the server's charge slot through the inventory packet");
            Assert.Equal(6, Forge.ChargeSlots[0].StackSize, "all six of them");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
        }

        [VsTest]
        public async Task TheBlockInfoTextIsNotRebuiltEveryCall()
        {
            // GetBlockInfo runs once a frame for whatever the player is looking at, and working out
            // what a crucible will make is not cheap - on a mix that matches no alloy it walks every
            // alloy in the game. Measured at 4.6us and 9.6KB a call, which at 60fps is most of a
            // megabyte a second of garbage from one forge. It is cached; this is the guard.
            //
            // Counting rebuilds rather than bytes: GC counters are process-wide, so with a client
            // attached an allocation measurement is just noise from the render thread.
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.FuelSlot.Itemstack = World.Stack("game:coke", 4);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 8)), 8);
            be.AddCharge(new DummySlot(World.Stack("game:nugget-cassiterite", 12)), 12);
            be.MarkDirty(true);

            var sb = new System.Text.StringBuilder();
            be.GetBlockInfo(null, sb);
            Assert.Contains(sb.ToString(), "will not combine", "the text is the expensive one");

            int before = be.ChargeTextRebuilds;
            for (int i = 0; i < 200; i++)
            {
                sb.Clear();
                be.GetBlockInfo(null, sb);
            }

            int rebuilds = be.ChargeTextRebuilds - before;
            Assert.Less(rebuilds, 5, $"200 calls in a row rebuilt the text {rebuilds} times");
        }

        [VsTest]
        public async Task ChangingTheChargeRefreshesTheTextAtOnce()
        {
            // Cheap is no good if it is also stale: adding ore has to show up straight away rather
            // than at the cache's leisure.
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 4)), 4);
            be.MarkDirty(true);

            var sb = new System.Text.StringBuilder();
            be.GetBlockInfo(null, sb);
            Assert.Contains(sb.ToString(), "4x", "four nuggets to start with");

            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 6)), 6);

            sb.Clear();
            be.GetBlockInfo(null, sb);
            Assert.Contains(sb.ToString(), "10x", "and ten straight after adding six more");
        }

        [VsTest(TimeoutMs = 90000), RequiresClient]
        public async Task TheWindowRefusesThingsTheCrucibleCannotMelt()
        {
            // The slot filters are the only thing standing between a dialog and a player storing
            // their boots in a forge.
            await EmptyHandedAtAForge();

            Assert.False(ItemSlotCrucibleCharge.Accepts(World.Stack("game:coke")), "coke is fuel, not a charge");
            Assert.False(ItemSlotCrucibleCharge.Accepts(World.Stack("game:stick")), "a stick melts into nothing");
            Assert.True(ItemSlotCrucibleCharge.Accepts(World.Stack("game:nugget-nativecopper")), "a nugget belongs");

            Assert.True(ItemSlotForgeFuel.Accepts(World.Stack("game:coke")), "coke is fuel");
            Assert.False(ItemSlotForgeFuel.Accepts(World.Stack("game:nugget-nativecopper")), "a nugget is not fuel");

            Assert.True(ItemSlotForgeWorkItem.Accepts(World.Stack("game:crucible-brown-fired")), "a crucible fits the work slot");
            Assert.True(ItemSlotForgeWorkItem.Accepts(World.Stack("game:ingot-copper")), "so does an ingot - smithing must still work");
            Assert.False(ItemSlotForgeWorkItem.Accepts(World.Stack("game:stick")), "a stick does not");

            await Task.CompletedTask;
        }
    }
}
