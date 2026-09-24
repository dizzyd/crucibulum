# Compatibility with other forges

This mod attaches to the forge by patching its `class` and `entityClass` to its own, so any mod
that ships a forge of its own with its own classes is invisible to it until it is patched too.

## QPTech's ChiselTools

[ChiselTools](https://mods.vintagestory.at/chiseltools) adds `chiseltools:chiseledforge`, a
decorative forge whose front can be chiselled. It declares its own classes:

```
chiseltools.src.BlockDecoForge -> Vintagestory.GameContent.BlockForge
chiseltools.src.BEDecoForge    -> Vintagestory.GameContent.BlockEntityForge
```

Both extend the same vanilla types this mod does, so the two are siblings and cannot both own the
block. Supporting it takes only a patch — `assets/crucibulum/patches/chiseltools-forge.json`,
guarded with `dependsOn` so it is inert when ChiselTools is not installed — because **the
chiselling is not in their classes at all**:

| | where it lives | survives the swap |
|---|---|---|
| applying a cover, wrench click | `BBChiseledCover` (block behaviour) | attached, but not reached — see below |
| cover persistence, drops, selection and collision boxes | `BEBChiseledCover` (block entity behaviour) | yes |
| the cover's mesh | `BEBChiseledCover.OnTesselation` | attached, but not reached — see below |
| naming the forge after its cover | `BlockDecoForge.GetPlacedBlockName` | no — reimplemented |
| break wrapper | `BlockDecoForge.OnBlockBroken` | no — the behaviour covers it |
| calling the cover behaviour on a click | `BlockDecoForge.TryDecoInteract` | no — reimplemented |
| drawing the cover instead of the forge | `BEDecoForge.OnTesselation` | no — reimplemented |

Behaviours are declared on the blocktype rather than the class, so they stay attached when the
class underneath changes — but staying attached is not the same as being called, and **a forge is
deaf to its behaviours in four places**. Three are vanilla's and one was this mod's own, and the
first two were each found by a player rather than by this suite — which is why the other two were
gone looking for rather than waited on.

| chain | swallowed by | what it cost ChiselTools |
|---|---|---|
| `Block.OnBlockInteractStart` → `BlockBehaviors` | vanilla `BlockForge` | no cover could be applied, no wrench click |
| `BlockEntity.OnTesselation` → `Behaviors` | vanilla `BlockEntityForge` | a cover was kept but never drawn |
| `BlockEntity.GetBlockInfo` → `Behaviors` | vanilla `BlockEntityForge` | a locked shape said nothing |
| `BlockEntity.OnReceivedServerPacket` → `Behaviors` | **this mod** | a removed cover went on being drawn |

Restoring a chain is not the end of it, either. ChiselTools cache the mesh they build from a cover,
and clear that cache whenever their own code runs — `SetShape`, `GenMesh`, `DumpInventory`. What
does not clear it is *arriving data*: their `FromTreeAttributes` takes the new stack and leaves the
mesh alone, and a replacement broadcasts no reset packet the way a wrench removal does. A client
that applies a cover itself is fine, because it runs `SetShape` locally on the way past. A client
that only receives the change goes on drawing the cover that was replaced — which is every player
in the world except the one holding the chisel, and so invisible to hands-on testing.
`RemeshChiselledCoverIfChanged` compares the cover before and after each tree and calls their
`GenMesh` when it moved — compared rather than rebuilt unconditionally, because a melting forge
sends a great many trees and building a microblock mesh is not free.

The audit that found the last two is worth repeating on any vanilla class this mod replaces: list
the overrides, check each for a `base` call, and check what the base would have done. The whole
block side chains properly — `BlockForge` swallows only `OnBlockInteractStart` — and on the block
entity side `BlockEntityForge` swallows exactly `GetBlockInfo` and `OnTesselation`. Restoring all
four costs a plain forge nothing: its one entity behaviour, `TemperatureSensitive`, overrides only
`Initialize`.

**`BlockForge` never runs the block behaviour chain.** Its `OnBlockInteractStart` hands the click
to the block entity and returns, so `Block.OnBlockInteractStart` — the loop that gives every
`BlockBehavior` on the blocktype a look at the click — is never reached. Their block class worked
around that by calling `BBChiseledCover.TryAddCover` and `DoWrenchClick` by hand first, in
`TryDecoInteract`. Put our class on their blocktype and both gestures are simply gone: the forge
works as a forge and refuses a chiselled block, which is how a player reported it against 1.4.0.

**`BlockEntityForge` never runs the block entity behaviour chain either.** Its `OnTesselation`
draws the forge's own mesh and returns `true`, so `BlockEntity.OnTesselation` — the loop that lets
a `BlockEntityBehavior` draw — is never reached, and `BEBChiseledCover.OnTesselation` is where the
cover's mesh comes from. `BEDecoForge` existed to do that by hand. Fixing only the click left 1.4.1
in a state the same player reported straight back: the forge took the chiselled block, kept it, and
looked no different, so covers could be cycled in and out invisibly.

**`BlockEntityForge.GetBlockInfo` writes the forge's four lines and returns**, so a behaviour's
own lines never appear. ChiselTools use theirs to say a cover's shape has been locked — and since
ctrl-wrench locking only became reachable again when the click chain was restored, leaving this one
alone would have meant a wrench that silently refuses to work with nothing anywhere saying why.
`BehaviorBlockInfo` writes them, before the crucible lines because those branches return early.

**This mod swallowed the packet chain itself.** `OnReceivedServerPacket` returned early on anything
that was not its own dialog-close, and ChiselTools broadcast packet `32322` on a wrench click to
tell the client to drop the mesh it is drawing. Their `FromTreeAttributes` clears the cover's
*stack* but not the `meshdata` built from it, so that packet is the only thing that clears the
mesh: without it the client went on drawing a cover that had already been dropped on the floor —
1.4.1's bug in a mirror. `OnReceivedClientPacket` had the same hole and now chains too, though
nothing on a forge sends one today.

`BlockEntityCrucibulumForge.TesselateBehaviors` restores the second loop the way
`BlockCrucibulumForge.TryBlockBehaviors` restores the first. A behaviour that draws the block
*replaces* the forge's own mesh rather than being laid over it — which is what `BEDecoForge` did,
and the only thing that looks right, since a cover fills the same cube and drawing both leaves the
two fighting over every face. The blast gate goes on either way: it is hardware rather than
decoration and has to stay visible to be aimed at.

`BlockCrucibulumForge.TryBlockBehaviors` restores the engine's own loop ahead of the forge's
handling, rather than reaching for their behaviour by name, so any other mod hanging a behaviour on
a forge gets its click too. Two consequences worth knowing:

- `BBChiseledCover` claims a click only for `game:chiseledblock` or a wrench, so nothing a forge
  already does is at risk.

### What a cover hides

A cover is drawn *instead of* the forge and fills the whole block, so a cover chiselled as a full
cube swallows the blast gate and its inlet — confirmed by eye, not reasoned about. Carving the
front voxels away is the remedy, and it is the thing ChiselTools exists for.

Nothing functional goes with it. The selection box is still the forge's, because their cover's
`GetSelectionBoxes` is never asked for on a forge — neither `BlockDecoForge` nor this mod's block
class calls it — so a hidden gate still takes the click that works it, and
`ACoveredForgeStillWorksItsGate` holds that, and `APlayerCanWorkAGateThroughACover` holds it
through the client's own raycast rather than a hit position of the test's choosing — the distinction
that matters here, since the cover is drawn `0–1` while the box stays the forge's inset one, so what
the player sees and what the ray hits are no longer the same solid. Pushing the gate mesh clear of the cover was
considered and rejected: there is no offset that is right for an arbitrary chiselled shape, and
anything past `z = 1` sits in the neighbouring block while the selection box stays where it was,
which trades a cosmetic surprise for a real one.
- The vanilla forge blocktype declares `Lockable`, which vanilla's own class likewise never calls.
  With the chain restored, a reinforced-and-locked forge now refuses interaction to players without
  the key, where vanilla lets them use it regardless. That is what the blocktype asks for, but it
  is a change to vanilla forges and not only to ChiselTools', so `tests/ForgeLockable.cs` pins both
  directions: a stranger is refused, and the owner still gets in. The second is the one that would
  hurt — a forge nobody can use looks exactly like a mod that ate someone's base.

  Locking it against the test player is more roundabout than it looks. `IPlayer` carries an
  `internal` member as of 1.22, so a stand-in player cannot be written outside their assembly; and
  `getOrCreateReinforcmentsAt` deserializes the chunk's moddata on every call, so the object
  `GetReinforcment` hands back is a copy and writing to it changes nothing. Their `TryLock` lays the
  record down and only the owner is rewritten afterwards, which keeps the test clear of their index
  and their format.

The other thing their class had that ours needs is the block's name.
`BlockCrucibulumForge.GetPlacedBlockName` asks their `BEBChiseledCover.GetChiseledName()` for it —
by type and method name, since this mod does not reference theirs — and falls back to the ordinary
block name when that lookup finds nothing, which is what an uncovered forge shows in any case.

### The tripwire

Patching another mod's asset by path is a coupling: if they rename the blocktype, move the cover
back into the class, or rename the naming method, the patch stops applying or starts applying
wrongly, and nothing in this mod's own suite would notice.

`tests/CompatChiselTools.cs` exists to fail loudly when that happens. Eighteen tests: seven on a bare
chiselled forge (the patch lands, both cover behaviours survive, a chiselled block goes on with a
click and again with a real one, it still draws as a forge, the naming method still resolves, metal
melts) and eleven on one with granite actually chiselled onto it - it keeps its cover and its name through
`GetPlacedBlockName`, draws that cover instead of the forge, redraws when the cover is
replaced under it, gives it back to a wrench and stops drawing it, names itself in the block info
once its shape is locked, still works its blast gate, still melts, survives a save and reload, and
hands the cover back when broken.
The covered half is the half that matters: a bare chiselled forge is indistinguishable from a plain
one, so testing only that proves nothing about their feature.

Two of them go through the click rather than around it. `AChiselledBlockGoesOnWithAClick` calls
`Block.OnBlockInteractStart` on the server, which is the dispatch that broke;
`APlayerCanClickAChiselledBlockOntoTheForge` stands a player in front of the forge and right-clicks
it, through the client's own input, selection raycast and interaction packet — the path it was
reported on. The other eight reach straight for `BEBChiseledCover.SetShape`, which is exactly why
the suite stayed green while the gesture that calls it was missing.

Two more ask what is actually drawn, which is the gap 1.4.1 fell into: a cover is kept on the block
entity whether or not anything renders it, so ten tests can pass over a forge that looks bare.
`ACoveredForgeDrawsTheCoverAndNotTheForge` hands the client's block entity a recording
`ITerrainMeshPool` and asserts the cover's own `MeshData` reached it and the plain forge mesh did
not; `AnUncoveredChiselledForgeStillDrawsAsAForge` is the other half, that restoring the chain has
not cost a bare forge its shape. Both compare by reference — each mesh is a singleton its owner
hands out — so "this mesh, not that one" is answerable rather than approximate.

The fixture builds a real cover the way the game does, converting a solid block in place and
picking it up. Pass a name to `WasPlaced` - a null one is stored as a set-but-null `blockName` and
vanilla's own `BlockMicroBlock.GetHeldItemName` then splits it and throws.

They no-op when ChiselTools is not installed, so they mean nothing in an ordinary run - put the mod
in the path first:

```bash
cd ../vstestkit
cp /path/to/ChiselTools*.zip /tmp/compat/
cp ../crucibulum/Releases/crucibulum_*.zip /tmp/compat/
ssh <host> 'cd vstestkit-crucibulum && VSTK_EXTRA_MODS=/tmp/compat bash scripts/boot.sh --client'
ssh <host> 'cd vstestkit-crucibulum && bash scripts/run.sh ~/mods/crucibulum/tests --filter CompatChiselTools'
```

Verified against ChiselTools 1.17.7 on Vintage Story 1.22.7.

## Smithing Plus

[Smithing Plus](https://mods.vintagestory.at/smithingplus) hands a tool head back when a tool
breaks. It is a plain vanilla `workitem-<metal>` carrying the recipe's voxels, eroded to
`BrokenToolVoxelPercent` of them, and a `brokenCount` attribute; the mod's intended way to recover
the metal is to chisel it into bits worth what is left. It has no smelting code of its own and does
not touch combustible props, the firepit, or the crucible.

It does not need to. A vanilla firepit crucible refuses a work item, and an ingot, and it does so
by **size**: `InventoryBase.CanContain` compares an item's `size` against the container's
`maxContentDimensions`, the fired crucible declares a mouth of 0.125 × 0.25 × 0.125, and a work
item carries the collectible default of 0.5. The combustible props that would melt it into a
whole ingot are never consulted. Nuggets are 0.0625 on a side and go in.

This mod's charge slot mirrored the crucible's smelting rules and not its mouth, so until 1.4.0 the
forge took all three, and a broken head melted into 100 units - which is the balance hole a
Smithing Plus user reported. `ItemSlotCrucibleCharge.FitsMouth` now applies the same limit the
firepit does, read off the seated crucible's own attribute, so parity holds for any crucible from
any mod that declares one. The two switches, `MeltIngots` and `MeltBrokenToolHeads`, are exemptions
from that check and each admits only its own class.

A crucible has to be *there* for any of that to mean anything. Until 1.4.3 the limit was read off
whatever sat in the work slot, and an absent or already-molten crucible declares no mouth - so with
the crucible on the mouse cursor, or one that had just finished a melt still seated, the size check
and both switches stopped running rather than refusing, and an ingot went in. `Admits` asks for a
fired `BlockSmeltingContainer` first and the mouth second; a modded container that genuinely
declares no mouth still takes what it likes. A broken head is recognised by the `brokenCount`
attribute, on the stack or on the `repairedToolStack` it carries, read by name so nothing of
theirs is referenced.

`tests/ForgeChargeSize.cs` builds a head the way their `ItemDamagedPatches` does - voxels, recipe
id, and the broken tool hung off it as `repairedToolStack` carrying the count - rather than
borrowing theirs, so it runs without the mod and fails if the size rule ever stops holding at the
forge. It was verified in-game first: against a bare 1.22.6 firepit holding a fired crucible, a
nugget went in and an ingot, a work item and a broken head were each refused on the drag, the
shift-click and the put.

Their author's own view is in [issue 102](https://github.com/jayugg/SmithingPlus/issues/102): a
mod that lets work items melt to 100 units is the problem, and the head should be chiselled.

### The tripwire

`tests/CompatSmithingPlus.cs` runs against their code rather than a copy of its output. A copper
pickaxe is worn to its last point in the player's hand and broken under their `DamageItem`
prefix, the work item they hand back is checked for the shape `ForgeChargeSize` assumes, and it is
offered to the forge. The answer must match the config file the game booted with - the tests read
the file and the live config and assert both agree - so a boot on the shipped file exercises the
refusal and a boot with the switches on exercises the admission, which the in-process toggles in
`ForgeChargeSize` cannot show. Their chisel-a-work-item recipe is checked to still be registered,
since that is the path the refusal is protecting.

They no-op without the mod. With it, and both boots:

```bash
cd ../vstestkit
bash scripts/sync-linux.sh dizzyd@vsclient.home --mod ../crucibulum/crucibulum
ssh <host> 'mkdir -p ~/mods/sp-compat && cp smithingplus_*.zip ~/mods/sp-compat/'

# shipped config: refused
ssh <host> 'cd vstestkit-crucibulum && bash scripts/run.sh mods/crucibulum/tests \
    --mod mods/crucibulum/crucibulum --mods ~/mods/sp-compat --client --filter CompatSmithingPlus'

# both switches on, through the file the first boot wrote: admitted
ssh <host> 'cd vstestkit-crucibulum && f=run/crucibulum/data/ModConfig/crucibulum.json &&
    sed -i "s/\"MeltIngots\": false/\"MeltIngots\": true/; s/\"MeltBrokenToolHeads\": false/\"MeltBrokenToolHeads\": true/" $f &&
    VSTK_KEEP=1 bash scripts/run.sh mods/crucibulum/tests \
    --mod mods/crucibulum/crucibulum --mods ~/mods/sp-compat --client --filter CompatSmithingPlus'
```

`VSTK_KEEP=1` is what carries the edited file into the second boot; without it the run directory
is recreated and the shipped defaults come back. Verified against Smithing Plus 1.9.0-rc.1 on
Vintage Story 1.22.6, both boots.

### Their forge patch, and why the base is not called for a crucible

Running the whole suite with them in the pack turned up a second thing, unrelated to melting.
`ShowWorkablePatches` postfixes `BlockEntityForge.GetBlockInfo` to colour the temperature once the
work item is workable, and to do that it asks the work item for its metal material. A vanilla
forge only ever holds metal, so the lookup always succeeds and is cached. A crucible has none: the
lookup fails, the failure is not cached, and the next call walks every smithing and grid recipe
again. `GetBlockInfo` runs once a frame for whatever is being looked at.

| work item in the forge | without Smithing Plus | with |
|---|---|---|
| ingot | 6.7 µs | 4.7 µs |
| fired crucible, empty | 402 µs | **42,697 µs** |
| crucible with a charge | 376 µs | 22,038 µs |

Forty milliseconds a frame is a forge that drops the client to about twenty frames a second for
as long as a crucible is looked at. The cause is on their side - a missing negative cache - but
the trigger is this mod, since nothing else puts a non-metal work item in a forge. With the change
below the empty-crucible call measures 79 µs with them in the pack and 47 µs without.

`BlockEntityCrucibulumForge.GetBlockInfo` therefore calls the base only when the work item is not
a crucible, and otherwise writes the base's four lines itself from the same Lang keys and the same
public fields. A Harmony postfix on the base never runs for a crucible and still runs for every
forge it was written for. `tests/ForgeBlockInfoPatches.cs` installs a counting postfix of its own
and asserts exactly that: one call for a bare forge, one for an ingot, none for a crucible, and
the contents and fuel lines present in all three. The cache test in `ForgeDialog` now measures its
loop and allows rebuilds in proportion, so a slow postfix from some other mod fails it for being
slow rather than for a cache that is working.

## Worlds that predate the mod

The same patching that makes a forge ours only applies to forges placed from then on. A saved
chunk records the class its block entity was written with, so an existing forge loads as
`BlockEntityForge` under a `BlockCrucibulumForge` — half-working, and only fixed by breaking and
replacing it.

`CrucibulumModSystem` swaps those as their chunks load, carrying the old block entity's tree
across. Two things about that are easy to get wrong and are worth keeping:

- **It runs a tick after `ChunkColumnLoaded`, not during.** At the event itself the column is
  present but its block entities are not — every chunk reports zero — so scanning there finds
  nothing to upgrade, silently and always. The column is re-fetched rather than captured, since it
  may have gone again by the time the callback runs.
- **It is gated on the block being ours**, so it upgrades a ChiselTools forge too. Their cover
  rides on a behaviour and travels with the tree; `CompatChiselTools` asserts that, because getting
  it wrong would destroy decorated forges across an existing base rather than fail loudly.

`tests/ForgeUpgrade.cs` builds the legacy state directly — spawn the vanilla block entity by name
onto a block that is ours — because the harness cannot save and reload across two different mod
sets. The end-to-end path was checked by hand instead: plant a forge in a world booted without the
mod, stop, reboot with `VSTK_KEEP=1` and the mod in the path, and confirm the block entity comes
back as ours with its contents.

## ConfigLib

`CrucibulumModSystem.RegisterWithConfigLib` hands the config to
[ConfigLib](https://mods.vintagestory.at/configlib) when it is installed, by reflection against
`ConfigLib.ConfigLibModSystem.RegisterCustomManagedConfig` — so it is optional at build time as
well as run time, and nothing third-party lives in this repo or the release zip.

The settings screen is the visible half. The half that matters is the sync: ConfigLib pushes the
server's values to clients, which this mod does not do on its own. Clients render numbers derived
from this config, so without it a retuned server has every client quoting ceilings that are not
true there — the melting point cue included, which exists precisely so nobody has to guess.

`[Category]`, `[Description]` and `[Range]` on `CrucibulumConfig` are the entire schema, since
`RegisterCustomManagedConfig` reflects over the config object. They are BCL attributes and inert
when ConfigLib is absent. A field added without them appears in the screen as a bare name.

`tests/CompatConfigLib.cs` is the tripwire. Binding against a method name in a mod this one does
not reference would fail silently if ConfigLib renamed or resignatured it — the mod would keep
working and simply stop syncing. The tests assert the binding took, that the method still exists
with its six parameters, and that every setting carries a description. They no-op without
ConfigLib, so run them with it and `vsimgui` in the mod path:

```bash
cp configlib_*.zip vsimgui_*.zip ../crucibulum/Releases/crucibulum_*.zip /tmp/clmods/
ssh <host> 'cd vstestkit-crucibulum && VSTK_EXTRA_MODS=/tmp/clmods bash scripts/boot.sh --client'
ssh <host> 'cd vstestkit-crucibulum && bash scripts/run.sh ~/mods/crucibulum/tests --filter CompatConfigLib'
```

Verified against ConfigLib 1.12.0 (with vsimgui 1.2.7) on Vintage Story 1.22.

### Other forges

The same patch shape works for any forge whose block entity derives from `BlockEntityForge`, as
long as whatever else that mod adds lives in behaviours rather than in the class. Check with
reflection before assuming - a class that overrides `BurnRate`, replaces the inventory, or does its
own tick will not survive having its class replaced.
