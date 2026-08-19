using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FirstPersonLegs;

/// <summary>
/// Renders the player's own legs in first person, using the same shader pipeline as vanilla FP hands.
///
/// Approach (read before changing anything):
///   We do NOT tessellate the player ourselves. Re-running the player tessellation pipeline (via
///   entity.OnTesselation / EntityShapeRenderer.TesselateShape) is unsafe: under PlayerModelLib it
///   re-enters PlayerSkinBehavior.Tesselate, whose overlay-atlas combine step builds a recursive
///   texture-node graph that goes cyclic for custom models and overflows the stack (StackOverflow in
///   PML's TextureUtils.AppendNodeName). PML also runs that work on a background thread, so re-triggering
///   it from the render loop is racy regardless. Seraph / female-seraph models don't hit it - PML never
///   builds that overlay graph for them - but genuine custom models do.
///
///   Instead we reuse the full-body mesh the player's own renderer already builds and uploads every
///   tessellation, read by reflection off <c>EntityPlayerShapeRenderer.thirdPersonMeshRef</c>. That mesh
///   comes from the renderer's safe, guarded path, so it always has the correct custom model,
///   clothes/armor, skin and joint IDs, and costs us nothing to obtain.
///
///   To show only the legs we exploit the skeleton rather than the geometry: each Opaque frame we upload
///   a modified copy of the animator's joint matrices where every non-leg bone is collapsed to a
///   zero-area point at the model origin - a matrix of all zeros except element 15 = 1. Vertices weighted
///   to those bones degenerate to a single point and don't rasterize, while leg-bone vertices, and any
///   armor/clothing parented to leg bones such as greaves or a belt, keep their real matrices and render
///   normally. That's why worn gear shows on the legs for free, under both vanilla and PlayerModelLib.
///
/// Leg bones:
///   The leg joint set is "everything under LowerTorso minus the UpperTorso subtree", robust to renamed
///   leg bones on custom models, with a name-based UpperFootL/R fallback, plus LowerBackAttachment.
///   entity.GetBoneName() allows per-entity config overrides, the same pattern vanilla arms use.
///   Recomputed whenever the player model re-tessellates, since the skeleton may change on model/skin swap.
/// </summary>
public class FpLegsRenderer : IRenderer
{
    #region IRenderer

    /// <summary>
    /// Render at 0.39: after opaque terrain, <c>SystemRenderTerrain</c> at order 0.37, but before
    /// entities/hands/held items, <c>SystemRenderEntities.OnRenderOpaque3D</c> at order 0.4. That same
    /// pass also draws the first-person arms and held item/block via
    /// <c>EntityPlayerShapeRenderer.DoRender3DOpaque</c>, so there's no separate later pass for hands.
    /// Held blocks with a translucent pass draw alpha-test + full depth write, not real no-depth-write
    /// blending, so legs rendered after them, the old 0.85 value, get depth-stamped-out by the held block
    /// and vanish instead of showing through. Drawing legs first lets the held item's blended pass
    /// composite correctly on top.
    /// </summary>
    public double RenderOrder => 0.39;

    /// <summary>Ignored for global renderers; set to 1 per convention.</summary>
    public int RenderRange => 1;

    #endregion

    #region Private state

    private readonly ICoreClientAPI capi;
    private bool enabled;

    /// <summary>Joint IDs of the leg bones (pelvis + legs + belt). Everything else gets collapsed.</summary>
    private readonly HashSet<int> legJointIds = new();

    /// <summary>When set, <see cref="legJointIds"/> gets recomputed from the live animator next fresh frame.</summary>
    private bool legJointsDirty = true;

    /// <summary>Animator the leg joint set was last computed from - recompute if it changes (model swap).</summary>
    private IAnimator lastAnimator;

    /// <summary>Scratch buffer for the per-frame modified joint matrices, non-leg bones collapsed.</summary>
    private float[] poseBuffer;

