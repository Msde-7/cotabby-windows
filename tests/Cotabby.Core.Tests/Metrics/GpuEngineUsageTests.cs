using Cotabby.Core.Metrics;

namespace Cotabby.Core.Tests;

public class GpuEngineUsageTests
{
    private const string Gpu = "luid_0x00000000_0x000187F8_phys_0";
    private const string OtherGpu = "luid_0x00000000_0x00019702_phys_0";

    private static GpuEngineReading Engine(int pid, int engine, string type, double percent, string adapter = Gpu) =>
        new($"pid_{pid}_{adapter}_eng_{engine}_engtype_{type}", percent);

    [Fact]
    public void ParsesAnInstanceName()
    {
        Assert.True(GpuEngineUsage.TryParse(
            "pid_12572_luid_0x00000000_0x000187f8_phys_0_eng_3_engtype_Copy", out var engine));
        Assert.Equal(12572, engine.ProcessId);
        Assert.Equal("luid_0x00000000_0x000187f8_phys_0", engine.Adapter);
        Assert.Equal("copy", engine.EngineType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("luid_0x0_0x1_phys_0")]
    [InlineData("pid_abc_luid_0x0_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_12_luid_0x0_0x1_phys_0_eng_0_engtype_")]
    [InlineData("pid_12_luid_0x0_0x1_phys_0")]
    public void RejectsNamesThatDoNotFollowThePattern(string name) =>
        Assert.False(GpuEngineUsage.TryParse(name, out _));

    [Fact]
    public void SumsEachEngineTypeAndTakesTheBusiest()
    {
        GpuEngineReading[] readings =
        [
            Engine(10, 0, "3D", 20),
            Engine(10, 1, "Copy", 5),
            Engine(10, 2, "Copy", 30),
            Engine(10, 3, "Compute_0", 12),
        ];
        // The two copy engines add up to 35, which beats 3D at 20.
        Assert.Equal(35, GpuEngineUsage.ProcessPercent(readings, 10));
    }

    [Fact]
    public void DeviceShareSumsEveryProcessOnTheSameEngine()
    {
        GpuEngineReading[] readings =
        [
            Engine(10, 0, "3D", 20),
            Engine(20, 0, "3D", 17),
            Engine(20, 1, "VideoDecode", 9),
        ];
        Assert.Equal(37, GpuEngineUsage.DevicePercent(readings));
        Assert.Equal(20, GpuEngineUsage.ProcessPercent(readings, 10));
    }

    [Fact]
    public void AdaptersAreNotAddedTogether()
    {
        GpuEngineReading[] readings = [Engine(10, 0, "3D", 60), Engine(10, 0, "3D", 70, OtherGpu)];
        Assert.Equal(70, GpuEngineUsage.DevicePercent(readings));
    }

    [Fact]
    public void ShareIsClampedToOneHundred()
    {
        GpuEngineReading[] readings = [Engine(10, 0, "3D", 80), Engine(20, 0, "3D", 45)];
        Assert.Equal(100, GpuEngineUsage.DevicePercent(readings));
    }

    [Fact]
    public void ProcessIdMatchesExactly()
    {
        // pid 12 must not pick up the engines of pid 123.
        GpuEngineReading[] readings = [Engine(123, 0, "3D", 50)];
        Assert.Null(GpuEngineUsage.ProcessPercent(readings, 12));
        Assert.Equal(50, GpuEngineUsage.ProcessPercent(readings, 123));
    }

    [Fact]
    public void NoEnginesIsNoDataNotZero()
    {
        Assert.Null(GpuEngineUsage.DevicePercent([]));
        Assert.Null(GpuEngineUsage.ProcessPercent([Engine(20, 0, "3D", 5)], 10));
        // An idle engine the process does own is a real 0.
        Assert.Equal(0, GpuEngineUsage.ProcessPercent([Engine(10, 0, "3D", 0)], 10));
    }

    [Fact]
    public void IgnoresUnparseableInstancesAndBadValues()
    {
        GpuEngineReading[] readings =
        [
            new("_Total", 99),
            Engine(10, 0, "3D", double.NaN),
            Engine(10, 1, "3D", -4),
            Engine(10, 2, "Copy", 6),
        ];
        Assert.Equal(6, GpuEngineUsage.ProcessPercent(readings, 10));
    }
}
