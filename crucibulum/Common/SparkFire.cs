// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Crucibulum;

/// <summary>
/// What a spark or a lick of flame from a hot workshop block may set light to, for the crucible's
/// sparks and the bloomery's flames and sparks alike. Only with
/// <see cref="CrucibulumConfig.SparksSpreadFire"/> on and the world's own fire spread allowed.
///
/// Two ways in, because the two things meet fuel differently. A spark comes down somewhere and
/// starts a fire there if something beside it burns (<see cref="TryLight"/>). A flame reaches the
/// burning thing itself, so fire starts beside whatever it touched (<see cref="TryLightFuel"/>) -
/// which is how a roof built straight down onto a chimney catches, where nothing a spark could land
/// in would ever have it beside it.
///
/// Either way the spark or flame is held to its source's claim boundary, for where it reaches and
/// for the fuel it would set burning, and anything solid between where it starts and where it
/// reaches stops it. A pile of firewood or coal it reaches is lit directly, as vanilla's fire lights
/// one. Only the spark or flame itself: a fire it starts is ordinary vanilla fire from then on, and
/// spreads as one.
/// </summary>
public static class SparkFire
{
    /// <summary>
    /// The odds that a spark comes down within the next <paramref name="dt"/> seconds, from the
    /// configured average gap between them.
    /// </summary>
    public static double LandingChance(float dt) =>
        Math.Min(1, dt / Math.Max(1f, CrucibulumModSystem.Config.SparkLandingSeconds));

    /// <summary>
    /// A spark from the block at <paramref name="source"/>, starting at <paramref name="from"/>,
    /// coming down in the open space at <paramref name="at"/>. <paramref name="sourceCells"/> are the
    /// cells the source itself occupies, which the line from <paramref name="from"/> may pass through.
    /// <paramref name="what"/> names the source in the audit log. True if anything caught.
    /// </summary>
    public static bool TryLight(ICoreAPI api, BlockPos source, BlockPos[] sourceCells, Vec3d from, BlockPos at, string what)
    {
        if (!Allowed(api)) return false;
        if (!IsWithinClaimBoundary(api, source, at) || !CanReach(api.World.BlockAccessor, sourceCells, from, at)) return false;
        if (TryLightPile(api, at, source, what)) return true;
        if (!IsOpen(api.World.BlockAccessor, at)) return false;

        foreach (BlockFacing facing in BlockFacing.ALLFACES)
        {
            BlockPos fuel = at.AddCopy(facing);
            if (IsFuel(api, source, fuel)) return StartFire(api, at, fuel, source, what);
        }
        return false;
    }

    /// <summary>
    /// A flame from the block at <paramref name="source"/>, starting at <paramref name="from"/>,
    /// reaching the block at <paramref name="fuel"/>. If that burns, fire starts in whichever open
    /// space touching it is nearest the flame - on the side the flame came from, as near as the shape
    /// of things allows. Nothing is asked of the line to that space: the flame has reached the fuel,
    /// and the fire stands against it. True if anything caught.
    /// </summary>
    public static bool TryLightFuel(ICoreAPI api, BlockPos source, BlockPos[] sourceCells, Vec3d from, BlockPos fuel, string what)
    {
        if (!Allowed(api)) return false;
        if (!IsWithinClaimBoundary(api, source, fuel) || !CanReach(api.World.BlockAccessor, sourceCells, from, fuel)) return false;
        if (TryLightPile(api, fuel, source, what)) return true;
        if (!IsFuel(api, source, fuel)) return false;

        IBlockAccessor ba = api.World.BlockAccessor;
        BlockPos fireAt = BlockFacing.ALLFACES
            .Select(facing => fuel.AddCopy(facing))
            .Where(cell => !sourceCells.Any(c => c.Equals(cell)) && IsOpen(ba, cell) && IsWithinClaimBoundary(api, source, cell))
            .OrderBy(cell => from.SquareDistanceTo(cell.X + 0.5, cell.Y + 0.5, cell.Z + 0.5))
            .FirstOrDefault();

        return fireAt != null && StartFire(api, fireAt, fuel, source, what);
    }

