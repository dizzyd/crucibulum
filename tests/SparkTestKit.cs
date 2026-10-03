using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crucibulum;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using Vintagestory.Server;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>Every setting the spark tests turn, as it was before a test turned it.</summary>
    sealed class SparkSettings
    {
        bool sparksSpreadFire, bloomerySparks, allowFireSpread;
        float meltSparkRate, meltDoneSparkBurst, sparkLandingSeconds;

        public static SparkSettings Take() => new()
        {
            sparksSpreadFire = CrucibulumModSystem.Config.SparksSpreadFire,
            bloomerySparks = CrucibulumModSystem.Config.BloomerySparks,
            meltSparkRate = CrucibulumModSystem.Config.MeltSparkRate,
            meltDoneSparkBurst = CrucibulumModSystem.Config.MeltDoneSparkBurst,
            sparkLandingSeconds = CrucibulumModSystem.Config.SparkLandingSeconds,
            allowFireSpread = Sapi.World.Config.GetBool("allowFireSpread"),
        };

        public void Restore()
        {
            CrucibulumModSystem.Config.SparksSpreadFire = sparksSpreadFire;
            CrucibulumModSystem.Config.BloomerySparks = bloomerySparks;
            CrucibulumModSystem.Config.MeltSparkRate = meltSparkRate;
            CrucibulumModSystem.Config.MeltDoneSparkBurst = meltDoneSparkBurst;
            CrucibulumModSystem.Config.SparkLandingSeconds = sparkLandingSeconds;
            Sapi.World.Config.SetBool("allowFireSpread", allowFireSpread);
        }
    }

    /// <summary>
    /// The mod's own particles at one block's column, on either side, from the game's own
    /// SpawnParticles. Test-only: a Harmony prefix for as long as this is held.
    ///
    /// The blocks these watch throw particles of their own from the same place - the forge sparks and
    /// smokes, the bloomery sparks - so position is not enough. The mod's sparks and smoke are told
    /// apart by colour: they are clones of vanilla's big-metal-spark and held-smoke templates, which
    /// neither block's own use. Its flames are the only advanced particles thrown from there; a
    /// burning block's real fire is spawned through the async particle manager, which this does not see.
    /// Of those, flames are the fire block's quads and embers its cubes.
    /// </summary>
    sealed class ParticleWatch : IDisposable
    {
        public enum Kind { Spark, Smoke, Flame, Ember }

        public sealed record Spawn(bool OnServer, Kind Kind, Vec3d At, float MinQuantity, float AddQuantity, float AddVelocityY, bool Collides)
        {
            public double Y => At.Y;
        }

        static readonly object gate = new();
        static readonly List<Spawn> seen = new();
        static BlockPos watching;

        readonly Harmony harmony = new("crucibulum.tests.particlewatch");

        public ParticleWatch(BlockPos pos)
        {
            lock (gate) { seen.Clear(); watching = pos.Copy(); }
            var prefix = new HarmonyMethod(typeof(ParticleWatch).GetMethod(nameof(Saw), BindingFlags.NonPublic | BindingFlags.Static));
            harmony.Patch(AccessTools.Method(typeof(ClientMain), nameof(ClientMain.SpawnParticles), new[] { typeof(IParticlePropertiesProvider), typeof(IPlayer) }), prefix);
            harmony.Patch(AccessTools.Method(typeof(ServerMain), nameof(ServerMain.SpawnParticles), new[] { typeof(IParticlePropertiesProvider), typeof(IPlayer) }), prefix);
        }

        static void Saw(object __instance, IParticlePropertiesProvider __0)
        {
            bool onServer = __instance is ServerMain;
            Spawn spawn = __0 switch
            {
                SimpleParticleProperties p when p.Color == BlockSmeltedContainer.bigMetalSparks.Color =>
                    new Spawn(onServer, Kind.Spark, p.MinPos.Clone(), p.MinQuantity, p.AddQuantity, p.AddVelocity.Y, p.WithTerrainCollision),
                SimpleParticleProperties p when p.Color == BlockSmeltedContainer.smokeHeld.Color =>
                    new Spawn(onServer, Kind.Smoke, p.MinPos.Clone(), p.MinQuantity, p.AddQuantity, p.AddVelocity.Y, p.WithTerrainCollision),
                // The fire block's flames are quads; its embers are little cubes.
                AdvancedParticleProperties a =>
                    new Spawn(onServer, a.ParticleModel == EnumParticleModel.Quad ? Kind.Flame : Kind.Ember,
                        a.basePos.Clone(), a.Quantity.avg, a.Quantity.var, a.Velocity[1].avg, a.TerrainCollision),
                _ => null,
            };
            if (spawn == null) return;

            double x = __0 is AdvancedParticleProperties ap ? ap.basePos.X : ((SimpleParticleProperties)__0).MinPos.X;
            double z = __0 is AdvancedParticleProperties aq ? aq.basePos.Z : ((SimpleParticleProperties)__0).MinPos.Z;

            lock (gate)
            {
                if (watching == null) return;
                if (Math.Floor(x) != watching.X || Math.Floor(z) != watching.Z) return;
                seen.Add(spawn);
            }
        }

        public List<Spawn> Of(Kind kind, bool onServer) { lock (gate) return seen.Where(s => s.OnServer == onServer && s.Kind == kind).ToList(); }
        public List<Spawn> Sparks(bool onServer) => Of(Kind.Spark, onServer);
        public List<Spawn> Smoke(bool onServer) => Of(Kind.Smoke, onServer);
        public List<Spawn> Flames(bool onServer) => Of(Kind.Flame, onServer);
        public List<Spawn> Embers(bool onServer) => Of(Kind.Ember, onServer);
        public void Clear() { lock (gate) seen.Clear(); }

        public void Dispose()
        {
            harmony.UnpatchAll(harmony.Id);
            lock (gate) { watching = null; seen.Clear(); }
        }
    }
}
