namespace PiPlay.Tests;

/// <summary>
/// Startup never crashes on the session mutex (REQ-APP-01). A name that exists but refuses this
/// process leaves the launch without a mutex, so it can only hand off or report the running
/// instance; it can never continue as the primary without having won the mutex.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class SessionMutexTests
{
    private static string UniqueName() => $@"Local\PiPlay.Tests.SessionMutex.{Guid.NewGuid():N}";

    [Fact]
    public void A_name_squatted_by_another_kind_of_handle_is_reported_instead_of_thrown()
    {
        var name = UniqueName();
        using var squatter = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var reported = new List<Exception>();

        var mutex = App.TryCreateSessionMutex(name, out var createdNew, reported.Add);

        Assert.Null(mutex);
        Assert.False(createdNew);
        Assert.IsType<WaitHandleCannotBeOpenedException>(Assert.Single(reported));
    }

    [Fact]
    public void A_free_name_is_created_and_owned()
    {
        using var mutex = App.TryCreateSessionMutex(
            UniqueName(), out var createdNew, ex => throw new InvalidOperationException("unexpected", ex));

        Assert.NotNull(mutex);
        Assert.True(createdNew);
        mutex!.ReleaseMutex();   // owned by this thread, or this would throw
    }
}