    /// <summary>Whether we already logged the no-leg-bones warning, to avoid per-frame log spam.</summary>
    private bool warnedNoLegBones;

    /// <summary>
    /// Latch for the player-model update signal. Set true whenever <c>entity.ShapeFresh</c> == false,
    /// meaning the body is mid-re-tessellation after <c>MarkShapeModified()</c>: armor/clothing
    /// equip-unequip, body-size or model changes, skin edits, shape reloads. ShapeFresh returning true
    /// means the body finished updating, so recompute the leg joint set, since the skeleton may have
    /// changed. Latching rather than edge-detecting means a flag flip between two of our frames never
    /// gets missed.
    /// </summary>
    private bool shapeWasModified;

    /// <summary>Diagnostic logging toggle, read from config.</summary>
    private bool Verbose => FirstPersonLegsCore.Config?.VerboseLogging ?? false;

    /// <summary>
    /// Shared FP-hand shader compiled by ModSystemFpHands ("entityanimated" + ALLOWDEPTHOFFSET) - the
    /// same shader vanilla uses to draw first-person arms.
    /// </summary>
    private ModSystemFpHands modSys;

    /// <summary>
    /// Reflection accessor for the full-body mesh the player renderer builds and keeps. Present on the
    /// vanilla base type EntityPlayerShapeRenderer; PlayerModelLib's renderer derives from it and writes
    /// the same field, so this works for both.
    /// </summary>
    private static readonly FieldInfo ThirdPersonMeshRefField =
        typeof(EntityPlayerShapeRenderer).GetField("thirdPersonMeshRef",
            BindingFlags.NonPublic | BindingFlags.Instance);

    #endregion

    #region Constructor / lifecycle

    public FpLegsRenderer(ICoreClientAPI capi, bool enabled)
    {
        this.capi = capi;
        this.enabled = enabled;

        // Grab the ModSystem that owns the already-compiled FP shader + UBO
        modSys = capi.ModLoader.GetModSystem<ModSystemFpHands>(true);

        // Recompute the leg joint set after atlas/shape reloads (skeleton may have changed)
        capi.Event.ReloadTextures += OnShapeModified;
        capi.Event.ReloadShapes   += OnShapeModified;
    }

    #endregion

