// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Crucibulum;

/// <summary>
/// Flames out of a burning bloomery's chimney and sparks out of its front, with
/// <see cref="CrucibulumConfig.BloomerySparks"/> on - and, with
/// <see cref="CrucibulumConfig.SparksSpreadFire"/> as well, the fires they start. Fitted to vanilla's
/// bloomery by an asset patch, so the bloomery itself is untouched. The client throws the particles,
/// the server decides what catches; both read the config live, and do nothing with the option off.
/// </summary>
public class BEBehaviorBloomeryFire : BlockEntityBehavior
{
    public const string Name = "CrucibulumBloomeryFire";

    // The fire block's particle definitions: embers, flames, smoke - in that order.
    private const int FireEmbers = 0;
    private const int FireFlames = 1;

    private static AdvancedParticleProperties chimneyFlames;
    private static AdvancedParticleProperties chimneyEmbers;
    private static SimpleParticleProperties frontSparks;

    /// <summary>The top of the chimney: vanilla's is ten sixteenths tall.</summary>
    private const double ChimneyTop = 1 + 10 / 16.0;

    /// <summary>
    /// Of the chances to start a fire, the share that are the chimney's flames rather than a spark
    /// from the front. The flames are where most of the fire is.
    /// </summary>
    private const double FlameShare = 2 / 3.0;

    private BlockFacing front;
    private Vec3d chimneyMouth;
    private Vec3d opening;
    private BlockPos[] sourceCells;

    public BEBehaviorBloomeryFire(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);

        // Vanilla's own sparks go out against the bloomery's facing, so the opening faces the other way.
        front = (BlockFacing.FromCode(Blockentity.Block.LastCodePart()) ?? BlockFacing.NORTH).Opposite;
        Vec3i n = front.Normali;
        chimneyMouth = new Vec3d(Pos.X + 0.5, Pos.Y + ChimneyTop, Pos.Z + 0.5);
        opening = new Vec3d(Pos.X + 0.5 + n.X * 7 / 16.0, Pos.Y + 2 / 16.0, Pos.Z + 0.5 + n.Z * 7 / 16.0);

        // The bloomery and its chimney: what a flame or spark starts inside, and so may pass through.
        sourceCells = new[] { Pos.Copy(), Pos.UpCopy() };

