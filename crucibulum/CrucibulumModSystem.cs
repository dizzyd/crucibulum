// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using Vintagestory.API.Common;
using Vintagestory.GameContent;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
using System.Linq;
using System.Collections.Generic;
using System;

namespace Crucibulum;

public class CrucibulumModSystem : ModSystem
{
    public const string ConfigFile = "crucibulum.json";

    public static CrucibulumConfig Config { get; private set; } = new();

    public override void StartPre(ICoreAPI api)
    {
        base.StartPre(api);

        CrucibulumConfig config = null;
        try
        {
            config = api.LoadModConfig<CrucibulumConfig>(ConfigFile);
        }
        catch (System.Exception e)
        {
            api.Logger.Error("[crucibulum] {0} is malformed, falling back to defaults: {1}", ConfigFile, e.Message);
        }

        // Always write it back, not only when it is missing. A config from an older version parses
        // fine and quietly keeps its defaults for anything added since, so the file on disk ends up
        // describing settings that are no longer the ones in effect - and the new knobs are not
        // there to be turned. Rewriting adds what is new and drops what has been removed.
        config ??= new CrucibulumConfig();
        api.StoreModConfig(config, ConfigFile);

        Config = config;
    }

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        // The forge blocktype is repointed at these by assets/crucibulum/patches/forge.json.
        api.RegisterBlockClass("CrucibulumForge", typeof(BlockCrucibulumForge));
        api.RegisterBlockEntityClass("CrucibulumForge", typeof(BlockEntityCrucibulumForge));

