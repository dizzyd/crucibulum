using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Crucibulum;

/// <summary>
/// The forge's crucible window.
///
/// Laid out like the firepit's, because that is the one every player already knows: the four
/// ingredient slots across the top, the crucible below them, fuel below that, and a progress
/// arrow between the crucible and what it will pour. What the firepit does not show, and this
/// does, is the blend — each metal's share of the melt, on the same measure alloy recipes are
/// written in, and the ratio to aim for when the mix is wrong.
/// </summary>
public class GuiDialogCrucibleForge : GuiDialogBlockEntity
{
    private long lastRedrawMs;
    private EnumPosFlag screenPos;
    private string currentStatus = "";
    private bool wasMelting;

    private ElementBounds chargeSlotBounds;
    private ElementBounds meltBarBounds;

    protected override double FloatyDialogPosition => 0.6;
    protected override double FloatyDialogAlign => 0.8;
    public override double DrawOrder => 0.2;

    public GuiDialogCrucibleForge(string title, InventoryBase inventory, BlockPos pos, SyncedTreeAttribute tree, ICoreClientAPI capi)
        : base(title, inventory, pos, capi)
    {
        if (IsDuplicate) return;

        tree.OnModified.Add(new TreeModifiedListener { listener = OnAttributesModified });
        Attributes = tree;
    }

    private void OnInventorySlotModified(int slotid)
    {
        // Recomposing from inside the slot-modified callback throws; queue it instead.
        capi.Event.EnqueueMainThreadTask(SetupDialog, "setupcrucibleforgedlg");
    }

    private void SetupDialog()
    {
        ItemSlot hoveredSlot = capi.World.Player.InventoryManager.CurrentHoveredSlot;
        if (hoveredSlot != null && hoveredSlot.Inventory?.InventoryID != Inventory?.InventoryID) hoveredSlot = null;

        currentStatus = Attributes.GetString("statusText", "");

        // Top to bottom: what is in the crucible, what will come of it, how hot it is and how far
        // along, and what is burning underneath.
        //
        // No output slot and so no arrow between two of them: the crucible is not a furnace that
        // moves metal from one slot to another, it becomes the molten thing in place. An arrow
        // here would point at nothing, which is what the first cut of this window did.
        const double panelWidth = 300;
        const double rowLabelX = 62;
        const double slotSize = 48;
        const double gap = 12;

        CairoFont statusFont = CairoFont.WhiteDetailText();

        chargeSlotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 4, 1);

        // The status block is measured rather than given a fixed height. One line for a plain melt,
        // three for a mix that will not combine - reserving room for the worst case left a hole
        // under the common one, which is what made the window look padded out with nothing.
        double statusY = chargeSlotBounds.fixedY + chargeSlotBounds.fixedHeight + gap;
        double statusHeight = string.IsNullOrEmpty(currentStatus)
            ? 0
            : new TextDrawUtil().GetMultilineTextHeight(statusFont, currentStatus, panelWidth) / RuntimeEnv.GUIScale;

        ElementBounds statusBounds = ElementBounds.Fixed(0, statusY, panelWidth, Math.Max(statusHeight, 1));