        if (api.Side == EnumAppSide.Client) Blockentity.RegisterGameTickListener(OnClientTick, 100);
        else Blockentity.RegisterGameTickListener(OnServerTick, 200);
    }

    /// <summary>Burning, with its chimney in place, and the option on.</summary>
    public bool Throwing =>
        CrucibulumModSystem.Config.BloomerySparks
        && Blockentity is BlockEntityBloomery { IsBurning: true }
        && Api.World.BlockAccessor.GetBlock(Pos.UpCopy()).Code?.Path.Contains("bloomerychimney") == true;

    private void OnClientTick(float dt)
    {
        if (!Throwing) return;

        SpawnChimneyFlames();
        SpawnFrontSparks();
    }

    /// <summary>
    /// The fire block's own flames and embers, cloned once - the originals are shared by every fire
    /// in the world - and made bigger, denser and thrown harder, as a column out of the stack.
    ///
    /// Terrain collision is set rather than inherited: vanilla turns it off on its shared template
    /// the first time a burning block spawns from it, so a clone's would otherwise depend on whether
    /// some fire had burned yet. On, so a roof over the stack stops the flames instead of their
    /// showing through it. They start at the chimney's top or just above, never inside it.
    /// </summary>
    private void SpawnChimneyFlames()
    {
        if (chimneyFlames == null)
        {
            AdvancedParticleProperties[] fire = Api.World.GetBlock(new AssetLocation("fire"))?.ParticleProperties;
            if (fire == null || fire.Length <= FireFlames) return;

            chimneyFlames = fire[FireFlames].Clone();
            chimneyFlames.Quantity = NatFloat.createUniform(6, 2);
            chimneyFlames.Size = NatFloat.createUniform(0.35f, 0.1f);
            chimneyFlames.Velocity = new[] { NatFloat.createUniform(0, 0.1f), NatFloat.createUniform(1.6f, 0.5f), NatFloat.createUniform(0, 0.1f) };
            chimneyFlames.PosOffset = new[] { NatFloat.createUniform(0, 0.3f), NatFloat.createUniform(0.05f, 0.05f), NatFloat.createUniform(0, 0.3f) };
            chimneyFlames.LifeLength = NatFloat.createUniform(0.6f, 0.15f);
            chimneyFlames.TerrainCollision = true;

            chimneyEmbers = fire[FireEmbers].Clone();
            chimneyEmbers.Quantity = NatFloat.createUniform(1.5f, 1);
            chimneyEmbers.Velocity = new[] { NatFloat.createUniform(0, 0.4f), NatFloat.createUniform(2.5f, 1f), NatFloat.createUniform(0, 0.4f) };
            chimneyEmbers.PosOffset = new[] { NatFloat.createUniform(0, 0.3f), NatFloat.createUniform(0.05f, 0.05f), NatFloat.createUniform(0, 0.3f) };
            chimneyEmbers.TerrainCollision = true;
        }

        chimneyFlames.basePos.Set(chimneyMouth);
        Api.World.SpawnParticles(chimneyFlames);

        chimneyEmbers.basePos.Set(chimneyMouth);
        Api.World.SpawnParticles(chimneyEmbers);
    }

    /// <summary>
    /// The sparks a poured crucible throws, as a steady stream out of the opening at the foot - from
    /// across the middle of it, between the walls either side.
    /// </summary>
    private void SpawnFrontSparks()
    {
        Vec3i n = front.Normali;
        int ax = Math.Abs(n.Z), az = Math.Abs(n.X);   // across the opening

        SimpleParticleProperties sparks = frontSparks ??= BlockSmeltedContainer.bigMetalSparks.Clone(Api.World);
        sparks.MinQuantity = 1;
        sparks.AddQuantity = 2;
        sparks.MinPos.Set(opening.X - 2 / 16.0 * ax, opening.Y, opening.Z - 2 / 16.0 * az);
        sparks.AddPos.Set(4 / 16.0 * ax, 0.1, 4 / 16.0 * az);
        sparks.MinVelocity.Set(n.X * 1f - 0.5f * ax, 0.5f, n.Z * 1f - 0.5f * az);
        sparks.AddVelocity.Set(n.X * 1.5f + 1f * ax, 1.5f, n.Z * 1.5f + 1f * az);
        sparks.MinSize = sparks.MaxSize = 0.25f;
        sparks.LifeLength = 0.5f;
        sparks.GravityEffect = 1f;
        sparks.Bounciness = 0.3f;
        sparks.WithTerrainCollision = true;
        sparks.VertexFlags = 128;
        Api.World.SpawnParticles(sparks);
    }

    /// <summary>
    /// One chance at a fire per bloomery, at <see cref="CrucibulumConfig.SparkLandingSeconds"/> on
    /// average as for a crucible, which then goes to the flames or a spark.
    /// </summary>
    private void OnServerTick(float dt)
    {
        if (!CrucibulumModSystem.Config.SparksSpreadFire || !Throwing) return;

        Random rand = Api.World.Rand;
        if (rand.NextDouble() >= SparkFire.LandingChance(dt)) return;

        if (rand.NextDouble() < FlameShare) LickNearbyFuel(rand);
        else LandSpark(SparkLanding(rand));
    }

    /// <summary>
    /// The chimney's flames finding something to burn: they try everything they reach, in no
    /// particular order, and set light to the first that catches - one fire for the chance, however
    /// much is there to burn. A flame licks round whatever is there rather than landing on one spot,
    /// so a single piece of wood anywhere around the top of the stack catches at the first chance,
    /// where picking one spot at random spent most chances on open air.
    /// </summary>
    private void LickNearbyFuel(Random rand)
    {
        flameReach ??= FlameReach();
        for (int i = flameReach.Length - 1; i > 0; i--)
        {
            int j = rand.Next(i + 1);
            (flameReach[i], flameReach[j]) = (flameReach[j], flameReach[i]);
        }

        foreach (BlockPos at in flameReach)
        {
            if (LickWithFlame(at)) return;
        }
    }

    private BlockPos[] flameReach;

    /// <summary>
    /// What the chimney's flames reach: the three-by-three around the top of the stack, from the
    /// height of its mouth up two blocks - as high as the flames and embers rise. The column itself
    /// goes straight up, but the heat off it puts everything that close at risk: a roof laid over the
    /// stack or with a gap under it, a beam or a wall beside it, a corner diagonally off it.
    /// </summary>
    private BlockPos[] FlameReach()
    {
        BlockPos chimney = Pos.UpCopy();
        var reach = new BlockPos[26];
        int n = 0;
        for (int dy = 0; dy <= 2; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;   // the chimney itself
                    reach[n++] = chimney.AddCopy(dx, dy, dz);
                }
            }
        }
        return reach;
    }

    /// <summary>Somewhere in front of the opening, a block or two out, at about floor height.</summary>
    private BlockPos SparkLanding(Random rand)
    {
        Vec3i n = front.Normali;
        int reach = rand.Next(1, 3);
        int across = rand.Next(-1, 2);
        return Pos.AddCopy(n.X * reach + Math.Abs(n.Z) * across, rand.Next(-1, 1), n.Z * reach + Math.Abs(n.X) * across);
    }

    /// <summary>The chimney's flames reaching the block at <paramref name="at"/>. True if anything caught.</summary>
    protected bool LickWithFlame(BlockPos at) =>
        Throwing && SparkFire.TryLightFuel(Api, Pos, sourceCells, chimneyMouth, at, "the bloomery's chimney");

    /// <summary>A spark from the opening coming down at <paramref name="at"/>. True if anything caught.</summary>
    protected bool LandSpark(BlockPos at) =>
        Throwing && SparkFire.TryLight(Api, Pos, sourceCells, opening, at, "the bloomery");
}
