using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Desktop.ViewModels;
using SenseNova.TokenBurner.Infrastructure.Api;

namespace SenseNova.TokenBurner.Tests;

[TestClass]
public sealed class TrayWindowTests
{
    [TestMethod]
    public Task CloseHidesToTrayAndRestoreSynchronizesHiddenConfiguration()
        => OnWindow(async (form, vm) =>
        {
            var tray = Field<NotifyIcon>(form, "_tray");
            Assert.IsTrue(tray.Visible);
            form.Close();
            Assert.IsFalse(form.Visible);
            Assert.IsFalse(form.IsDisposed);
            Assert.IsTrue(tray.Visible);
            Assert.IsTrue(vm.IsIdle);
            vm.Percentage = 50;
            Field<ToolStripMenuItem>(form, "_trayShow").PerformClick();
            Assert.IsTrue(form.Visible);
            Assert.AreEqual(50, Field<TrackBar>(form, "_percentage").Value);
            Assert.IsFalse(Field<ToolStripMenuItem>(form, "_trayStop").Enabled);
            Assert.IsFalse(Field<ToolStripMenuItem>(form, "_trayDisablePlan").Enabled);
            await Task.CompletedTask;
        });

    [TestMethod]
    public Task MinimizeRestoresPriorWindowStateWithoutClosingOrStarting()
        => OnWindow(async (form, vm) =>
        {
            form.WindowState = FormWindowState.Maximized;
            form.WindowState = FormWindowState.Minimized;
            Assert.IsFalse(form.Visible);
            Assert.IsFalse(form.IsDisposed);
            Field<ToolStripMenuItem>(form, "_trayShow").PerformClick();
            Assert.IsTrue(form.Visible);
            Assert.AreEqual(FormWindowState.Maximized, form.WindowState);
            Assert.IsFalse(vm.IsRunActive);
            Assert.IsFalse(vm.ScheduleEnabled);
            for (var i = 0; i < 3; i++)
            {
                form.Close();
                Field<ToolStripMenuItem>(form, "_trayShow").PerformClick();
            }
            Assert.AreEqual(RecoveryState.Ready, vm.Recovery.State);
            await Task.CompletedTask;
        });

    [TestMethod]
    [DataRow(FormWindowState.Normal)]
    [DataRow(FormWindowState.Maximized)]
    public Task OrdinaryWindowRestoresPriorStateAfterNativeMinimize(FormWindowState expected)
        => OnWindow(async (form, _) =>
        {
            form.WindowState = expected;
            await Task.Delay(50);
            Assert.AreEqual(expected, form.WindowState);
            form.WindowState = FormWindowState.Minimized;
            await Task.Delay(50);
            Assert.IsFalse(form.Visible);
            Field<ToolStripMenuItem>(form, "_trayShow").PerformClick();
            await Task.Delay(50);
            Assert.IsTrue(form.Visible);
            Assert.AreEqual(expected, form.WindowState);
        }, ordinaryWindow: true);

    [TestMethod]
    public Task TrayExitClosesHiddenWindowAndDisablesFurtherActions()
        => OnWindow(async (form, vm) =>
        {
            form.Close();
            Field<ToolStripMenuItem>(form, "_trayExit").PerformClick();
            Assert.IsFalse(vm.IsIdle);
            Assert.IsFalse(vm.StartRunCommand.CanExecute(null));
            Assert.AreEqual(ScheduleState.Shutdown, vm.Schedule.State);
            await Task.CompletedTask;
        });

    [TestMethod]
    [DataRow(CloseReason.WindowsShutDown)]
    [DataRow(CloseReason.ApplicationExitCall)]
    public Task SystemCloseReasonRequestsExitInsteadOfHiding(CloseReason reason)
        => OnWindow(async (form, vm) =>
        {
            // Only this fixture receives the close event; this does not shut down Windows.
            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, [new FormClosingEventArgs(reason, false)]);
            Assert.IsFalse(vm.IsIdle);
            Assert.AreEqual(ScheduleState.Shutdown, vm.Schedule.State);
            await Task.CompletedTask;
        });

    private static Task OnWindow(Func<Form, MainWindowViewModel, Task> action, bool ordinaryWindow = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            var bodyDone = false;
            var closed = false;
            try
            {
                var vm = new MainWindowViewModel(mockMode: true, runStore: new MemoryRunStateStore());
                var type = typeof(MainWindowViewModel).Assembly.GetType("SenseNova.TokenBurner.Desktop.MainForm", true)!;
                using var form = (Form)Activator.CreateInstance(type, vm)!;
                var requestExit = type.GetMethod("RequestExit", BindingFlags.Instance | BindingFlags.NonPublic)!;
                // Ordinary-window regression retains native placement; others use transparent fixtures.
                // Neither mode drives native mouse input or verifies visual layout.
                form.Opacity = ordinaryWindow ? 1 : 0;
                form.ShowInTaskbar = ordinaryWindow;
                using var watchdog = new System.Windows.Forms.Timer { Interval = 10000 };
                watchdog.Tick += (_, _) => { failure ??= new TimeoutException("隔离托盘控件测试超时"); Application.ExitThread(); };
                form.FormClosed += (_, _) => closed = true;
                form.Shown += async (_, _) =>
                {
                    watchdog.Start();
                    try { await vm.InitializeAsync(); await action(form, vm); bodyDone = true; }
                    catch (Exception exception) { failure = exception; }
                    finally { if (!form.IsDisposed) requestExit.Invoke(form, null); }
                };
                Application.Run(form);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.IsTrue(bodyDone && closed);
                Assert.IsFalse(Field<NotifyIcon>(form, "_tray").Visible);
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static T Field<T>(Form form, string name)
        => (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
}
