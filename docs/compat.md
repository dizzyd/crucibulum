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

### Other forges

The same patch shape works for any forge whose block entity derives from `BlockEntityForge`, as
long as whatever else that mod adds lives in behaviours rather than in the class. Check with
reflection before assuming - a class that overrides `BurnRate`, replaces the inventory, or does its
own tick will not survive having its class replaced.
