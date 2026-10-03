using System.Linq;
using System.Reflection;
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
    /// A burning bloomery's flames out of its chimney and sparks out of its front, with BloomerySparks
    /// on - and, with SparksSpreadFire as well, the fires they can start.
    ///
    /// Where a lick of flame or a spark lands is random, so the landing is driven directly at a chosen
    /// spot; what is under test is what it does, and when it is allowed to.
    /// </summary>
    public class BloomeryFire
    {
        // Standing on the ground, facing north, so its opening is to the south (+Z).
        static BlockPos BloomeryPos => P(8, 1, 8);
        static BlockPos ChimneyPos => P(8, 2, 8);

        static BlockEntityBloomery Bloomery => World.BE<BlockEntityBloomery>(BloomeryPos);
        static BEBehaviorBloomeryFire Fire => Bloomery.GetBehavior<BEBehaviorBloomeryFire>();

        // A beam beside the top of the chimney, with open space under it; and the spot straight over
        // the stack, where a roof built down onto it would be.
        static BlockPos BesideChimney => P(9, 2, 8);
        static BlockPos Beam => P(9, 3, 8);
        static BlockPos OverChimney => P(8, 3, 8);

        // Two blocks out in front of the opening, with a log beyond: where a spark comes down.
        static BlockPos InFront => P(8, 1, 10);
        static BlockPos LogInFront => P(8, 1, 11);

        const string LogCode = "game:log-placed-oak-ud";

        SparkSettings before;

        [BeforeEach]
        public async Task TakeTheSettingsAndKeepThePlayerAlive()
        {
            before = SparkSettings.Take();   // before any await, so nothing has had a chance to move them
            await TestLife.Alive();
        }

        [AfterEach]
        public void PutTheSettingsBack() => before?.Restore();

        static void Allow(bool bloomery, bool spread, bool world = true)
        {
            CrucibulumModSystem.Config.BloomerySparks = bloomery;
            CrucibulumModSystem.Config.SparksSpreadFire = spread;
            Sapi.World.Config.SetBool("allowFireSpread", world);
        }

        /// <summary>A bloomery with its chimney, loaded with ore and charcoal, lit if asked.</summary>
        static async Task ABloomery(bool lit = true)
        {
            World.SetBlock("game:bloomerybase-north", BloomeryPos);
            World.SetBlock("game:bloomerychimney", ChimneyPos);
            World.SetBlock(LogCode, Beam);
            World.SetBlock(LogCode, LogInFront);
            await Ticks(2);

            // Its slots are private and filling it as a player would means the player's hand; the
            // inventory is the same one either way.
            var inv = (InventoryBase)typeof(BlockEntityBloomery)
                .GetField("bloomeryInv", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(Bloomery);
            inv[0].Itemstack = World.Stack("game:charcoal", 4);
            inv[1].Itemstack = World.Stack("game:nugget-hematite", 4);

            if (lit) Assert.True(Bloomery.TryIgnite(), "the bloomery lit");
            Assert.True(Sapi.World.BlockAccessor.GetBlock(BesideChimney).Replaceable >= 6000, "open space beside the chimney");
            Assert.True(Sapi.World.BlockAccessor.GetBlock(InFront).Replaceable >= 6000, "open space in front");
        }

        static bool Lick(BlockPos at) => (bool)typeof(BEBehaviorBloomeryFire)
            .GetMethod("LickWithFlame", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Fire, new object[] { at });

        static void LickNearbyFuel(System.Random rand) => typeof(BEBehaviorBloomeryFire)
            .GetMethod("LickNearbyFuel", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Fire, new object[] { rand });

        static bool Spark(BlockPos at) => (bool)typeof(BEBehaviorBloomeryFire)
            .GetMethod("LandSpark", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Fire, new object[] { at });

        static string BlockAt(BlockPos pos) => Sapi.World.BlockAccessor.GetBlock(pos).Code?.Path ?? "air";

        /// <summary>A fire burning <paramref name="fuel"/>, in one of the spaces touching it, if there is one.</summary>
        static BEBehaviorBurning FireOn(BlockPos fuel)
        {
            foreach (BlockFacing facing in BlockFacing.ALLFACES)
            {
                var burning = Sapi.World.BlockAccessor.GetBlockEntity(fuel.AddCopy(facing))?.GetBehavior<BEBehaviorBurning>();
                if (burning?.IsBurning == true && burning.FuelPos.Equals(fuel)) return burning;
            }
            return null;
        }

        [VsTest]
        public async Task EveryBloomeryCarriesTheBehaviour()
        {
            // Fitted by an asset patch, which fails silently: a wrong path and the bloomery is simply
            // vanilla's.
            await ABloomery(lit: false);
            Assert.NotNull(Fire, "the bloomery has the flames-and-sparks behaviour");
        }

        [VsTest]
        public async Task ItIsOffByDefault()
        {
            await ABloomery();
            Assert.False(new CrucibulumConfig().BloomerySparks, "off out of the box");

            Allow(bloomery: new CrucibulumConfig().BloomerySparks, spread: true);
            Assert.False(Fire.Throwing, "a burning bloomery throws nothing at the default");
            Assert.False(Lick(Beam), "and its chimney lights nothing");
            Assert.Null(FireOn(Beam), "no fire");
        }

        [VsTest]
        public async Task TheChimneysFlamesLightABeamBesideIt()
        {
            await ABloomery();
            Allow(bloomery: true, spread: true);

            Assert.True(Fire.Throwing, "a burning bloomery throws, with the option on");
            Assert.True(Lick(Beam), "the flames caught");
            Assert.NotNull(FireOn(Beam), "a fire against the beam");
        }

        [VsTest]
        public async Task ARoofStraightOverTheChimneyCatches()
        {
            // Nothing a spark could land in has the roof beside it - straight under it is the chimney -
            // so it is the flames reaching the roof itself that set it alight, from the side.
            await ABloomery();
            World.SetBlock(LogCode, OverChimney);
            await Ticks(1);
            Allow(bloomery: true, spread: true);

            Assert.True(Lick(OverChimney), "the flames caught the roof");
            var burning = FireOn(OverChimney);
            Assert.NotNull(burning, "a fire against the roof");
            Assert.Equal(OverChimney.Y, burning.FirePos.Y, "beside it, the chimney being in the way below");
        }

        [VsTest(TimeoutMs = 120000)]
        public async Task LeftToBurnARoofOverTheStackCatchesOnItsOwn()
        {
            // The whole way through, by the bloomery's own tick and its own dice: nothing driven
            // directly. One chance a second, the shortest gap the setting allows, so that what takes a
            // minute or two at the default is over in seconds here.
            await ABloomery();
            World.SetBlock("game:air", Beam);
            World.SetBlock(LogCode, OverChimney);
            await Ticks(1);
            Allow(bloomery: true, spread: true);
            CrucibulumModSystem.Config.SparkLandingSeconds = 1;

            long start = Sapi.World.ElapsedMilliseconds;
            await Until(() => FireOn(OverChimney) != null, 1800, "the roof over the stack to catch");
            Log($"  caught after {(Sapi.World.ElapsedMilliseconds - start) / 1000.0:0.0}s");
        }

        [VsTest]
        public async Task OneChanceIsEnoughForWoodAnywhereAroundTheTop()
        {
            // The flames try everything they reach and light what burns, so wood over the stack,
            // beside its mouth, or beside it just above, each catches at the first chance - whichever
            // way the dice fall. A flame spending its chance on one spot of open air is what made a
            // roof over the stack take minutes.
            //
            // Spread is only on for the attempt itself, with no tick in between: left on while the wood
            // goes in, the bloomery's own tick could light it first, and the attempt under test would
            // pass without having done anything.
            await ABloomery();
            World.SetBlock("game:air", Beam);

            BlockPos[] places = { OverChimney, BesideChimney, Beam };
            for (int seed = 0; seed < places.Length * 3; seed++)
            {
                BlockPos wood = places[seed % places.Length];
                Allow(bloomery: true, spread: false);
                World.SetBlock(LogCode, wood);
                await Ticks(1);

                Allow(bloomery: true, spread: true);
                Assert.Null(FireOn(wood), "nothing alight before the attempt");
                LickNearbyFuel(new System.Random(seed));
                Assert.NotNull(FireOn(wood), $"wood at {wood} caught at the first chance (seed {seed})");
                Allow(bloomery: true, spread: false);

                // Clear the wood and the fire against it before the next round.
                World.SetBlock("game:air", wood);
                foreach (BlockFacing f in BlockFacing.ALLFACES)
                {
                    BlockPos next = wood.AddCopy(f);
                    if (!next.Equals(ChimneyPos) && !next.Equals(BloomeryPos)) World.SetBlock("game:air", next);
                }
                await Ticks(1);
            }
        }

        [VsTest]
        public async Task OneChanceLightsOneThingHoweverMuchIsThere()
        {
            // A chance at a fire is one fire: with wood all round the top of the stack, the flames light
            // the first piece that catches and leave the rest for the chances after. Spread on only for
            // the attempt, as above.
            await ABloomery();
            BlockPos[] wood = { OverChimney, Beam, BesideChimney, P(7, 2, 8), P(8, 2, 9) };
            foreach (BlockPos w in wood) World.SetBlock(LogCode, w);
            await Ticks(1);

            Allow(bloomery: true, spread: true);
            LickNearbyFuel(new System.Random(0));
            int burning = wood.Count(w => FireOn(w) != null);
            Allow(bloomery: true, spread: false);

            Assert.Equal(1, burning, "one piece of five alight after one chance");
        }

        [VsTest]
        public async Task AStoneCapOnTheChimneyHoldsTheFlamesIn()
        {
            await ABloomery();
            World.SetBlock("game:rock-granite", OverChimney);
            await Ticks(1);
            Allow(bloomery: true, spread: true);

            Assert.False(Lick(Beam), "flames through a block of granite");
            Assert.Null(FireOn(Beam), "no fire on the beam");
        }

        [VsTest]
        public async Task FlamesLightOnlyWhatBurns()
        {
            await ABloomery();
            Allow(bloomery: true, spread: true);

            Assert.False(Lick(BesideChimney), "flames reaching open air");
            Assert.Equal("air", BlockAt(BesideChimney), "start nothing there");
        }

        [VsTest]
        public async Task TheFlamesKeepToTheBloomerysClaim()
        {
            await ABloomery();
            Allow(bloomery: true, spread: true);

            var claim = new LandClaim { OwnedByPlayerUid = "someone-else", LastKnownOwnerName = "someone else", ProtectionLevel = 1 };
            claim.Areas.Add(new Cuboidi(Beam.X, Beam.Y, Beam.Z, Beam.X + 1, Beam.Y + 1, Beam.Z + 1));
            Sapi.World.Claims.Add(claim);
            try
            {
                Assert.False(Lick(Beam), "a beam in a claim the bloomery is not part of");
                Assert.Null(FireOn(Beam), "no fire on it");
            }
            finally
            {
                Sapi.World.Claims.Remove(claim);
            }
        }

        [VsTest]
        public async Task ASparkFromTheFrontLightsWhatItLandsBy()
        {
            await ABloomery();
            Allow(bloomery: true, spread: true);

            Assert.True(Spark(InFront), "the spark caught");
            Assert.Equal("fire", BlockAt(InFront), "fire in front of the bloomery");
        }

        [VsTest]
        public async Task WithoutSparksSpreadFireTheyAreOnlyForShow()
        {
            await ABloomery();
            Allow(bloomery: true, spread: false);

            Assert.True(Fire.Throwing, "still throwing");
            Assert.False(Lick(Beam), "the flames light nothing");
            Assert.False(Spark(InFront), "nor the sparks");
            Assert.Null(FireOn(Beam), "no fire by the chimney");
            Assert.Equal("air", BlockAt(InFront), "none in front");
        }

        [VsTest]
        public async Task AColdBloomeryThrowsNothing()
        {
            await ABloomery(lit: false);
            Allow(bloomery: true, spread: true);

            Assert.False(Fire.Throwing, "an unlit bloomery");
            Assert.False(Lick(Beam), "lights nothing");
            Assert.Null(FireOn(Beam), "no fire");
        }

        [VsTest]
        public async Task SparksKeepToTheBloomerysClaim()
        {
            // The same boundary as the crucible's, by way of the same code - this is the guard that it
            // is the bloomery's position being asked about.
            await ABloomery();
            Allow(bloomery: true, spread: true);

            var claim = new LandClaim { OwnedByPlayerUid = "someone-else", LastKnownOwnerName = "someone else", ProtectionLevel = 1 };
            claim.Areas.Add(new Cuboidi(InFront.X, InFront.Y, InFront.Z, LogInFront.X + 1, LogInFront.Y + 1, LogInFront.Z + 1));
            Sapi.World.Claims.Add(claim);
            try
            {
                Assert.False(Spark(InFront), "a spark into a claim the bloomery is not part of");
                Assert.Equal("air", BlockAt(InFront), "no fire");
            }
            finally
            {
                Sapi.World.Claims.Remove(claim);
            }
        }

        [VsTest(TimeoutMs = 60000), RequiresClient]
        public async Task ItThrowsFlamesFromTheTopAndSparksFromTheFront()
        {
            await ABloomery();
            Allow(bloomery: true, spread: false);

            // Vanilla turns terrain collision off on the fire block's shared flame template the first
            // time a burning block spawns from it. Done here, before the chimney's are first cloned,
            // so the clones cannot have had it handed down.
            var fire = Sapi.World.GetBlock(new AssetLocation("fire")).ParticleProperties;
            bool[] was = { fire[0].TerrainCollision, fire[1].TerrainCollision };
            fire[0].TerrainCollision = fire[1].TerrainCollision = false;
            foreach (string name in new[] { "chimneyFlames", "chimneyEmbers" })
                typeof(BEBehaviorBloomeryFire).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);

            using var particles = new ParticleWatch(BloomeryPos);
            try
            {
                await OnClient();
                await Until(() => particles.Flames(onServer: false).Count > 5 && particles.Sparks(onServer: false).Count > 2, 60,
                    "flames and sparks on the client");

                var flames = particles.Flames(onServer: false);
                var embers = particles.Embers(onServer: false);
                var sparks = particles.Sparks(onServer: false);

                Assert.True(flames.TrueForAll(f => f.Y >= ChimneyPos.Y + 0.6), "the flames come out of the chimney top");
                Assert.True(flames.TrueForAll(f => f.Collides) && embers.TrueForAll(e => e.Collides),
                    "and stop at what is over it, whatever the fire block's own template has been left as");
                Assert.True(sparks.TrueForAll(s => s.Y < BloomeryPos.Y + 0.5), "the sparks out of the opening at its foot");
                Assert.True(sparks.TrueForAll(s => s.At.Z > BloomeryPos.Z + 0.5),
                    "out of its south side - against its facing, where vanilla throws its own");

                // Heavy at the top, moderate below: over the same stretch, the flames alone come to
                // more than twice the sparks out of the front, and the embers add to the top's share.
                double flameCount = flames.Sum(f => (double)f.MinQuantity);
                double emberCount = embers.Sum(e => (double)e.MinQuantity);
                double sparkCount = sparks.Sum(s => s.MinQuantity + s.AddQuantity / 2.0);
                Log($"  flames thrown: {flameCount:0}; embers: {emberCount:0}; sparks: {sparkCount:0}");
                Assert.Greater(flameCount, sparkCount * 2, "far more flames out of the chimney than sparks out of the front");
                Assert.Greater(emberCount, 0.0, "with embers among them");

                CrucibulumModSystem.Config.BloomerySparks = false;
                particles.Clear();
                await Ticks(20);
                Assert.Equal(0, particles.Flames(onServer: false).Count + particles.Embers(onServer: false).Count, "nothing out of the chimney with the option off");
                Assert.Equal(0, particles.Sparks(onServer: false).Count, "nor the front");
            }
            finally
            {
                fire[0].TerrainCollision = was[0];
                fire[1].TerrainCollision = was[1];
            }
        }
    }
}
