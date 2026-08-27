using System.Threading.Tasks;
using Vintagestory.API.Config;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// The strings and shapes the mod ships.
    ///
    /// A lang key that does not resolve shows the player the raw key - "crucibulum:forge-nomix" in
    /// the middle of a window - and nothing in the build catches it. Neither does anything catch a
    /// key that has been renamed in code and left behind in the file.
    /// </summary>
    public class ForgeAssets
    {
        /// <summary>Every key the mod asks Lang for. Keep in step with the code.</summary>
        static readonly string[] Keys =
        {
            "crucibulum:forge-crucible-title",
            "crucibulum:forge-charge",
            "crucibulum:forge-charge-share",
            "crucibulum:forge-charge-nocrucible",
            "crucibulum:forge-melting",
            "crucibulum:forge-toocold",
            "crucibulum:forge-molten",
            "crucibulum:forge-solidified",
            "crucibulum:forge-nomix",
            "crucibulum:forge-alloy-needs",
            "crucibulum:forge-alloy-part",
            "crucibulum:dlg-charge",
            "crucibulum:dlg-empty",
            "crucibulum:dlg-nocrucible",
            "crucibulum:dlg-nofuel",
            "crucibulum:dlg-unlit",
            "crucibulum:dlg-fuelhours",
            "crucibulum:blockhelp-forge-addcrucible",
            "crucibulum:blockhelp-forge-takecrucible",
            "crucibulum:blockhelp-forge-opencrucible",
            "crucibulum:blockhelp-forge-addcharge",
            "crucibulum:blockhelp-forge-addchargestack",
            "crucibulum:blockhelp-forge-takecharge",
        };

        [VsTest]
        public async Task EveryStringTheModAsksForIsTranslated()
        {
            foreach (string key in Keys)
            {
                Assert.True(Lang.HasTranslation(key, findWildcarded: false, logErrors: false),
                    "missing lang entry: " + key);

                string text = Lang.GetUnformatted(key);
                Assert.NotEqual(key, text, key + " resolves to something other than its own name");
                Assert.False(string.IsNullOrWhiteSpace(text), key + " is not blank");
            }

            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheConfigIsLoadedWithItsDefaults()
        {
            // StartPre writes and reads ModConfig/crucibulum.json. If that threw, every knob would
            // silently be a zero and the balance would be nothing like what the README claims.
            var cfg = CrucibulumModSystem.Config;
            Assert.NotNull(cfg, "the config loaded");

            Assert.Greater(cfg.CrucibleTempBonus, 0, "crucible temperature bonus");
            Assert.Greater(cfg.CrucibleFuelUseVsFirepit, 0f, "fuel share against a firepit");
            Assert.Greater(cfg.MoltenHoldFuelShare, 0f, "hold share");
            Assert.Greater(cfg.HeatRate, 0f, "heat rate");
            Assert.Greater(cfg.CrucibleThermalMass, 0f, "crucible thermal mass");
            Assert.Greater(cfg.MeltSpeedMultiplier, 0f, "melt speed");

            await Task.CompletedTask;
        }
    }
}
