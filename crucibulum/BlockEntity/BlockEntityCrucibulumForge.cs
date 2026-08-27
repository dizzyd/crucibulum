using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Crucibulum;

/// <summary>
/// A vanilla forge that will also hold a crucible.
///
/// The crucible itself rides in the forge's own work item slot -- the 1.22 forge already accepts,
/// heats and renders any collectible flagged <c>forgable</c>, so the body of the crucible glows
/// through the incandescence range for free. What this class adds is the charge: four ingredient
/// slots that stand in for the firepit's cooking slots, so that
/// <see cref="BlockSmeltingContainer"/>'s own CanSmelt/DoSmelt run unmodified and every alloy
/// recipe in the game works here too.
///
/// The charge lives in the forge rather than in the crucible stack, exactly as it does in a
/// firepit. Pull the crucible back out mid-melt and the ore stays behind in the coals.
/// </summary>
public class BlockEntityCrucibulumForge : BlockEntityForge
{
    public const int ChargeSlotCount = 4;

    /// <summary>Slot 0 is the vanilla forge's work item, slot 1 its fuel; 2-5 are the charge.</summary>
    public const int FirstChargeSlot = 2;

    protected ChargeSlotProvider chargeProvider;
    protected CrucibleRenderer crucibleRenderer;
    protected GuiDialogCrucibleForge clientDialog;

    /// <summary>Seconds of accumulated melt time, in the same units the firepit uses.</summary>
    protected float meltProgress;

    protected double lastCrucibleTickHours;
    protected bool wasMolten;

    /// <summary>Our own view of the crucible's temperature; see HeatCrucible.</summary>
    protected float heatedTemp;
    protected ItemStack heatedStack;

    /// <summary>
    /// The block info text, rebuilt a few times a second rather than a few dozen.
    ///
    /// GetBlockInfo runs once a frame for whatever the player is looking at, and working out what
    /// the crucible will make is not cheap: it walks the charge, and on a mix that matches no alloy
    /// it walks every alloy in the game. Measured at 22us and 15KB of garbage per call, which at
    /// 60fps is close to a megabyte a second of it from a single forge.
    ///
    /// Only the *text* is cached. What the fire is doing drives the fuel rate and the melt, and a
    /// stale answer there would be a real bug rather than a stale sentence.
    /// </summary>
    /// <summary>How often a drifting temperature or melt bar is worth a full sync to clients.</summary>
    protected const long SoftSyncMs = 500;
    protected long lastSyncMs;
    protected CrucibleWork lastSyncedWork;

    protected const long StatusTtlMs = 200;
    protected long statusAtMs = long.MinValue;
    protected string statusText;

    /// <summary>Throw the cached text away: contents changed, so it no longer describes them.</summary>
    public void InvalidateStatusText()
    {
        statusAtMs = long.MinValue;
        statusText = null;
    }

    /// <summary>
    /// Every path that changes what is in the forge ends here, so this is the one honest place to
    /// drop the cached description. Hanging it off Inventory.SlotModified was not enough: a slot
    /// assigned directly, which is how DoSmelt and half of vanilla move stacks, never raises it.
    /// </summary>
    public override void MarkDirty(bool redrawOnClient = false, IPlayer skipPlayer = null)
    {
        InvalidateStatusText();
        base.MarkDirty(redrawOnClient, skipPlayer);
    }

    /// <summary>
    /// How many times the block info text has actually been built. Allocation cannot be measured
    /// from inside a running game - GC counters are process-wide and every other thread is busy -
    /// so the cache is guarded by counting rebuilds instead.
    /// </summary>
    public int ChargeTextRebuilds { get; private set; }

    /// <summary>How far the climb is choked while the charge is changing state. Vanilla's figure.</summary>
    public const float LatentHeatDivisor = 11f;

    public ItemStack CrucibleStack => IsCrucible(WorkItemStack) ? WorkItemStack : null;
    public float MeltProgress => meltProgress;

    /// <summary>The four ingredient slots, in dialog order.</summary>
    public ItemSlot[] ChargeSlots => chargeProvider.Slots;

    /// <summary>The charge as BlockSmeltingContainer wants to see it.</summary>
    public ISlotProvider ChargeProvider => chargeProvider;

    public bool ChargeEmpty
    {
        get
        {
            for (int i = 0; i < ChargeSlotCount; i++) if (!inv[FirstChargeSlot + i].Empty) return false;
            return true;
        }
    }

    /// <summary>
    /// The forge's own two slots plus four for the crucible's charge, in one inventory.
    ///
    /// One inventory rather than two because the dialog syncs exactly one, and a window showing the
    /// ore but not the fuel it is melting over would be a strange thing to hand someone. Slots 0
    /// and 1 keep their vanilla meaning, so BlockEntityForge's WorkItemSlot and FuelSlot still point
    /// where they always did. Widening is safe for forges saved before this mod: slot loading walks
    /// the *new* slot count and simply finds nothing at the indices that were not there.
    /// </summary>
    public BlockEntityCrucibulumForge()
    {
        inv = new InventoryGeneric(2 + ChargeSlotCount, null, null, NewSlot);
        chargeProvider = new ChargeSlotProvider(inv, FirstChargeSlot, ChargeSlotCount);
    }

    protected static ItemSlot NewSlot(int slotId, InventoryGeneric self) => slotId switch
    {
        0 => new ItemSlotForgeWorkItem(self),
        1 => new ItemSlotForgeFuel(self),
        _ => new ItemSlotCrucibleCharge(self),
    };

    public static bool IsCrucible(ItemStack stack) => stack?.Collectible is BlockSmeltingContainer or BlockSmeltedContainer;

