using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace FirstPersonLegs;

public class FirstPersonLegsConfig
{
    /// <summary>Whether the first-person leg renderer is enabled.</summary>
    /// <remarks>
    /// Attributes are read reflectively by the optional ConfigLib integration (see
    /// <see cref="FirstPersonLegsConfigLib"/>) to build the in-game settings GUI; they are inert when
    /// ConfigLib is not installed. <c>[Category("ClientSide")]</c> marks the setting client-side so it is
    /// never overridden by a server, matching this mod's client-only nature.
    /// </remarks>
    [Category("ClientSide")]
    [Description("Render the player's own legs in first person.")]
    public bool FpLegsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum backward lean (radians) applied to the leg assembly when looking straight down. Negative
    /// folds the pelvis backward out of the downward view; positive folds it forward into view. This
    /// approximates Immersive First Person's torso fold, which this mod can't drive via bone poses.
    /// </summary>
    [Category("ClientSide")]
    [Range(-1.5, 0)]
    [Description("Backward lean (radians) of the legs at full look-down")]
    public float LookDownLeanRad { get; set; } = -0.24f;

    /// <summary>
    /// Maximum distance (metres) the leg assembly is pushed backward in world space when looking straight
    /// down, so the pelvis clears the bottom of the view. Pairs with <see cref="LookDownLeanRad"/>.
    /// </summary>
    [Category("ClientSide")]
    [Range(-1.0, 1.0)]
    [Description("Backward shift (metres) of the legs at full look-down, by foot position. 0.0 centers feet in frame.")]
    public float LookDownBackShift { get; set; } = 0.12f;

    /// <summary>
    /// Maximum distance (metres) the leg assembly is pushed backward in world space while swimming, at
    /// full dive/surface pitch. The vanilla "Swim" animation bakes an ~80-90° forward pitch into the
    /// LowerTorso/leg bone chain by itself; our fixed (non-eye-riding) first-person camera doesn't move
    /// to compensate the way Immersive First Person's camera does (it rides the animated "Eyes" bone), so
    /// without this the pitched pelvis pivot sits directly under the camera and the legs read as hanging
    /// straight down instead of trailing behind the swimmer. Scaled by how steeply the player is looking
    /// up/down (0 at the horizon, full at straight up/down). Pairs with <see cref="SwimDownShift"/>.
    /// </summary>
    [Category("ClientSide")]
    [Range(0.0, 1.5)]
    [Description("Backward shift (metres) of the legs while swimming, scaled by look pitch.")]
    public float SwimBackShift { get; set; } = 1.0f;

    /// <summary>
    /// Maximum distance (metres) the leg assembly is pushed downward in world space while swimming, at
    /// full dive/surface pitch. Pairs with <see cref="SwimBackShift"/>.
    /// </summary>
    [Category("ClientSide")]
    [Range(0.0, 1.5)]
    [Description("Downward shift (metres) of the legs while swimming, scaled by look pitch.")]
    public float SwimDownShift { get; set; } = 0.25f;

    /// <summary>
    /// Multiplier applied to the player's entity size when drawing the first-person legs, on top of the
    /// vanilla <c>Properties.Client.Size</c>. 1.0 leaves the legs at their natural size; raising it scales
    /// the whole leg assembly up (useful if the legs read as too small/distant in first person).
    /// </summary>
    [Category("ClientSide")]
    [Range(1.0, 2.0)]
    [Description("Scale multiplier for the first-person legs, on top of the player's normal size.")]
    public float SizeMultiplier { get; set; } = 1.0f;

    /// <summary>
    /// Fixed vertical field of view (degrees) the legs are rendered through, independent of the player's
    /// world FOV — the same trick vanilla uses for the first-person arms (a fixed <c>fpHandsFoV</c>
    /// projection). This keeps the legs' on-screen size/position constant as the world FOV changes; with
    /// the world FOV the leg tops climb into view at high FOV. The look-down and size tuning is calibrated
    /// against this value, so 70 matches the mod's default tuning — change it and you may need to retune.
    /// </summary>
    [Category("ClientSide")]
    [Range(40.0, 120.0)]
    [Description("Fixed FOV (degrees) the legs render through, so they don't change with the world FOV.")]
    public float LegRenderFov { get; set; } = 70.0f;

    /// <summary>
    /// Depth bias (in normalized device depth units) applied to the legs via the shared FP-hand
    /// shader's <c>gl_FragDepth</c> override — the same mechanism vanilla uses to keep the
    /// first-person hands/held item from being occluded by world geometry they clip into. Negative
    /// values pull the rendered depth closer to the camera, so the portion of the legs that pokes
    /// into a block draws over that block's face instead of being hidden behind it. Kept small
    /// relative to vanilla's hand value (around -0.3) since the legs sit farther from the camera and
    /// a large bias makes them visibly float above/through the ground even when not clipping.
    /// </summary>
    [Category("ClientSide")]
    [Range(-1.0, 0.0)]
    [Description("Depth bias applied to the legs so clipped portions draw over intersecting block faces instead of being occluded by them.")]
    public float LegDepthOffset { get; set; } = -0.02f;

    /// <summary>
    /// When true, the renderer logs diagnostics on every leg-mesh rebuild (texture source, full-body
    /// and filtered vertex counts, resolved leg joints). Useful for debugging compatibility with
    /// player-model mods (e.g. PlayerModelLib custom races). Off by default to avoid log spam.
    /// </summary>
    [Category("ClientSide")]
    [Description("Log diagnostics on every leg-mesh rebuild. For debugging player-model compatibility.")]
    public bool VerboseLogging { get; set; } = false;
}
