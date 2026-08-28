using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Crucibulum;
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
        static ItemStack AChiselledGraniteBlock()
        {
            BlockPos scratch = P(2, 0, 2);
            World.SetBlock("game:chiseledblock", scratch);

            var bec = Sapi.World.BlockAccessor.GetBlockEntity(scratch) as BlockEntityChisel;
            Assert.NotNull(bec, "chiseledblock gives a BlockEntityChisel");
            // Named, not null. A null name is stored as a set-but-null "blockName" attribute, and
            // vanilla's own BlockMicroBlock.GetHeldItemName then splits it and throws - reachable
            // only by building one in code like this, but it makes the fixture useless.
            bec.WasPlaced(Sapi.World.GetBlock(new AssetLocation("game:rock-granite")), "Chiselled granite");
            bec.MarkDirty(true);

            ItemStack stack = Sapi.World.BlockAccessor.GetBlock(scratch).OnPickBlock(Sapi.World, scratch);
            World.SetBlock("game:air", scratch);
            return stack;
        }

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
    }
}