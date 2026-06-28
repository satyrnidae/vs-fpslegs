using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FirstPersonLegs;

/// <summary>
/// Renders the player's own legs in first person, using the same shader pipeline as the vanilla FP
/// hands.
///
/// Approach (important — read before changing anything):
///   We do NOT tessellate the player ourselves. Re-running the player tessellation pipeline (via
///   entity.OnTesselation / EntityShapeRenderer.TesselateShape) is unsafe: under PlayerModelLib it
///   re-enters PlayerSkinBehavior.Tesselate, whose overlay-atlas combine step builds a recursive
///   texture-node graph that becomes cyclic for custom models and overflows the stack (StackOverflow in
///   PML's TextureUtils.AppendNodeName). PML also runs that work on a background thread, so re-triggering
///   it from the render loop is racy regardless. The seraph / female-seraph models don't hit it because
///   PML never builds that overlay graph for them; genuine custom models do.
///
///   Instead we REUSE the full-body mesh the player's own renderer already builds and uploads every
///   tessellation — <c>EntityPlayerShapeRenderer.thirdPersonMeshRef</c> (read by reflection). That mesh
///   is produced by the renderer's safe, guarded path, so it always has the correct custom model,
///   clothes/armor, skin and joint IDs, and costs us nothing to obtain.
///
///   To show ONLY the legs we exploit the skeleton rather than the geometry: each Opaque frame we upload
///   a modified copy of the animator's joint matrices in which every NON-leg bone is collapsed to a
///   zero-area point at the model origin (a matrix of all zeros except element 15 = 1). Vertices weighted
///   to those bones degenerate to a single point and are not rasterised, while leg-bone vertices (and any
///   armor/clothing parented to leg bones — greaves, belt…) keep their real matrices and render normally.
///   This is why worn gear shows on the legs for free, under both vanilla and PlayerModelLib.
///
/// Leg bones:
///   The leg joint set is "everything under LowerTorso minus the UpperTorso subtree" (robust to renamed
///   leg bones on custom models), with a name-based UpperFootL/R fallback, plus LowerBackAttachment.
///   entity.GetBoneName() allows per-entity config overrides (same pattern as vanilla arms). It is
///   recomputed whenever the player model re-tessellates (skeleton may change on model/skin swap).
/// </summary>
public class FpLegsRenderer : IRenderer
{
    // -----------------------------------------------------------------
    // IRenderer
    // -----------------------------------------------------------------

    /// <summary>
    /// Render at 0.85 — after most opaque geometry (0.5) but before the hands (which appear to
    /// run inside EntityPlayerShapeRenderer at ~0.9).
    /// </summary>
    public double RenderOrder => 0.85;

    /// <summary>Ignored for global renderers; set to 1 as per convention.</summary>
    public int RenderRange => 1;

    // -----------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------

    private readonly ICoreClientAPI capi;
    private bool enabled;

    /// <summary>Joint IDs of the leg bones (pelvis + legs + belt). Everything else is collapsed.</summary>
    private readonly HashSet<int> legJointIds = new();

    /// <summary>When set, <see cref="legJointIds"/> is recomputed from the live animator next fresh frame.</summary>
    private bool legJointsDirty = true;

    /// <summary>The animator the leg joint set was last computed from — recompute if it changes (model swap).</summary>
    private IAnimator lastAnimator;

    /// <summary>Scratch buffer for the per-frame modified joint matrices (non-leg bones collapsed).</summary>
    private float[] poseBuffer;

    /// <summary>Whether we already logged that no leg bones were found (avoid per-frame log spam).</summary>
    private bool warnedNoLegBones;

    /// <summary>
    /// Latch for the player-model update signal. Set true whenever we observe <c>entity.ShapeFresh</c>
    /// == false (the body is mid-re-tessellation after <c>MarkShapeModified()</c> — armor/clothing
    /// equip-unequip, body-size or model changes, skin edits, shape reloads). When ShapeFresh returns to
    /// true we know the body finished updating and recompute the leg joint set (the skeleton may have
    /// changed). Latching rather than edge-detecting means we never miss a change that flips the flag
    /// between two of our frames.
    /// </summary>
    private bool shapeWasModified;

    /// <summary>Diagnostic logging toggle, read from config.</summary>
    private bool Verbose => FirstPersonLegsCore.Config?.VerboseLogging ?? false;

    /// <summary>
    /// Reuse the shared FP-hand shader compiled by ModSystemFpHands ("entityanimated" +
    /// ALLOWDEPTHOFFSET).  This is the same shader that draws vanilla first-person arms.
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

