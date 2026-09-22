using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// QPTech's ChiselTools adds a decorative forge of its own, with its own block and block entity
    /// classes. This mod attaches by patching a forge's class and entityClass to its own, so the two
    /// cannot both own the block - and without help, a chiselled forge is simply not crucible
    /// capable.
    ///
    /// It works out because the chiselling is not in their classes at all: BBChiseledCover and
    /// BEBChiseledCover carry the whole feature and are declared on the blocktype, so they survive
    /// the class being swapped underneath them. Their block class adds only four small methods and
    /// their block entity one.
    ///
    /// Surviving is not the same as being reached, though: BlockForge never runs the block
    /// behaviour chain, so BBChiseledCover only ever got its click because their block class called
    /// it by hand. BlockCrucibulumForge.TryBlockBehaviors restores that, and
    /// AChiselledBlockGoesOnWithAClick is the test that would have caught it going missing.
    ///
    /// These tests are the tripwire on that arrangement. If ChiselTools renames the blocktype, moves
    /// the cover back into the class, or renames the naming method, the patch silently stops
    /// applying or starts applying wrongly - and none of this mod's own tests would notice.
    ///
    /// They no-op when ChiselTools is not installed, which is the usual case; run them with it in
    /// the mod path to mean anything. See docs/compat.md.
    /// </summary>
    public class CompatChiselTools
    {
        const string ChiselledForge = "chiseltools:chiseledforge";
        static BlockPos ForgePos => P(8, 0, 8);

        static bool Installed => Sapi.ModLoader.IsModEnabled("chiseltools");

        static bool Absent(string what)
        {
            if (Installed) return false;
            Log($"  ChiselTools not installed - {what} not checked");
            return true;
        }

        static async Task<BlockEntityCrucibulumForge> AChiselledForge()
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock(ChiselledForge, ForgePos);
            await Ticks(2);
            return World.BE<BlockEntityCrucibulumForge>(ForgePos);
        }


        /// <summary>
        /// A genuinely chiselled block, made the way the game makes one: convert a solid block in
        /// place and pick it back up. A bare chiseledblock stack carries no material and their
        /// cover reads as nameless, which is how an earlier version of these tests managed to
        /// "apply" a cover and assert nothing.
        /// </summary>
        static ItemStack AChiselledGraniteBlock() => AChiselledBlockOf("granite");

        static ItemStack AChiselledBlockOf(string rock)
        {
            BlockPos scratch = P(2, 0, 2);
            World.SetBlock("game:chiseledblock", scratch);

            var bec = Sapi.World.BlockAccessor.GetBlockEntity(scratch) as BlockEntityChisel;
            Assert.NotNull(bec, "chiseledblock gives a BlockEntityChisel");
            // Named, not null. A null name is stored as a set-but-null "blockName" attribute, and
            // vanilla's own BlockMicroBlock.GetHeldItemName then splits it and throws - reachable
            // only by building one in code like this, but it makes the fixture useless.
            bec.WasPlaced(Sapi.World.GetBlock(new AssetLocation("game:rock-" + rock)), "Chiselled " + rock);
            bec.MarkDirty(true);

            ItemStack stack = Sapi.World.BlockAccessor.GetBlock(scratch).OnPickBlock(Sapi.World, scratch);
            World.SetBlock("game:air", scratch);
            return stack;
        }

        static Vintagestory.API.Server.IServerPlayer ThePlayer =>
            (Vintagestory.API.Server.IServerPlayer)Sapi.World.AllOnlinePlayers.First();

        static object CoverOf(BlockEntity be) =>
            be.Behaviors.FirstOrDefault(x => x.GetType().Name == "BEBChiseledCover");

        static bool ApplyCover(BlockEntity be, ItemStack cover)
        {
            object beh = CoverOf(be);
            var set = beh.GetType().GetMethod("SetShape");
            return (bool)set.Invoke(beh, new object[] { new DummySlot(cover), true });
        }

        static string CoverName(BlockEntity be)
        {
            object beh = CoverOf(be);
            return beh.GetType().GetMethod("GetChiseledName", System.Type.EmptyTypes).Invoke(beh, null) as string;
        }

        /// <summary>
        /// A mesh pool that keeps what it was handed. Comparing by reference rather than by
        /// counting vertices is what makes the assertions exact: the cover's mesh and the forge's
        /// are both singletons their owners hand out, so "this mesh, not that one" is answerable.
        /// </summary>
        class RecordingMesher : ITerrainMeshPool
        {
            public readonly List<MeshData> Meshes = new();

            public void AddMeshData(MeshData data, int lodLevel = 1) => Meshes.Add(data);
            public void AddMeshData(MeshData data, float[] tfMatrix, int lodLevel = 1) => Meshes.Add(data);
            public void AddMeshData(MeshData data, ColorMapData colorMapData, int lodLevel = 1) => Meshes.Add(data);
        }

        static MeshData CoverMeshOf(BlockEntity be)
        {
            object beh = CoverOf(be);
            return beh?.GetType().GetMethod("GetChiseledMesh", System.Type.EmptyTypes).Invoke(beh, null) as MeshData;
        }

        /// <summary>A chiselled forge with granite actually chiselled onto it.</summary>
        static async Task<BlockEntityCrucibulumForge> ACoveredForge()
        {
            var be = await AChiselledForge();
            Assert.True(ApplyCover(be, AChiselledGraniteBlock()), "the cover went on");
            be.MarkDirty(true);
            await Ticks(2);
            return be;
        }

        [VsTest]
        public async Task ThePatchFindsTheirBlocktype()
        {
            if (Absent("the patch")) return;

            Block b = Sapi.World.GetBlock(new AssetLocation(ChiselledForge));
            Assert.NotNull(b, $"{ChiselledForge} exists - if this is null they renamed the blocktype "
                            + "and the patch in assets/crucibulum/patches/chiseltools-forge.json is aimed at nothing");

            Log($"  {ChiselledForge}: class={b.GetType().Name} entityClass={b.EntityClass}");
            Assert.IsType<BlockCrucibulumForge>(b, "their forge takes our block class");
            Assert.Equal("CrucibulumForge", b.EntityClass, "and our block entity");
        }

        [VsTest]
        public async Task TheirChiselCoverSurvivesTheSwap()
        {
            if (Absent("the cover behaviours")) return;

            var be = await AChiselledForge();
            Assert.NotNull(be, "placing their forge gives our block entity");

            string[] behaviors = be.Behaviors.Select(x => x.GetType().Name).ToArray();
            Log("  live behaviours: " + string.Join(", ", behaviors));
            Assert.True(behaviors.Contains("BEBChiseledCover"),
                "their cover behaviour is still on the block entity - without it we have taken their "
                + "decorative forge and given nothing back");

            string[] blockBehaviors = be.Block.BlockBehaviors.Select(x => x.GetType().Name).ToArray();
            Assert.True(blockBehaviors.Contains("BBChiseledCover"), "and the block behaviour that applies a cover");
        }

        [VsTest, RequiresClient]   // TryAddCover takes the block out of a player's hand
        public async Task AChiselledBlockGoesOnWithAClick()
        {
            if (Absent("applying a cover by clicking")) return;

            // Every other test here reaches straight for BEBChiseledCover.SetShape, which is
            // precisely why none of them noticed that the gesture that calls it had gone. Applying
            // a cover lives in a block behaviour, and BlockForge.OnBlockInteractStart hands the
            // click to its block entity without ever running the behaviour chain - so with our
            // class on their forge, a chiselled block in hand did nothing at all. Reported by a
            // player on 1.4.0: the decorative forge worked as a forge but refused the decoration.
            //
            // Driven through the block's own OnBlockInteractStart rather than a client click, so
            // what is asserted is the dispatch that broke, with no inventory copy in the way.
            var be = await AChiselledForge();
            Assert.True(string.IsNullOrEmpty(CoverName(be)), "it starts bare");

            ItemSlot hand = ThePlayer.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = AChiselledGraniteBlock();
            hand.MarkDirty();
            await Ticks(1);

            var sel = new BlockSelection { Position = ForgePos.Copy(), Face = BlockFacing.UP, HitPosition = new Vec3d(0.5, 0.875, 0.5) };
            bool handled = be.Block.OnBlockInteractStart(Sapi.World, ThePlayer, sel);
            await Ticks(2);

            Log($"  after the click: handled={handled}, cover \"{CoverName(be)}\"");
            Assert.True(handled, "the click was taken");
            Assert.False(string.IsNullOrEmpty(CoverName(be)),
                "the chiselled block went onto the forge - if this fails, BBChiseledCover is not "
                + "being run and their decorative forge cannot be decorated");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task APlayerCanClickAChiselledBlockOntoTheForge()
        {
            if (Absent("the gesture as a player performs it")) return;

            // The same fix, reached the way it was reported: stand in front of the forge, hold a
            // chiselled block, right-click it. AChiselledBlockGoesOnWithAClick drives the block's
            // dispatch from the server; this one goes out through the client's own input, the
            // selection raycast and the interaction packet, so it covers the whole path rather
            // than the one method on it.
            var be = await AChiselledForge();
            Assert.True(string.IsNullOrEmpty(CoverName(be)), "it starts bare");

            ItemSlot hand = ThePlayer.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = AChiselledGraniteBlock();
            hand.MarkDirty();
            await Ticks(4);

            // The client keeps its own copy of the inventory and the interaction begins there, so a
            // stack written server-side without waiting for the sync drives an empty hand - and the
            // failure then reads as "the forge refused it" when nothing was ever held.
            await OnClient();
            string held = Capi.World.Player.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible.Code.ToString();
            await OnServer();
            Log($"  the client is holding: {held}");
            Assert.Equal("game:chiseledblock", held, "the client is really holding the chiselled block");

            await Player.StandNear(ForgePos);
            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            await Until(() => !string.IsNullOrEmpty(CoverName(be)), 60, "the cover to go on");

            Log($"  cover after the click: \"{CoverName(be)}\"");

            // A click the forge does not take falls through to placing what is held, so a
            // regression here does not merely do nothing - it drops a chiselled block on the forge.
            Assert.Equal(ChiselledForge, World.BlockCode(ForgePos), "the forge is still the forge");
        }

        [VsTest]
        public async Task TheCoverNameIsStillReachable()
        {
            if (Absent("the cover name")) return;

            // Our block class replaces theirs, and theirs is where the decorative forge got its
            // name from the material chiselled onto it. We ask their behaviour for it by name, so
            // this asserts the lookup still resolves rather than quietly falling back forever.
            var be = await AChiselledForge();
            var cover = be.Behaviors.FirstOrDefault(x => x.GetType().Name == "BEBChiseledCover");
            Assert.NotNull(cover, "the cover behaviour");
            Assert.NotNull(cover.GetType().GetMethod("GetChiseledName", System.Type.EmptyTypes),
                "BEBChiseledCover.GetChiseledName() still exists - BlockCrucibulumForge.GetPlacedBlockName "
                + "calls it by name to keep their naming working");

            // Uncovered, their name is empty and we fall through to the ordinary block name.
            string name = be.Block.GetPlacedBlockName(Sapi.World, ForgePos);
            Log($"  uncovered name: \"{name}\"");
            Assert.False(string.IsNullOrEmpty(name), "an uncovered chiselled forge still has a name");
        }

        [VsTest]
        public async Task AChiselledForgeMeltsMetal()
        {
            if (Absent("melting")) return;

            var be = await AChiselledForge();
            Assert.Equal(6, be.Inventory.Count, "work item, fuel and four charge slots, as on a plain forge");

            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            be.FuelSlot.Itemstack = World.Stack("game:coke", 4);
            be.TryIgnite();
            be.MarkDirty(true);
            await Ticks(2);

            var sb = new StringBuilder();
            be.GetBlockInfo(null, sb);
            string info = sb.ToString();
            Log("  block info: " + info.Replace("\n", " | ").Trim());

            Assert.Contains(info, "100 units", "it reads the charge like any other forge");
            Assert.Contains(info, "1084", "and quotes the melting point");
        }

        [VsTest]
        public async Task ACoveredForgeKeepsItsCoverAndItsName()
        {
            if (Absent("a covered forge")) return;

            var be = await ACoveredForge();

            string chiselled = CoverName(be);
            Log($"  cover name from their behaviour: \"{chiselled}\"");
            Assert.False(string.IsNullOrEmpty(chiselled), "a covered forge has a cover name");

            // The point of GetPlacedBlockName: their naming is in the class we replaced, so ours
            // has to hand the question back to their behaviour.
            string placed = be.Block.GetPlacedBlockName(Sapi.World, ForgePos);
            Log($"  GetPlacedBlockName: \"{placed}\"");
            Assert.Equal(chiselled, placed, "the forge is still named after what was chiselled onto it");
        }

        [VsTest]
        public async Task ACoveredForgeStillMelts()
        {
            if (Absent("melting under a cover")) return;

            var be = await ACoveredForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            be.FuelSlot.Itemstack = World.Stack("game:coke", 4);
            be.TryIgnite();
            be.MarkDirty(true);
            await Ticks(2);

            var sb = new StringBuilder();
            be.GetBlockInfo(null, sb);
            Assert.Contains(sb.ToString(), "100 units", "a decorated forge melts like any other");
            Assert.False(string.IsNullOrEmpty(CoverName(be)), "and has not lost its cover doing so");
        }

        [VsTest]
        public async Task ACoverSurvivesASaveAndReload()
        {
            if (Absent("cover persistence")) return;

            // Their cover is persisted by the behaviour's own ToTreeAttributes, which only runs
            // because our block entity chains to base. Silence here means covers vanishing on
            // chunk unload.
            var be = await ACoveredForge();
            string before = CoverName(be);

            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            be.ToTreeAttributes(tree);

            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock(ChiselledForge, ForgePos);
            await Ticks(2);

            var fresh = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            Assert.True(string.IsNullOrEmpty(CoverName(fresh)), "a new one starts bare");

            fresh.FromTreeAttributes(tree, Sapi.World);
            fresh.MarkDirty(true);
            await Ticks(2);

            Log($"  cover after reload: \"{CoverName(fresh)}\"");
            Assert.Equal(before, CoverName(fresh), "the cover came back off the tree");
        }

        [VsTest]
        public async Task BreakingACoveredForgeGivesTheCoverBack()
        {
            if (Absent("cover drops")) return;

            // Their behaviour drops the chiselled block on break, and only gets the chance because
            // our OnBlockBroken chains to base after spawning the charge.
            var be = await ACoveredForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(2);

            // Items only. World.Entities returns everything in radius, the player included, and
            // clearing leftovers with an unfiltered loop kills him - no damage source, no death
            // reason, just a death screen that then blocks every interaction test after it.
            foreach (var e in World.Entities(ForgePos, 6).OfType<EntityItem>()) e.Die(EnumDespawnReason.Removed);
            await Ticks(2);

            // Waited on rather than counted out in ticks: the spawned entities are not in the world
            // the instant the break returns, and querying too early reads as the crucible having
            // been destroyed - which it did, intermittently, when this waited a fixed six ticks.
            be.OnBlockBroken(null);
            World.SetBlock("game:air", ForgePos);
            await Until(() => World.Entities(ForgePos, 6).OfType<EntityItem>().Count() >= 2, 60,
                "the forge to drop its contents");

            string[] dropped = World.Entities(ForgePos, 6)
                .OfType<EntityItem>()
                .Select(e => e.Itemstack?.Collectible.Code.ToString())
                .Where(c => c != null)
                .ToArray();

            Log("  dropped: " + string.Join(", ", dropped));
            Assert.True(dropped.Any(c => c.Contains("chiseledblock")),
                "the chiselled cover came back rather than being destroyed with the forge");
            Assert.True(dropped.Any(c => c.Contains("crucible")), "and the crucible with it");
        }


        [VsTest]
        public async Task ALegacyChiselledForgeUpgradesWithoutLosingItsCover()
        {
            if (Absent("the upgrade path")) return;

            // Someone who already had chiselled forges when this mod arrived gets their block
            // entities swapped for ours as the chunks load. Their cover rides on a behaviour, and
            // the swap carries the whole tree across - but getting that wrong would quietly destroy
            // decorated forges all over an existing base, which is not a thing to leave to reasoning.
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock(ChiselledForge, ForgePos);
            await Ticks(2);

            // Their block entity on our block: exactly what a pre-existing chiselled forge loads as.
            Sapi.World.BlockAccessor.RemoveBlockEntity(ForgePos);
            Sapi.World.BlockAccessor.SpawnBlockEntity("BEDecoForge", ForgePos);
            await Ticks(2);

            BlockEntity legacy = Sapi.World.BlockAccessor.GetBlockEntity(ForgePos);
            Assert.False(legacy is BlockEntityCrucibulumForge, "starts as theirs, not ours");
            Assert.True(ApplyCover(legacy, AChiselledGraniteBlock()), "with a cover on it");
            legacy.MarkDirty(true);
            await Ticks(2);

            string before = CoverName(legacy);
            Assert.False(string.IsNullOrEmpty(before), "the cover took");

            var chunk = Sapi.WorldManager.GetChunk(ForgePos);
            CrucibulumModSystem.UpgradeForgesIn(Sapi, new[] { (IWorldChunk)chunk });
            await Ticks(2);

            var fresh = World.BE<BlockEntityCrucibulumForge>(ForgePos);
            Assert.NotNull(fresh, "it came back as ours");
            Log($"  cover through the upgrade: \"{CoverName(fresh)}\"");
            Assert.Equal(before, CoverName(fresh), "and kept the cover it was decorated with");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task ACoveredForgeDrawsTheCoverAndNotTheForge()
        {
            if (Absent("the cover's mesh")) return;

            // A cover is kept on the block entity whether or not anything draws it, so every test
            // above stayed green while a covered forge rendered as a plain forge - which is what a
            // player reported against 1.4.1: the block took the chiselled block, remembered it, and
            // looked no different. BlockEntityForge.OnTesselation draws its own mesh and returns
            // without running the block entity behaviours, and the cover's mesh lives in one.
            //
            // Asked of the client's block entity, because the mesh only exists there.
            var be = await ACoveredForge();
            await Ticks(10);

            MeshData cover = null, forge = null;
            bool drew = false;
            var pool = new RecordingMesher();

            await OnClient();
            BlockEntity clientBe = Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            if (clientBe != null)
            {
                cover = CoverMeshOf(clientBe);
                forge = Capi.TesselatorManager.GetDefaultBlockMesh(clientBe.Block);
                drew = clientBe.OnTesselation(pool, Capi.Tesselator);
            }
            await OnServer();

            Assert.NotNull(clientBe, "the client has the forge");
            Assert.NotNull(cover, "and their behaviour has a mesh for what was chiselled onto it");
            Log($"  {pool.Meshes.Count} meshes offered, cover among them: {pool.Meshes.Any(m => ReferenceEquals(m, cover))}");

            Assert.True(drew, "the block entity reports it drew the block itself");
            Assert.True(pool.Meshes.Any(m => ReferenceEquals(m, cover)),
                "the cover's own mesh reached the mesher - if this fails the forge is storing a "
                + "cover it never draws, and a decorated forge looks exactly like a plain one");
            Assert.False(pool.Meshes.Any(m => ReferenceEquals(m, forge)),
                "and the plain forge mesh did not, since the two fill the same cube");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AnUncoveredChiselledForgeStillDrawsAsAForge()
        {
            if (Absent("the uncovered mesh")) return;

            // The other half of the same call. Running the behaviours must not cost a bare forge
            // its own shape, which is what it would do if the chain were allowed to claim the draw
            // whether or not a behaviour had anything to give.
            var be = await AChiselledForge();
            await Ticks(10);

            MeshData forge = null;
            var pool = new RecordingMesher();

            await OnClient();
            BlockEntity clientBe = Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            forge = Capi.TesselatorManager.GetDefaultBlockMesh(clientBe.Block);
            clientBe.OnTesselation(pool, Capi.Tesselator);
            await OnServer();

            Log($"  {pool.Meshes.Count} meshes offered, forge among them: {pool.Meshes.Any(m => ReferenceEquals(m, forge))}");
            Assert.True(pool.Meshes.Any(m => ReferenceEquals(m, forge)),
                "an uncovered chiselled forge still draws as a forge");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AWrenchTakesTheCoverBackOff()
        {
            if (Absent("the wrench click")) return;

            // The other half of BBChiseledCover: a wrench click calls DumpInventory, which drops
            // the cover and broadcasts packet 32322 so the client clears the mesh it is drawing.
            // That packet lands in BEBChiseledCover.OnReceivedServerPacket - a behaviour again - so
            // it needs the block entity to pass server packets down the chain.
            var be = await ACoveredForge();
            await Ticks(10);

            ItemSlot hand = ThePlayer.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = World.Stack("game:wrench-copper");
            hand.MarkDirty();
            await Ticks(4);

            var sel = new BlockSelection { Position = ForgePos.Copy(), Face = BlockFacing.UP, HitPosition = new Vec3d(0.5, 0.875, 0.5) };
            bool handled = be.Block.OnBlockInteractStart(Sapi.World, ThePlayer, sel);
            await Ticks(10);

            Log($"  wrench click: handled={handled}, cover now \"{CoverName(be)}\"");
            Assert.True(handled, "the wrench click was taken");
            Assert.True(string.IsNullOrEmpty(CoverName(be)), "and the cover came off");

            // What the player sees is the point: a cover removed server side but still drawn on the
            // client is the 1.4.1 bug in a mirror.
            MeshData cover = null, forge = null;
            var pool = new RecordingMesher();

            await OnClient();
            BlockEntity clientBe = Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            cover = CoverMeshOf(clientBe);
            forge = Capi.TesselatorManager.GetDefaultBlockMesh(clientBe.Block);
            clientBe.OnTesselation(pool, Capi.Tesselator);
            await OnServer();

            Log($"  client after the wrench: cover mesh {(cover == null ? "gone" : "still there")}, "
                + $"{pool.Meshes.Count} meshes offered, forge among them: {pool.Meshes.Any(m => ReferenceEquals(m, forge))}");

            Assert.Null(cover, "the client dropped the cover's mesh");
            Assert.True(pool.Meshes.Any(m => ReferenceEquals(m, forge)),
                "and draws the forge again rather than a cover that is no longer there");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task ALockedCoverSaysSoInTheBlockInfo()
        {
            if (Absent("the shape lock notice")) return;

            // Ctrl with a wrench locks a cover's shape, after which their wrench click refuses to
            // take it off. The only thing that says so is BEBChiseledCover.GetBlockInfo, and
            // BlockEntityForge.GetBlockInfo writes the forge's own lines and returns without ever
            // chaining to BlockEntity.GetBlockInfo. Restoring the click without this leaves a
            // player holding a wrench that silently does nothing.
            var be = await ACoveredForge();
            await Ticks(4);

            var before = new StringBuilder();
            be.GetBlockInfo(ThePlayer, before);
            Assert.False(before.ToString().Contains(CoverName(be)),
                "an unlocked cover says nothing, so the assertion below is about the lock");

            ItemSlot hand = ThePlayer.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = World.Stack("game:wrench-copper");
            hand.MarkDirty();
            await Ticks(2);

            var sel = new BlockSelection { Position = ForgePos.Copy(), Face = BlockFacing.UP, HitPosition = new Vec3d(0.5, 0.875, 0.5) };
            ThePlayer.Entity.Controls.CtrlKey = true;
            try
            {
                Assert.True(be.Block.OnBlockInteractStart(Sapi.World, ThePlayer, sel), "ctrl-wrench was taken");
            }
            finally
            {
                ThePlayer.Entity.Controls.CtrlKey = false;
            }
            await Ticks(4);

            Assert.False(string.IsNullOrEmpty(CoverName(be)), "locking keeps the cover on");

            var after = new StringBuilder();
            be.GetBlockInfo(ThePlayer, after);
            string info = after.ToString();
            Log("  block info: " + info.Replace("\n", " | ").Trim());

            Assert.Contains(info, CoverName(be),
                "a locked cover names itself in the block info - if this fails their behaviour's "
                + "GetBlockInfo is not being reached and a locked shape gives the player nothing");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task ReplacingACoverUpdatesWhatTheClientDraws()
        {
            if (Absent("replacing a cover")) return;

            // The observing-player case, and the one the removal fix does not reach. SetShape is
            // driven server side only - which is what a second player in the world sees - so the
            // client learns about the new cover through the synced tree and nothing else.
            // BEBChiseledCover.FromTreeAttributes takes the new stack but leaves meshdata alone,
            // and their OnTesselation only regenerates when meshdata is null. A wrench removal
            // broadcasts packet 32322 to force that; a replacement sends nothing at all.
            var be = await ACoveredForge();
            await Ticks(10);

            MeshData first = null, second = null, forge = null;
            string firstName = null, secondName = null;
            var before = new RecordingMesher();
            var after = new RecordingMesher();

            await OnClient();
            BlockEntity clientBe = Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            clientBe.OnTesselation(before, Capi.Tesselator);
            first = CoverMeshOf(clientBe);
            firstName = CoverName(clientBe);
            await OnServer();

            Assert.NotNull(first, "the client has a mesh for the first cover");
            Assert.True(before.Meshes.Any(m => ReferenceEquals(m, first)), "and draws it");
            Log($"  first cover on the client: \"{firstName}\"");

            Assert.True(ApplyCover(be, AChiselledBlockOf("andesite")), "a second, different cover went on");
            be.MarkDirty(true);
            await Ticks(10);

            await OnClient();
            clientBe = Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            clientBe.OnTesselation(after, Capi.Tesselator);
            second = CoverMeshOf(clientBe);
            secondName = CoverName(clientBe);
            forge = Capi.TesselatorManager.GetDefaultBlockMesh(clientBe.Block);
            await OnServer();

            Log($"  second cover on the client: \"{secondName}\", {after.Meshes.Count} meshes offered, "
                + $"mesh changed: {!ReferenceEquals(first, second)}");

            // The stack syncs, so this half already worked and is here to isolate the defect: what
            // is stale is the mesh built from it, not the data.
            Assert.Equal("Chiselled andesite", secondName, "the client knows which cover it now has");

            // Asked the same way as ACoveredForgeDrawsTheCoverAndNotTheForge, because "the mesh is
            // a different object" is satisfied by drawing nothing and by falling back to the bare
            // forge, neither of which is a cover being redrawn.
            Assert.NotNull(second, "there is a mesh for the cover that replaced it");
            Assert.False(ReferenceEquals(first, second),
                "built fresh rather than the one for the cover it replaced - if this fails an "
                + "observing player is still looking at the old cover");
            Assert.True(after.Meshes.Any(m => ReferenceEquals(m, second)),
                "and that mesh is what the client draws");
            Assert.False(after.Meshes.Any(m => ReferenceEquals(m, forge)),
                "with no bare forge under it");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task ACoveredForgeStillWorksItsGate()
        {
            if (Absent("the gate under a cover")) return;

            // A full-cube cover hides the blast gate - it is drawn at the forge's front face, and
            // the cover fills the whole block. That is what covering something in a solid block
            // does, and chiselling the front voxels away is the remedy the mod exists for; see
            // docs/compat.md. What must not follow from it is a gate that stops working, so this
            // pins the half that matters: the click still lands on it through the cover.
            //
            // It lands because the selection box is still the forge's. Their cover's own
            // GetSelectionBoxes is never called for a forge - neither their block class nor ours
            // asks for it - so what the player aims at is unchanged by what is drawn.
            var be = await ACoveredForge();
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Open);

            // A crucible in the slot is what makes this about aim: with the work item slot empty
            // any click on the forge works the gate, hit position or not.
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(10);

            ItemSlot hand = ThePlayer.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = null;
            hand.MarkDirty();
            await Ticks(2);

            var onTheGate = new Vec3d(0.5, 0.3, 0.94);
            Assert.True(be.IsGateHit(onTheGate), "the front face still reads as the gate under a cover");

            var sel = new BlockSelection { Position = ForgePos.Copy(), Face = BlockFacing.SOUTH, HitPosition = onTheGate };
            bool handled = be.Block.OnBlockInteractStart(Sapi.World, ThePlayer, sel);
            await Ticks(4);

            Log($"  covered gate: handled={handled}, gate now {be.GatePosition}, cover \"{CoverName(be)}\"");
            Assert.True(handled, "the click was taken");
            Assert.Equal(GatePosition.Half, be.GatePosition, "and moved the gate a notch");
            Assert.False(string.IsNullOrEmpty(CoverName(be)), "without disturbing the cover");
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task APlayerCanWorkAGateThroughACover()
        {
            if (Absent("a real click on a covered gate")) return;

            // ACoveredForgeStillWorksItsGate hands IsGateHit a hit position of its own choosing,
            // which proves the branch and not the aim. This one makes the client's own raycast
            // produce it, through a full-cube cover, which is the thing actually in doubt: the
            // cover is drawn 0-1 while the selection box stays the forge's inset one, so what the
            // player sees and what the ray hits are no longer the same solid.
            //
            // Raised a block, because a forge sunk in the plot's ground has no front face to aim at.
            BlockPos raised = P(8, 1, 8);
            World.SetBlock("game:air", raised);
            await Ticks(1);
            World.SetBlock(ChiselledForge, raised);
            await Ticks(4);

            var be = World.BE<BlockEntityCrucibulumForge>(raised);
            Assert.NotNull(be, "the raised forge is ours");
            Assert.True(ApplyCover(be, AChiselledGraniteBlock()), "with a cover on it");
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Open);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.MarkDirty(true);
            await Ticks(20);

            ItemSlot hand = ThePlayer.InventoryManager.ActiveHotbarSlot;
            hand.Itemstack = null;
            hand.MarkDirty();
            await Ticks(2);

            await Player.Teleport(new Vec3d(raised.X + 0.5, raised.Y - 1, raised.Z + 2.0));
            await Interact.LookAt(new Vec3d(raised.X + 0.5, raised.Y + 0.28, raised.Z + 0.94));

            // The selection is recomputed on a render callback, so this waits on it rather than
            // counting ticks - an unfocused window renders far more slowly than it ticks.
            BlockSelection sel = null;
            for (int i = 0; i < 60 && sel == null; i++)
            {
                await OnClient();
                BlockSelection got = Capi.World.Player.CurrentBlockSelection;
                await OnServer();
                if (got != null && got.Position.Equals(raised)) sel = got;
                else await Ticks(1);
            }

            Assert.NotNull(sel, "the client's own raycast selected the covered forge");
            Log($"  raycast hit {sel.HitPosition} on the {sel.Face?.Code} face");
            Assert.True(be.IsGateHit(sel.HitPosition),
                $"and landed on the gate band at {sel.HitPosition} - the cover does not move what "
                + "the ray hits, because the selection box is still the forge's");

            await Input.Click(EnumMouseButton.Right, 3);
            await Until(() => be.GatePosition == GatePosition.Half, 80, "the gate to move a notch");

            Log($"  after a real click: gate {be.GatePosition}, cover \"{CoverName(be)}\"");
            Assert.False(string.IsNullOrEmpty(CoverName(be)), "and the cover is still on");

            World.SetBlock("game:air", raised);
            await Ticks(1);
        }
    }
}