using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Crucibulum;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// ConfigLib, if the player has it.
    ///
    /// The settings screen is the visible half. The half that matters is that ConfigLib syncs the
    /// server's values to clients, which this mod does not do on its own - and clients display
    /// numbers derived from this config, so without the sync a retuned server has everyone quoting
    /// ceilings that are not true there.
    ///
    /// The binding is by reflection, against a method name in a mod this one does not reference. If
    /// ConfigLib renames or resignatures it, nothing here would notice: the mod would carry on
    /// working and simply stop syncing, silently. That is what these are for.
    ///
    /// They no-op when ConfigLib is not installed, which is the ordinary case. Run them with it in
    /// the mod path to mean anything - see docs/compat.md.
    /// </summary>
    public class CompatConfigLib
    {
        static bool Installed => Sapi.ModLoader.IsModEnabled("configlib");

        static bool Absent(string what)
        {
            if (Installed) return false;
            Log($"  ConfigLib not installed - {what} not checked");
            return true;
        }

        [VsTest]
        public async Task TheConfigIsHandedToConfigLib()
        {
            if (Absent("the binding")) return;

            Assert.True(CrucibulumModSystem.ConfigLibBound,
                "the config reached ConfigLib - if this is false the method was not found or threw, "
                + "and the server's settings will not sync to clients");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task ConfigLibStillHasTheMethodWeCallByName()
        {
            if (Absent("the method")) return;

            var system = Sapi.ModLoader.GetModSystem("ConfigLib.ConfigLibModSystem");
            Assert.NotNull(system, "ConfigLib.ConfigLibModSystem still exists under that name");

            MethodInfo register = system.GetType().GetMethod("RegisterCustomManagedConfig");
            Assert.NotNull(register, "RegisterCustomManagedConfig still exists");

            // Six arguments: domain, the config object, the file name, and three callbacks.
            var types = register.GetParameters().Select(p => p.ParameterType.Name).ToArray();
            Log("  RegisterCustomManagedConfig(" + string.Join(", ", types) + ")");
            Assert.Equal(6, types.Length, "and still takes the six arguments this mod passes");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task EverySettingIsDescribedForTheScreen()
        {
            // ConfigLib reflects over the config object, so these attributes are the entire schema.
            // A field added without them shows up in the screen as a bare name with no explanation.
            var undescribed = typeof(CrucibulumConfig)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.GetCustomAttribute<DescriptionAttribute>() == null)
                .Select(f => f.Name)
                .ToArray();

            Log($"  {typeof(CrucibulumConfig).GetFields(BindingFlags.Public | BindingFlags.Instance).Length} settings, "
              + $"{undescribed.Length} without a description");
            Assert.Equal(0, undescribed.Length, "every setting is described: " + string.Join(", ", undescribed));
            await Task.CompletedTask;
        }
    }
}
