using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

/// Turns every Godot native call into a managed exception (instead of a segfault),
/// and replaces the handful of engine calls the game logic genuinely needs with pure C#.
public static class GodotStubs
{
    public class GodotUnavailableException : Exception
    {
        public GodotUnavailableException(string m) : base(m) { }
    }

    static readonly Harmony H = new("sts2.headless");

    public static void Install()
    {
        var godot = typeof(Godot.GD).Assembly;
        var nf = godot.GetType("Godot.NativeInterop.NativeFuncs", throwOnError: true)!;
        var trap = new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(Trap), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        foreach (var m in nf.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.IsGenericMethodDefinition || m.GetMethodBody() == null) continue;
            try { H.Patch(m, prefix: trap); n++; } catch { }
        }
        Console.WriteLine($"[stubs] trapped {n} native entry points");

        // StringName/NodePath are built in many static ctors (input action names, node paths). Leave their native
        // handle empty instead of calling the engine; nothing headless ever resolves them.
        var skipCtor = new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(Skip), BindingFlags.Static | BindingFlags.NonPublic));
        H.Patch(typeof(Godot.StringName).GetConstructor(new[] { typeof(string) })!, prefix: skipCtor);
        H.Patch(typeof(Godot.NodePath).GetConstructor(new[] { typeof(string) })!, prefix: skipCtor);
        foreach (var m in nf.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            if ((m.Name.EndsWith("_destroy") || m.Name.EndsWith("_new_copy")) && m.ReturnType == typeof(void))
                try { H.Patch(m, prefix: skipCtor); } catch { }

        Replace(typeof(Godot.GD), "Print", nameof(PrintStub), typeof(string));
        Replace(typeof(Godot.GD), "PrintErr", nameof(PrintStub), typeof(string));
        Replace(typeof(Godot.OS), "GetCmdlineArgs", nameof(EmptyArgs));
        Replace(typeof(Godot.OS), "HasFeature", nameof(False), typeof(string));

        Replace(typeof(Godot.Time), "GetTicksMsec", nameof(TicksMsec));
        Replace(typeof(Godot.Time), "GetTicksUsec", nameof(TicksUsec));

        // Cosmetic commands (speech bubbles, VFX, SFX) are no-ops headless.
        var skip = new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(Skip), BindingFlags.Static | BindingFlags.NonPublic));
        var skipNull = new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(SkipNull), BindingFlags.Static | BindingFlags.NonPublic));
        var sts2 = typeof(MegaCrit.Sts2.Core.Models.ModelDb).Assembly;
        foreach (var cls in new[] { "TalkCmd", "ThinkCmd", "VfxCmd", "SfxCmd" })
            foreach (var m in sts2.GetType("MegaCrit.Sts2.Core.Commands." + cls, true)!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                if (m.ReturnType == typeof(void)) H.Patch(m, prefix: skip);
                else if (!m.ReturnType.IsValueType) H.Patch(m, prefix: skipNull);
            }

        // A fake SceneTree/root (never constructed natively) for code that only reads frame timing off it.
        FakeTree = (Godot.SceneTree)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Godot.SceneTree));
        FakeRoot = (Godot.Window)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Godot.Window));
        GC.SuppressFinalize(FakeTree); GC.SuppressFinalize(FakeRoot);
        Replace(typeof(Godot.Engine), "GetMainLoop", nameof(MainLoop));
        H.Patch(typeof(Godot.SceneTree).GetProperty("Root")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(Root), BindingFlags.Static | BindingFlags.NonPublic)));
        H.Patch(typeof(Godot.Node).GetMethod("GetProcessDeltaTime")!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(Delta), BindingFlags.Static | BindingFlags.NonPublic)));

        // Asset cache lookups (textures/scenes for UI) -> null.
        foreach (var m in sts2.GetType("MegaCrit.Sts2.Core.Assets.AssetCache", true)!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            if (m.Name.StartsWith("Get") && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && typeof(Godot.GodotObject).IsAssignableFrom(m.ReturnType)) H.Patch(m, prefix: skipNull);

        // Choice screens with no test-mode path (relic choice) or a fixed one (bundle choice: always the first)
        H.Patch(sts2.GetType("MegaCrit.Sts2.Core.Commands.RelicSelectCmd", true)!.GetMethod("FromChooseARelicScreen")!,
            prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(ChooseRelicPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        H.Patch(typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen")!,
            prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(ChooseBundlePrefix), BindingFlags.Static | BindingFlags.NonPublic)));

        // Event UI that carries game logic:
        //  - Crystal Sphere minigame: the screen drives cell clicks -> hand the minigame to the driver instead.
        //  - Trial "double down" opens the abandon-run popup -> abandon directly. Its portrait VFX is cosmetic.
        H.Patch(sts2.GetType("MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen", true)!.GetMethod("ShowScreen", BindingFlags.Static | BindingFlags.Public)!,
            prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(CaptureCrystalSphere), BindingFlags.Static | BindingFlags.NonPublic)));
        var trial = sts2.GetType("MegaCrit.Sts2.Core.Models.Events.Trial", true)!;
        H.Patch(trial.GetMethod("DoubleDown", BindingFlags.Instance | BindingFlags.NonPublic)!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(AbandonRun), BindingFlags.Static | BindingFlags.NonPublic)));
        H.Patch(trial.GetMethod("AddVfxAnchoredToPortrait", BindingFlags.Instance | BindingFlags.NonPublic)!, prefix: skip);

        // No disk I/O / UI / achievements headless (all called from the combat-won path).
        var skipTask = new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(SkipTask), BindingFlags.Static | BindingFlags.NonPublic));
        var saveMgr = typeof(MegaCrit.Sts2.Core.Saves.SaveManager);
        var achievements = sts2.GetType("MegaCrit.Sts2.Core.Achievements.AchievementsHelper", true)!;
        H.Patch(typeof(MegaCrit.Sts2.Core.Runs.RunManager).GetMethod("WriteReplay")!, prefix: skip);
        H.Patch(sts2.GetType("MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet", true)!.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public)!, prefix: skip);
        H.Patch(saveMgr.GetMethod("SaveRun")!, prefix: skipTask);
        H.Patch(saveMgr.GetMethod("UpdateProgressAfterCombatWon")!, prefix: skip);
        H.Patch(saveMgr.GetMethod("SaveProgressFile")!, prefix: skip);
        foreach (var m in achievements.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
            if (m.ReturnType == typeof(void)) H.Patch(m, prefix: skip);

        // No resources headless: Exists -> false, Load -> null (only icons/scenes for UI ask for them).
        foreach (var m in typeof(Godot.ResourceLoader).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (m.Name == "Exists") H.Patch(m, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(False), BindingFlags.Static | BindingFlags.NonPublic)));
            else if (m.Name == "Load" && !m.IsGenericMethodDefinition) H.Patch(m, prefix: skipNull);
        }

        // NGame must stay null for the engine-free paths (NRun.Instance etc. key off it), but a few call sites use
        // NGame.Instance.X() without a null check (screen shake). Rewrite only those sites to an inert fake.
        var uiSingletons = sts2.GetTypes().Where(t => t.Name.StartsWith("N") && typeof(Godot.Node).IsAssignableFrom(t) && !t.IsGenericTypeDefinition
            && t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)?.PropertyType == t).ToArray();
        // monster models that drive their own background UI node (Kaiser Crab boss)
        foreach (var name in new[] { "MegaCrit.Sts2.Core.Models.Monsters.Crusher", "MegaCrit.Sts2.Core.Models.Monsters.Rocket" })
            UiGetters.Add(sts2.GetType(name, true)!.GetProperty("Background", BindingFlags.Instance | BindingFlags.NonPublic)!.GetGetMethod(true)!);
        FakeUncheckedCallSites(sts2, uiSingletons);

        // No localization tables headless: text resolves to its loc key.
        var loc = typeof(MegaCrit.Sts2.Core.Localization.LocString);
        foreach (var name in new[] { "GetFormattedText", "GetRawText" })
            H.Patch(loc.GetMethod(name, Type.EmptyTypes)!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(LocKey), BindingFlags.Static | BindingFlags.NonPublic)));
        H.Patch(loc.GetMethod("Exists", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null)!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(True), BindingFlags.Static | BindingFlags.NonPublic)));

        // Sentry (crash reporter) touches Godot in its static ctor; disable it and surface exceptions instead.
        var sentry = typeof(MegaCrit.Sts2.Core.Debug.SentryService);
        H.Patch(sentry.GetProperty("IsEnabled")!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(False), BindingFlags.Static | BindingFlags.NonPublic)));
        H.Patch(sentry.GetMethod("CaptureException", new[] { typeof(Exception) })!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(ReportException), BindingFlags.Static | BindingFlags.NonPublic)));
    }

    /// Makes T.Instance return an uninitialized T whose public methods do nothing.
    static void FakeSingleton(Type t)
    {
        var fake = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        GC.SuppressFinalize(fake);
        Fakes[t] = fake;
        H.Patch(t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetGetMethod()!, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(FakeInstance), BindingFlags.Static | BindingFlags.NonPublic)));
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName || m.IsGenericMethodDefinition) continue;
            string stub = m.ReturnType == typeof(void) ? nameof(Skip)
                : m.ReturnType == typeof(int) ? nameof(ZeroInt)
                : m.ReturnType == typeof(bool) ? nameof(False)
                : typeof(System.Threading.Tasks.Task) == m.ReturnType ? nameof(SkipTask)
                : !m.ReturnType.IsValueType ? nameof(SkipNull) : null;
            if (stub != null) H.Patch(m, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(stub, BindingFlags.Static | BindingFlags.NonPublic)));
        }
    }
    static readonly System.Collections.Generic.Dictionary<Type, object> Fakes = new();
    static readonly List<MethodInfo> UiGetters = new();

    /// Finds every method whose IL does `call T::get_Instance` NOT followed by `dup` (i.e. no `?.`), and transpiles
    /// just that call to return an inert fake T whose public methods are no-ops.
    static void FakeUncheckedCallSites(Assembly asm, Type[] types)
    {
        var tokens = new HashSet<int>();
        foreach (var t in types)
        {
            var getter = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)!.GetGetMethod()!;
            tokens.Add(getter.MetadataToken);
            var fake = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            GC.SuppressFinalize(fake);
            Fakes[t] = fake;
        }
        foreach (var g in UiGetters) tokens.Add(g.MetadataToken);
        int token = 0, sites = 0;
        var faked = new HashSet<Type>();
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in asm.GetTypes())
        {
            if (type.IsGenericTypeDefinition) continue;
            foreach (var m in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
            {
                if (m.IsGenericMethodDefinition) continue;
                byte[] il;
                try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
                if (il == null) continue;
                bool hit = false;
                for (int i = 0; i + 5 < il.Length && !hit; i++)
                    if (il[i] == 0x28 && tokens.Contains(BitConverter.ToInt32(il, i + 1)) && (IsUncheckedUseByte(il[i + 5]) || il[i + 5] == 0x28)) hit = true;
                if (!hit) continue;
                try
                {
                    H.Patch(m, transpiler: new HarmonyMethod(typeof(GodotStubs).GetMethod(nameof(SwapInstance), BindingFlags.Static | BindingFlags.NonPublic)));
                    sites++;
                }
                catch { }
            }
        }
        foreach (var t in Array.Empty<Type>())
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName || m.IsGenericMethodDefinition || m.IsAbstract) continue;
                string stub = m.ReturnType == typeof(void) ? nameof(Skip)
                    : typeof(System.Threading.Tasks.Task) == m.ReturnType ? nameof(SkipTask)
                    : !m.ReturnType.IsValueType ? nameof(SkipNullIfFake) : null;
                if (stub != null) try { H.Patch(m, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(stub, BindingFlags.Static | BindingFlags.NonPublic))); } catch (Exception e) { if (Verbose) Console.WriteLine($"[stubs] cannot patch {t.Name}.{m.Name}: {e.Message.Split((char)10)[0]}"); }
            }
        Console.WriteLine($"[stubs] {types.Length} UI singletons; faked {sites} methods with unchecked .Instance use");
    }
    static bool SkipNullIfFake(object __instance, ref object __result)
    {
        if (__instance == null || !Fakes.ContainsKey(__instance.GetType()) || !ReferenceEquals(Fakes[__instance.GetType()], __instance)) return true;
        __result = null; return false;
    }
    static Type _swapType;
    /// Rewrites `X.Instance.Member(args)` (no null check) into: push null, drop args, push default(result).
    /// Calling a patched no-op on a fake object is not enough: the JIT may inline the original member body.
    static System.Collections.Generic.IEnumerable<CodeInstruction> SwapInstance(System.Collections.Generic.IEnumerable<CodeInstruction> code, MethodBase __originalMethod)
    {
        var list = code.ToList();
        if (Environment.GetEnvironmentVariable("STS_NOSWAP") == "1") return list;
        var getters = Fakes.Keys.Select(t => (t, g: t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetGetMethod()))
            .Where(x => x.g != null).Concat(UiGetters.Select(g => (t: g.ReturnType, g: (MethodInfo)g))).ToList();
        for (int i = 0; i < list.Count - 1; i++)
        {
            var hit = getters.FirstOrDefault(x => list[i].Calls(x.g!));
            if (hit.t == null || !IsUncheckedUse(list[i + 1])) continue;
            if (!hit.g!.IsStatic) // instance getter: drop `this` first
            {
                list.Insert(i, new CodeInstruction(System.Reflection.Emit.OpCodes.Pop).MoveLabelsFrom(list[i]));
                i++;
            }
            list[i].opcode = System.Reflection.Emit.OpCodes.Ldnull; list[i].operand = null;
            Type cur = hit.t;
            int j = i + 1;
            while (cur != null)
            {
                // the consumer: first instance call/ldfld on `cur` within a short window (args are loaded in between)
                int k = -1;
                for (int q = j; q < list.Count && q < j + 16; q++)
                {
                    if ((list[q].opcode == System.Reflection.Emit.OpCodes.Callvirt || list[q].opcode == System.Reflection.Emit.OpCodes.Call)
                        && list[q].operand is MethodInfo mi && !mi.IsStatic && mi.DeclaringType!.IsAssignableFrom(cur)) { k = q; break; }
                    if (list[q].opcode == System.Reflection.Emit.OpCodes.Ldfld && list[q].operand is FieldInfo fi && fi.DeclaringType!.IsAssignableFrom(cur)) { k = q; break; }
                }
                if (k < 0) break;
                Type ret;
                var repl = new List<CodeInstruction>();
                if (list[k].operand is MethodInfo m)
                {
                    for (int a = 0; a < m.GetParameters().Length + 1; a++) repl.Add(new CodeInstruction(System.Reflection.Emit.OpCodes.Pop));
                    ret = m.ReturnType;
                }
                else { repl.Add(new CodeInstruction(System.Reflection.Emit.OpCodes.Pop)); ret = ((FieldInfo)list[k].operand).FieldType; }
                if (ret != typeof(void))
                    repl.Add(new CodeInstruction(System.Reflection.Emit.OpCodes.Call, typeof(GodotStubs).GetMethod(nameof(Default), BindingFlags.Static | BindingFlags.Public)!.MakeGenericMethod(ret)));
                repl[0].labels.AddRange(list[k].labels);
                repl[0].blocks.AddRange(list[k].blocks);
                list.RemoveAt(k);
                list.InsertRange(k, repl);
                int after = k + repl.Count;
                // chained use of a UI object (e.g. NCombatRoom.Instance.Ui.X()) -> keep stripping
                cur = ret != typeof(void) && typeof(Godot.GodotObject).IsAssignableFrom(ret) && after < list.Count && IsUncheckedUse(list[after]) ? ret : null;
                j = after;
            }
        }
        return list;
    }
    /// default(T), but a completed Task for Task-returning UI calls so `await` works.
    public static T Default<T>()
    {
        if (typeof(T) == typeof(System.Threading.Tasks.Task)) return (T)(object)System.Threading.Tasks.Task.CompletedTask;
        return default;
    }
    public static T GetFake<T>() => (T)Fakes[typeof(T)];

    // `X.Instance` immediately dereferenced (member call/field) or followed by argument loads for such a call.
    // Anything else (dup for ?., branches, stloc, ldnull compare, IsInstanceValid) is a null check: leave it alone.
    static bool IsUncheckedUseByte(byte b) =>
        b == 0x6F /*callvirt*/ || b == 0x7B /*ldfld*/ || b == 0x7C /*ldflda*/ || b == 0x72 /*ldstr*/ ||
        (b >= 0x02 && b <= 0x09) /*ldarg.n ldloc.n*/ || b == 0x0E || b == 0x11 /*ldarg.s ldloc.s*/ ||
        (b >= 0x15 && b <= 0x23) /*ldc.**/ || b == 0x7E /*ldsfld*/;
    static bool IsUncheckedUse(CodeInstruction next) =>
        (next.opcode.Size == 1 && IsUncheckedUseByte((byte)next.opcode.Value))
        // a parameterless static call is just loading an argument (e.g. PreloadManager.Cache), not a null check
        || (next.opcode == System.Reflection.Emit.OpCodes.Call && next.operand is MethodInfo sm && sm.IsStatic && sm.GetParameters().Length == 0);
    static bool FakeInstance(MethodBase __originalMethod, ref object __result) { __result = Fakes[__originalMethod.DeclaringType!]; return false; }
    static bool ZeroInt(ref int __result) { __result = 0; return false; }

    static void Replace(Type t, string name, string stub, params Type[] args)
    {
        var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public, null, args, null)
                ?? throw new MissingMethodException(t.Name, name);
        H.Patch(m, prefix: new HarmonyMethod(typeof(GodotStubs).GetMethod(stub, BindingFlags.Static | BindingFlags.NonPublic)));
    }

    static bool Trap(MethodBase __originalMethod) =>
        throw new GodotUnavailableException("Godot native call: " + __originalMethod.Name);

    static bool PrintStub(string what) { if (Verbose) Console.WriteLine(what); return false; }
    public static readonly System.Collections.Generic.Dictionary<string, int> GameExceptions = new();
    public static bool QuietExceptions = false;
    static bool ReportException(Exception ex)
    {
        var frame = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at MegaCrit")) ?? "?";
        var key = ex.GetType().Name + " " + frame;
        GameExceptions[key] = GameExceptions.GetValueOrDefault(key) + 1;
        if (!QuietExceptions) Console.WriteLine("[game exception] " + ex);
        return false;
    }
    static bool LocKey(MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result) { __result = __instance.LocEntryKey; return false; }
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static bool TicksMsec(ref ulong __result) { __result = (ulong)Clock.ElapsedMilliseconds; return false; }
    static bool TicksUsec(ref ulong __result) { __result = (ulong)(Clock.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency); return false; }
    static Godot.SceneTree FakeTree; static Godot.Window FakeRoot;
    static bool MainLoop(ref Godot.MainLoop __result) { __result = FakeTree; return false; }
    static bool Root(ref Godot.Window __result) { __result = FakeRoot; return false; }
    static bool Delta(ref double __result) { __result = 1.0 / 60; return false; }
    static bool Skip() => false;
    /// Set by the driver: decides "choose a relic" / "choose a bundle" screens.
    public static Func<IReadOnlyList<MegaCrit.Sts2.Core.Models.RelicModel>, int> RelicChooser;
    public static Func<IReadOnlyList<IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel>>, int> BundleChooser;
    static bool ChooseRelicPrefix(IReadOnlyList<MegaCrit.Sts2.Core.Models.RelicModel> relics, ref System.Threading.Tasks.Task<MegaCrit.Sts2.Core.Models.RelicModel> __result)
    {
        int i = relics.Count == 0 || RelicChooser == null ? -1 : RelicChooser(relics);
        __result = System.Threading.Tasks.Task.FromResult(i < 0 ? null : relics[i]);
        return false;
    }
    static bool ChooseBundlePrefix(IReadOnlyList<IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel>> bundles, ref System.Threading.Tasks.Task<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> __result)
    {
        if (bundles.Count == 0 || MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsEnding) { __result = System.Threading.Tasks.Task.FromResult<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>>(Array.Empty<MegaCrit.Sts2.Core.Models.CardModel>()); return false; }
        int i = BundleChooser == null ? 0 : BundleChooser(bundles);
        __result = System.Threading.Tasks.Task.FromResult<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>>(bundles[i]);
        return false;
    }
    /// Minigame waiting for the driver to click cells (set by NCrystalSphereScreen.ShowScreen).
    public static object PendingCrystalSphere;
    static bool CaptureCrystalSphere(object grid, ref object __result) { PendingCrystalSphere = grid; __result = null; return false; }
    static bool AbandonRun(ref System.Threading.Tasks.Task __result)
    {
        MegaCrit.Sts2.Core.Runs.RunManager.Instance.Abandon();
        __result = System.Threading.Tasks.Task.CompletedTask; return false;
    }
    static bool SkipTask(ref System.Threading.Tasks.Task __result) { __result = System.Threading.Tasks.Task.CompletedTask; return false; }
    static bool SkipNull(ref object __result) { __result = null; return false; }
    static bool EmptyArgs(ref string[] __result) { __result = Array.Empty<string>(); return false; }
    static bool True(ref bool __result) { __result = true; return false; }
    static bool False(ref bool __result) { __result = false; return false; }

    public static bool Verbose = false;
}
