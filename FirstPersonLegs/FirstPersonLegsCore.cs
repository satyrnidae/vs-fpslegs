using System;
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

    // Client-only mod - never load on the server
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        Logger = Mod.Logger;
        ModId = Mod.Info.ModID;
    }

    public override void AssetsLoaded(ICoreAPI api)
    {
        // Load config, write defaults to disk if no file exists yet
        FirstPersonLegsConfig loaded = null;
        try { loaded = api.LoadModConfig<FirstPersonLegsConfig>("firstpersonlegs.json"); }
        catch { /* malformed JSON - fall back to defaults */ }
        Config = loaded ?? new FirstPersonLegsConfig();
        if (loaded == null)
            api.StoreModConfig(Config, "firstpersonlegs.json");

        // Optional ConfigLib GUI. AssetsLoaded is the one phase that satisfies both constraints:
        //   * Runs after lang assets load, so ConfigLib's one-time Localize() of each setting label
        //     resolves the firstpersonlegs:setting-* keys - unloaded in Start, so raw codes would show.
        //   * Runs before the world-join config sync, where ConfigLib snapshots its mod list while
        //     building the config window - registering later (e.g. StartClientSide) leaves us off it.
        // onChanged reads legsRenderer lazily, so it's fine the renderer builds later in
        // StartClientSide. Guard the call so ConfigLib types only ever get touched, and thus only ever
        // need to resolve at runtime, when the mod is actually installed - see FirstPersonLegsConfigLib.
        if (api.ModLoader.IsModEnabled(FirstPersonLegsConfigLib.ConfigLibModId))
        {
            try
            {
                FirstPersonLegsConfigLib.TryRegister(
                    api, Config, onChanged: () => legsRenderer?.SetEnabled(Config.FpLegsEnabled));
            }
            catch (Exception e)
            {
                Logger?.Warning("[firstpersonlegs] Failed to register ConfigLib integration: {0}", e);
            }
        }
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // FP legs renderer, driven by mod config loaded in AssetsLoaded
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
