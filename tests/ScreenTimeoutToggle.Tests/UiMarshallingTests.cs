using System.Windows.Forms;
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
/// Nothing here can be asserted from the main test thread: WinForms controls must be
/// created on an STA thread, so each case runs on one.
/// </para>
/// </remarks>
public class UiMarshallingTests
{
    /// <summary>
    /// Documents the trap: a freshly created control reports <c>InvokeRequired == false</c>
    /// on any thread, which is why <c>TrayApp</c> must force handle creation.
    /// </summary>
    [Fact]
    public void FreshControl_HasNoHandle_AndClaimsMarshallingIsNotNeeded()
    {
        RunOnStaThread(() =>
        {
            using var control = new Control();

            Assert.False(control.IsHandleCreated);
            Assert.False(control.InvokeRequired,
                "a control without a handle always reports InvokeRequired == false, " +
                "which is the defect TrayApp v1.0.7 fixes");
        });
    }

    /// <summary>
    /// The fix: touching <see cref="Control.Handle"/> forces creation, after which
    /// <c>InvokeRequired</c> means what it says.
    /// </summary>
    [Fact]
    public void TouchingHandle_CreatesIt_SoMarshallingCanWork()
    {
        RunOnStaThread(() =>
        {
            using var control = new Control();
            _ = control.Handle;

            Assert.True(control.IsHandleCreated);
            // Same thread created the handle and is asking, so marshalling is not needed
            // here — the point is that the question is now answerable at all.
            Assert.False(control.InvokeRequired);
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread and rethrows whatever it
    /// threw, so an assertion failure surfaces with its original stack.
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
