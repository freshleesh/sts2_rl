using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.Linq;

// --server: stdout carries the JSON protocol only; everything else the process prints goes to stderr
System.IO.StreamWriter proto = null;
if (args.Contains("--server")) { proto = new System.IO.StreamWriter(Console.OpenStandardOutput()); Console.SetOut(Console.Error); }

if (Environment.GetEnvironmentVariable("STS_REFL") is string refl)
{
    var nf = typeof(Godot.GD).Assembly.GetType("Godot.NativeInterop.NativeFuncs")!;
    foreach (var m in nf.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        if (m.Name.Contains(refl)) Console.WriteLine($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) body={m.GetMethodBody() != null}");
    foreach (var f in typeof(Godot.StringName).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)) Console.WriteLine($"StringName field {f.FieldType.FullName} {f.Name}");
    return;
}
Boot.Init();
if (Environment.GetEnvironmentVariable("STS_PROBE") == "1")
{
    try { MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager.Instance?.Play("x", 1f, MegaCrit.Sts2.Core.Audio.Debug.PitchVariance.Small); Console.WriteLine("audio ok"); } catch (Exception e) { Console.WriteLine("audio: " + e); }
    try { GodotStubs.GetFake<MegaCrit.Sts2.Core.Nodes.NGame>().ScreenShakeTrauma(MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeStrength.Strong); Console.WriteLine("shake ok"); } catch (Exception e) { Console.WriteLine("shake: " + e); }
    return;
}
if (Environment.GetEnvironmentVariable("STS_LISTSM") is string smOf)
{
    foreach (var t in typeof(MegaCrit.Sts2.Core.Models.ModelDb).Assembly.GetTypes().Where(t => t.FullName!.StartsWith(smOf + "+<")))
        Console.WriteLine($"{t.FullName} valueType={t.IsValueType}");
    return;
}
if (Environment.GetEnvironmentVariable("STS_IDPATCH") is string idType)
{
    // experiment: identity-transpile one async state machine's MoveNext, then play normally
    var t = typeof(MegaCrit.Sts2.Core.Models.ModelDb).Assembly.GetTypes().First(t => t.FullName == idType);
    var mm = t.GetMethod("MoveNext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
    new HarmonyLib.Harmony("idpatch").Patch(mm, transpiler: new HarmonyLib.HarmonyMethod(typeof(GodotStubs).GetMethod(Environment.GetEnvironmentVariable("STS_IDMODE") ?? "Identity")));
    Console.WriteLine($"[idpatch] {idType} isValueType={t.IsValueType}");
}
if (Environment.GetEnvironmentVariable("STS_DUMPIL") is string dumpType)
{
    var parts = dumpType.Split("::");
    var t = typeof(MegaCrit.Sts2.Core.Models.ModelDb).Assembly.GetTypes().First(t => t.FullName == parts[0]);
    var mm = t.GetMethod(parts.Length > 1 ? parts[1] : "MoveNext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
    foreach (var ins in HarmonyLib.PatchProcessor.GetCurrentInstructions(mm)) Console.WriteLine("   " + ins);
    return;
}
if (args.Contains("--server"))
{
    GodotStubs.QuietExceptions = true;
    var input = new System.IO.StreamReader(Console.OpenStandardInput());
    void Send(object o) { proto.WriteLine(System.Text.Json.JsonSerializer.Serialize(o)); proto.Flush(); }
    var versionFile = System.IO.Path.Combine(AppContext.BaseDirectory, "GAME_VERSION.json");
    string gameVersion = System.IO.File.Exists(versionFile)
        ? System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(versionFile)).RootElement.TryGetProperty("version", out var gvv) ? gvv.GetString() : "?"
        : "?";
    Send(new { type = "ready", game_version = gameVersion, characters = MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters.Select(c => c.Id.Entry).ToList() });
    AsyncPump.Run(async () =>
    {
        System.Text.Json.JsonElement? pending = null;
        while (true)
        {
            System.Text.Json.JsonElement cmd;
            if (pending is { } p) { cmd = p; pending = null; }
            else
            {
                var line = input.ReadLine();
                if (line == null) return;
                cmd = System.Text.Json.JsonDocument.Parse(line).RootElement.Clone();
            }
            string c = cmd.TryGetProperty("cmd", out var cv) ? cv.GetString() : null;
            if (c == "quit") return;
            if (c != "reset") { Send(new { type = "error", message = "expected {\"cmd\":\"reset\"} or {\"cmd\":\"quit\"}" }); continue; }
            string seed = cmd.TryGetProperty("seed", out var sv) ? sv.ToString() : Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            string character = cmd.TryGetProperty("character", out var chv) ? chv.GetString() : "IRONCLAD";
            int maxTurns = cmd.TryGetProperty("max_combat_turns", out var mv) ? mv.GetInt32() : 100;
            var agent = new RemoteAgent(input, proto);
            int stopAfterAct = cmd.TryGetProperty("stop_after_act", out var sav) ? sav.GetInt32() : 0;
            bool god = cmd.TryGetProperty("god_mode", out var gv) && gv.GetBoolean(); // testing only
            var driver = new RunDriver(agent, godMode: god, character: character, maxCombatTurns: maxTurns, stopAfterAct: stopAfterAct);
            agent.State = () => driver.State;
            if (cmd.TryGetProperty("relics", out var rv)) driver.ExtraRelics = rv.EnumerateArray().Select(x => x.GetString()!).ToList();
            if (cmd.TryGetProperty("potions", out var pv)) driver.ExtraPotions = pv.EnumerateArray().Select(x => x.GetString()!).ToList();
            try
            {
                var r = await driver.Play(seed);
                Send(new
                {
                    type = "end", seed, victory = r.Victory, act = r.Act, floor = r.Floor, hp = r.Hp,
                    max_hp = driver.State?.Players[0].Creature.MaxHp ?? 0,
                    reason = r.EndedIn, error = r.Error,
                    swallowed_exceptions = GodotStubs.GameExceptions.Values.Sum(),
                });
            }
            catch (ClientCommandException cce) { pending = cce.Command; } // reset/quit mid-run
        }
    });
    return;
}
int n = int.Parse(Environment.GetEnvironmentVariable("STS_N") ?? "1");
GodotStubs.QuietExceptions = n > 1;
bool log = Environment.GetEnvironmentVariable("STS_LOG") == "1" || n == 1;
string seedPrefix = Environment.GetEnvironmentVariable("STS_SEED") ?? "RUN";

if (Environment.GetEnvironmentVariable("STS_SWEEP") is string sweep) // "events" | "encounters"
{
    int reps = int.Parse(Environment.GetEnvironmentVariable("STS_REPS") ?? "10");
    GodotStubs.QuietExceptions = Environment.GetEnvironmentVariable("STS_ONLY") == null;
    var models = sweep switch
    {
        "events" => MegaCrit.Sts2.Core.Models.ModelDb.AllEvents.Cast<MegaCrit.Sts2.Core.Models.AbstractModel>(),
        "ancients" => MegaCrit.Sts2.Core.Models.ModelDb.AllAncients.Cast<MegaCrit.Sts2.Core.Models.AbstractModel>(),
        _ => MegaCrit.Sts2.Core.Models.ModelDb.AllEncounters.Cast<MegaCrit.Sts2.Core.Models.AbstractModel>(),
    };
    string only = Environment.GetEnvironmentVariable("STS_ONLY");
    AsyncPump.Run(async () =>
    {
        int bad = 0;
        foreach (var canon in models.Where(m => only == null || m.Id.Entry == only).OrderBy(m => m.Id.Entry))
        {
            var outcomes = new Dictionary<string, int>();
            int exBefore = GodotStubs.GameExceptions.Values.Sum();
            for (int k = 0; k < reps; k++)
            {
                var o = await new RunDriver(new RandomAgent(k), log: only != null, godMode: true, maxCombatTurns: 1000).PlayRoom($"SW{k}", canon is MegaCrit.Sts2.Core.Models.EncounterModel enc ? enc.ToMutable() : canon);
                outcomes[o] = outcomes.GetValueOrDefault(o) + 1;
            }
            int ex = GodotStubs.GameExceptions.Values.Sum() - exBefore;
            bool ok = ex == 0 && outcomes.Keys.All(o => o is "ok" or "death" or "abandoned" or "turn-limit");
            if (!ok) bad++;
            Console.WriteLine($"{(ok ? "  " : "!!")} {canon.Id.Entry,-32} {string.Join(", ", outcomes.Select(kv => $"{kv.Key} x{kv.Value}"))}{(ex > 0 ? $"  [swallowed {ex}]" : "")}");
        }
        Console.WriteLine($"[sweep] {bad} problem {sweep}");
        foreach (var (k, v) in GodotStubs.GameExceptions.OrderByDescending(e => e.Value)) Console.WriteLine($"   {v,5}  {k}");
    });
    return;
}

AsyncPump.Run(async () =>
{
    var sw = Stopwatch.StartNew();
    int wins = 0, floors = 0;
    var endings = new System.Collections.Generic.Dictionary<string, int>();
    for (int k = 0; k < n; k++)
    {
        var r = await new RunDriver(new RandomAgent(k), log, godMode: Environment.GetEnvironmentVariable("STS_GOD") == "1", character: Environment.GetEnvironmentVariable("STS_CHAR") ?? "IRONCLAD").Play(seedPrefix + k);
        if (r.Victory) wins++;
        floors += r.Floor;
        endings[r.EndedIn] = endings.GetValueOrDefault(r.EndedIn) + 1;
        if (log || r.Error != null) Console.WriteLine($"[run {k}] {r with { Error = null }}" + (r.Error != null ? "\n" + r.Error : ""));
    }
    double secs = sw.Elapsed.TotalSeconds;
    Console.WriteLine($"[bench] {n} runs in {secs:F2}s = {n / secs:F1} runs/s | wins {wins} | avg floor {(double)floors / n:F1}");
    foreach (var (k, v) in endings.OrderByDescending(e => e.Value)) Console.WriteLine($"   {v,5}  {k}");
    Console.WriteLine($"[swallowed game exceptions] {GodotStubs.GameExceptions.Values.Sum()}");
    foreach (var (k, v) in GodotStubs.GameExceptions.OrderByDescending(e => e.Value)) Console.WriteLine($"   {v,5}  {k}");
});
