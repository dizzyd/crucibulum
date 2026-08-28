using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// Keeps the shared test player on his feet.
    ///
    /// The suites share one player across every test. If anything kills him, the death screen then
    /// suppresses every world interaction that follows - so one test takes out every interaction
    /// test after it, with failures that say nothing about what went wrong. Eighteen at once, the
    /// first time this happened.
    ///
    /// The cause was the tests, not the game. Clearing dropped items with
    /// <c>World.Entities(pos, r)</c> and calling Die on each one killed the player too, because
    /// that returns every entity in radius and he was standing in it. Those loops now filter to
    /// EntityItem.
    ///
    /// Two things it was NOT, both measured rather than assumed, because the first guess here was
    /// wrong: standing beside a lit forge does no damage at all - full health for twenty-four
    /// seconds against a burning one, body temperature flat at 37 - and it is not starvation, with
    /// forty-eight in-game hours advanced and saturation untouched.
    ///
    /// This stays as a backstop, and reports the recorded reason rather than guessing, so the next
    /// one names itself. A death with source and type of -1 means no damage source at all: someone
    /// called Die on him directly, as above.
    /// </summary>
    public static class TestLife
    {
        public static async Task Alive()
        {
            var entity = Sapi?.World?.AllOnlinePlayers?.FirstOrDefault()?.Entity;
            if (entity == null) return;   // headless, no player to look after

            bool wasDead = !entity.Alive;
            if (wasDead)
            {
                // EnumDamageSource / EnumDamageType, as Entity.Die records them.
                Log($"  player died: source={(EnumDamageSource)entity.WatchedAttributes.GetInt("deathReason", -1)}"
                  + $" type={(EnumDamageType)entity.WatchedAttributes.GetInt("deathDamageType", -1)}"
                  + $" by={entity.WatchedAttributes.GetString("deathByEntity", "-")}");
                entity.Revive();
            }

            var health = entity.GetBehavior<EntityBehaviorHealth>();
            if (health != null && health.Health < health.MaxHealth)
            {
                health.Health = health.MaxHealth;
            }

            if (wasDead)
            {
                await Gui.CloseDialogs();
                await Ticks(3);
            }
        }
    }
}
