using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

/// Runs an async entry point on a single-threaded SynchronizationContext, like Godot's main thread.
/// Every game continuation is posted back to this one thread, so game state is never touched concurrently.
public static class AsyncPump
{
    sealed class Ctx : SynchronizationContext
    {
        public readonly BlockingCollection<(SendOrPostCallback, object)> Queue = new();
        public override void Post(SendOrPostCallback d, object state) => Queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object state) => throw new NotSupportedException();
    }

    static Ctx _ctx;

    /// Yields until no other game continuation is queued on the pump, i.e. the game is blocked or quiet.
    public static async Task Idle(int quietRounds = 3)
    {
        int quiet = 0;
        while (quiet < quietRounds)
        {
            await Task.Yield();
            quiet = _ctx.Queue.Count == 0 ? quiet + 1 : 0;
        }
    }

    public static void Run(Func<Task> main)
    {
        var prev = SynchronizationContext.Current;
        var ctx = _ctx = new Ctx();
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            var t = main();
            t.ContinueWith(_ => ctx.Queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (d, s) in ctx.Queue.GetConsumingEnumerable()) d(s);
            t.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }
}
