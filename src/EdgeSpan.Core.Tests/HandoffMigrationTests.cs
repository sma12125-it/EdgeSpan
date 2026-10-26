using EdgeSpan.Product;

namespace EdgeSpan.Core.Tests;

public sealed class HandoffMigrationTests
{
    private static AppSettings LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "edgespan-mig-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            return SettingsStore.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldInstallWithHandoffOffIsTurnedOnOnce()
    {
        var s = LoadJson("{\"handoff\":\"off\"}");
        Assert.Equal("both", s.Handoff);
        Assert.True(s.HandoffDefaulted);
    }

    [Fact]
    public void ChoiceMadeAfterMigrationIsKept()
    {
        var s = LoadJson("{\"handoff\":\"pc\",\"handoffDefaulted\":true}");
        Assert.Equal("pc", s.Handoff);
    }

    [Fact]
    public void FreshInstallDefaultsToBoth()
    {
        var s = SettingsStore.Load(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.Equal("both", s.Handoff);
        Assert.True(s.HandoffDefaulted);
    }
}
