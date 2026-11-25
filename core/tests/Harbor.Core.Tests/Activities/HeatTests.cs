using Harbor.Core.Heat;
using Harbor.Core.Math;
using Xunit;

namespace Harbor.Core.Tests.Activities;

public class HeatTests
{
    [Fact]
    public void Crimes_Raise_Level_And_Repeats_Are_Throttled()
    {
        var t = new ActivityTown();
        var a = t.Player("rook", Vec3d.Zero);
        t.Heat.Report(a.Id, "shots_fired");            // 40 → level 1
        Assert.Equal(0, t.Heat.Report(a.Id, "shots_fired")); // same crime inside repeat window
        Assert.Equal(1, t.Heat.LevelOf(a.Id));
        t.Heat.Report(a.Id, "robbery");                // 160 → level 3
        Assert.Equal(3, t.Heat.LevelOf(a.Id));
        Assert.Equal(0, t.Heat.Report(a.Id, "not_a_crime"));
    }

    [Fact]
    public void Heat_Decays_Only_After_Quiet_Period()
    {
        var t = new ActivityTown();
        var a = t.Player("rook", Vec3d.Zero);
        t.Heat.Report(a.Id, "atm_hack"); // 60 → level 2
        t.Wait(Repo.Data.Heat.DecayDelaySeconds - 1);
        Assert.Equal(60, t.Heat.HeatOf(a.Id), 3);
        t.Wait(1 + 60 / Repo.Data.Heat.DecayPerSecond + 1);
        Assert.Equal(0, t.Heat.LevelOf(a.Id));
        var changes = t.Events.OfType<WantedLevelChanged>().Where(e => e.ActorId == a.Id).Select(e => e.NewLevel).ToList();
        Assert.Equal(new[] { 2, 1, 0 }, changes);
    }

    [Fact]
    public void AddLevels_And_Clear()
    {
        var t = new ActivityTown();
        var a = t.Player("rook", Vec3d.Zero);
        t.Heat.AddLevels(a.Id, 2);
        Assert.Equal(2, t.Heat.LevelOf(a.Id));
        t.Heat.AddLevels(a.Id, 10);
        Assert.Equal(Repo.Data.Heat.MaxLevel, t.Heat.LevelOf(a.Id));
        t.Heat.Clear(a.Id);
        Assert.Equal(0, t.Heat.LevelOf(a.Id));
    }
}
