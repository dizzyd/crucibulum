using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Crucibulum;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VsTestkit.Testing;
using static VsTestkit.Testing.Vs;

namespace Crucibulum.Tests
{
    /// <summary>
    /// The blast gate: a sheet of metal fitted across the forge's air inlet.
    ///
    /// A forge is not set to a temperature, it is given air. Vanilla already models that - the
    /// ceiling is <c>MaxTemperature * (1 + extraOxygenRate)</c> and a bellows drives the multiplier
    /// up - so the gate is the same lever below one rather than a new mechanic. Nothing here should
    /// let a player choose a number.
    /// </summary>
    public class ForgeBlastGate
    {
        /// <summary>A forge is a heat source and these tests stand next to lit ones. See TestLife.</summary>
        [BeforeEach]
        public async Task KeepThePlayerAlive() => await TestLife.Alive();

        static BlockPos ForgePos => P(8, 0, 8);
        static BlockEntityCrucibulumForge Forge => World.BE<BlockEntityCrucibulumForge>(ForgePos);

        static async Task<BlockEntityCrucibulumForge> AForge(bool lit = true)
        {
            World.SetBlock("game:air", ForgePos);
            await Ticks(1);
            World.SetBlock("game:forge", ForgePos);
            await Ticks(2);

            var be = Forge;
            if (lit)
            {
                be.FuelSlot.Itemstack = World.Stack("game:coke", 4);
                be.TryIgnite();
            }
            be.MarkDirty(true);
            return be;
        }

        [VsTest]
        public async Task AForgeWithoutAGateIsUntouched()
        {
            // The whole feature has to be invisible until someone fits a plate, or every forge in
            // every world quietly changes behaviour.
            var be = await AForge();
            Assert.False(be.HasGate, "no gate to start with");
            Assert.Close(1f, be.AirFactor, 0.0001f, "full draught");
            Assert.Close(be.MaxTemperature * (1 + 0f), be.CrucibleMaxTemperature() - CrucibulumModSystem.Config.CrucibleTempBonus, 1f,
                "and the ceiling is exactly what it always was");
        }

        [VsTest]
        public async Task APlateFittedBecomesTheGate()
        {
            var be = await AForge();
            var hand = new DummySlot(World.Stack("game:metalplate-copper", 3));

            Assert.True(be.FitGate(hand, null), "the plate was taken");
            Assert.True(be.HasGate, "the forge has a gate");
            Assert.Equal("copper", be.GateMetal, "made of what was fitted");
            Assert.Equal(2, hand.StackSize, "one plate, not the stack");

            Assert.True(be.TakeGate(null), "and it comes back out");
            Assert.False(be.HasGate, "leaving a plain forge");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TheGateThrottlesTheFireAndTheFuel()
        {
            var be = await AForge();
            float openCeiling = be.CrucibleMaxTemperature();
            float openBurn = be.BurnRate;

            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Shut);
            await Ticks(2);

            Log($"  open: {openCeiling:0}degC at burn {openBurn:0.###} | shut: {Forge.CrucibleMaxTemperature():0}degC at burn {Forge.BurnRate:0.###}");

            Assert.Less(Forge.CrucibleMaxTemperature(), openCeiling, "a shut gate lowers the ceiling");
            Assert.Less(Forge.BurnRate, openBurn, "and burns less fuel, because that is what less air means");
        }

        [VsTest]
        public async Task EveryPositionSitsBetweenShutAndOpen()
        {
            var be = await AForge();
            float last = float.MaxValue;

            foreach (var position in new[] { GatePosition.Open, GatePosition.Half, GatePosition.Quarter, GatePosition.Shut })
            {
                be.FitGateForTesting(World.Stack("game:metalplate-copper"), position);
                float ceiling = be.CrucibleMaxTemperature();
                Log($"  {position}: {ceiling:0} degC");
                Assert.Less(ceiling, last, $"{position} is cooler than the notch before it");
                last = ceiling;
            }
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task AWorkItemIsHeldAtWhatTheDampedFireReaches()
        {
            // The point of the thing, and the part vanilla fights: its tick drives the work item at
            // MaxTemperature regardless, and that property is not virtual. Ours has to pull it back.
            var be = await AForge();
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Shut);

            var ingot = World.Stack("game:ingot-copper");
            be.WorkItemSlot.Itemstack = ingot;
            ingot.Collectible.SetTemperature(Sapi.World, ingot, 1100f);
            be.MarkDirty(true);

            float ceiling = be.MaxTemperature * BlastGate.AirFactor(GatePosition.Shut);
            for (int i = 0; i < 40; i++)
            {
                await Hours(0.05);
                await Ticks(10);
            }

            float temp = Forge.WorkItemStack.Collectible.GetTemperature(Sapi.World, Forge.WorkItemStack);
            Log($"  ingot settled at {temp:0} degC against a damped ceiling of {ceiling:0} degC");
            Assert.Less(temp, 1100f, "the ingot came down off full forge heat");
            Assert.Less(temp, ceiling + 60f, "to about what the damped fire can hold");
        }

