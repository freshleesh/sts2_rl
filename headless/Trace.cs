using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

/// Debug helper: logs entry and Task completion of selected async methods.
public static class Trace
{
    static readonly Harmony H = new("sts2.trace");
    public static void Method(Type t, string name)
    {
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            if (m.Name == name && !m.IsGenericMethodDefinition)
                H.Patch(m, prefix: new HarmonyMethod(typeof(Trace).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                           postfix: new HarmonyMethod(typeof(Trace).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
    }
    static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    static void Pre(MethodBase __originalMethod) => Console.WriteLine($"{Sw.ElapsedMilliseconds,6}  > {__originalMethod.DeclaringType!.Name}.{__originalMethod.Name}");
    static void Post(MethodBase __originalMethod, object __result)
    {
        var n = $"{__originalMethod.DeclaringType!.Name}.{__originalMethod.Name}";
        if (__result is Task t) t.ContinueWith(x => Console.WriteLine($"{Sw.ElapsedMilliseconds,6}  < {n} [{x.Status}]{(x.Exception != null ? " " + x.Exception.InnerException!.Message : "")}"));
    }
}
