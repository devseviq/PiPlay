using PiPlay.Services;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class ConsecutiveFailureGateTests
{
    [Fact]
    public void Failure_counts_accumulate_until_recovery_ends_the_episode()
    {
        var gate = new ConsecutiveFailureGate();
        Assert.Equal(1, gate.RecordFailureCount());
        Assert.Equal(2, gate.RecordFailureCount());
        Assert.Equal(3, gate.RecordFailureCount());
        Assert.Equal(2, gate.RecordSuccess());
        Assert.Null(gate.RecordSuccess());
        Assert.Equal(1, gate.RecordFailureCount());
    }
}