        RegisterWithConfigLib(api);
    }

    private const string ConfigLibSystem = "ConfigLib.ConfigLibModSystem";
    private const string ConfigLibRegister = "RegisterCustomManagedConfig";

    /// <summary>Whether ConfigLib is installed and took the config. Asserted in a test.</summary>
    public static bool ConfigLibBound { get; private set; }

    /// <summary>
    /// Hands <see cref="Config"/> to ConfigLib if it is installed.
    ///
    /// The visible half is an in-game settings screen. The half that matters more here is that
    /// ConfigLib syncs the server's values to every client, which this mod does not do on its own -
    /// each side reads its own ModConfig file. That is a real problem for this config in
    /// particular, because clients *display* numbers derived from it: the block info and the
    /// window's "Blast gate: half open - 1020C" line both come from CrucibleMaxTemperature, which
    /// the tempature bonus and the four gate factors feed. Retune a server without this and every
    /// client quotes ceilings that are not true there - including the melting point cue, which
    /// exists precisely so nobody has to guess.
    ///
    /// Bound by reflection rather than a compile-time reference, so ConfigLib is optional when
    /// building this as well as when running it, and nothing third-party lives in the repo or the
    /// release zip. The surface is a single method, and RegisterCustomManagedConfig reflects over
    /// the config object itself - so the [Category], [Description] and [Range] attributes on
    /// CrucibulumConfig are the whole schema, and those are BCL attributes, inert when ConfigLib
    /// is absent.
    /// </summary>
    private void RegisterWithConfigLib(ICoreAPI api)
    {
        var system = api.ModLoader.GetModSystem(ConfigLibSystem);
        if (system == null) return;   // not installed, which is the ordinary case

        var register = system.GetType().GetMethod(ConfigLibRegister);
        if (register == null)
        {
            api.Logger.Warning("[crucibulum] configlib is installed but has no {0} - the forge's "
                + "settings will not appear in its screen and will not sync from the server.",
                ConfigLibRegister);
            return;
        }

        try
        {
            register.Invoke(system, new object[]
            {
                "crucibulum",   // domain
                Config,         // the object it reflects over
                ConfigFile,     // reuse the file this mod already writes, so there is one source
                null,           // onSyncedFromServer - everything is read live, nothing to rebuild
                null,           // onSettingChanged
                null,           // onConfigSaved
            });

            ConfigLibBound = true;
        }
        catch (Exception e)
        {
            // Reflection wraps whatever went wrong inside the call in a TargetInvocationException
            // whose own message says nothing, so unwrap it or this is unactionable.
            api.Logger.Warning("[crucibulum] could not hand the config to configlib: {0}",
                (e as System.Reflection.TargetInvocationException)?.InnerException ?? e);
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        // Deferred by a tick, not run from the event itself: at ChunkColumnLoaded the column is
        // there but its block entities are not - every chunk reports none - so scanning then finds
        // nothing to upgrade, every time. The column is re-fetched rather than captured, because it
        // may be gone again by the time this runs.
        api.Event.ChunkColumnLoaded += (chunkCoord, chunks) =>
            api.Event.RegisterCallback(_ => UpgradeForgesIn(api, ColumnAt(api, chunkCoord)), 0);
    }

    /// <summary>
    /// Brings forges that were placed before this mod was installed up to date.
    ///
    /// Patching the blocktype changes what gets built for a forge placed from now on. It does not
    /// touch the ones already in the world: a saved chunk records the class its block entity was
    /// written with, so an existing forge comes back as a vanilla BlockEntityForge even though its
    /// *block* is now ours. That reads as a forge that half works - the interaction help offers the
    /// crucible, because that comes off the block, and nothing happens when you try it, because the
    /// block entity underneath knows nothing about crucibles. Breaking and replacing it fixed it,
    /// which is not something to ask of anyone with a built base.
    ///
    /// So each forge is swapped as its chunk loads. The old block entity's tree carries straight
    /// over: vanilla's inventory is this one's first two slots in the same order, work item then
    /// fuel, and the charge slots simply come up empty. Behaviours travel with it too, which is
    /// what keeps a ChiselTools forge's chiselled cover through the upgrade.
    /// </summary>
    private static IWorldChunk[] ColumnAt(ICoreServerAPI api, Vec2i coord)
    {
        int chunkSize = api.WorldManager.ChunkSize;
        int height = api.WorldManager.MapSizeY / chunkSize;

        IWorldChunk[] column = new IWorldChunk[height];
        for (int cy = 0; cy < height; cy++)
        {
            column[cy] = api.WorldManager.GetChunk(coord.X, cy, coord.Y);
        }
        return column;
    }

    public static void UpgradeForgesIn(ICoreServerAPI api, IWorldChunk[] chunks)
    {
        if (chunks == null) return;

        foreach (IWorldChunk chunk in chunks)
        {
            if (chunk?.BlockEntities == null) continue;

            // Copied, because replacing a block entity edits the dictionary being walked.
            foreach (KeyValuePair<BlockPos, BlockEntity> entry in chunk.BlockEntities.ToArray())
            {
                if (entry.Value is BlockEntityCrucibulumForge) continue;

                // Only forges whose blocktype this mod has taken over - a vanilla forge, or a
                // modded one the patches point at us. Anything else is none of our business.
                if (entry.Value is not BlockEntityForge stale || stale.Block is not BlockCrucibulumForge) continue;

                Upgrade(api, stale, entry.Key);
            }
        }
    }

    private static void Upgrade(ICoreServerAPI api, BlockEntityForge stale, BlockPos pos)
    {
        try
        {
            TreeAttribute tree = new TreeAttribute();
            stale.ToTreeAttributes(tree);

            api.World.BlockAccessor.RemoveBlockEntity(pos);
            api.World.BlockAccessor.SpawnBlockEntity("CrucibulumForge", pos);

            if (api.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityCrucibulumForge fresh) return;

            fresh.FromTreeAttributes(tree, api.World);
            fresh.MarkDirty(true);
        }
        catch (Exception e)
        {
            // One bad forge should not take a chunk load down with it.
            api.Logger.Warning("[crucibulum] could not upgrade the forge at {0}: {1}", pos, e.Message);
        }
    }
}
