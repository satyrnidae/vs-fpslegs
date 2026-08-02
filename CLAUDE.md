# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A **client-only Vintage Story mod** (game 1.20.0, .NET 10) that renders the player's own legs in
first person, reproducing how the vanilla *Immersive First Person* mode draws the body. There is no
server component — `FirstPersonLegsCore.ShouldLoad` returns false on the server side.

## Build & run

The `VINTAGE_STORY` environment variable must point at the game install — every project reference and
the launch config resolve DLLs and the game executable from it.

- **Compile the mod** (fast iteration): `dotnet build -c Debug FirstPersonLegs/FirstPersonLegs.csproj`
  Output goes to `FirstPersonLegs/bin/Debug/Mods/mod` (the `OutputPath` is structured as a loadable
  mod folder).
- **Package a release zip**: `./build.sh` (or `build.ps1`, or `dotnet run --project CakeBuild/CakeBuild.csproj`).
  This runs the Cake pipeline: ValidateJson → Build (clean + `dotnet publish`) → Package. The zip lands
  in `Releases/<modid>_<version>.zip`, named from `FirstPersonLegs/modinfo.json`.
  - Skip JSON asset validation with `./build.sh --skipJsonValidation=true`.
  - `modinfo.json` is the single source of truth for version/modid — Cake reads it via the
    `Vintagestory.API.Common.ModInfo` type, so its fields must stay deserializable.
- **Debug in-game**: VS Code launch config "Launch Client" builds Debug, then starts the game with
  `--addModPath .../bin/Debug/Mods` so the freshly built mod loads. (Note: the launch config's
  CakeBuild path still references `net7.0`, while the projects target `net10.0`.)

There are no automated tests in this repo.

## Architecture

The whole mod is three files in `FirstPersonLegs/`:

- **`FirstPersonLegsCore.cs`** — the `ModSystem` entry point. Loads/creates `firstpersonlegs.json`
  config, constructs the renderer, and registers it on the `Opaque` render stage.
- **`FirstPersonLegsConfig.cs`** — single-flag config (`FpLegsEnabled`).
- **`FpLegsRenderer.cs`** — the entire feature. This is where all the complexity lives.

### How the renderer works (read `FpLegsRenderer.cs`'s header comment first)

The renderer does **not** subclass `EntityShapeRenderer`/`EntityPlayerShapeRenderer`. It reimplements
the vanilla first-person draw inline, which is the key thing to understand before changing anything:

1. **Mesh build** (`RebuildMesh`): calls the player's existing `EntityShapeRenderer.TesselateShape()`
   to run the full vanilla tessellation (which assigns `JointId`s), then in the callback filters the
   resulting `MeshData` down to just the leg-related bone subtrees — `UpperFootL/R` (recursive),
   `LowerBackAttachment` (recursive), and `LowerTorso` (this bone's joint only, non-recursive, to get
   the pelvis/belt without pulling in chest/arms/head). The filtered mesh is uploaded as a
   `MultiTextureMeshRef`. Bone names are resolved via `entity.GetBoneName(...)` so per-entity config
   overrides work, mirroring vanilla arms.

2. **Per-frame draw** (`OnRenderFrame`, runs every Opaque frame): only when camera is FirstPerson and
   GUIs aren't hidden. It reuses the **shared FP-hand shader and "Animation" UBO** owned by
   `ModSystemFpHands` (`capi.ModLoader.GetModSystem<ModSystemFpHands>`) — the same shader vanilla uses
   for first-person arms. It sets the full set of `entityanimated` uniforms, builds a model matrix
   based at `CameraMatrixOrigin` (player feet at world origin, camera at eye height), and uploads the
   animator bone matrices into the Animation UBO before `RenderMultiTextureMesh`.

### Two non-obvious decisions baked into the matrix code

These were deliberate and reverting them reintroduces known bugs (see the inline comments):

- **Fixed-FOV projection, `depthOffset = 0`.** The legs are drawn through a fixed vertical FOV
  (`FirstPersonLegsConfig.LegRenderFov`, default 70°) via `Set3DProjection(ZFar, fov)` /
  `Reset3DProjection()` — the same mechanism vanilla uses for the first-person *arms*
  (`EntityPlayerShapeRenderer.DoRender3DOpaque`, gated on `HandRenderFov`). This makes the legs
  invariant to the player's world FOV slider; with the plain world projection (the old approach) their
  tops climbed into view at high FOV. A perspective FOV only scales screen X/Y — depth is
  FOV-independent — so the legs stay correctly occluded by the world. We use a dedicated `LegRenderFov`
  rather than the arms' `fpHandsFoV` because the look-down fold and size are tuned against a specific
  FOV (70°); an earlier attempt that used a *narrow* hand FOV without re-tuning zoomed the legs up and
  blocked the view. `depthOffset` uses a small negative bias (`FirstPersonLegsConfig.LegDepthOffset`,
  default -0.02) — the same `gl_FragDepth` trick vanilla uses for the first-person hands/held item, so
  the portion of the legs that clips into world geometry (e.g. walking up to a wall) draws over the
  intersecting block face instead of being depth-occluded by it. Kept much smaller than vanilla's hand
  value (~-0.3) since the legs sit farther from the camera; too large a bias makes them visibly float
  above/through the ground even when not clipping.
- **Rigid "look-down fold".** IFP folds the torso out of view when looking down via bone poses
  (`PlayerHeadController.SetTorsoOffsets`), which this mod can't drive. Instead it approximates the net
  effect rigidly: as the view tilts down it leans the assembly back (`LookDownLeanRad`) and pushes it
  backward in world space (`LookDownBackShift`). Those two constants are the tuning knobs for that
  behavior.

Remember `Mat4f.X(M,M,...)` appends on the right, so the code order of transforms is the **reverse**
of the vertex-transform order.

### Reload handling

The mesh is rebuilt lazily via a `meshDirty` flag set on `api.Event.ReloadTextures` /
`api.Event.ReloadShapes` and on enable-toggle. Always go through the flag rather than rebuilding
synchronously — `TesselateShape` marshals its callback to the main thread.

## Reference dependencies

The mod compiles against game DLLs from `$VINTAGE_STORY` with `<Private>false</Private>` (not copied to
output): `VintagestoryAPI`, `VintagestoryLib`, and the bundled mods `VSSurvivalMod`, `VSEssentials`,
`VSCreativeMod` (the latter is where `ModSystemFpHands` lives). Reading these via the
gradle-dependency-analyst-style workflow isn't applicable here — decompile/inspect the DLLs directly if
you need vanilla rendering internals.
