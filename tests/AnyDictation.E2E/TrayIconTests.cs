using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using AnyDictation.App;
using Xunit;

namespace AnyDictation.E2E;

public sealed class TrayIconTests
{
    [Fact]
    public void StateIconsCanBeReusedAcrossRecordingAndRetryCycles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var tray = new TrayIcon(() => { }, () => { }, () => { }, () => { }, () => { });
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    tray.SetState(SessionState.Recording);
                    tray.SetState(SessionState.Recognizing);
                    tray.SetState(SessionState.Idle);
                    tray.SetState(SessionState.Recording);
                    tray.SetState(SessionState.Recognizing);
                    tray.SetState(SessionState.RetryPending);
                    tray.SetState(SessionState.Recognizing);
                    tray.SetState(SessionState.Idle);
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Tray icon test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
