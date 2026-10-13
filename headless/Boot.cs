using System;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

/// One-time headless initialization of the game assembly (no Godot engine).
public static class Boot
{
    public static void Init()
    {
        GodotStubs.Verbose = Environment.GetEnvironmentVariable("STS_VERBOSE") == "1";
        GodotStubs.Install();
        if (Environment.GetEnvironmentVariable("STS_FCE") == "2")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Console.WriteLine("[first-chance] " + e.Exception + "\n--- thrown at:\n" + new System.Diagnostics.StackTrace(1, false));
        if (Environment.GetEnvironmentVariable("STS_FCE") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Console.WriteLine("[first-chance] " + e.Exception.GetType().Name + ": " + e.Exception.Message.Split('\n')[0] + " @ " + (e.Exception.StackTrace ?? "").Split((char)10)[0].Trim() + " IL_" + (new System.Diagnostics.StackTrace(e.Exception).GetFrame(0)?.GetILOffset() ?? -1).ToString("X4"));

        TestMode.TurnOnInternal();
        NonInteractiveMode.AutoSlayerCheck = () => true;
        typeof(ModManager).GetProperty("State")!.SetValue(null, ModManagerState.Skipped);
        // default settings/prefs (fast mode etc.) without touching disk
        MegaCrit.Sts2.Core.Saves.SaveManager.Instance.InitSettingsDataForTest();
        MegaCrit.Sts2.Core.Saves.SaveManager.Instance.InitPrefsDataForTest();
        ModelDb.Init();
        MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache.Init();
        ModelDb.InitIds();
        MegaCrit.Sts2.Core.Multiplayer.Serialization.MessageTypes.Initialize();
        MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionTypes.Initialize();
    }
}
