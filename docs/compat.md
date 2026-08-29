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
| applying a cover, wrench click | `BBChiseledCover` (block behaviour) | yes |
| cover mesh, persistence, drops, selection and collision boxes | `BEBChiseledCover` (block entity behaviour) | yes |
| naming the forge after its cover | `BlockDecoForge.GetPlacedBlockName` | no — reimplemented |
| deco interaction, break, tesselation wrappers | their two classes | no — behaviours cover it |

Behaviours are declared on the blocktype rather than the class, so they stay attached when the
class underneath changes. What is actually lost is the five thin methods their two classes add,
four of which have behaviour-level equivalents the engine calls anyway.

The fifth is the block's name. `BlockCrucibulumForge.GetPlacedBlockName` asks their
`BEBChiseledCover.GetChiseledName()` for it — by type and method name, since this mod does not
reference theirs — and falls back to the ordinary block name when that lookup finds nothing, which
is what an uncovered forge shows in any case.

### The tripwire

Patching another mod's asset by path is a coupling: if they rename the blocktype, move the cover
back into the class, or rename the naming method, the patch stops applying or starts applying
wrongly, and nothing in this mod's own suite would notice.

`tests/CompatChiselTools.cs` exists to fail loudly when that happens. Eight tests: four on a bare
chiselled forge (the patch lands, both cover behaviours survive, the naming method still resolves,
metal melts) and four on one with granite actually chiselled onto it - it keeps its cover and its
name through `GetPlacedBlockName`, still melts, survives a save and reload, and hands the cover
back when broken. The covered half is the half that matters: a bare chiselled forge is
indistinguishable from a plain one, so testing only that proves nothing about their feature.

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

Verified against ChiselTools 1.17.6 on Vintage Story 1.22.

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
