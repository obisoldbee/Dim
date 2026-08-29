using System.Windows.Forms;
using OBDim.UI;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// v1.0.7: executable specification of the mechanism behind the critical threading
/// defect, so it cannot be reintroduced by someone "simplifying" <c>_syncRoot</c>.
/// </summary>
/// <remarks>
/// <c>NotifyIcon</c> is not a <see cref="Control"/>, so <c>TrayApp</c> keeps a hidden
/// control purely to marshal UI work back to the UI thread. That only works while the
/// control owns a window handle, and a <see cref="Control"/> does not create one until
/// something asks for it. Without a handle, <c>InvokeRequired</c> walks the parent chain
/// for a marshaling control, finds none, and returns <b>false</b> — which reads exactly
/// like "you are already on the UI thread", so every <c>BeginInvoke</c> guarded by it
/// silently ran in place on the caller's thread.
/// <para>
/// v1.0.8: these tests used to build a bare <see cref="Control"/> and assert on that,
/// which proved nothing about <c>TrayApp</c> — QA commented the handle line out of the
/// production constructor and all 396 tests stayed green. They now call
/// <see cref="TrayApp.CreateSyncRoot"/>, the exact code the constructor runs, and assert
/// the property that actually makes marshalling work: <c>InvokeRequired</c> is true when
/// asked from a thread that is not the one owning the handle.
/// </para>
/// <para>
/// Nothing here can be asserted from the main test thread: WinForms controls must be
/// created on an STA thread, and the handle-owning thread has to stay alive for the
/// cross-thread probe, so each case runs on a dedicated STA thread.
/// </para>
/// </remarks>
public class UiMarshallingTests
{
    /// <summary>
    /// The fix, in the smallest possible form: the control <c>TrayApp</c> uses for
    /// marshalling owns a window handle.
    /// </summary>
    [Fact]
    public void CreateSyncRoot_ReturnsAControlWithAHandle()
    {
        RunOnStaThread(() =>
        {
            using var syncRoot = TrayApp.CreateSyncRoot();

            Assert.True(syncRoot.IsHandleCreated,
                "the marshaling control must own a window handle, otherwise InvokeRequired " +
                "always returns false and every BeginInvoke silently runs on the caller's thread");
        });
    }

    /// <summary>
    /// The property that actually matters: asked from another thread, the control reports
    /// that marshalling is required. This is the assertion that turns red if
    /// <c>_ = control.Handle;</c> is removed from <see cref="TrayApp.CreateSyncRoot"/> —
    /// a handle-less control answers <c>false</c> from every thread.
    /// </summary>
    [Fact]
    public void CreateSyncRoot_ReportsInvokeRequiredFromAnotherThread()
    {
        Control? syncRoot = null;
        var handleCreated = new ManualResetEventSlim(false);
        var releaseOwner = new ManualResetEventSlim(false);
        var invokeRequiredFromOtherThread = false;

        // The thread that creates the handle must outlive the probe, so it parks on an
        // event instead of exiting.
        var owner = new Thread(() =>
        {
            syncRoot = TrayApp.CreateSyncRoot();
            handleCreated.Set();
            releaseOwner.Wait();
            syncRoot.Dispose();
        });
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();

        try
        {
            Assert.True(handleCreated.Wait(TimeSpan.FromSeconds(30)),
                "the STA thread did not create the control in time");

            // A separate thread, deliberately not the one owning the handle.
            var probe = new Thread(() => invokeRequiredFromOtherThread = syncRoot!.InvokeRequired);
            probe.Start();
            Assert.True(probe.Join(TimeSpan.FromSeconds(30)), "the probe thread did not finish in time");
        }
        finally
        {
            releaseOwner.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(30)), "the STA thread did not finish in time");
        }

        Assert.True(invokeRequiredFromOtherThread,
            "InvokeRequired must be true from a thread that does not own the handle — " +
            "this is the guard that catches a missing _ = control.Handle in CreateSyncRoot");
    }

    /// <summary>
    /// Contrast case: a plain control, with no handle forced, claims from any thread that
    /// marshalling is unnecessary. This is the trap the production code fell into, written
    /// down so the two behaviours can be compared side by side.
    /// </summary>
    [Fact]
    public void PlainControl_WithoutAHandle_ClaimsMarshallingIsNeverNeeded()
    {
        RunOnStaThread(() =>
        {
            using var control = new Control();

            Assert.False(control.IsHandleCreated);
            Assert.False(control.InvokeRequired);
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread and rethrows whatever it
    /// threw, so an assertion failure surfaces with its original message.
    /// </summary>
    /// <param name="body">Code that creates WinForms controls.</param>
    private static void RunOnStaThread(Action body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA test thread did not finish in time");

        if (failure != null)
        {
            throw new Xunit.Sdk.XunitException(
                "Assertion failed on the STA thread: " + failure.Message, failure);
        }
    }
}
