using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace FirstPersonLegs;

/// <summary>
/// Client-side entry point. Loads config and attaches the first-person legs renderer.
/// </summary>
public class FirstPersonLegsCore : ModSystem
{
    public static ILogger Logger { get; private set; }
    public static string ModId { get; private set; }
    public static FirstPersonLegsConfig Config { get; private set; }

    private FpLegsRenderer legsRenderer;

    // Client-only mod — never load on the server.
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        Logger = Mod.Logger;
        ModId = Mod.Info.ModID;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // Load config; write defaults to disk when no file exists yet.
        FirstPersonLegsConfig loaded = null;
        try { loaded = api.LoadModConfig<FirstPersonLegsConfig>("firstpersonlegs.json"); }
        catch { /* malformed JSON — fall back to defaults */ }
        Config = loaded ?? new FirstPersonLegsConfig();
        if (loaded == null)
            api.StoreModConfig(Config, "firstpersonlegs.json");

        // FP legs renderer — driven by mod config.
        legsRenderer = new FpLegsRenderer(api, Config.FpLegsEnabled);
        api.Event.RegisterRenderer(legsRenderer, EnumRenderStage.Opaque, "firstpersonlegs:fpLegs");
    }

    public override void Dispose()
    {
        legsRenderer?.Dispose();
        legsRenderer = null;
        Logger = null;
        ModId = null;
        Config = null;
        base.Dispose();
    }
}
