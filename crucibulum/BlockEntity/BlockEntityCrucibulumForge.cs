// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
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

        if (api.Side == EnumAppSide.Server) inv.SlotModified += OnSlotModifiedServer;

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

        // A sheet of metal fitted across the air inlet. It is the plate itself that becomes the
        // gate, so there is no new item to craft - and taking it back out leaves a plain forge.
        if (CrucibulumModSystem.Config.EnableBlastGate
            && !slot.Empty && IsGatePlate(slot.Itemstack) && !HasGate && !byPlayer.Entity.Controls.ShiftKey)
        {
            return FitGate(slot, byPlayer);
        }

        if (byPlayer.Entity.Controls.ShiftKey)
        {
            // Shift is the crucible itself, in or back out again, and nothing else. Loading and
            // unloading the charge is the window's job: having a second way to do it meant three
            // more lines of interaction help competing for the same shift-click, and the one the
            // help offered was not always the one that ran.
            //
            // Everything else under shift - fuel, ingots, ignition - is the vanilla forge's.
            if (slot.Empty) return TakeCrucible(byPlayer, blockSel) || TakeGate(byPlayer);
            if (IsCrucible(slot.Itemstack)) return PutCrucible(slot, byPlayer, blockSel);
            return false;
        }

        // A crucible held against a bare forge goes in, exactly as it does at a firepit, which
        // takes one on a plain click too.
        if (!slot.Empty && IsCrucible(slot.Itemstack) && WorkItemSlot.Empty)
        {
            return PutCrucible(slot, byPlayer, blockSel);
        }

        // The gate is worked by clicking the plate. Nothing is typed and no temperature is chosen -
        // the plate moves a notch and the fire goes where the air puts it.
        //
        // On a bare forge that is the whole front of the block, since nothing else there wants the
        // click. On a loaded one it is the plate itself and nothing more: a forge holding an ingot
        // hands it back on a plain click, and taking that click for the gate left an ingot that
        // could only be got out by pulling the whole plate off first.
        if (HasGate && CrucibulumModSystem.Config.EnableBlastGate
            && (WorkItemSlot.Empty || IsGateHit(blockSel?.HitPosition)))
        {
            CycleGate(byPlayer);
            return true;
        }

        // The window belongs to the crucible, so it only opens for a forge that is holding one.
        // A bare forge stays a bare forge: clicking it does nothing, exactly as in vanilla.
        //
        // Smithing keeps working for the same reason. A forge holding an ingot, a plate or a work
        // item hands it over on a plain click - that is the whole rhythm of working at an anvil -
        // because there is no crucible in it to open a window for.
        if (CrucibleStack == null) return false;

        // With a crucible in, the click opens whatever is in hand, which also stops a hot crucible
        // being yanked out by a click meant for the window.
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

    /// <summary>
    /// Shift + empty hand lifts the crucible straight out, and the charge comes with it.
    ///
    /// The charge is handed back first, while the crucible is still in place, so that
    /// <see cref="OnSlotModifiedServer"/> finds nothing left to deal with when the work slot
    /// empties a moment later.
    /// </summary>
    protected bool TakeCrucible(IPlayer byPlayer, BlockSelection blockSel)
    {
        if (CrucibleStack == null) return false;

        TakeCharge(byPlayer);

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

    /// <summary>
    /// The charge lives in the forge's own slots, not in the crucible stack, so a crucible that
    /// leaves by any route other than <see cref="TakeCrucible"/> - dragged out through the window,
    /// or swapped for an ingot there - would otherwise leave its ore behind in slots nobody can
    /// see: the window only opens for a forge holding a crucible, so the ore sits invisible until
    /// the next crucible goes in and silently inherits it. The firepit drops its cooking slots the
    /// moment the container leaves; this does the same, except that it hands the ore to whoever
    /// has the window open rather than tipping it onto the floor at their feet.
    ///
    /// Only the event path. DoSmelt assigns the slot directly, which raises nothing, and it
    /// empties the charge itself - so a melt completing never comes through here.
    /// </summary>
    protected void OnSlotModifiedServer(int slotId)
    {
        if (slotId != 0 || CrucibleStack != null || ChargeEmpty) return;
        TakeCharge(actingPlayer ?? WindowOpener());
    }

    /// <summary>
    /// Whose inventory packet is being handled right now. Set for the duration of
    /// <see cref="OnReceivedClientPacket"/>, so a slot change made through the window can be
    /// traced to the player who made it - which the slot event itself does not say.
    /// </summary>
    protected IPlayer actingPlayer;

    /// <summary>
    /// A player with the window open, if anyone has. The fallback when no packet is in flight,
    /// and an approximation if two players happen to have the same forge open at once.
    /// </summary>
    protected IPlayer WindowOpener()
    {
        foreach (IPlayer player in Api.World.AllOnlinePlayers)
        {
            if (player.InventoryManager?.HasInventory(Inventory) == true) return player;
        }
        return null;
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
    /// Draws the flap onto the forge.
    ///
    /// Into the chunk mesh rather than through the per-frame renderer the crucible uses: a plate
    /// only moves when someone moves it, so it is static geometry and re-meshed on MarkDirty.
    ///
    /// Two things have to be right. The base call draws the forge itself - and, on a ChiselTools
    /// forge, the chiselled cover, which goes through the same path - so skipping it loses both.
    /// And the forge turns to face whoever placed it, so the flap takes the same MeshAngleRad
    /// rotation or it ends up on whichever wall happens to be south.
    /// </summary>
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        bool skipDefault = base.OnTesselation(mesher, tessThreadTesselator);

        // The inlet first, sunk into the wall and never moving, then the flap over it. Without the
        // hole the plate reads as decoration bolted to the front, and an open gate reads as nothing
        // at all rather than as somewhere air gets in.
        MeshData vent = VentMesh();
        if (vent != null)
        {
            vent = vent.Clone();
            vent.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, MeshAngleRad, 0);
            mesher.AddMeshData(vent);
        }

        MeshData gate = GateMesh();
        if (gate != null)
        {
            gate = gate.Clone();
            gate.Translate(BlastGate.Slide(GatePosition), 0, 0);
            gate.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, MeshAngleRad, 0);
            mesher.AddMeshData(gate);
        }

        return skipDefault;
    }

    /// <summary>
    /// The flap, in the metal it was made of, tesselated once per metal and kept - every forge with
    /// a copper gate shares one mesh.
    /// </summary>
    protected MeshData GateMesh()
    {
        if (!HasGate || Api is not ICoreClientAPI capi) return null;

        string metal = GateMetal;
        Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(
            capi, "crucibulumGateMeshes", () => new Dictionary<string, MeshData>());

        if (cache.TryGetValue(metal, out MeshData cached)) return cached;

        Shape shape = Shape.TryGet(capi, "crucibulum:shapes/block/blastgate.json");
        if (shape == null)
        {
            capi.Logger.Warning("[crucibulum] blast gate shape not found");
            return null;
        }

        ITexPositionSource ingots = capi.Tesselator.GetTextureSource(
            capi.World.GetBlock(new AssetLocation("ingotpile")), returnNullWhenMissing: true);
        if (ingots == null || ingots[metal] == null) metal = "copper";

        capi.Tesselator.TesselateShape(
            "crucibulum-blastgate", shape, out MeshData mesh, new MetalTextureSource(capi, ingots, metal));

        cache[GateMetal] = mesh;
        return mesh;
    }

    /// <summary>The inlet behind the flap. One mesh for every forge that has a gate.</summary>
    protected MeshData VentMesh()
    {
        if (!HasGate || Api is not ICoreClientAPI capi) return null;

        return ObjectCacheUtil.GetOrCreate(capi, "crucibulumVentMesh", () =>
        {
            Shape shape = Shape.TryGet(capi, "crucibulum:shapes/block/blastvent.json");
            if (shape == null)
            {
                capi.Logger.Warning("[crucibulum] blast vent shape not found");
                return null;
            }

            capi.Tesselator.TesselateShape(
                "crucibulum-blastvent", shape, out MeshData mesh, new AtlasTextureSource(capi));
            return mesh;
        });
    }

    /// <summary>Resolves a shape's own texture paths straight off the block atlas.</summary>
    private class AtlasTextureSource : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;

        public AtlasTextureSource(ICoreClientAPI capi) => this.capi = capi;

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                capi.BlockTextureAtlas.GetOrInsertTexture(
                    new AssetLocation("game:block/coal/bituminous"), out _, out TextureAtlasPosition pos);
                return pos;
            }
        }
    }

    /// <summary>Paints the flap with whichever metal was fitted.</summary>
    private class MetalTextureSource : ITexPositionSource
    {
        private readonly ICoreClientAPI capi;
        private readonly ITexPositionSource ingots;
        private readonly string metal;

        public MetalTextureSource(ICoreClientAPI capi, ITexPositionSource ingots, string metal)
        {
            this.capi = capi;
            this.ingots = ingots;
            this.metal = metal;
        }

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;
        public TextureAtlasPosition this[string textureCode] => ingots[metal];
    }

    /// <summary>
    /// What the gate is doing, in the only terms that mean anything: how far it is over, and where
    /// the fire ends up because of it. Never a setpoint - the player moved a plate, and this is the
    /// consequence.
    /// </summary>
    public void AppendGateInfo(StringBuilder dsc)
    {
        if (!HasGate) return;

        dsc.AppendLine(Lang.Get("crucibulum:forge-gate",
            Lang.Get(BlastGate.LangKey(GatePosition)), (int)GateCeiling));
    }

    /// <summary>
    /// Where the fire ends up at this notch, for the forge as it is loaded now.
    ///
    /// A crucible is held above the fuel's own ceiling - that is what CrucibleTempBonus is for - so
    /// the two are not the same number, and quoting the bare fuel figure at someone watching a
    /// crucible was 340 degC out on coke at half open. The gate readout is only ever seen in the
    /// crucible window, which is the one place the bare figure is certainly wrong.
    /// </summary>
    public float GateCeiling => CrucibleStack != null
        ? CrucibleMaxTemperature()
        : MaxTemperature * (1 + extraOxygenRate) * AirFactor;

    /// <summary>
    /// Why the charge will not melt. Blaming the fuel is only right when the fuel is the limit - a
    /// throttled gate can hold a fire below its charge's melting point on fuel that would otherwise
    /// manage it easily, and being told to fetch better coke would send someone the wrong way.
    /// </summary>
    protected string TooColdText(float meltingPoint, float ceiling)
    {
        bool gateIsTheLimit = AirFactor < 1f
            && meltingPoint <= ceiling / AirFactor;

        return Lang.Get(
            gateIsTheLimit ? "crucibulum:forge-toocold-gate" : "crucibulum:forge-toocold",
            (int)meltingPoint, (int)ceiling);
    }

    /// <summary>Sheet metal, which is what a blast gate is made of.</summary>
    public static bool IsGatePlate(ItemStack stack) => IsGatePlate(stack?.Collectible);

    public static bool IsGatePlate(CollectibleObject collectible) =>
        collectible?.Code?.Path.StartsWith("metalplate-") == true;

    public bool FitGate(ItemSlot fromSlot, IPlayer byPlayer)
    {
        GateStack = fromSlot.TakeOut(1);
        GatePosition = GatePosition.Open;
        fromSlot.MarkDirty();

        Api.World.PlaySoundAt(new AssetLocation("sounds/block/plate"), Pos, 0.4375, byPlayer, true);
        Api.World.Logger.Audit("{0} fitted a blast gate to the forge at {1}.", byPlayer?.PlayerName, Pos);
        MarkDirty(true);
        return true;
    }

    /// <summary>Fits a gate without a player, for tests and the screenshot scenes.</summary>
    public void FitGateForTesting(ItemStack plate, GatePosition position)
    {
        GateStack = plate;
        GatePosition = position;
        MarkDirty(true);
    }

    public bool TakeGate(IPlayer byPlayer)
    {
        if (!HasGate) return false;

        ItemStack plate = GateStack;
        GateStack = null;
        GatePosition = GatePosition.Open;

        if (byPlayer?.InventoryManager.TryGiveItemstack(plate) != true)
        {
            Api.World.SpawnItemEntity(plate, Pos);
        }

        Api.World.PlaySoundAt(new AssetLocation("sounds/block/plate"), Pos, 0.4375, byPlayer, true);
        MarkDirty(true);
        return true;
    }

    /// <summary>How far up the front of the forge a click still counts as the plate.</summary>
    public const double GateBandTop = 0.5;

    /// <summary>How far out from the middle of the block the gate's face is; the plate is at 15/16.</summary>
    private const double GateFaceDepth = 0.35;

    /// <summary>
    /// Whether a click landed on the plate rather than on the forge.
    ///
    /// The plate slides along one face, and the inlet behind it belongs to the same thing, so this
    /// is the band both live in - the lower front of the block - rather than the plate's exact
    /// voxels at its current notch. Above the band the forge is its hearth, and a click there is a
    /// click for whatever is in the fire.
    ///
    /// The selection box is a plain cuboid in the blocktype and does not turn with the block, while
    /// the plate is drawn into a mesh that does, so the hit has to be rotated back by MeshAngleRad
    /// before it means anything.
    /// </summary>
    public bool IsGateHit(Vec3d hitPosition)
    {
        if (!HasGate || hitPosition == null) return false;
        if (hitPosition.Y > GateBandTop) return false;

        float c = GameMath.Cos(MeshAngleRad);
        float s = GameMath.Sin(MeshAngleRad);
        double x = hitPosition.X - 0.5;
        double z = hitPosition.Z - 0.5;

        // The mesh is rotated by +MeshAngleRad about the block centre, so this is that turn undone.
        return s * x + c * z > GateFaceDepth;
    }

    public void CycleGate(IPlayer byPlayer)
    {
        if (!HasGate) return;

        GatePosition = BlastGate.Next(GatePosition);
        Api.World.PlaySoundAt(new AssetLocation("sounds/block/plate"), Pos, 0.3, byPlayer, true);
        MarkDirty(true);
    }

    /// <summary>
    /// Holds the work item at what the damped fire can actually reach.
    ///
    /// Vanilla's tick drives the work item at MaxTemperature regardless, and that property is not
    /// virtual, so the ceiling cannot be lowered by overriding it. This listener runs after that
    /// one, so the metal is brought back down here instead - at the rate the fire would have heated
    /// it, rather than snapped, so a piece cooling to a damped setting takes the time it should.
    ///
    /// Both directions, because a damped forge is still a fire: a piece below the damped ceiling
    /// climbs to it at vanilla's own rate. This method only ever brought metal *down* until the
    /// ModDB report that an ingot in a throttled forge never heated at all.
    /// </summary>
    protected void ApplyGate(double hoursPassed)
    {
        ItemStack work = WorkItemStack;
        if (work == null || IsCrucible(work))   // the crucible has its own ceiling
        {
            gatedStack = null;
            return;
        }

        float stackTemp = work.Collectible.GetTemperature(Api.World, work);

        // Full draught, or a dead fire, is vanilla's business again - but the shadow still follows
        // the stack rather than being abandoned where it stood. Left holding a reading from minutes
        // ago, the next click on the gate wrote that reading straight back over the metal: a piece
        // brought up to heat with the gate open snapped back down to whatever it had been the last
        // time the gate was closed.
        if (AirFactor >= 1f || !IsBurning)
        {
            gatedStack = work;
            gatedTemp = stackTemp;
            return;
        }

        // The same shadow HeatCrucible keeps, and for the same reason. Vanilla's tick drives the
        // work item at MaxTemperature every tick, about ten times harder than this pulls back, so
        // simply nudging the stack down leaves the two fighting and settling well above the damped
        // ceiling - measured at 563 degC against a target of 440. Holding our own figure and
        // writing it over the top each tick ignores that rise, exactly as the crucible does.
        //
        // A fall is still real and wins: dousing, or the stack cooling on its own between ticks.
        if (!ReferenceEquals(gatedStack, work) || stackTemp < gatedTemp)
        {
            gatedStack = work;
            gatedTemp = stackTemp;
        }

        float ceiling = MaxTemperature * (1 + extraOxygenRate) * AirFactor;

        if (gatedTemp > ceiling)
        {
            float dt = (float)(hoursPassed * RealSecondsPerInGameHour()) * CrucibulumModSystem.Config.HeatRate;
            float step = (1 + GameMath.Clamp((gatedTemp - ceiling) / 30, 0, 1.6f)) * dt;
            gatedTemp = ApproachTemperature(gatedTemp, ceiling, step);
        }
        else
        {
            // And it climbs, which for a long time it did not: the shadow only ever fell, so a
            // damped forge pinned the metal at whatever it was holding and a cold ingot under a
            // throttled gate simply never heated.
            //
            // At vanilla's own rate, which is what its tick would have done unthrottled, so a
            // damped fire heats a piece exactly as it always did and only stops lower.
            gatedTemp = Math.Min(ceiling,
                gatedTemp + (float)(hoursPassed * VanillaForgeTempGainPerHour * (1 + extraOxygenRate)));
        }

        // Written every tick, not only while the figure is still falling. Stopping once the shadow
        // settled on the ceiling left vanilla's tick free to drive the stack straight back up to
        // the undamped ceiling, which is what a damped forge did until this line moved out of the
        // block above: the shadow read 440 and the metal sat at 800.
        if (stackTemp > gatedTemp + 0.01f)
        {
            work.Collectible.SetTemperature(Api.World, work, gatedTemp, false);
            MarkDirty();
        }
    }

    /// <summary>The work item temperature the gate is holding. See ApplyGate.</summary>
    protected float gatedTemp;
    protected ItemStack gatedStack;

    /// <summary>
    /// Degrees an in-game hour the vanilla forge tick adds to a work item, before the oxygen
    /// multiplier. BlockEntityForge's own figure, kept here so a damped fire heats at the rate an
    /// undamped one would rather than at some rate of this mod's invention.
    /// </summary>
    protected const double VanillaForgeTempGainPerHour = 1500;

    /// <summary>The ingot equivalent of one charge stack: what it would smelt down to.</summary>
    protected float IngotEquivalents(ItemStack stack)
    {
        CombustibleProperties props = stack?.Collectible.GetCombustibleProperties(Api.World, stack, null);
        if (props?.SmeltedStack == null || props.SmeltedRatio <= 0) return 0;
        return (float)stack.StackSize * props.SmeltedStack.ResolvedItemstack.StackSize / props.SmeltedRatio;
    }

    /// <summary>
    /// Brings colder charge and the crucible to one temperature, weighted by mass - the clay
    /// included, since it is carrying heat of its own. Throw a fistful of cold ore into a nearly
    /// molten crucible and you have genuinely set yourself back, which is what happens at a real
    /// furnace.
    ///
    /// This runs on the tick rather than where metal is added, because the window puts metal
    /// straight into the slots through the ordinary inventory machinery - there is no single
    /// "charge added" call left to hang it off. It used to hang off one, which meant loading
    /// through the window skipped the penalty entirely while shift-clicking paid it.
    ///
    /// A stack already at the crucible's temperature is in equilibrium and contributes nothing, so
    /// this does nothing in the ordinary case and settles up exactly once per arrival. AddCharge
    /// matches temperatures itself before merging, so metal that came that way is already settled
    /// by the time this sees it and is not charged twice.
    /// </summary>
    protected float EqualiseCharge(ItemStack crucible, float crucibleTemp)
    {
        float settled = Math.Max(0.1f, CrucibulumModSystem.Config.CrucibleThermalMass);
        float coldMass = 0, coldHeat = 0, total = 0;

        foreach (ItemSlot slot in ChargeSlots)
        {
            ItemStack stack = slot.Itemstack;
            if (stack == null) continue;

            float mass = IngotEquivalents(stack);
            if (mass <= 0) continue;
            total += mass;

            // Colder only. Metal hotter than the crucible is left to the sync below, which is the
            // behaviour this replaced and not something to change quietly here.
            float temp = stack.Collectible.GetTemperature(Api.World, stack);
            if (temp < crucibleTemp - 0.5f) { coldMass += mass; coldHeat += temp * mass; }
            else settled += mass;
        }

        // Only when metal has actually arrived. The charge is synced to the crucible at the end of
        // every tick, so it always reads one tick's heating *behind* the crucible - which looks
        // exactly like cold metal. Settling up on that would drag the crucible back down every
        // tick and a crucible would never reach its melting point at all.
        bool arrived = lastChargeIngots >= 0 && total > lastChargeIngots + 0.0001f;
        lastChargeIngots = total;
        if (!arrived) return crucibleTemp;

        if (coldMass <= 0) return crucibleTemp;

        float mixed = (crucibleTemp * settled + coldHeat) / (settled + coldMass);
        crucible.Collectible.SetTemperature(Api.World, crucible, mixed);
        heatedTemp = mixed;
        heatedStack = crucible;
        return mixed;
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
            // Less air is less fuel burnt, which is what makes damping the fire a trade rather
            // than a free lunch: run cool and the coke lasts.
            float rate = base.BurnRate * AirFactor;
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

        float fuelCeiling = (MaxTemperature + cfg.CrucibleTempBonus) * (1 + extraOxygenRate) * AirFactor;

        return Math.Min(ceiling, fuelCeiling);
    }

    /// <summary>
    /// The air inlet's flap, and the plate someone fitted to make one. No plate, no gate, and the
    /// forge behaves exactly as it always did.
    /// </summary>
    public GatePosition GatePosition { get; protected set; } = GatePosition.Open;

    public ItemStack GateStack { get; protected set; }
    public bool HasGate => GateStack != null;

    /// <summary>What the fitted gate does to the fire, or nothing at all when there is no gate.</summary>
    public float AirFactor =>
        HasGate && CrucibulumModSystem.Config.EnableBlastGate ? BlastGate.AirFactor(GatePosition) : 1f;

    public string GateMetal =>
        GateStack?.Collectible.Variant.TryGetValue("metal", out string metal) == true ? metal : "copper";

    /// <summary>Charge mass at the last tick, to spot metal arriving. See EqualiseCharge.</summary>
    protected float lastChargeIngots = -1;

    /// <summary>The crucible temperature clients were last told about. See OnCrucibleTick.</summary>
    protected float lastSyncedTemp = float.MinValue;

    protected void OnCrucibleTick(float dt)
    {
        if (Api.Side != EnumAppSide.Server) return;

        double hoursPassed = Api.World.Calendar.TotalHours - lastCrucibleTickHours;
        if (hoursPassed < 0) hoursPassed = 0;
        lastCrucibleTickHours = Api.World.Calendar.TotalHours;

        ApplyGate(hoursPassed);

        ItemStack crucible = CrucibleStack;
        if (crucible == null)
        {
            // No crucible: any leftover charge just cools on its own in the itemstack.
            if (meltProgress != 0) { meltProgress = 0; MarkDirty(); }
            wasMolten = false;
            return;
        }

        // Vanilla relights an unfuelled forge from its own hot contents. Its idle branch calls
        // TryIgnite() whenever the work item is over 900 degC, and TryIgnite() tests only whether
        // it is already burning - not CanIgnite, which is the property that knows about fuel. The
        // relit tick then heats the contents and, through SetTemperature's delayCooldown, pushes
        // the next cooling half an in-game hour into the future. A molten crucible sits far above
        // 900, so it relights the dead forge every few seconds and holds its own melt for nothing.
        //
        // This listener is registered after base.Initialize, so it runs after the vanilla tick:
        // putting the fire out here lands between the relight and the tick that would spend it.
        //
        // Only while the crucible is actually drawing heat. A forge keeping an ingot warm behaves
        // exactly as vanilla does, which is what someone smithing expects.
        if (burning && FuelLevel <= 0 && CrucibleDrawsHeat)
        {
            burning = false;
            MarkDirty(true);
        }

        // The vanilla forge tick already pushed the crucible up to the forge's own ceiling and then
        // stopped. Carry it the rest of the way to the crucible ceiling; vanilla never lowers a
        // temperature, so the two ticks do not fight.
        CrucibleWork work = WorkState;
        float crucibleTemp = HeatCrucible(crucible, hoursPassed, work);
        crucibleTemp = EqualiseCharge(crucible, crucibleTemp);

        // Only when something actually moved. Marking dirty serialises all six slots and sends them
        // to every client in range, and a forge sitting at its ceiling has nothing to tell them.
        //
        // Measured against what clients were last *told*, not against a reading taken a few lines
        // earlier in this same tick. GetTemperature performs the cooling as a side effect of being
        // called, so a before/after pair around it always reports no change while the crucible is
        // cooling - which is why an open window sat frozen at whatever the temperature had been
        // when the fire went out. Half a degree, because the readout is in whole ones.
        bool dirty = Math.Abs(crucibleTemp - lastSyncedTemp) > 0.5f;

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
        lastSyncedTemp = crucibleTemp;
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

    /// <summary>
    /// Our own packet, kept clear of vanilla's block entity ids and of the slot packets, which are
    /// everything under 1000.
    /// </summary>
    public const int CycleGatePacketId = 1701;

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

        // Working the gate through the window. A forge holding a crucible opens the window on a
        // click, so the click that works the gate on a bare forge is not available here.
        if (packetid == CycleGatePacketId)
        {
            CycleGate(player);
            return;
        }

        if (packetid == (int)EnumBlockEntityPacketId.Open)
        {
            player.InventoryManager?.OpenInventory(Inventory);
            return;
        }

        if (packetid < 1000)
        {
            actingPlayer = player;
            try
            {
                Inventory.InvNetworkUtil.HandleClientPacket(player, packetid, data);
            }
            finally
            {
                actingPlayer = null;
            }
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

        // What it has to reach before anything happens. Zero once it is molten, when the question
        // has stopped being interesting, so the window can just ask whether this is above zero.
        tree.SetFloat("meltingPoint", crucible == null || IsMoltenCrucible(crucible)
            ? 0
            : crucible.Collectible.GetMeltingPoint(Api.World, chargeProvider, WorkItemSlot));

        tree.SetFloat("meltProgress", meltProgress);
        tree.SetFloat("maxMeltTime", crucible == null
            ? 0
            : crucible.Collectible.GetMeltingDuration(Api.World, chargeProvider, WorkItemSlot));

        tree.SetInt("haveGate", HasGate ? 1 : 0);
        tree.SetString("gatePositionKey", BlastGate.LangKey(GatePosition));
        tree.SetInt("gateCeiling", (int)GateCeiling);

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
        if (meltingPoint > ceiling) sb.AppendLine(TooColdText(meltingPoint, ceiling));

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
        GatePosition = (GatePosition)tree.GetInt("gatePosition");
        GateStack = tree.GetItemstack("gateStack");
        GateStack?.ResolveBlockOrItem(worldForResolving);

        // Contents just arrived from the server; whatever we last said about them is stale.
        InvalidateStatusText();
        crucibleRenderer?.OnContentsChanged();

        if (Api?.Side == EnumAppSide.Client && clientDialog != null) SetDialogValues(clientDialog.Attributes);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        tree.SetFloat("meltProgress", meltProgress);
        tree.SetInt("gatePosition", (int)GatePosition);
        if (GateStack != null) tree.SetItemstack("gateStack", GateStack);
    }

    public override void OnBlockBroken(IPlayer byPlayer = null)
    {
        // BlockEntityForge.OnBlockBroken spawns the fuel and the work item and then clears the whole
        // inventory - which now includes the charge, so it has to go out before that runs.
        foreach (ItemSlot slot in ChargeSlots)
        {
            if (!slot.Empty) Api.World.SpawnItemEntity(slot.Itemstack, Pos);
        }

        if (GateStack != null)
        {
            Api.World.SpawnItemEntity(GateStack, Pos);
            GateStack = null;
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

        AppendGateInfo(dsc);

        float duration = crucible.Collectible.GetMeltingDuration(Api.World, chargeProvider, WorkItemSlot);
        float meltingPoint = crucible.Collectible.GetMeltingPoint(Api.World, chargeProvider, WorkItemSlot);
        float ceiling = CrucibleMaxTemperature();

        if (meltProgress > 0 && duration > 0)
        {
            dsc.AppendLine(Lang.Get("crucibulum:forge-melting", (int)(100 * meltProgress / duration)));
        }
        else if (meltingPoint > ceiling)
        {
            dsc.AppendLine(TooColdText(meltingPoint, ceiling));
        }
        else if (meltingPoint > 0 && !IsMoltenCrucible(crucible))
        {
            // Otherwise there is no way to find out what it is waiting for except to watch the
            // number climb and hope. The fuel can reach this one - that is the branch above.
            dsc.AppendLine(Lang.Get("crucibulum:forge-meltsat", (int)meltingPoint));
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