    #region Public helpers

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (enabled) legJointsDirty = true; // recompute leg joint set when toggled on
    }

    #endregion

    #region IRenderer

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!enabled) return;

        IClientPlayer player = capi.World.Player;
        if (player == null) return;

        // Only render in first-person, not IFP, not 3rd-person
        if (player.CameraMode != EnumCameraMode.FirstPerson) return;

        // Don't render when GUIs are hidden (F1 mode) - mirrors vanilla arm behavior
        if (capi.HideGuis) return;

        EntityPlayer entityPlayer = player.Entity;
        if (entityPlayer == null) return;

        // Don't render while sleeping - lying down drops the camera to mattress height and the legs
        // would fill the whole view. The tiredness behavior (VSEssentials) flags this in the synced
        // "tiredness"/"isSleeping" watched attribute, so read it directly via API types instead of
        // taking a hard dependency on EntityBehaviorTiredness.
        ITreeAttribute tirednessTree = entityPlayer.WatchedAttributes.GetTreeAttribute("tiredness");
        if (tirednessTree != null && tirednessTree.GetInt("isSleeping", 0) > 0) return;

        // Drive the legs from the third-person animator, not entity.AnimManager. For the self player in
        // first person, AnimManager returns the FP arm animator (selfFpAnimManager), which plays the
        // "-fp" variants meant for the floating-hands view and doesn't run full body/leg locomotion. We
        // render the third-person body mesh (thirdPersonMeshRef), so pair it with the matching
        // third-person animator (TpAnimManager) - the one that plays the full walk/idle cycle, including
        // the Drocust drider's spider-leg motion. Its matrices stay fresh in first person because the
        // self-shadow pass renders the third-person body every frame.
        IAnimator anim = entityPlayer.TpAnimManager?.Animator;
        if (anim == null) return;

        // The player model re-tessellates whenever something calls MarkShapeModified(): armor/clothing
        // equip-unequip, body-size or model changes, skin edits, clearing entity.ShapeFresh until
        // re-tessellation completes. Recompute the leg joint set right after - the skeleton can change on
        // a model swap - by latching the dirty state and acting once ShapeFresh returns true.
        bool shapeFresh = entityPlayer.ShapeFresh;
        if (!shapeFresh)
            shapeWasModified = true;
        else if (shapeWasModified)
        {
            shapeWasModified = false;
            legJointsDirty = true;
        }

        // Also recompute if the animator instance itself changed, since a model swap rebuilds it
        if (!ReferenceEquals(anim, lastAnimator))
        {
            lastAnimator = anim;
            legJointsDirty = true;
        }

        if (legJointsDirty && shapeFresh)
        {
            ComputeLegJoints(anim, entityPlayer);
            legJointsDirty = false;
        }

        if (legJointIds.Count == 0) return;

        // Reuse the player renderer's own full-body mesh: custom model, clothes, correct joints
        if (ThirdPersonMeshRefField == null) return;
        if (entityPlayer.Properties.Client.Renderer is not EntityPlayerShapeRenderer renderer) return;
        if (ThirdPersonMeshRefField.GetValue(renderer) is not MultiTextureMeshRef bodyMeshRef) return;
        if (bodyMeshRef.Disposed) return;

        IShaderProgram shader = modSys?.fpModeHandShader;
        if (shader == null || shader.Disposed) return;

        // ------------------------------------------------------------------
        // 1. Render the legs through a fixed field of view, mirroring how vanilla draws the first-person
        //    arms. EntityPlayerShapeRenderer.DoRender3DOpaque swaps in a hand-FOV projection
        //    (Set3DProjection(ZFar, HandRenderFov)) before drawing the arms and resets it afterwards, so
        //    the arms keep the same on-screen footprint regardless of the player's world FOV. With the
        //    world projection, the old approach, the legs grow/shrink with the FOV slider and their tops
        //    climb into view at high FOV. A perspective FOV only scales screen X/Y - depth is
        //    FOV-independent - so the legs stay correctly occluded by the world while becoming invariant
        //    to the world FOV.
        //
        //    We use a dedicated LegRenderFov (default 70deg) instead of the arms' HandRenderFov: the
        //    look-down fold and size below are tuned against a specific FOV, so the legs get their own
        //    fixed value rather than tracking the player's separate fpHandsFoV preference.
        // ------------------------------------------------------------------
        float legFovRad = (FirstPersonLegsCore.Config?.LegRenderFov ?? 70f) * (MathF.PI / 180f);
        capi.Render.Set3DProjection(capi.Render.ShaderUniforms.ZFar, legFovRad);

        // ------------------------------------------------------------------
        // 2. Bind shader, upload per-frame uniforms.
        // ------------------------------------------------------------------
        shader.Use();

        shader.Uniform("rgbaAmbientIn",   capi.Render.AmbientColor);
        shader.Uniform("rgbaFogIn",       capi.Render.FogColor);
        shader.Uniform("fogMinIn",        capi.Render.FogMin);
        shader.Uniform("fogDensityIn",    capi.Render.FogDensity);
        // Fixed-FOV projection set just above, reset at the end of the frame
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
        shader.Uniform("alphaTest",       0.05f);
        shader.Uniform("lightPosition",   capi.Render.ShaderUniforms.LightPosition3D);

        // Small negative depth bias so the portion of the legs clipping into world geometry (e.g.
        // walking up to a wall) draws over the intersecting block face instead of being depth-occluded
        // by it - the same gl_FragDepth trick vanilla uses for the first-person hands/held item
        // (EntityPlayerShapeRenderer.DoRender3DOpaque), just with a smaller, leg-tuned magnitude since
        // the legs sit farther from the camera than hands and a large bias reads as floating above ground.
        shader.Uniform("depthOffset", -0.02f);

        // Per-entity lighting, sampled from the block position below the player
        Vec4f lightRgbs = capi.World.BlockAccessor.GetLightRGBs(
            (int)entityPlayer.Pos.X,
            (int)(entityPlayer.Pos.InternalY),
            (int)entityPlayer.Pos.Z);
        shader.Uniform("rgbaLightIn",     lightRgbs);

        // Extra uniforms required by entityanimated.vsh / .fsh
        shader.Uniform("extraGlow",       entityPlayer.Properties.Client.GlowLevel);
        shader.Uniform("addRenderFlags",  0);
        shader.Uniform("windWaveIntensity", 0f);
        shader.Uniform("entityId",        (int)entityPlayer.EntityId);
        shader.Uniform("glitchFlicker",   0);
        shader.Uniform("frostAlpha",      0f);
        shader.Uniform("renderColor",     new Vec4f(1f, 1f, 1f, 1f));
        shader.Uniform("glitchEffectStrength", 0f);
        shader.Uniform("waterWaveCounter", capi.Render.ShaderUniforms.WaterWaveCounter);

        // ------------------------------------------------------------------
        // 3. Push the camera-relative model-view matrix and build the model matrix,
        //    based on how Immersive First Person renders the body.
        //
        //    CameraMatrixOrigin: world->view, player entity origin (feet) at world (0,0,0), camera at
        //    eye height above it, full pitch + yaw baked in. Body mesh at its true position lands
        //    below the view when looking ahead, revealed when looking down.
        //
        //    Base transform mirrors EntityPlayerShapeRenderer.loadModelMatrixForPlayer, the
        //    ImmersiveFirstPerson / self branch: body roll, yaw+90deg, walk-cycle pitch, sideways
        //    strafe swivel, entity size scale, then the -0.5 seraph re-centre.
        //
        //    Look-down fold: IFP keeps the body out of the camera when looking down by rotating the
        //    torso/foot bones via PlayerHeadController.SetTorsoOffsets, but only when ImmersiveFpMode is
        //    on. We can't drive those bone poses, so we reproduce the net effect rigidly - as the view
        //    tilts down, lean the assembly back via RotateZ, pivoting near the feet so the pelvis swings
        //    away from the eye, and push it backward in world space so the pelvis clears the bottom of
        //    the view.
        //
        //    Mat4f.X(M,M,...) appends on the right, so code order is the reverse of the vertex-transform
        //    order.
        // ------------------------------------------------------------------
        capi.Render.GlPushMatrix();
        capi.Render.GlLoadMatrix(capi.Render.CameraMatrixOrigin);

        const float Deg = MathF.PI / 180f;

        // Shape base rotations - all 0 for seraph, but read them to match vanilla exactly
        var shape = entityPlayer.Properties.Client.Shape;
        float shapeRotX = shape?.rotateX ?? 0f;
        float shapeRotY = shape?.rotateY ?? 0f;
        float shapeRotZ = shape?.rotateZ ?? 0f;

        float bodyYaw   = entityPlayer.BodyYaw;               // ~smoothedBodyYaw, engine smooths it, close enough
        float walkPitch = entityPlayer.WalkPitch;            // walk-cycle bob
        float swivel    = entityPlayer.sidewaysSwivelAngle;  // strafe lean

        // Look-down amount: 0 at the horizon, 1 looking straight down.
        float lookDown  = GameMath.Clamp(-(float)entityPlayer.Pos.GetViewVector().Y, 0f, 1f);
        bool swimming   = entityPlayer.Swimming;

        // ---- Look-down fold tunables ----------------------------------------
        // Negative LeanRad folds the pelvis backward, away from the facing direction, toward
        // the bottom of the downward view. Positive folded it forward into the view.
        const float LookDownLeanRad = -0.24f; // max backward lean at straight-down (~14deg)
        const float LookDownBackShift = 0.12f; // max metres pushed backward at straight-down
        const float SwimBackShift = 0.6f; // max metres pushed backward while swimming
        const float SwimDownShift = 0.25f; // max metres pushed down while swimming
        float size      = entityPlayer.Properties.Client.Size;

        // ---------------------------------------------------------------------

        float backX, backZ, downY, leanZ;
        if (swimming)
        {
            // The vanilla "Swim" animation bakes an ~80-90deg forward pitch into the LowerTorso/leg
            // bone chain by itself (see FpLegsRenderer header/CLAUDE.md). Real Immersive First Person
            // gets away with also stacking WalkPitch on top because its camera rides the same animated
            // "Eyes" bone, via EntityPlayer.updateLocalEyePosImmersiveFpMode, so camera and legs move
            // together. Our camera is fixed at a plain FP eye position, so adding WalkPitch here just
            // over-rotates the legs past horizontal with nothing compensating - the original "rotated
            // 90deg forward and detached" bug. Skip WalkPitch entirely and instead rigidly shift the
            // whole assembly backward/downward, scaled by how steeply the player is looking to match
            // WalkPitch's own -sin(pitch) target, so the already-pitched pelvis pivot reads as trailing
            // behind the swimmer instead of hanging straight down under the camera.
            float swimPitch = GameMath.Clamp(-(float)entityPlayer.Pos.GetViewVector().Y, -1f, 1f);
            backX = -MathF.Sin(bodyYaw) * SwimBackShift * swimPitch;
            backZ = -MathF.Cos(bodyYaw) * SwimBackShift * swimPitch;
            downY = -SwimDownShift * MathF.Abs(swimPitch);
            leanZ = 0f;
        }
        else
        {
            // World-space backward offset, opposite the body's facing direction. Forward in this
            // frame is (sin, cos); backward is (-sin, -cos). Pushing back moves the pelvis toward
            // the bottom of the downward view.
            backX = -MathF.Sin(bodyYaw) * LookDownBackShift * lookDown;
            backZ = -MathF.Cos(bodyYaw) * LookDownBackShift * lookDown;
            downY = 0f;
            leanZ = LookDownLeanRad * lookDown;
        }

        float[] modelMat = Mat4f.Create();
        Mat4f.Identity(modelMat);

        // Applied LAST to vertices (world space): slide the whole assembly as we look down / swim.
        Mat4f.Translate(modelMat, modelMat, backX, downY, backZ);

        Mat4f.RotateX(modelMat, modelMat, entityPlayer.Pos.Roll + shapeRotX * Deg);
        Mat4f.RotateY(modelMat, modelMat, bodyYaw + (90f + shapeRotY) * Deg);
        // Walk-cycle pitch + look-down backward lean, pivoting near the feet so the pelvis swings back.
        // Skipped while swimming - see the swim branch above for why.
        if (!swimming)
            Mat4f.RotateZ(modelMat, modelMat, walkPitch + shapeRotZ * Deg + leanZ);
        else
            Mat4f.RotateZ(modelMat, modelMat, shapeRotZ * Deg);
        Mat4f.RotateX(modelMat, modelMat, swivel);
        Mat4f.Scale(modelMat, modelMat, size, size, size);
        Mat4f.Translate(modelMat, modelMat, -0.5f, 0f, -0.5f);

        shader.UniformMatrix("modelMatrix",  modelMat);
        shader.UniformMatrix("viewMatrix",   capi.Render.CurrentModelviewMatrix);

        // ------------------------------------------------------------------
        // 4. Upload the joint matrices into the "Animation" UBO, but with every non-leg bone collapsed
        //    to a zero-area point, so only the legs and gear parented to leg bones draw.
        // ------------------------------------------------------------------
        float[] matrices = BuildLegOnlyMatrices(anim);
        shader.UBOs["Animation"].Update(matrices, 0, anim.MaxJointId * 16 * 4);

        // ------------------------------------------------------------------
        // 5. Draw the (joint-masked) body mesh.
        // ------------------------------------------------------------------
        capi.Render.RenderMultiTextureMesh(bodyMeshRef, "entityTex", 0);

        capi.Render.GlPopMatrix();
        shader.Stop();

        // Restore the frame's normal world projection so later renderers (vanilla's FP arms and held
        // item, drawn via SystemRenderEntities at order 0.4) draw with the player's real FOV. Mirrors
        // EntityPlayerShapeRenderer's Reset3DProjection after it finishes the arms.
        capi.Render.Reset3DProjection();
    }

    #endregion

    #region Joint masking

    /// <summary>
    /// Returns a joint-matrix array, in the scratch <see cref="poseBuffer"/>, that is a copy of the
    /// animator's live matrices with every bone not in <see cref="legJointIds"/> replaced by a "collapse"
    /// matrix: all zeros except element 15 = 1. That matrix maps every vertex of a non-leg bone to the
    /// model origin with w = 1, so the resulting zero-area triangles are not rasterized, leaving only the
    /// legs visible. Leg bones keep their real, animated matrices.
    /// </summary>
    private float[] BuildLegOnlyMatrices(IAnimator anim)
    {
        float[] src = anim.Matrices;
        int jointCount = anim.MaxJointId;
        int len = jointCount * 16;

        if (poseBuffer == null || poseBuffer.Length < len)
            poseBuffer = new float[len];

        Array.Copy(src, poseBuffer, len);

        for (int j = 0; j < jointCount; j++)
        {
            if (legJointIds.Contains(j)) continue;

            int b = j * 16;
            for (int k = 0; k < 16; k++) poseBuffer[b + k] = 0f;
            poseBuffer[b + 15] = 1f; // keep w = 1, so collapsed verts land at (0,0,0,1), not NaN
        }

        return poseBuffer;
    }

    private void OnShapeModified()
    {
        legJointsDirty = true;
    }

    /// <summary>
    /// Computes the leg joint set from the live animator: "everything under the lower-body root minus the
    /// UpperTorso subtree". This is model-agnostic; it works even when custom models rename individual
    /// leg bones, as long as the standard LowerTorso/UpperTorso split exists. Falls back to a name-based
    /// UpperFootL/R lookup, plus LowerBackAttachment - belt / lower-back armor - added explicitly.
    ///
    /// Lower-body root is normally LowerTorso. Special case: the Drocust (Locust Drider) race hangs legs,
    /// abdomen and six spider legs off a higher "Body" bone as siblings of LowerTorso, so LowerTorso's own
    /// subtree is just the pelvis, which is why only the pelvis rendered before this fix. Detected
    /// structurally: a "Body" bone that is an ancestor of LowerTorso, then used as the root so the whole
    /// drider lower body comes along, while still excluding the UpperTorso (chest/arms/head) subtree.
    /// Deliberately specific to Drocust; no other supported player model has a "Body" bone above its
    /// LowerTorso, so nothing else is affected. Standard seraph models keep the LowerTorso root, since
    /// their LowerTorso sits directly under the shape root, whose other children are head cosmetics we
    /// must not pull in.
    /// </summary>
    private void ComputeLegJoints(IAnimator animator, EntityPlayer entityPlayer)
    {
        legJointIds.Clear();

        ElementPose poseLowerTorso = animator.GetPosebyName(
            entityPlayer.GetBoneName("lowerTorsoBoneName", "LowerTorso"),
            StringComparison.InvariantCultureIgnoreCase);
        ElementPose poseUpperTorso = animator.GetPosebyName(
            entityPlayer.GetBoneName("upperTorsoBoneName", "UpperTorso"),
            StringComparison.InvariantCultureIgnoreCase);

        // Pick the lower-body root: LowerTorso by default, or the Drocust drider's "Body" container when
        // it exists and is an ancestor of LowerTorso - the only model that needs this.
        ElementPose lowerBodyRoot = poseLowerTorso;
        if (poseLowerTorso != null)
        {
            ElementPose poseBody = animator.GetPosebyName(
                entityPlayer.GetBoneName("lowerBodyRootBoneName", "Body"),
                StringComparison.InvariantCultureIgnoreCase);
            if (poseBody != null && SubtreeContainsJoint(poseBody, poseLowerTorso.ForElement.JointId))
                lowerBodyRoot = poseBody;
        }

        if (lowerBodyRoot != null && poseUpperTorso != null)
        {
            LoadJointIdsRecursive(lowerBodyRoot, legJointIds);

            var skipJoints = new HashSet<int>();
            LoadJointIdsRecursive(poseUpperTorso, skipJoints);
            legJointIds.ExceptWith(skipJoints);
        }
        else
        {
            if (poseLowerTorso != null)
                legJointIds.Add(poseLowerTorso.ForElement.JointId);

            ElementPose poseR = animator.GetPosebyName(
                entityPlayer.GetBoneName("upperFootRBoneName", "UpperFootR"),
                StringComparison.InvariantCultureIgnoreCase);
            ElementPose poseL = animator.GetPosebyName(
                entityPlayer.GetBoneName("upperFootLBoneName", "UpperFootL"),
                StringComparison.InvariantCultureIgnoreCase);
            if (poseR != null) LoadJointIdsRecursive(poseR, legJointIds);
            if (poseL != null) LoadJointIdsRecursive(poseL, legJointIds);
        }

        // LowerBackAttachment: belt / lower-back armor attachment point. Added explicitly in case it's
        // parented under UpperTorso and was therefore excluded above
        ElementPose poseLBA = animator.GetPosebyName(
            "LowerBackAttachment", StringComparison.InvariantCultureIgnoreCase);
        if (poseLBA != null)
            LoadJointIdsRecursive(poseLBA, legJointIds);

        if (Verbose)
            capi.Logger.Notification(
                "[FirstPersonLegs] leg joints: root={0} lowerTorso={1} upperTorso={2} count={3} maxJointId={4}",
                lowerBodyRoot?.ForElement?.Name ?? "<null>",
                poseLowerTorso != null, poseUpperTorso != null, legJointIds.Count, animator.MaxJointId);

        if (legJointIds.Count == 0)
        {
            if (!warnedNoLegBones)
            {
                capi.Logger.Warning("[FirstPersonLegs] FpLegsRenderer: could not find leg bones - " +
                    "no LowerTorso/UpperTorso/UpperFoot poses on this model - legs will not render.");
                warnedNoLegBones = true;
            }
        }
        else
        {
            warnedNoLegBones = false;
        }
    }

    /// <summary>
    /// Recursively collects JointIds from an ElementPose subtree.
    /// Copied directly from EntityPlayerShapeRenderer.loadJointIdsRecursive.
    /// </summary>
    private static void LoadJointIdsRecursive(ElementPose pose, HashSet<int> outIds)
    {
        outIds.Add(pose.ForElement.JointId);
        foreach (ElementPose child in pose.ChildElementPoses)
            LoadJointIdsRecursive(child, outIds);
    }

    /// <summary>True if <paramref name="jointId"/> belongs to any element in this pose's subtree.</summary>
    private static bool SubtreeContainsJoint(ElementPose pose, int jointId)
    {
        if (pose.ForElement.JointId == jointId) return true;
        foreach (ElementPose child in pose.ChildElementPoses)
            if (SubtreeContainsJoint(child, jointId)) return true;
        return false;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        capi.Event.ReloadTextures -= OnShapeModified;
        capi.Event.ReloadShapes   -= OnShapeModified;

        // thirdPersonMeshRef is owned by the player's renderer - we only borrow it, never dispose it
        legJointIds.Clear();
        poseBuffer = null;
    }

    #endregion
}