        [VsTest]
        public async Task TheReadoutBlamesTheGateRatherThanTheFuel()
        {
            // Coke can melt copper easily. If the gate is what is stopping it, saying "this fuel
            // tops out" sends someone off to find better fuel they already have.
            var be = await AForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Shut);
            be.MarkDirty(true);
            await Ticks(2);

            var sb = new StringBuilder();
            Forge.GetBlockInfo(null, sb);
            string info = sb.ToString();
            Log("  block info: " + info.Replace("\n", " | ").Trim());

            Assert.Contains(info, "Blast gate", "the readout says where the gate is");
            Assert.Contains(info, "gate is holding", "and blames the gate, not the coke");
        }

        [VsTest]
        public async Task TheGateComesBackWhenTheForgeIsBroken()
        {
            var be = await AForge();
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Half);
            await Ticks(2);

            // Items only. World.Entities returns everything in radius, the player included, and
            // clearing leftovers with an unfiltered loop kills him - no damage source, no death
            // reason, just a death screen that then blocks every interaction test after it.
            foreach (var e in World.Entities(ForgePos, 6).OfType<EntityItem>()) e.Die(EnumDespawnReason.Removed);
            await Ticks(2);

            be.OnBlockBroken(null);
            World.SetBlock("game:air", ForgePos);
            await Until(() => World.Entities(ForgePos, 6).OfType<EntityItem>()
                .Any(x => x.Itemstack?.Collectible.Code.Path.StartsWith("metalplate") == true), 60, "the plate to drop");