    public static bool IsMoltenCrucible(ItemStack stack) => stack?.Collectible is BlockSmeltedContainer;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);

        lastCrucibleTickHours = api.World.Calendar.TotalHours;

        RegisterGameTickListener(OnCrucibleTick, 200);

        if (api is ICoreClientAPI capi)
        {
            capi.Event.RegisterRenderer(crucibleRenderer = new CrucibleRenderer(this, capi), EnumRenderStage.Opaque, "crucibulum-crucible");
            RegisterGameTickListener(OnMoltenParticleTick, 150);
        }
    }

    #region Interaction

    /// <summary>
    /// Runs before <see cref="BlockEntityForge.OnPlayerInteract"/>. Returns true only for the
    /// interactions the vanilla forge has no answer for, so everything else falls through untouched.
    /// </summary>
    public bool OnPlayerInteractCrucible(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;

        if (byPlayer.Entity.Controls.ShiftKey)
        {
            // Shift with a crucible: in, or back out again.
            if (slot.Empty)
            {
                if (TakeCrucible(byPlayer, blockSel)) return true;
                if (WorkItemSlot.Empty && !ChargeEmpty && TakeCharge(byPlayer))
                {
                    Api.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), Pos, 0.4375, byPlayer, true);
                    return true;
                }
                return false;
            }

            if (IsCrucible(slot.Itemstack)) return PutCrucible(slot, byPlayer, blockSel);

            // Loading the crucible a click at a time. Everything else - fuel, ingots, ignition -
            // is the vanilla forge's business.
            if (!CanAcceptCharge || !IsCrucibleCharge(slot.Itemstack)) return false;

            int quantity = byPlayer.Entity.Controls.CtrlKey ? slot.StackSize : 1;
            int moved = AddCharge(slot, quantity);
            if (moved == 0) return false;

            Api.World.Logger.Audit("{0} Put {1}x{2} into a forge crucible at {3}.",
                byPlayer.PlayerName, moved, slot.Itemstack?.Collectible.Code, blockSel.Position);
            Api.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), Pos, 0.4375, byPlayer, true);
            return true;
        }

        // A crucible held against a bare forge goes in, exactly as it does at a firepit, which
        // takes one on a plain click too.
        if (!slot.Empty && IsCrucible(slot.Itemstack) && WorkItemSlot.Empty)
        {
            return PutCrucible(slot, byPlayer, blockSel);
        }

        // Smithing has to keep working. A plain click on a forge holding an ingot, a plate or a
        // work item still hands it over - that is the whole rhythm of working at an anvil - and a
        // plain click with something in hand on a bare forge still does nothing at all, rather
        // than putting a window in the smith's face.
        //
        // A forge holding a crucible opens whatever is in hand, which also stops a hot crucible
        // being yanked out by a click meant for the window.
        bool holdingCrucible = CrucibleStack != null;
        if (!holdingCrucible && (!WorkItemSlot.Empty || !slot.Empty)) return false;

        ToggleDialog(byPlayer);
        return true;
    }

    protected bool PutCrucible(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (TrySetCrucible(slot))
        {
            Api.World.Logger.Audit("{0} Set 1x{1} into the forge at {2}.",
                byPlayer.PlayerName, WorkItemStack.Collectible.Code, blockSel.Position);
            Api.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), Pos, 0.4375, byPlayer, true);
        }

        // Handled either way. Letting a refused crucible fall through to the vanilla forge would
        // hand it to BEForge's "merge heatable item" branch, which is guarded by !forgable - and
        // the crucible deliberately is not - so it would stack a second crucible into the slot
        // that DoSmelt then silently eats.
        return true;
    }

    /// <summary>Shift + empty hand lifts the crucible straight out, charge and all.</summary>
    protected bool TakeCrucible(IPlayer byPlayer, BlockSelection blockSel)
    {
        if (CrucibleStack == null) return false;

        ItemStack stack = WorkItemSlot.TakeOutWhole();
        WorkItemSlot.MarkDirty();

        Api.World.Logger.Audit("{0} Took 1x{1} from the forge at {2}.",
            byPlayer.PlayerName, stack.Collectible.Code, blockSel.Position);

        if (!byPlayer.InventoryManager.TryGiveItemstack(stack))
        {
            Api.World.SpawnItemEntity(stack, Pos);
        }
        else
        {
            Api.ModLoader.GetModSystem<ModSystemSubTongsDurability>()?.OnItemPickedUp(byPlayer.Entity, stack);
        }

        Api.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), Pos, 0.4375, byPlayer, true);
        MarkDirty(true);
        return true;
    }

    /// <summary>A crucible is in place and has not already gone molten.</summary>
    public bool CanAcceptCharge => CrucibleStack != null && !IsMoltenCrucible(CrucibleStack);

    /// <summary>
    /// Moves one crucible from a slot into the forge. False if the forge is already holding
    /// something. Kept free of any player so it can be exercised without one.
    /// </summary>
    public bool TrySetCrucible(ItemSlot fromSlot)
    {
        if (!WorkItemSlot.Empty) return false;
        if (fromSlot.Empty || !IsCrucible(fromSlot.Itemstack)) return false;

        WorkItemSlot.Itemstack = fromSlot.TakeOut(1);
        fromSlot.MarkDirty();
        MarkDirty(true);
        return true;
    }

    /// <summary>
    /// Mirrors the vanilla crucible's own slot filter: storageType 4 (Metallurgy) plus something
    /// that actually melts and needs a container to do it. Deliberately rejects anything the forge
    /// would rather burn, so coal keeps going to the fuel slot.
    /// </summary>
    public bool IsCrucibleCharge(ItemStack stack)
    {
        if (stack == null) return false;

        CombustibleProperties props = stack.Collectible.GetCombustibleProperties(Api.World, stack, null);
        if (props is { BurnTemperature: > 1000 }) return false;
        if (props?.SmeltedStack == null || props.MeltingPoint <= 0 || !props.RequiresContainer) return false;

        int storageType = CrucibleStack?.ItemAttributes?["storageType"].AsInt((int)EnumItemStorageFlags.Metallurgy)
                          ?? (int)EnumItemStorageFlags.Metallurgy;

        return (stack.Collectible.GetStorageFlags(stack) & (EnumItemStorageFlags)storageType) != 0;
    }

    /// <summary>
    /// Moves up to <paramref name="quantity"/> from a slot into the crucible and returns how many
    /// actually went. Kept free of any player so it can be exercised without one.
    /// </summary>
    public int AddCharge(ItemSlot fromSlot, int quantity = 1)
    {
        ItemSlot target = null;

        // Prefer merging into a slot already holding the same thing, so four different ingredients
        // still fit for an alloy.
        foreach (ItemSlot s in ChargeSlots)
        {
            if (target != null) break;
            if (!s.Empty && s.Itemstack.Equals(Api.World, fromSlot.Itemstack, GlobalConstants.IgnoredStackAttributes)
                && s.StackSize < s.Itemstack.Collectible.MaxStackSize)
            {
                target = s;
            }
        }
        foreach (ItemSlot s in ChargeSlots)
        {
            if (target == null && s.Empty) target = s;
        }

        if (target == null) return 0;

        float crucibleTemp = CrucibleStack?.Collectible.GetTemperature(Api.World, CrucibleStack) ?? 20f;
        float incomingTemp = fromSlot.Itemstack.Collectible.GetTemperature(Api.World, fromSlot.Itemstack);
        float hadIngots = ChargeIngotEquivalents();

        // Two stacks at different temperatures will not merge. Without matching them first you
        // cannot top up a crucible that has already begun to warm: the click is silently refused,
        // which looks exactly like the forge having decided it does not want any more ore.
        fromSlot.Itemstack.Collectible.SetTemperature(Api.World, fromSlot.Itemstack, crucibleTemp);

        int moved = fromSlot.TryPutInto(Api.World, target, quantity);
        if (moved == 0)
        {
            // Put the temperature back rather than leaving a mysteriously warm stack in hand.
            fromSlot.Itemstack?.Collectible.SetTemperature(Api.World, fromSlot.Itemstack, incomingTemp);
            return 0;
        }

        MixInColdMetal(crucibleTemp, incomingTemp, hadIngots, ChargeIngotEquivalents() - hadIngots);

        fromSlot.MarkDirty();
        MarkDirty(true);
        return moved;
    }

    /// <summary>
    /// Cold metal tipped into a hot crucible cools the whole lot, in proportion to how much was
    /// already in there against how much is going in - and against the clay itself, which is
    /// carrying heat of its own. Throw a fistful of cold ore into a nearly-molten crucible and you
    /// have genuinely set yourself back, which is what happens at a real furnace.
    /// </summary>
    protected void MixInColdMetal(float crucibleTemp, float incomingTemp, float hadIngots, float addedIngots)
    {
        if (addedIngots <= 0 || incomingTemp >= crucibleTemp) return;

        float vessel = Math.Max(0.1f, CrucibulumModSystem.Config.CrucibleThermalMass);
        float mixed = (crucibleTemp * (vessel + hadIngots) + incomingTemp * addedIngots)
                      / (vessel + hadIngots + addedIngots);

        ItemStack crucible = CrucibleStack;
        if (crucible == null) return;

        crucible.Collectible.SetTemperature(Api.World, crucible, mixed);
        heatedTemp = mixed;
        heatedStack = crucible;
    }

    /// <summary>
    /// Empties the crucible back into the player's hands, or onto the ground if there is no player
    /// or no room. Returns false if there was nothing to give back.
    /// </summary>
    public bool TakeCharge(IPlayer byPlayer)
    {
        bool any = false;

        foreach (ItemSlot s in ChargeSlots)
        {
            if (s.Empty) continue;

            ItemStack stack = s.TakeOutWhole();
            any = true;

            if (byPlayer != null)
            {
                Api.World.Logger.Audit("{0} Took {1}x{2} from a forge crucible at {3}.",
                    byPlayer.PlayerName, stack.StackSize, stack.Collectible.Code, Pos);
            }

            if (byPlayer == null || !byPlayer.InventoryManager.TryGiveItemstack(stack))
            {
                Api.World.SpawnItemEntity(stack, Pos);
            }
        }

        if (!any) return false;

        meltProgress = 0;
        MarkDirty(true);
        return true;
    }

    #endregion

    #region Heating and melting

    /// <summary>
    /// Fuel goes faster while the crucible has metal in it.
    ///
    /// The two devices burn on different clocks: a firepit spends <c>BurnDuration</c> *real*
    /// seconds per item, while the forge spends <c>1/BurnRate</c> *in-game hours*. At the default
    /// calendar that is 40 real seconds against 240 - so melting over a forge came out six times
    /// cheaper than the same melt in a firepit, and under one lump of coke would see off a full
    /// 700-unit crucible.
    ///
    /// The multiplier is derived from the calendar rather than hardcoded at 6, so it stays honest
    /// on a server that has changed CalendarSpeedMul or day length, and it is derived per fuel, so
    /// fuels whose firepit burn duration differs stay in step too.
    ///
    /// Only charges. An empty crucible sitting in a lit forge costs nothing extra; one with ore in
    /// it, or holding metal that has to stay liquid, is what the fire is doing work for.
    /// </summary>
    public override float BurnRate
    {
        get
        {
            float rate = base.BurnRate;
            if (!CrucibleDrawsHeat) return rate;

            // Guard the zero: a multiplier of 0 would mean a burn rate of 0, which is not "vanilla
            // rate" but infinite fuel - the opposite of what turning this off is meant to do.
            float share = CrucibulumModSystem.Config.CrucibleFuelUseVsFirepit;
            if (share <= 0) return rate;

            float working = FirepitParityMultiplier() * share;

            // Holding a melt liquid is not the same job as making one. It only replaces losses.
            if (WorkState == CrucibleWork.Holding) working *= CrucibulumModSystem.Config.MoltenHoldFuelShare;

            // Never below vanilla. Whatever the arithmetic says, putting a crucible in must not end
            // up *rewarding* the player with fuel that lasts longer than it would have without one.
            return rate * Math.Max(1f, working);
        }
    }

    /// <summary>There is metal in the crucible that the fire has to work on.</summary>
    public bool CrucibleDrawsHeat => WorkState != CrucibleWork.None;

    /// <summary>
    /// What the fire is doing, which is what decides the fuel bill.
    ///
    /// Bringing a charge up to temperature and then melting it both cost real energy - sensible
    /// heat and then latent heat. Keeping metal that is already liquid from freezing only has to
    /// replace what the crucible loses to the air, which is a far smaller thing.
    /// </summary>
    public CrucibleWork WorkState
    {
        get
        {
            ItemStack crucible = CrucibleStack;
            if (crucible == null) return CrucibleWork.None;

            if (crucible.Collectible is BlockSmeltedContainer smelted)
            {
                var contents = smelted.GetContents(Api.World, crucible);
                if (contents.Key == null) return CrucibleWork.None;

                // A charge that has been let go solid is a melt to do all over again.
                return smelted.HasSolidifed(crucible, contents.Key, Api.World)
                    ? CrucibleWork.Heating
                    : CrucibleWork.Holding;
            }

            if (ChargeEmpty) return CrucibleWork.None;

            float meltingPoint = crucible.Collectible.GetMeltingPoint(Api.World, chargeProvider, WorkItemSlot);
            float temp = crucible.Collectible.GetTemperature(Api.World, crucible);
            return meltingPoint > 0 && temp >= meltingPoint ? CrucibleWork.Melting : CrucibleWork.Heating;
        }
    }

    /// <summary>What the game ships: 60 x 0.5, which makes a day 48 real minutes.</summary>
    public const float DefaultSpeedOfTime = 60f;
    public const float DefaultCalendarSpeedMul = 0.5f;

    /// <summary>How much faster the forge must burn to cost what a firepit costs for the same melt.</summary>
    public float FirepitParityMultiplier()
    {
        float forgeHoursPerItem = 1f / Math.Max(0.0001f, base.BurnRate);

        float forgeRealSeconds = forgeHoursPerItem * RealSecondsPerInGameHour();

        // What the same lump would have bought in a firepit.
        CombustibleProperties props = FuelSlot.Itemstack?.Collectible
            .GetCombustibleProperties(Api.World, FuelSlot.Itemstack, null);
        float firepitRealSeconds = props?.BurnDuration ?? 0;
        if (firepitRealSeconds <= 0) return 1f;

        return Math.Max(1f, forgeRealSeconds / firepitRealSeconds);
    }

    /// <summary>
    /// The ceiling a crucible may be driven to in this forge. The forge's own ceiling
    /// (700 + the fuel's tempGainDeg) plus a crucible bonus, multiplied by the bellows oxygen
    /// boost, then clamped by the crucible's maxHeatableTemp -- an attribute vanilla ships on the
    /// crucible (1200) but never reads. That clamp is what keeps iron out of reach.
    /// </summary>
    public float CrucibleMaxTemperature()
    {
        CrucibulumConfig cfg = CrucibulumModSystem.Config;

        int ceiling = cfg.MaxCrucibleTemperature > 0
            ? cfg.MaxCrucibleTemperature
            : CrucibleStack?.ItemAttributes?["maxHeatableTemp"].AsInt(1200) ?? 1200;

        float fuelCeiling = (MaxTemperature + cfg.CrucibleTempBonus) * (1 + extraOxygenRate);

        return Math.Min(ceiling, fuelCeiling);
    }

    protected void OnCrucibleTick(float dt)
    {
        if (Api.Side != EnumAppSide.Server) return;

        double hoursPassed = Api.World.Calendar.TotalHours - lastCrucibleTickHours;
        if (hoursPassed < 0) hoursPassed = 0;
        lastCrucibleTickHours = Api.World.Calendar.TotalHours;

        ItemStack crucible = CrucibleStack;
        if (crucible == null)
        {
            // No crucible: any leftover charge just cools on its own in the itemstack.
            if (meltProgress != 0) { meltProgress = 0; MarkDirty(); }
            wasMolten = false;
            return;
        }

        // The vanilla forge tick already pushed the crucible up to the forge's own ceiling and then
        // stopped. Carry it the rest of the way to the crucible ceiling; vanilla never lowers a
        // temperature, so the two ticks do not fight.
        CrucibleWork work = WorkState;
        float before = crucible.Collectible.GetTemperature(Api.World, crucible);
        float crucibleTemp = HeatCrucible(crucible, hoursPassed, work);

        // Only when something actually moved. Marking dirty serialises all six slots and sends them
        // to every client in range, and a forge sitting at its ceiling has nothing to tell them.
        bool dirty = Math.Abs(crucibleTemp - before) > 0.01f;

        // The charge sits in the crucible, so it holds the crucible's temperature. This is also
        // what BlockSmeltingContainer reads to decide the temperature of the metal it pours out.
        foreach (ItemSlot slot in ChargeSlots)
        {
            ItemStack stack = slot.Itemstack;
            if (stack == null) continue;
            if (Math.Abs(stack.Collectible.GetTemperature(Api.World, stack) - crucibleTemp) < 0.01f) continue;
            stack.Collectible.SetTemperature(Api.World, stack, crucibleTemp);
            dirty = true;
        }

        bool stateChanged = work != lastSyncedWork;
        lastSyncedWork = work;

        if (UpdateMelt(dt, crucible, crucibleTemp)) dirty = true;
        if (!dirty && !stateChanged) return;

        // A change of job is worth telling clients about at once. Temperature creeping and a melt
        // bar advancing are not: at 200ms that is five full inventory syncs a second per forge, and
        // twice a second reads just as live.
        long now = Api.World.ElapsedMilliseconds;
        if (!stateChanged && now - lastSyncMs < SoftSyncMs) return;

        lastSyncMs = now;
        MarkDirty();
    }

    /// <summary>
    /// Brings the crucible towards the temperature the fire can hold it at.
    ///
    /// The step is proportional to how far there is left to go, which is how heat actually behaves
    /// and how the firepit already models it: a cold crucible climbs briskly and then eases in over
    /// the last stretch rather than slamming into the ceiling and stopping dead.
    ///
    /// While the charge is genuinely melting the climb all but halts. That is latent heat - at the
    /// melting point the fire's energy goes into changing the metal's state, not into raising its
    /// temperature, and a thermometer in a real crucible sits almost still until the charge is
    /// through.
    /// </summary>
    protected float HeatCrucible(ItemStack crucible, double hoursPassed, CrucibleWork work)
    {
        float stackTemp = crucible.Collectible.GetTemperature(Api.World, crucible);

        // Re-seed when the stack changes under us - a crucible swapped in, or DoSmelt handing back
        // a molten one - and whenever something has pushed the temperature *down*. Anything that
        // cools the crucible is real and wins: CoolNow dousing the forge with a water bucket or
        // rain, or the stack's own passive cooling between ticks. A rise, on the other hand, is the
        // vanilla forge tick laying its linear ramp over the top, and that is exactly what this
        // shadow exists to ignore.
        if (!ReferenceEquals(heatedStack, crucible) || stackTemp < heatedTemp)
        {
            heatedStack = crucible;
            heatedTemp = stackTemp;
        }

        if (!IsBurning)
        {
            // Off the fire it cools on the itemstack's own schedule, which is already ticking.
            heatedTemp = stackTemp;
            return heatedTemp;
        }

        float target = CrucibleMaxTemperature();
        float dt = (float)(hoursPassed * RealSecondsPerInGameHour()) * CrucibulumModSystem.Config.HeatRate;
        float f = (1 + GameMath.Clamp((target - heatedTemp) / 30, 0, 1.6f)) * dt;

        // Thermal mass. The same fire poured into more metal raises it more slowly, so a brim-full
        // crucible is a long job before any of it even begins to melt.
        f /= ThermalMass();

        if (work == CrucibleWork.Melting) f /= LatentHeatDivisor;

        heatedTemp = ApproachTemperature(heatedTemp, target, f);
        crucible.Collectible.SetTemperature(Api.World, crucible, heatedTemp);
        return heatedTemp;
    }

    /// <summary>
    /// How much slower this crucible climbs than an empty one, from the mass of metal in it.
    ///
    /// Measured in ingot-equivalents, the same currency the shares and the yield are quoted in, so
    /// twenty nuggets and one ingot weigh the same here as they do everywhere else. The clay itself
    /// has real thermal mass too, which is what keeps an almost-empty crucible from heating
    /// instantly and sets how much the charge matters relative to the vessel.
    ///
    /// Vanilla does not do this for crucibles - the firepit damps by the *container's* stack size,
    /// which for a crucible is always one - so a firepit heats twenty nuggets and a brim-full
    /// crucible at exactly the same rate.
    /// </summary>
    public float ThermalMass()
    {
        float vessel = Math.Max(0.1f, CrucibulumModSystem.Config.CrucibleThermalMass);
        return (vessel + ChargeIngotEquivalents()) / vessel;
    }

    /// <summary>
    /// The metal in the crucible, in ingots. Reads the charge slots for a crucible still being
    /// melted, and the poured contents for one that already has.
    /// </summary>
    public float ChargeIngotEquivalents()
    {
        ItemStack crucible = CrucibleStack;

        if (crucible?.Collectible is BlockSmeltedContainer smelted)
        {
            // Contents are quoted in units, of which an ingot is a hundred.
            return smelted.GetContents(Api.World, crucible).Value / 100f;
        }

        float total = 0;
        foreach (ItemSlot slot in ChargeSlots)
        {
            ItemStack stack = slot.Itemstack;
            if (stack == null) continue;

            CombustibleProperties props = stack.Collectible.GetCombustibleProperties(Api.World, stack, null);
            if (props?.SmeltedStack == null || props.SmeltedRatio <= 0) continue;

            total += (float)stack.StackSize * props.SmeltedStack.ResolvedItemstack.StackSize / props.SmeltedRatio;
        }

        return total;
    }

    /// <summary>
    /// The firepit's own changeTemperature, which cannot be called from here - it is an instance
    /// method on BlockEntityFirepit. Same arithmetic, so both devices ease in the same way.
    /// </summary>
    protected static float ApproachTemperature(float from, float to, float dt)
    {
        float diff = Math.Abs(from - to);
        dt += dt * (diff / 28f);

        if (diff < dt || diff < 1) return to;
        return from + (from > to ? -dt : dt);
    }

    /// <summary>Real seconds one in-game hour is worth, at this world's calendar.</summary>
    public float RealSecondsPerInGameHour()
    {
        float timeMul = Api.World.Calendar.SpeedOfTime * Api.World.Calendar.CalendarSpeedMul;
        if (timeMul <= 0) timeMul = DefaultSpeedOfTime * DefaultCalendarSpeedMul;
        return 3600f / timeMul;
    }

    /// <summary>Accumulates melt time on the same curve the firepit uses.</summary>
    protected bool UpdateMelt(float dt, ItemStack crucible, float temp)
    {
        if (ChargeEmpty || IsMoltenCrucible(crucible) || !CanSmelt(crucible))
        {
            if (meltProgress == 0) return false;
            meltProgress = 0;
            return true;
        }

        float meltingPoint = crucible.Collectible.GetMeltingPoint(Api.World, chargeProvider, WorkItemSlot);
        float before = meltProgress;

        if (meltingPoint > 0 && temp >= meltingPoint)
        {
            meltProgress += GameMath.Clamp((int)(temp / meltingPoint), 1, 30) * dt * CrucibulumModSystem.Config.MeltSpeedMultiplier;
        }
        else if (meltProgress > 0)
        {
            meltProgress = Math.Max(0, meltProgress - dt);
        }

        if (meltProgress > crucible.Collectible.GetMeltingDuration(Api.World, chargeProvider, WorkItemSlot))
        {
            DoSmelt();   // marks dirty itself, and zeroes the progress
            return false;
        }

        // A crucible sitting below its melting point is the common case; syncing an unchanged
        // number five times a second for every forge in the world is not worth it.
        return meltProgress != before;
    }

    protected bool CanSmelt(ItemStack crucible)
    {
        return crucible.Collectible is BlockSmeltingContainer
               && crucible.Collectible.CanSmelt(Api.World, chargeProvider, crucible, null);
    }

    protected void DoSmelt()
    {
        // BlockSmeltingContainer.DoSmelt writes the result to the output slot, nulls the input slot
        // and empties the charge, so it needs a slot of its own to write into.
        DummySlot outputSlot = new DummySlot();
        WorkItemStack.Collectible.DoSmelt(Api.World, chargeProvider, WorkItemSlot, outputSlot);

        meltProgress = 0;

        if (outputSlot.Empty) return;

        WorkItemSlot.Itemstack = outputSlot.Itemstack;
        outputSlot.Itemstack = null;
        WorkItemSlot.MarkDirty();

        Api.World.PlaySoundAt(new AssetLocation("sounds/effect/extinguish"), Pos, 0.25, null, false, 16);
        Api.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), Pos, 0.4375, null, true, 16);
        SpawnReadySparks();

        MarkDirty(true);
    }

    protected static SimpleParticleProperties burstSparks;
    protected static SimpleParticleProperties idleSparks;
    protected static SimpleParticleProperties moltenSmoke;

    /// <summary>
    /// A molten crucible smokes. Vanilla already does this for one held in the hand and one sitting
    /// in ground storage, so a forge that stayed silent would be the odd one out -- and unlike the
    /// glow, a plume carries across a workshop and does not need the player to be able to see down
    /// into the mouth.
    /// </summary>
    protected void OnMoltenParticleTick(float dt)
    {
        // Cheapest checks first: most forges in the world are not holding a molten crucible, and
        // this runs on every one of them several times a second.
        ItemStack crucible = WorkItemStack;
        if (crucible?.Collectible is not BlockSmeltedContainer smelted) return;

        var contents = smelted.GetContents(Api.World, crucible);
        if (contents.Key == null || smelted.HasSolidifed(crucible, contents.Key, Api.World)) return;

        double mouthY = Pos.InternalY + 11 / 16.0 + (FuelLevel - 1) / 64.0 + 5 / 16.0;

        moltenSmoke ??= BlockSmeltedContainer.smokeHeld.Clone(Api.World);
        moltenSmoke.MinQuantity = 1;
        moltenSmoke.AddQuantity = 0;
        moltenSmoke.MinPos.Set(Pos.X + 6.5 / 16.0, mouthY, Pos.Z + 6.5 / 16.0);
        moltenSmoke.AddPos.Set(3 / 16.0, 0.05, 3 / 16.0);
        Api.World.SpawnParticles(moltenSmoke);

        if (Api.World.Rand.NextDouble() < 0.12)
        {
            idleSparks ??= BlockSmeltedContainer.bigMetalSparks.Clone(Api.World);
            idleSparks.MinQuantity = 1;
            idleSparks.AddQuantity = 1;
            idleSparks.MinPos.Set(Pos.X + 6.5 / 16.0, mouthY, Pos.Z + 6.5 / 16.0);
            idleSparks.AddPos.Set(3 / 16.0, 0.05, 3 / 16.0);
            idleSparks.MinVelocity.Set(-0.2f, 0.5f, -0.2f);
            idleSparks.AddVelocity.Set(0.4f, 0.8f, 0.4f);
            Api.World.SpawnParticles(idleSparks);
        }
    }

    protected void SpawnReadySparks()
    {
        SimpleParticleProperties sparks = burstSparks ??= BlockSmeltedContainer.bigMetalSparks.Clone(Api.World);
        sparks.MinQuantity = 12;
        sparks.AddQuantity = 8;
        sparks.MinPos.Set(Pos.X + 6.5 / 16.0, Pos.InternalY + 1.0, Pos.Z + 6.5 / 16.0);
        sparks.AddPos.Set(3 / 16.0, 0.05, 3 / 16.0);
        sparks.MinVelocity.Set(-0.5f, 0.6f, -0.5f);
        sparks.AddVelocity.Set(1f, 1.4f, 1f);
        Api.World.SpawnParticles(sparks);
    }

    #endregion


    #region Dialog

    public string DialogTitle => Lang.Get("crucibulum:forge-crucible-title");

    /// <summary>
    /// The forge is a BlockEntityContainer, not a BlockEntityOpenableContainer, so none of the
    /// open/close plumbing comes for free. This is that protocol, kept deliberately close to
    /// BEOpenableContainer's so it behaves the way every other container in the game does.
    /// </summary>
    protected void ToggleDialog(IPlayer byPlayer)
    {
        if (Api.Side != EnumAppSide.Client) return;
        ICoreClientAPI capi = (ICoreClientAPI)Api;

        if (clientDialog != null)
        {
            clientDialog.TryClose();
            return;
        }

        SyncedTreeAttribute tree = new SyncedTreeAttribute();
        SetDialogValues(tree);

        clientDialog = new GuiDialogCrucibleForge(DialogTitle, Inventory, Pos, tree, capi);
        clientDialog.OnClosed += () =>
        {
            clientDialog = null;
            capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Close, null);
        };

        clientDialog.TryOpen();
        capi.Network.SendPacketClient(Inventory.Open(byPlayer));
        capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Open, null);
    }

    public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
    {
        if (packetid == (int)EnumBlockEntityPacketId.Close)
        {
            player.InventoryManager?.CloseInventory(Inventory);
            return;
        }

        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use))
        {
            Api.World.Logger.Audit("Player {0} sent an inventory packet to a forge crucible at {1} but has no claim access. Rejected.",
                player.PlayerName, Pos);
            return;
        }

        if (packetid == (int)EnumBlockEntityPacketId.Open)
        {
            player.InventoryManager?.OpenInventory(Inventory);
            return;
        }

        if (packetid < 1000)
        {
            Inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
            Api.World.BlockAccessor.GetChunkAtBlockPos(Pos).MarkModified();

            // A slot change can turn a valid alloy into an invalid one; the melt has to notice.
            MarkDirty();
        }
    }

    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid != (int)EnumBlockEntityPacketId.Close) return;

        (Api.World as IClientWorldAccessor)?.Player.InventoryManager.CloseInventory(Inventory);
        clientDialog?.TryClose();
        clientDialog?.Dispose();
        clientDialog = null;
    }

    /// <summary>Everything the dialog draws that is not a slot.</summary>
    public void SetDialogValues(ITreeAttribute tree)
    {
        ItemStack crucible = CrucibleStack;

        tree.SetFloat("crucibleTemp", crucible?.Collectible.GetTemperature(Api.World, crucible)
                                      ?? (WorkItemStack?.Collectible.GetTemperature(Api.World, WorkItemStack) ?? 0));
        tree.SetFloat("maxTemp", CrucibleMaxTemperature());
        tree.SetInt("burning", IsBurning ? 1 : 0);
        tree.SetFloat("fuelLevel", FuelLevel);
        tree.SetFloat("fuelHours", FuelLevel / Math.Max(0.0001f, (1 + extraOxygenRate) * BurnRate));
        tree.SetInt("haveCrucible", crucible != null ? 1 : 0);

        tree.SetFloat("meltProgress", meltProgress);
        tree.SetFloat("maxMeltTime", crucible == null
            ? 0
            : crucible.Collectible.GetMeltingDuration(Api.World, chargeProvider, WorkItemSlot));

        tree.SetString("statusText", DialogStatusText(crucible));
    }

    /// <summary>
    /// The same verdict the block info gives, worded for a window that is already showing the slots:
    /// the shares, and then what will or will not come of them.
    /// </summary>
    protected string DialogStatusText(ItemStack crucible)
    {
        if (crucible == null) return Lang.Get("crucibulum:dlg-nocrucible");
        if (IsMoltenCrucible(crucible))
        {
            var contents = ((BlockSmeltedContainer)crucible.Collectible).GetContents(Api.World, crucible);
            if (contents.Key == null) return "";

            bool solid = ((BlockSmeltedContainer)crucible.Collectible).HasSolidifed(crucible, contents.Key, Api.World);
            return Lang.Get(solid ? "crucibulum:forge-solidified" : "crucibulum:forge-molten",
                contents.Value, BlockSmeltingContainer.GetMetal(contents.Key));
        }

        if (ChargeEmpty) return Lang.Get("crucibulum:dlg-empty");

        ItemStack[] stacks = chargeProvider.Slots.Select(s => s.Itemstack).ToArray();
        StringBuilder sb = new StringBuilder();

        if (stacks.Count(st => st != null) > 1)
        {
            double[] shares = ChargeShares(stacks);
            sb.AppendLine(string.Join(" · ", stacks
                .Select((st, i) => st == null ? null : $"{BlockSmeltingContainer.GetMetal(SmeltedOf(st) ?? st)} {Math.Round(shares[i] * 100)}%")
                .Where(t => t != null)));
        }

        string outputText = (crucible.Collectible as BlockSmeltingContainer)?.GetOutputText(Api.World, chargeProvider, WorkItemSlot);
        if (outputText != null)
        {
            sb.AppendLine(outputText);
        }
        else
        {
            sb.AppendLine(Lang.Get("crucibulum:forge-nomix"));
            AlloyRecipe candidate = CandidateAlloy(stacks);
            if (candidate != null) sb.AppendLine(AlloyRatioText(candidate));
        }

        float meltingPoint = crucible.Collectible.GetMeltingPoint(Api.World, chargeProvider, WorkItemSlot);
        float ceiling = CrucibleMaxTemperature();
        if (meltingPoint > ceiling) sb.AppendLine(Lang.Get("crucibulum:forge-toocold", (int)meltingPoint, (int)ceiling));

        return sb.ToString().TrimEnd();
    }

    protected ItemStack SmeltedOf(ItemStack stack) =>
        stack.Collectible.GetCombustibleProperties(Api.World, stack, null)?.SmeltedStack?.ResolvedItemstack;

    protected string AlloyRatioText(AlloyRecipe alloy)
    {
        string parts = string.Join(", ", alloy.Ingredients.Select(ing => Lang.Get(
            "crucibulum:forge-alloy-part",
            BlockSmeltingContainer.GetMetal(ing.ResolvedItemstack),
            (int)Math.Round(ing.MinRatio * 100),
            (int)Math.Round(ing.MaxRatio * 100))));

        return Lang.Get("crucibulum:forge-alloy-needs",
            BlockSmeltingContainer.GetMetal(alloy.Output.ResolvedItemstack), parts);
    }

    #endregion

    #region Persistence and lifecycle

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);

        meltProgress = tree.GetFloat("meltProgress");

        // Contents just arrived from the server; whatever we last said about them is stale.
        InvalidateStatusText();
        crucibleRenderer?.OnContentsChanged();

        if (Api?.Side == EnumAppSide.Client && clientDialog != null) SetDialogValues(clientDialog.Attributes);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetFloat("meltProgress", meltProgress);
    }

    public override void OnBlockBroken(IPlayer byPlayer = null)
    {
        // BlockEntityForge.OnBlockBroken spawns the fuel and the work item and then clears the whole
        // inventory - which now includes the charge, so it has to go out before that runs.
        foreach (ItemSlot slot in ChargeSlots)
        {
            if (!slot.Empty) Api.World.SpawnItemEntity(slot.Itemstack, Pos);
        }

        base.OnBlockBroken(byPlayer);
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        DisposeClientSide();
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        DisposeClientSide();
    }

    protected void DisposeClientSide()
    {
        crucibleRenderer?.Dispose();
        crucibleRenderer = null;

        if (clientDialog?.IsOpened() == true) clientDialog.TryClose();
        clientDialog?.Dispose();
        clientDialog = null;
    }

    #endregion

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);

        ItemStack crucible = CrucibleStack;

        if (IsMoltenCrucible(crucible))
        {
            var contents = ((BlockSmeltedContainer)crucible.Collectible).GetContents(Api.World, crucible);
            if (contents.Key != null)
            {
                bool solid = ((BlockSmeltedContainer)crucible.Collectible).HasSolidifed(crucible, contents.Key, Api.World);
                dsc.AppendLine(Lang.Get(solid ? "crucibulum:forge-solidified" : "crucibulum:forge-molten",
                    contents.Value, BlockSmeltingContainer.GetMetal(contents.Key)));
            }
            return;
        }

        if (ChargeEmpty) return;

        long now = Api?.World?.ElapsedMilliseconds ?? 0;
        if (statusText == null || now - statusAtMs >= StatusTtlMs)
        {
            statusText = BuildChargeText(crucible);
            statusAtMs = now;
        }

        dsc.Append(statusText);
    }

    /// <summary>
    /// What is in the crucible, what share of the melt each ingredient is, and what it will make.
    ///
    /// The forge has no GUI and this is the only place a player can read any of it, so it has to do
    /// the job the firepit's crucible dialog does -- and one it does not: vanilla says nothing at all
    /// when a mix does not match an alloy, which is indistinguishable from a mix that does. The
    /// percentages are the fix, because alloy recipes are written in exactly those terms.
    /// </summary>
    protected string BuildChargeText(ItemStack crucible)
    {
        ChargeTextRebuilds++;
        StringBuilder dsc = new StringBuilder();

        ItemStack[] stacks = chargeProvider.Slots.Select(s => s.Itemstack).ToArray();
        double[] shares = ChargeShares(stacks);
        bool blend = stacks.Count(st => st != null) > 1;

        for (int i = 0; i < stacks.Length; i++)
        {
            if (stacks[i] == null) continue;

            dsc.AppendLine(blend
                ? Lang.Get("crucibulum:forge-charge-share", stacks[i].StackSize, stacks[i].GetName(), (int)Math.Round(shares[i] * 100))
                : Lang.Get("crucibulum:forge-charge", stacks[i].StackSize, stacks[i].GetName()));
        }

        if (crucible == null)
        {
            dsc.AppendLine(Lang.Get("crucibulum:forge-charge-nocrucible"));
            return dsc.ToString();
        }

        string outputText = (crucible.Collectible as BlockSmeltingContainer)?.GetOutputText(Api.World, chargeProvider, WorkItemSlot);
        if (outputText != null)
        {
            dsc.AppendLine(outputText);
        }
        else
        {
            // Nothing will come out of this. Say so, and say what it would take.
            dsc.AppendLine(Lang.Get("crucibulum:forge-nomix"));

            AlloyRecipe candidate = CandidateAlloy(stacks);
            if (candidate != null)
            {
                string parts = string.Join(", ", candidate.Ingredients.Select(ing => Lang.Get(
                    "crucibulum:forge-alloy-part",
                    BlockSmeltingContainer.GetMetal(ing.ResolvedItemstack),
                    (int)Math.Round(ing.MinRatio * 100),
                    (int)Math.Round(ing.MaxRatio * 100))));

                dsc.AppendLine(Lang.Get("crucibulum:forge-alloy-needs",
                    BlockSmeltingContainer.GetMetal(candidate.Output.ResolvedItemstack), parts));
            }
        }

        float duration = crucible.Collectible.GetMeltingDuration(Api.World, chargeProvider, WorkItemSlot);
        float meltingPoint = crucible.Collectible.GetMeltingPoint(Api.World, chargeProvider, WorkItemSlot);
        float ceiling = CrucibleMaxTemperature();

        if (meltProgress > 0 && duration > 0)
        {
            dsc.AppendLine(Lang.Get("crucibulum:forge-melting", (int)(100 * meltProgress / duration)));
        }
        else if (meltingPoint > ceiling)
        {
            dsc.AppendLine(Lang.Get("crucibulum:forge-toocold", (int)meltingPoint, (int)ceiling));
        }

        return dsc.ToString();
    }

    /// <summary>
    /// Each ingredient's share of the melt, on the same measure alloy recipes use: the metal it
    /// smelts down to, not the number of lumps. Twenty nuggets and one ingot are both 100 units.
    /// </summary>
    protected double[] ChargeShares(ItemStack[] stacks)
    {
        double[] q = new double[stacks.Length];
        double total = 0;

        for (int i = 0; i < stacks.Length; i++)
        {
            if (stacks[i] == null) continue;
            CombustibleProperties props = stacks[i].Collectible.GetCombustibleProperties(Api.World, stacks[i], null);
            if (props?.SmeltedStack == null || props.SmeltedRatio <= 0) continue;

            q[i] = (double)stacks[i].StackSize * props.SmeltedStack.ResolvedItemstack.StackSize / props.SmeltedRatio;
            total += q[i];
        }

        if (total > 0)
        {
            for (int i = 0; i < q.Length; i++) q[i] /= total;
        }

        return q;
    }

    /// <summary>
    /// The one alloy made from exactly the metals currently in the crucible, if there is one -- so a
    /// mix that is merely at the wrong ratio can say which ratio it wants. Null when the crucible
    /// holds a combination no alloy uses, where quoting ranges would only mislead.
    /// </summary>
    protected AlloyRecipe CandidateAlloy(ItemStack[] stacks)
    {
        // Deliberately arrays and nested loops rather than sets: at most four ingredients against a
        // handful of alloys, and this used to allocate a HashSet per alloy on a path that a player
        // looking at the forge runs every frame.
        Span<int> present = stackalloc int[stacks.Length];
        int count = 0;

        foreach (ItemStack stack in stacks)
        {
            if (stack == null) continue;

            CombustibleProperties props = stack.Collectible.GetCombustibleProperties(Api.World, stack, null);
            ItemStack smelted = props?.SmeltedStack?.ResolvedItemstack;
            if (smelted == null) return null;

            int id = smelted.Collectible.Id;
            bool seen = false;
            for (int i = 0; i < count; i++) if (present[i] == id) { seen = true; break; }
            if (!seen) present[count++] = id;
        }

        if (count < 2) return null;

        AlloyRecipe found = null;
        List<AlloyRecipe> alloys = Api.GetMetalAlloys();
        if (alloys == null) return null;

        foreach (AlloyRecipe alloy in alloys)
        {
            if (alloy.Ingredients.Length != count) continue;

            bool matches = true;
            foreach (MetalAlloyIngredient ing in alloy.Ingredients)
            {
                int id = ing.ResolvedItemstack.Collectible.Id;
                bool seen = false;
                for (int i = 0; i < count; i++) if (present[i] == id) { seen = true; break; }
                if (!seen) { matches = false; break; }
            }

            if (!matches) continue;
            if (found != null) return null;   // ambiguous; better to say nothing
            found = alloy;
        }

        return found;
    }


    /// <summary>Presents the four charge slots to BlockSmeltingContainer the way a firepit does.</summary>
    public class ChargeSlotProvider : ISlotProvider
    {
        private readonly InventoryGeneric inv;
        private readonly int first;
        private readonly ItemSlot[] slots;

        // Rebuilt on every access: InventoryBase.FromTreeAttributes is free to hand back fresh slot
        // objects, and a stale array would silently smelt nothing.
        public ItemSlot[] Slots
        {
            get
            {
                for (int i = 0; i < slots.Length; i++) slots[i] = inv[first + i];
                return slots;
            }
        }

        public ChargeSlotProvider(InventoryGeneric inv, int first, int count)
        {
            this.inv = inv;
            this.first = first;
            slots = new ItemSlot[count];
        }
    }
}
