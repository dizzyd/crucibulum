// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using Vintagestory.API.Common;

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
    }
}