    // -----------------------------------------------------------------
    // Constructor / lifecycle
    // -----------------------------------------------------------------

    public FpLegsRenderer(ICoreClientAPI capi, bool enabled)
    {
        this.capi = capi;
        this.enabled = enabled;

        // Grab the ModSystem that owns the already-compiled FP shader + UBO.
        modSys = capi.ModLoader.GetModSystem<ModSystemFpHands>(true);

        // Recompute the leg joint set after atlas/shape reloads (the skeleton may have changed).
        capi.Event.ReloadTextures += OnShapeModified;
        capi.Event.ReloadShapes   += OnShapeModified;
    }

    // -----------------------------------------------------------------
    // Public helpers
    // -----------------------------------------------------------------

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (enabled) legJointsDirty = true; // recompute the leg joint set when toggled on
    }

    // -----------------------------------------------------------------
    // IRenderer
    // -----------------------------------------------------------------

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!enabled) return;

        IClientPlayer player = capi.World.Player;
        if (player == null) return;

        // Only render in first-person (not IFP, not 3rd-person).
        if (player.CameraMode != EnumCameraMode.FirstPerson) return;

        // Don't render when GUIs are hidden (F1 mode) — mirrors vanilla arm behaviour.
        if (capi.HideGuis) return;

        EntityPlayer entityPlayer = player.Entity;
        if (entityPlayer == null) return;

        // Drive the legs from the THIRD-PERSON animator, not entity.AnimManager. For the self player in
        // first person AnimManager returns the FP arm animator (selfFpAnimManager), which plays the "-fp"
        // animation variants intended for the floating-hands view and does not run the full body/leg
        // locomotion on the body. We render the third-person body mesh (thirdPersonMeshRef), so we pair it
        // with the matching third-person animator (TpAnimManager) — the one that plays the full walk/idle
        // cycle, including the Drocust drider's spider-leg motion. Its matrices stay fresh in first person
        // because the self-shadow pass renders the third-person body every frame.
        IAnimator anim = entityPlayer.TpAnimManager?.Animator;
        if (anim == null) return;

        // The player model re-tessellates whenever something calls MarkShapeModified() (armor/clothing
        // equip-unequip, body-size or model changes, skin edits…), which clears entity.ShapeFresh until
        // re-tessellation completes. Recompute the leg joint set right after that — the skeleton can
        // change on a model swap — by latching on the dirty state and acting when ShapeFresh returns true.
        bool shapeFresh = entityPlayer.ShapeFresh;
        if (!shapeFresh)
            shapeWasModified = true;
        else if (shapeWasModified)
        {
            shapeWasModified = false;
            legJointsDirty = true;
        }

        // Also recompute if the animator instance itself changed (a model swap rebuilds it).
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

        // Reuse the player renderer's own full-body mesh (custom model + clothes + correct joints).
        if (ThirdPersonMeshRefField == null) return;
        if (entityPlayer.Properties.Client.Renderer is not EntityPlayerShapeRenderer renderer) return;
        if (ThirdPersonMeshRefField.GetValue(renderer) is not MultiTextureMeshRef bodyMeshRef) return;
        if (bodyMeshRef.Disposed) return;

        IShaderProgram shader = modSys?.fpModeHandShader;
        if (shader == null || shader.Disposed) return;

        // ------------------------------------------------------------------
        // 1. Use the NORMAL world projection — exactly like Immersive First Person.
        //    IFP renders the body through the regular projection; the fixed "hand FOV"
        //    is used only for the non-immersive floating-hands path. Using a 60° FOV here
        //    zoomed the legs up so they dominated/blocked the view, so we leave the
        //    frame's current projection untouched.
        //    (ref: EntityPlayerShapeRenderer.DoRender3DOpaque — Set3DProjection(HandRenderFov)
        //     is gated behind renderMode == FirstPerson, never ImmersiveFirstPerson.)
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // 2. Bind shader, upload per-frame uniforms.
        // ------------------------------------------------------------------
        shader.Use();

        shader.Uniform("rgbaAmbientIn",   capi.Render.AmbientColor);
        shader.Uniform("rgbaFogIn",       capi.Render.FogColor);
        shader.Uniform("fogMinIn",        capi.Render.FogMin);
        shader.Uniform("fogDensityIn",    capi.Render.FogDensity);
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
        shader.Uniform("alphaTest",       0.05f);
        shader.Uniform("lightPosition",   capi.Render.ShaderUniforms.LightPosition3D);

        // No depth offset — IFP renders the body at true depth (the depth pull-toward-camera
        // is a floating-hands-only trick to keep hands from clipping into walls).
        shader.Uniform("depthOffset", 0f);

        // Per-entity lighting — sample from the block position below the player.
        Vec4f lightRgbs = capi.World.BlockAccessor.GetLightRGBs(
            (int)entityPlayer.Pos.X,
            (int)(entityPlayer.Pos.InternalY),
            (int)entityPlayer.Pos.Z);
        shader.Uniform("rgbaLightIn",     lightRgbs);

        // Extra uniforms required by entityanimated.vsh / .fsh.
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
        //    CameraMatrixOrigin = world→view with the player entity origin (the feet) at
        //    world (0,0,0) and the camera at eye height above it (full pitch + yaw baked in).
        //    So the body mesh, placed at its true position, renders at the real feet —
        //    below the view when looking ahead, and revealed when looking down.
        //
        //    The base transform mirrors EntityPlayerShapeRenderer.loadModelMatrixForPlayer
        //    (the ImmersiveFirstPerson / self branch): body roll, yaw+90°, walk-cycle pitch,
        //    sideways strafe swivel, entity size scale, then the -0.5 seraph re-centre.
        //
        //    Look-down fold: IFP keeps the body out of the camera when looking down by
        //    rotating the torso/foot bones (PlayerHeadController.SetTorsoOffsets), but that
        //    only runs when ImmersiveFpMode is on. We can't drive those bone poses here, so
        //    we reproduce the NET effect rigidly: as the view tilts down, lean the assembly
        //    back (RotateZ, pivoting near the feet so the pelvis swings away from the eye)
        //    and push it backward in world space so the pelvis clears the bottom of the view.
        //
        //    Mat4f.X(M,M,...) appends on the right, so code order is the REVERSE of the
        //    vertex-transform order.
        // ------------------------------------------------------------------
        capi.Render.GlPushMatrix();
        capi.Render.GlLoadMatrix(capi.Render.CameraMatrixOrigin);

        const float Deg = MathF.PI / 180f;

        // Shape base rotations (seraph: all 0, but read them to match vanilla exactly).
        var shape = entityPlayer.Properties.Client.Shape;
        float shapeRotX = shape?.rotateX ?? 0f;
        float shapeRotY = shape?.rotateY ?? 0f;
        float shapeRotZ = shape?.rotateZ ?? 0f;

        float bodyYaw   = entityPlayer.BodyYaw;               // ~smoothedBodyYaw (engine smooths it; close enough)
        float walkPitch = entityPlayer.WalkPitch;            // walk-cycle bob
        float swivel    = entityPlayer.sidewaysSwivelAngle;  // strafe lean

        // Look-down amount: 0 at the horizon, 1 looking straight down.
        float lookDown  = GameMath.Clamp(-(float)entityPlayer.Pos.GetViewVector().Y, 0f, 1f);

        // ---- Look-down fold tunables ----------------------------------------
        // Negative LeanRad folds the pelvis BACKWARD (away from the facing direction, toward
        // the bottom of the downward view). Positive folded it forward into the view.
        // Read live from config each frame so the optional ConfigLib GUI can tune them without a
        // restart; falls back to the defaults when no config is loaded.
        FirstPersonLegsConfig cfg = FirstPersonLegsCore.Config;
        float LookDownLeanRad   = cfg?.LookDownLeanRad   ?? -0.3f; // max backward lean at straight-down (~26°)
        float LookDownBackShift = cfg?.LookDownBackShift ?? 0.15f; // max metres pushed backward at straight-down
        float size      = entityPlayer.Properties.Client.Size * (cfg?.SizeMultiplier ?? 1.0f);

        // ---------------------------------------------------------------------

        // World-space backward offset (opposite the body's facing direction). Forward in this
        // frame is (sin, cos); backward is (-sin, -cos). Pushing back moves the pelvis toward
        // the bottom of the downward view.
        float backX = -MathF.Sin(bodyYaw) * LookDownBackShift * lookDown;
        float backZ = -MathF.Cos(bodyYaw) * LookDownBackShift * lookDown;

        float[] modelMat = Mat4f.Create();
        Mat4f.Identity(modelMat);

        // Applied LAST to vertices (world space): slide the whole assembly backward as we look down.
        Mat4f.Translate(modelMat, modelMat, backX, 0f, backZ);

        Mat4f.RotateX(modelMat, modelMat, entityPlayer.Pos.Roll + shapeRotX * Deg);
        Mat4f.RotateY(modelMat, modelMat, bodyYaw + (90f + shapeRotY) * Deg);
        // Walk-cycle pitch + look-down backward lean (pivots near the feet → pelvis swings back).
        Mat4f.RotateZ(modelMat, modelMat, walkPitch + shapeRotZ * Deg + LookDownLeanRad * lookDown);
        Mat4f.RotateX(modelMat, modelMat, swivel);
        Mat4f.Scale(modelMat, modelMat, size, size, size);
        Mat4f.Translate(modelMat, modelMat, -0.5f, 0f, -0.5f);

        shader.UniformMatrix("modelMatrix",  modelMat);
        shader.UniformMatrix("viewMatrix",   capi.Render.CurrentModelviewMatrix);

        // ------------------------------------------------------------------
        // 4. Upload the joint matrices into the "Animation" UBO — but with every non-leg bone
        //    collapsed to a zero-area point so only the legs (and gear parented to leg bones) draw.
        // ------------------------------------------------------------------
        float[] matrices = BuildLegOnlyMatrices(anim);
        shader.UBOs["Animation"].Update(matrices, 0, anim.MaxJointId * 16 * 4);

        // ------------------------------------------------------------------
        // 5. Draw the (joint-masked) body mesh.
        // ------------------------------------------------------------------
        capi.Render.RenderMultiTextureMesh(bodyMeshRef, "entityTex", 0);

        capi.Render.GlPopMatrix();
        shader.Stop();
        // Note: no Reset3DProjection — we never changed the projection (IFP uses the
        // frame's normal projection).
    }

    // -----------------------------------------------------------------
    // Joint masking
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns a joint-matrix array (in the scratch <see cref="poseBuffer"/>) that is a copy of the
    /// animator's live matrices with every bone NOT in <see cref="legJointIds"/> replaced by a "collapse"
    /// matrix (all zeros except element 15 = 1). That matrix maps every vertex of a non-leg bone to the
    /// model origin with w = 1, so the resulting zero-area triangles are not rasterised — leaving only the
    /// legs visible. Leg bones keep their real (animated) matrices.
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
            poseBuffer[b + 15] = 1f; // keep w = 1 so the collapsed verts land at (0,0,0,1), not NaN
        }

        return poseBuffer;
    }

    private void OnShapeModified()
    {
        legJointsDirty = true;
    }

    /// <summary>
    /// Computes the leg joint set from the live animator: "everything under the lower-body root minus the
    /// UpperTorso subtree" (model-agnostic — works even when custom models rename the individual leg
    /// bones, as long as the standard LowerTorso/UpperTorso split exists), with a name-based UpperFootL/R
    /// fallback, plus LowerBackAttachment (belt / lower-back armor) added explicitly.
    ///
    /// The lower-body root is normally LowerTorso. The one special case is the Drocust (Locust Drider)
    /// race: its drider shapes hang the legs, abdomen and six spider legs off a higher "Body" bone as
    /// SIBLINGS of LowerTorso, so LowerTorso's own subtree is just the pelvis (that is why only the pelvis
    /// rendered before). We detect it structurally — a "Body" bone that is an ancestor of LowerTorso — and
    /// use that as the root so the whole drider lower body comes along, while still excluding the
    /// UpperTorso (chest/arms/head) subtree. This is deliberately specific to Drocust: no other supported
    /// player model has a "Body" bone above its LowerTorso, so nothing else is affected. Standard seraph
    /// models keep the LowerTorso root (their LowerTorso sits directly under the shape root, whose other
    /// children are head cosmetics we must NOT pull in).
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
        // it exists and is an ancestor of LowerTorso (the only model that needs this).
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

        // LowerBackAttachment: belt / lower-back armor attachment point. Add explicitly in case it is
        // parented under UpperTorso (and was therefore excluded above).
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
                capi.Logger.Warning("[FirstPersonLegs] FpLegsRenderer: could not find leg bones " +
                    "(no LowerTorso/UpperTorso/UpperFoot poses on this model) — legs will not render.");
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

    // -----------------------------------------------------------------
    // IDisposable
    // -----------------------------------------------------------------

    public void Dispose()
    {
        capi.Event.ReloadTextures -= OnShapeModified;
        capi.Event.ReloadShapes   -= OnShapeModified;

        // Note: thirdPersonMeshRef is owned by the player's renderer — we only borrow it, never dispose it.
        legJointIds.Clear();
        poseBuffer = null;
    }
}