        double crucibleY = statusY + statusHeight + (statusHeight > 0 ? gap + 4 : 4);
        ElementBounds crucibleBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, crucibleY, 1, 1);

        // Melting adds a bar under the temperature, so the row is taller only while it is there.
        bool melting = Attributes.GetFloat("maxMeltTime", 0) > 0 && Attributes.GetFloat("meltProgress", 0) > 0;
        ElementBounds crucibleTempBounds = ElementBounds.Fixed(
            rowLabelX, crucibleY + (melting ? 6 : (slotSize - 24) / 2), panelWidth - rowLabelX, 24);

        meltBarBounds = ElementBounds.Fixed(rowLabelX, crucibleY + 32, panelWidth - rowLabelX, 10);

        double fuelY = crucibleY + slotSize + gap;
        ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, fuelY, 1, 1);
        ElementBounds fuelTextBounds = ElementBounds.Fixed(
            rowLabelX, fuelY + (slotSize - 24) / 2, panelWidth - rowLabelX, 24);

        ElementBounds panelBounds = ElementBounds.Fixed(0, 0, panelWidth, fuelY + slotSize);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(panelBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithFixedAlignmentOffset(IsRight(screenPos) ? -GuiStyle.DialogToScreenPadding : GuiStyle.DialogToScreenPadding, 0)
            .WithAlignment(IsRight(screenPos) ? EnumDialogArea.RightMiddle : EnumDialogArea.LeftMiddle);

        if (!capi.Settings.Bool["immersiveMouseMode"])
        {
            dialogBounds.fixedOffsetY += (panelBounds.fixedHeight + 65) * YOffsetMul(screenPos);
            dialogBounds.fixedOffsetX += (panelBounds.fixedWidth + 10) * XOffsetMul(screenPos);
        }

        int[] chargeSlotIds = new int[BlockEntityCrucibulumForge.ChargeSlotCount];
        for (int i = 0; i < chargeSlotIds.Length; i++) chargeSlotIds[i] = BlockEntityCrucibulumForge.FirstChargeSlot + i;

        SingleComposer = capi.Gui
            .CreateCompo("crucibulumforge" + BlockEntityPosition, dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(DialogTitle, () => TryClose())
            .BeginChildElements(bgBounds)
                .AddDynamicCustomDraw(panelBounds, OnBgDraw, "symbolDrawer")
                .AddStaticText(Lang.Get("crucibulum:dlg-charge"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, 6, panelWidth, 22))

                .AddItemSlotGrid(Inventory, SendInvPacket, 4, chargeSlotIds, chargeSlotBounds, "chargeSlots")

                .AddDynamicText("", statusFont, statusBounds, "statusText")

                .AddItemSlotGrid(Inventory, SendInvPacket, 1, new[] { 0 }, crucibleBounds, "crucibleSlot")
                .AddDynamicText("", CairoFont.WhiteDetailText(), crucibleTempBounds, "crucibleTemp")

                .AddItemSlotGrid(Inventory, SendInvPacket, 1, new[] { 1 }, fuelBounds, "fuelSlot")
                .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fuelText")
            .EndChildElements()
            .Compose();

        lastRedrawMs = capi.ElapsedMilliseconds;

        if (hoveredSlot != null) SingleComposer.OnMouseMove(new MouseEvent(capi.Input.MouseX, capi.Input.MouseY));

        SingleComposer.GetDynamicText("statusText").SetNewText(currentStatus, true);
        OnAttributesModified();
    }

    private void OnAttributesModified()
    {
        if (!IsOpened() || SingleComposer == null) return;

        float temp = Attributes.GetFloat("crucibleTemp");
        bool haveCrucible = Attributes.GetInt("haveCrucible") > 0;

        string tempText = !haveCrucible ? ""
            : temp <= 20 ? Lang.Get("Cold")
            : $"{temp:#}°C";
        SingleComposer.GetDynamicText("crucibleTemp").SetNewText(tempText);

        float hours = Attributes.GetFloat("fuelHours");
        bool burning = Attributes.GetInt("burning") > 0;
        string fuelText = Attributes.GetFloat("fuelLevel") <= 0
            ? Lang.Get("crucibulum:dlg-nofuel")
            : burning
                ? Lang.Get("crucibulum:dlg-fuelhours", hours.ToString("0.#"))
                : Lang.Get("crucibulum:dlg-unlit");
        SingleComposer.GetDynamicText("fuelText").SetNewText(fuelText);

        // The status text and the melt bar both change the height of the window, so a change in
        // either has to lay it out again rather than just re-letter it in place.
        string status = Attributes.GetString("statusText", "");
        bool melting = Attributes.GetFloat("maxMeltTime", 0) > 0 && Attributes.GetFloat("meltProgress", 0) > 0;
        if (status != currentStatus || melting != wasMelting)
        {
            currentStatus = status;
            wasMelting = melting;
            capi.Event.EnqueueMainThreadTask(SetupDialog, "setupcrucibleforgedlg");
            return;
        }

        if (capi.ElapsedMilliseconds - lastRedrawMs > 250)
        {
            SingleComposer.GetCustomDraw("symbolDrawer")?.Redraw();
            lastRedrawMs = capi.ElapsedMilliseconds;
        }
    }

    /// <summary>The flame under the fuel slot, and the melt arrow beside the crucible.</summary>
    /// <summary>
    /// The melt bar under the crucible's temperature. Drawn rather than composed so it can simply
    /// not be there when there is nothing melting, instead of sitting empty and looking stuck.
    /// </summary>
    private void OnBgDraw(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        float maxMelt = Attributes.GetFloat("maxMeltTime", 0);
        float progress = Attributes.GetFloat("meltProgress", 0);

        // Nothing melting, no bar. An empty trough sitting there permanently reads as a thing that
        // is stuck rather than a thing that has not started.
        if (maxMelt <= 0 || progress <= 0 || meltBarBounds == null) return;

        double rel = GameMath.Clamp(progress / maxMelt, 0, 1);

        double x = GuiElement.scaled(meltBarBounds.fixedX);
        double y = GuiElement.scaled(meltBarBounds.fixedY);
        double w = GuiElement.scaled(meltBarBounds.fixedWidth);
        double h = GuiElement.scaled(meltBarBounds.fixedHeight);

        ctx.Save();

        // Trough.
        GuiElement.RoundRectangle(ctx, x, y, w, h, 2);
        ctx.SetSourceRGBA(0, 0, 0, 0.4);
        ctx.Fill();

        {
            GuiElement.RoundRectangle(ctx, x, y, Math.Max(GuiElement.scaled(3), w * rel), h, 2);
            LinearGradient molten = new LinearGradient(x, 0, x + w, 0);
            molten.AddColorStop(0, new Color(0.65, 0.18, 0.02, 1));
            molten.AddColorStop(1, new Color(1, 0.66, 0.15, 1));
            ctx.SetSource(molten);
            ctx.Fill();
            molten.Dispose();
        }

        ctx.Restore();
    }

    private void SendInvPacket(object packet) =>
        capi.Network.SendBlockEntityPacket(BlockEntityPosition.X, BlockEntityPosition.Y, BlockEntityPosition.Z, packet);

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Inventory.SlotModified += OnInventorySlotModified;

        screenPos = GetFreePos("smallblockgui");
        OccupyPos("smallblockgui", screenPos);
        SetupDialog();
    }

    public override void OnGuiClosed()
    {
        Inventory.SlotModified -= OnInventorySlotModified;

        SingleComposer.GetSlotGrid("chargeSlots")?.OnGuiClosed(capi);
        SingleComposer.GetSlotGrid("crucibleSlot")?.OnGuiClosed(capi);
        SingleComposer.GetSlotGrid("fuelSlot")?.OnGuiClosed(capi);

        FreePos("smallblockgui", screenPos);
        base.OnGuiClosed();
    }
}