    private static bool Allowed(ICoreAPI api) =>
        CrucibulumModSystem.Config.SparksSpreadFire && api.World.Config.GetBool("allowFireSpread");

    /// <summary>
    /// Lights a pile of coal or firewood at <paramref name="at"/>, where it lies, as vanilla's fire
    /// lights one. A coal pile is not open space, so the fire-beside-fuel route would never reach it.
    /// </summary>
    private static bool TryLightPile(ICoreAPI api, BlockPos at, BlockPos source, string what)
    {
        switch (api.World.BlockAccessor.GetBlockEntity(at))
        {
            case BlockEntityCoalPile coal when !coal.IsBurning && coal.CanIgnite:
                coal.TryIgnite();
                api.World.Logger.Audit("A spark from {0} at {1} lit the coal pile at {2}.", what, source, at);
                return true;

            case BlockEntityGroundStorage pile when !pile.IsBurning && pile.CanIgnite:
                pile.TryIgnite();
                api.World.Logger.Audit("A spark from {0} at {1} lit the pile at {2}.", what, source, at);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Somewhere vanilla's fire could stand: replaceable, dry, and not already alight.</summary>
    private static bool IsOpen(IBlockAccessor ba, BlockPos pos) =>
        ba.GetBlock(pos).Replaceable >= 6000
        && ba.GetBlock(pos, BlockLayersAccess.Fluid).Id == 0
        && ba.GetBlockEntity(pos)?.GetBehavior<BEBehaviorBurning>()?.IsBurning != true;

    /// <summary>A block that burns, is not reinforced, is inside the boundary, and is not already burning.</summary>
    private static bool IsFuel(ICoreAPI api, BlockPos source, BlockPos pos)
    {
        if (!Burns(api.World, pos)) return false;
        if (api.ModLoader.GetModSystem<ModSystemBlockReinforcement>()?.IsReinforced(pos) == true) return false;
        if (!IsWithinClaimBoundary(api, source, pos)) return false;
        return api.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorBurning>() == null;
    }

    private static bool StartFire(ICoreAPI api, BlockPos at, BlockPos fuel, BlockPos source, string what)
    {
        Block fire = api.World.GetBlock(new AssetLocation("fire"));
        if (fire == null) return false;

        IBlockAccessor ba = api.World.BlockAccessor;
        ba.SetBlock(fire.BlockId, at);
        ba.GetBlockEntity(at)?.GetBehavior<BEBehaviorBurning>()?.OnFirePlaced(at, fuel, null);
        api.World.Logger.Audit("A spark from {0} at {1} started a fire at {2}.", what, source, at);
        return true;
    }

    /// <summary>
    /// The same question BEBehaviorBurning asks of a block before it will burn it, in the same order:
    /// a block's combustible properties decide if it has any, and only a block without them is asked
    /// as an ICombustible.
    /// </summary>
    private static bool Burns(IWorldAccessor world, BlockPos pos)
    {
        Block block = world.BlockAccessor.GetBlock(pos);
        CombustibleProperties props = block.GetCombustibleProperties(world, null, pos);
        if (props != null) return props.BurnDuration > 0;
        return block.GetInterface<ICombustible>(world, pos)?.GetBurnDuration(world, pos) > 0;
    }

    /// <summary>
    /// Whether a spark could fly from <paramref name="from"/> to <paramref name="at"/>: the straight
    /// line between them passes through no block's collision boxes. A straight line rather than the
    /// arc a spark falling to a spot below where it started would really follow, so a wall too low to
    /// be in the way of the arc can still stop one - erring the way that lets a stone enclosure
    /// fireproof a workshop.
    ///
    /// Every cell the line crosses is checked, the one it starts in included, so a block set right
    /// over the source stops it too. Only the source's own cells and the landing spot are left out.
    /// </summary>
    private static bool CanReach(IBlockAccessor ba, BlockPos[] sourceCells, Vec3d from, BlockPos at)
    {
        Vec3d to = new(at.X + 0.5, at.Y + 0.5, at.Z + 0.5);
        Vec3d dir = to - from;

        // Walk the cells the segment crosses in order, stepping across whichever cell face it
        // reaches first (Amanatides and Woo).
        int x = (int)Math.Floor(from.X), y = (int)Math.Floor(from.Y), z = (int)Math.Floor(from.Z);
        int stepX = Math.Sign(dir.X), stepY = Math.Sign(dir.Y), stepZ = Math.Sign(dir.Z);
        double tMaxX = FirstCrossing(from.X, dir.X, x), tMaxY = FirstCrossing(from.Y, dir.Y, y), tMaxZ = FirstCrossing(from.Z, dir.Z, z);
        double tDeltaX = dir.X == 0 ? double.PositiveInfinity : Math.Abs(1 / dir.X);
        double tDeltaY = dir.Y == 0 ? double.PositiveInfinity : Math.Abs(1 / dir.Y);
        double tDeltaZ = dir.Z == 0 ? double.PositiveInfinity : Math.Abs(1 / dir.Z);

        BlockPos cell = new(at.dimension);
        while (true)
        {
            cell.Set(x, y, z);
            if (cell.Equals(at)) return true;
            if (!sourceCells.Any(c => c.Equals(cell)) && SegmentHitsBlock(ba, cell, from, dir)) return false;

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { if (tMaxX > 1) return true; x += stepX; tMaxX += tDeltaX; }
            else if (tMaxY <= tMaxZ) { if (tMaxY > 1) return true; y += stepY; tMaxY += tDeltaY; }
            else { if (tMaxZ > 1) return true; z += stepZ; tMaxZ += tDeltaZ; }
        }
    }

    /// <summary>
    /// The land a spark from <paramref name="source"/> may touch. Unclaimed land always; claimed land
    /// only where every claim covering it also covers the source. It is the claims themselves that
    /// are compared, not their owners: a neighbouring claim belonging to the same player is still
    /// somewhere else. A spark has no player behind it to check permissions against, so this is the
    /// whole test.
    /// </summary>
    private static bool IsWithinClaimBoundary(ICoreAPI api, BlockPos source, BlockPos pos)
    {
        LandClaim[] there = api.World.Claims.Get(pos);
        if (there == null || there.Length == 0) return true;

        LandClaim[] here = api.World.Claims.Get(source) ?? Array.Empty<LandClaim>();
        return there.All(claim => here.Contains(claim));
    }

    /// <summary>The fraction of the way along a segment at which it first leaves cell <paramref name="cell"/> on one axis.</summary>
    private static double FirstCrossing(double origin, double delta, int cell)
    {
        if (delta == 0) return double.PositiveInfinity;
        double boundary = delta > 0 ? cell + 1 : cell;
        return (boundary - origin) / delta;
    }

    /// <summary>
    /// Whether the segment from <paramref name="from"/> along <paramref name="dir"/> passes through
    /// any of the collision boxes of the block in <paramref name="cell"/>. Merely touching a face
    /// does not count.
    /// </summary>
    private static bool SegmentHitsBlock(IBlockAccessor ba, BlockPos cell, Vec3d from, Vec3d dir)
    {
        Cuboidf[] boxes = ba.GetBlock(cell).GetCollisionBoxes(ba, cell);
        if (boxes == null) return false;

        foreach (Cuboidf box in boxes)
        {
            double enter = 0, leave = 1;
            if (!Slab(from.X, dir.X, cell.X + box.X1, cell.X + box.X2, ref enter, ref leave)) continue;
            if (!Slab(from.Y, dir.Y, cell.Y + box.Y1, cell.Y + box.Y2, ref enter, ref leave)) continue;
            if (!Slab(from.Z, dir.Z, cell.Z + box.Z1, cell.Z + box.Z2, ref enter, ref leave)) continue;
            if (enter < leave) return true;
        }
        return false;
    }

    /// <summary>Narrows [enter, leave] to where the segment lies between min and max on one axis.</summary>
    private static bool Slab(double origin, double delta, double min, double max, ref double enter, ref double leave)
    {
        if (delta == 0) return origin > min && origin < max;

        double t1 = (min - origin) / delta, t2 = (max - origin) / delta;
        if (t1 > t2) (t1, t2) = (t2, t1);
        enter = Math.Max(enter, t1);
        leave = Math.Min(leave, t2);
        return enter < leave;
    }
}
