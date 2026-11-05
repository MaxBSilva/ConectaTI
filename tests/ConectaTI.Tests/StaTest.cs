using System.Windows.Threading;
namespace ConectaTI.Tests;
internal static class StaTest
{
    // WPF permits one Application per process. Keep its STA/Dispatcher alive across tests.
    private static readonly Lazy<Task<Dispatcher>> Host = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var app = new ConectaTI.App.App { IsIsolatedHost = true, ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                ready.SetResult(dispatcher);
                Dispatcher.Run();
                GC.KeepAlive(app);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task;
    });
    public static async Task RunAsync(Func<Task> action)
    {
        var dispatcher = await Host.Value.WaitAsync(TimeSpan.FromSeconds(15));
        var operation = await dispatcher.InvokeAsync(action);
        await operation.WaitAsync(TimeSpan.FromSeconds(45));
    }
}