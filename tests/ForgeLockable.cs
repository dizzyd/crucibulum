using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// BlockForge.OnBlockInteractStart hands a click straight to its block entity and never chains
    /// to Block.OnBlockInteractStart, so no BlockBehavior declared on a forge blocktype ever sees a
    /// right click. BlockCrucibulumForge.TryBlockBehaviors puts that loop back, because ChiselTools
    /// keeps its whole "chisel a cover onto it" gesture in one - see CompatChiselTools.
    ///
    /// Restoring the chain has a second effect that has nothing to do with ChiselTools. The vanilla
    /// forge blocktype declares Lockable, which vanilla's own class is equally deaf to, so a
    /// reinforced and locked forge now refuses a stranger where stock Vintage Story lets him use it
    /// regardless. That is what the blocktype asks for, but it is a deliberate change to plain
    /// forges and belongs under test rather than in a comment.
    ///
    /// Both directions are here. Locking a stranger out is the change; letting the owner through is
    /// the half that would hurt if it broke, since a forge nobody can use looks exactly like a mod
    /// that ate someone's base.
    /// </summary>
    public class ForgeLockable
    {
        static BlockPos ForgePos => P(8, 0, 8);

        static ModSystemBlockReinforcement Reinforcements =>
            Sapi.ModLoader.GetModSystem<ModSystemBlockReinforcement>();

        static IPlayer Me => Sapi.World.AllOnlinePlayers.First();

        static BlockSelection TheTopFace =>
            new BlockSelection { Position = ForgePos.Copy(), Face = BlockFacing.UP, HitPosition = new Vec3d(0.5, 0.875, 0.5) };

        static async Task<BlockEntityCrucibulumForge> ABareForge()
        {
            Reinforcements.ClearReinforcement(ForgePos);
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);
            return World.BE<BlockEntityCrucibulumForge>(ForgePos);
        }

        static async Task ACrucibleInHand()
        {
            ItemSlot hand = Me.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = World.Stack("game:crucible-brown-fired");
            hand.MarkDirty();
            await Ticks(1);
        }

        /// <summary>
        /// Survival, because IsLockedForInteract waves a creative player holding the commandplayer
        /// privilege straight through - which is what the test player is by default, and would make
        /// every assertion here pass for the wrong reason.
        ///
        /// Not Player.SetGameMode: that runs /gamemode as the console, and the command then looks
        /// the caller up by name and dereferences the null it gets back. Run as the player it
        /// works. The mode is read back rather than assumed, since a silently unchanged one is
        /// exactly the false green this guards against.
        /// </summary>
        static async Task<EnumGameMode> SwitchTo(EnumGameMode mode)
        {
            EnumGameMode was = Me.WorldData.CurrentGameMode;
            if (was == mode) return was;

            await Cmd("/gamemode " + (mode == EnumGameMode.Creative ? "creative" : "survival"), Me.PlayerName);
            await Ticks(2);

            Assert.Equal(mode.ToString(), Me.WorldData.CurrentGameMode.ToString(),
                "the player actually changed game mode");
            return was;
        }

        /// <summary>
        /// Locks the forge in somebody else's name. Vanilla never locks a player out of his own
        /// block and this world holds exactly one player, so the lock has to belong to another one
        /// to mean anything.
        ///
        /// Their own TryLock writes the entry, and only the owner is rewritten afterwards. Two
        /// things make that the roundabout-looking way the short ways do not work: IPlayer carries
        /// an internal member as of 1.22, so a stand-in player cannot be written outside their
        /// assembly; and getOrCreateReinforcmentsAt deserializes the chunk's moddata fresh on every
        /// call, so what GetReinforcment hands back is a copy and writing to it changes nothing.
        /// Letting them lay the record out means this knows nothing of their index or their format.
        ///
        /// The result is read back through IsLockedForInteract before anything is asserted about a
        /// click, so a setup that quietly failed shows up as a failed setup.
        /// </summary>
        static void LockItAgainstUs()
        {
            Reinforcements.ClearReinforcement(ForgePos);
            Assert.True(Reinforcements.TryLock(ForgePos, Me, "game:ironlock"), "the forge took a lock");

            IWorldChunk chunk = Sapi.World.BlockAccessor.GetChunkAtBlockPos(ForgePos);
            Assert.NotNull(chunk, "the forge's chunk is loaded");

            byte[] data = chunk.GetModdata("reinforcements");
            Assert.NotNull(data, "their lock reached the chunk's moddata");

            var stored = SerializerUtil.Deserialize<Dictionary<int, BlockReinforcement>>(data);
            foreach (BlockReinforcement bre in stored.Values)
            {
                bre.PlayerUID = "crucibulum-test-stranger";
                bre.LastPlayername = "Somebody Else";
            }
            chunk.SetModdata("reinforcements", SerializerUtil.Serialize(stored));

            Assert.True(Reinforcements.IsLockedForInteract(ForgePos, Me),
                "and vanilla agrees the forge is now locked against us - if this fails the setup "
                + "did not take and nothing below would mean anything");
        }

        [VsTest, RequiresClient]   // a lock needs a player to be locked against
        public async Task ALockedForgeRefusesAStranger()
        {
            var be = await ABareForge();
            EnumGameMode was = await SwitchTo(EnumGameMode.Survival);

            try
            {
                // The control first, on the same forge and the same click: unreinforced, a crucible
                // held against a bare forge goes in. Without this the refusal below could be any
                // number of things that are not the lock.
                await ACrucibleInHand();
                Assert.True(be.Block.OnBlockInteractStart(Sapi.World, Me, TheTopFace), "an unlocked forge takes the click");
                Assert.False(be.WorkItemSlot.Empty, "and the crucible went in");

                be.WorkItemSlot.Itemstack = null;
                be.MarkDirty(true);
                await ACrucibleInHand();
                LockItAgainstUs();

                bool handled = be.Block.OnBlockInteractStart(Sapi.World, Me, TheTopFace);
                Log($"  locked: handled={handled}, work item slot empty={be.WorkItemSlot.Empty}");

                Assert.False(handled, "the click was refused");
                Assert.True(be.WorkItemSlot.Empty,
                    "and the crucible stayed in hand - if this fails the behaviour chain is not "
                    + "being run and Lockable is deaf again, which also means BBChiseledCover is");
            }
            finally
            {
                Reinforcements.ClearReinforcement(ForgePos);
                await SwitchTo(was);
            }
        }

        [VsTest, RequiresClient]
        public async Task TheOwnerOfALockedForgeStillUsesIt()
        {
            var be = await ABareForge();
            EnumGameMode was = await SwitchTo(EnumGameMode.Survival);

            try
            {
                // Locked, and left in our own name this time. Vanilla's own rule, and the reason
                // running the chain is safe: a lock is aimed at everyone except whoever set it.
                Assert.True(Reinforcements.TryLock(ForgePos, Me, "game:ironlock"), "the forge took a lock");
                await ACrucibleInHand();

                bool handled = be.Block.OnBlockInteractStart(Sapi.World, Me, TheTopFace);
                Log($"  own lock: handled={handled}, work item slot empty={be.WorkItemSlot.Empty}");

                Assert.True(handled, "our own lock does not shut us out");
                Assert.False(be.WorkItemSlot.Empty, "and the crucible went in");
            }
            finally
            {
                Reinforcements.ClearReinforcement(ForgePos);
                await SwitchTo(was);
            }
        }
    }
}