            Assert.True(true, "the plate came back rather than being destroyed with the forge");
        }

        [VsTest]
        public async Task TheGateSurvivesASaveAndReload()
        {
            var be = await AForge();
            be.FitGateForTesting(World.Stack("game:metalplate-iron"), GatePosition.Quarter);

            var tree = new Vintagestory.API.Datastructures.TreeAttribute();
            be.ToTreeAttributes(tree);

            be.TakeGate(null);
            Assert.False(Forge.HasGate, "cleared before reloading");

            be.FromTreeAttributes(tree, Sapi.World);

            Assert.True(Forge.HasGate, "the gate came back off the tree");
            Assert.Equal(GatePosition.Quarter, Forge.GatePosition, "at the notch it was left on");
            Assert.Equal("iron", Forge.GateMetal, "in the metal it was made of");
            await Task.CompletedTask;
        }

        [VsTest]
        public async Task TurningTheFeatureOffLeavesTheForgeAlone()
        {
            var be = await AForge();
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Shut);
            float damped = be.CrucibleMaxTemperature();

            CrucibulumModSystem.Config.EnableBlastGate = false;
            try
            {
                Assert.Close(1f, Forge.AirFactor, 0.0001f, "a disabled gate throttles nothing");
                Assert.Greater(Forge.CrucibleMaxTemperature(), damped, "and the fire is back to full draught");
            }
            finally
            {
                CrucibulumModSystem.Config.EnableBlastGate = true;
            }
            await Task.CompletedTask;
        }

        /// <summary>Opens the crucible window on a forge that has a gate fitted.</summary>
        static async Task<GuiDialogCrucibleForge> AWindowOverAGatedForge(bool withGate = true)
        {
            // Unlit, and the player out of harm's way. These tests stand someone next to a forge
            // repeatedly, and a lit one is a heat source: left burning across the class the player
            // cooks, dies, and the death screen then blocks every interaction that follows. The
            // gate needs no fire to be read or worked.
            var be = await AForge(lit: false);
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            if (withGate) be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Open);
            be.MarkDirty(true);

            // Long enough for the block entity's tree to reach the client, which is what the window
            // is drawn from - a few ticks and the gate simply reads as absent.
            await Ticks(30);
            await Player.StandNear(ForgePos);

            await Interact.UseBlock(ForgePos, BlockFacing.UP);
            return await Gui.WaitFor<GuiDialogCrucibleForge>(120);
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task TheWindowCarriesTheGate()
        {
            // A forge holding a crucible opens this window on a click, so the click that works the
            // gate on a bare forge is not available - the button is the only way to reach it
            // without pulling the crucible out first.
            var dlg = await AWindowOverAGatedForge();

            await OnClient();
            var button = dlg.SingleComposer.GetButton("gateButton");
            string label = (button as GuiElementTextButton)?.Text;
            await OnServer();

            Log($"  gate button reads: \"{label}\"");
            Assert.NotNull(button, "the window offers the gate");
            Assert.Contains(label ?? "", "Blast gate", "and says what it is");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task AForgeWithNoGateHasNoGateControl()
        {
            // Separate test rather than a second window in the one above: opening, closing and
            // reopening back to back left the second open racing the first close, and it failed
            // intermittently on a dialog that had not finished going away.
            var dlg = await AWindowOverAGatedForge(withGate: false);

            await OnClient();
            bool present = dlg.SingleComposer.GetButton("gateButton") != null;
            await OnServer();

            Assert.False(present, "no gate fitted, no button");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task ClickingTheButtonWorksTheGate()
        {
            // Driven through the button's own mouse handler rather than by sending the packet
            // directly, so this fails if the button is ever wired to nothing.
            var dlg = await AWindowOverAGatedForge();
            Assert.Equal(GatePosition.Open, Forge.GatePosition, "starts open");

            await OnClient();
            var button = dlg.SingleComposer.GetButton("gateButton");
            int x = (int)(button.Bounds.absX + button.Bounds.OuterWidth / 2);
            int y = (int)(button.Bounds.absY + button.Bounds.OuterHeight / 2);
            button.OnMouseDownOnElement(Capi, new MouseEvent(x, y, EnumMouseButton.Left, 0));
            button.OnMouseUpOnElement(Capi, new MouseEvent(x, y, EnumMouseButton.Left, 0));
            await OnServer();

            await Until(() => Forge.GatePosition == GatePosition.Half, 80, "the gate to move a notch");
            Log($"  after one click the gate is {Forge.GatePosition}");

            await Input.Press(GlKeys.Escape);
            await Gui.WaitGone<GuiDialogCrucibleForge>(120);
        }

        [VsTest(TimeoutMs = 120000), RequiresClient]
        public async Task ThePlateAndTheInletAreActuallyDrawn()
        {
            // The shapes are assets, and a missing or malformed one fails silently: the first cut
            // of this drew nothing at all and the only symptom was a bare forge in a screenshot.
            var be = await AForge(lit: false);
            be.FitGateForTesting(World.Stack("game:metalplate-copper"), GatePosition.Half);
            be.MarkDirty(true);
            await Ticks(30);

            await OnClient();
            var clientBe = (BlockEntityCrucibulumForge)Capi.World.BlockAccessor.GetBlockEntity(ForgePos);
            bool hasGate = clientBe?.HasGate == true;

            var gate = Mesh(clientBe, "GateMesh");
            var vent = Mesh(clientBe, "VentMesh");
            await OnServer();

            Assert.True(hasGate, "the client knows about the gate - without this nothing can be drawn");
            Log($"  gate mesh {gate?.VerticesCount ?? -1} verts, inlet mesh {vent?.VerticesCount ?? -1} verts");
            Assert.NotNull(gate, "the plate tesselated");
            Assert.Greater(gate.VerticesCount, 0, "into actual geometry");
            Assert.NotNull(vent, "the inlet tesselated");
            Assert.Greater(vent.VerticesCount, 0, "into actual geometry");
        }

        static MeshData Mesh(BlockEntityCrucibulumForge be, string method) =>
            be?.GetType()
              .GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
              ?.Invoke(be, null) as MeshData;

        // Copper: vanilla makes metal workable at half its melting point, so bits and nuggets are
        // workable from 542 degC and molten at 1084.
        const float CopperWorkable = 542f;
        const float CopperMelts = 1084f;

        static async Task<BlockEntityCrucibulumForge> ACrucibleOfCopperBits(GatePosition? gate)
        {
            var be = await AForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");
            be.AddCharge(new DummySlot(World.Stack("game:nugget-nativecopper", 20)), 20);
            be.FuelSlot.Itemstack = World.Stack("game:coke", 6);   // enough not to burn out mid-run
            if (gate != null) be.FitGateForTesting(World.Stack("game:metalplate-copper"), gate.Value);
            be.MarkDirty(true);

            peakTemp = 0;
            for (int i = 0; i < 60; i++)
            {
                await Hours(0.05);
                await Ticks(6);
                peakTemp = Math.Max(peakTemp, CrucibleTemp(Forge));
            }
            return Forge;
        }

        /// <summary>
        /// The hottest the crucible got, which is not the same as where it ends up: latent heat
        /// holds the temperature almost still while a charge is actually melting, and the fire
        /// burns down afterwards. Reading the final figure to decide whether something melted
        /// reports 933 degC on a crucible that plainly did.
        /// </summary>
        static float peakTemp;

        static float CrucibleTemp(BlockEntityCrucibulumForge be) =>
            be.CrucibleStack.Collectible.GetTemperature(Sapi.World, be.CrucibleStack);

        [VsTest(TimeoutMs = 240000)]
        public async Task ADampedCrucibleHoldsBitsWorkableWithoutMeltingThem()
        {
            // This is the case the whole gate was asked for: Smithing Plus's bit smithing heats
            // bits and native nuggets in a crucible at the workable temperature without letting
            // them melt, and until now the only control over a forge was which fuel you loaded.
            //
            // It runs through a different path from the work item clamp - ApplyGate leaves a
            // crucible alone because CrucibleMaxTemperature already carries the air factor - so it
            // is worth proving rather than assuming the two agree.
            var be = await ACrucibleOfCopperBits(GatePosition.Quarter);

            float temp = CrucibleTemp(be);
            Log($"  quarter-open crucible settled at {temp:0} degC (workable {CopperWorkable:0}, melts {CopperMelts:0})");

            Assert.Greater(temp, CopperWorkable, "hot enough to work the bits");
            Assert.Less(temp, CopperMelts, "but never hot enough to melt them");
            Assert.Less(peakTemp, CopperMelts, "and never was, at any point in the run");
            Assert.False(BlockEntityCrucibulumForge.IsMoltenCrucible(be.CrucibleStack), "still bits, not a pour");
            Assert.Close(0f, be.MeltProgress, 0.001f, "and no melt has begun");
        }

        [VsTest(TimeoutMs = 240000)]
        public async Task TheSameCrucibleMeltsWithTheGateOpen()
        {
            // The control. Without it the test above passes just as well on a forge that could
            // never melt copper in the first place, and proves nothing about the gate.
            var be = await ACrucibleOfCopperBits(GatePosition.Open);

            Log($"  open crucible peaked at {peakTemp:0} degC, melt progress {be.MeltProgress:0.00}");
            Assert.Greater(peakTemp, CopperMelts, "an open forge takes copper past melting");
            Assert.Greater(be.MeltProgress, 0f, "and the charge is going");
        }

        [VsTest]
        public async Task EveryBitSmithingMetalHasANotchThatHoldsIt()
        {
            // Copper, gold and silver are the three Smithing Plus names for bit smithing. Each
            // wants a ceiling at or above half its melting point and below the melting point
            // itself; if no notch lands in that band the feature is useless for that metal.
            var be = await AForge();
            be.WorkItemSlot.Itemstack = World.Stack("game:crucible-brown-fired");

            foreach (var (metal, melts) in new[] { ("copper", 1084f), ("gold", 1063f), ("silver", 961f) })
            {
                float workable = melts / 2;
                string fits = "";

                foreach (var position in new[] { GatePosition.Open, GatePosition.Half, GatePosition.Quarter, GatePosition.Shut })
                {
                    be.FitGateForTesting(World.Stack("game:metalplate-copper"), position);
                    float ceiling = be.CrucibleMaxTemperature();
                    if (ceiling >= workable && ceiling < melts) fits += (fits == "" ? "" : ", ") + position;
                }

                Log($"  {metal}: workable {workable:0}, melts {melts:0} -> {(fits == "" ? "no notch" : fits)}");
                Assert.False(fits == "", $"{metal} bits can be held workable without melting");
            }
            await Task.CompletedTask;
        }
    }
}
