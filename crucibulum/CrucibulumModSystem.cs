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

        if (config == null)
        {
            config = new CrucibulumConfig();
            api.StoreModConfig(config, ConfigFile);
        }

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
