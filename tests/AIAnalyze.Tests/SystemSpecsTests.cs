using AIAnalyze.Sensors;
using AIAnalyze.Storage;
using Xunit;

namespace AIAnalyze.Tests;

public class SystemSpecsTests
{
    private static SystemSpecs Sample(string driver = "32.0.1") => new(
        ComputerManufacturer: "ExampleCorp",
        ComputerModel: "Model X",
        OsName: "Microsoft Windows 11 Enterprise",
        OsVersion: "10.0.22631",
        OsBuild: "22631",
        CpuName: "AMD Ryzen 7 7800X3D",
        CpuCores: 8,
        CpuLogicalProcessors: 16,
        CpuMaxClockMhz: 4200,
        MotherboardManufacturer: "ExampleBoard",
        MotherboardProduct: "B650",
        BiosVersion: "1.2.3",
        RamTotalGb: 32.0,
        Gpus: [new GpuSpec("NVIDIA GeForce RTX 4070", driver, 12.0)]);

    [Fact]
    public void Specs_survive_a_json_round_trip()
    {
        var original = Sample();

        var restored = SystemSpecs.FromJson(original.ToJson());

        // Compare by content: record equality would compare the GPU list by reference.
        Assert.Equal(original.ToJson(), restored!.ToJson());
    }

    [Fact]
    public void Summary_names_cpu_ram_gpu_and_driver()
    {
        var summary = Sample("32.0.1").Summary;

        Assert.Contains("AMD Ryzen 7 7800X3D", summary);
        Assert.Contains("32.0 GB RAM", summary);
        Assert.Contains("NVIDIA GeForce RTX 4070 (driver 32.0.1)", summary);
    }

    [Fact]
    public void Snapshot_is_saved_once_and_again_only_when_it_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-test-{Guid.NewGuid():N}.db");
        try
        {
            var now = DateTimeOffset.UtcNow;
            using (var store = new SampleStore(path))
            {
                Assert.True(store.SaveSpecsIfChanged(now, Sample("32.0.1")));
                Assert.False(store.SaveSpecsIfChanged(now.AddDays(1), Sample("32.0.1")));
                Assert.True(store.SaveSpecsIfChanged(now.AddDays(2), Sample("32.0.2")));
            }
        }
        finally
        {
            // Connection pooling keeps the file open after Dispose; release it so the delete succeeds.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
