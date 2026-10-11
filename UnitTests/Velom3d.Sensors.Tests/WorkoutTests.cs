using System;
using System.IO;
using System.Linq;
using Velom3d.Rides;
using Xunit;

namespace Velom3d.Sensors.Tests;

public class WorkoutTests
{
    static Workout Sample(PowerUnit unit = PowerUnit.Watts) => new()
    {
        Id = "w1", Name = "Test", Unit = unit,
        Blocks =
        [
            new() { DurationS = 60, Kind = BlockPower.Ramp, Power = 100, PowerEnd = 200 },
            new() { DurationS = 120, Kind = BlockPower.Fixed, Power = 250, CadenceRpm = 95 },
            new() { DurationS = 30, Kind = BlockPower.Free },
        ],
    };

    [Fact]
    public void Targets_follow_block_kind()
    {
        var w = Sample();
        Assert.Equal(100, w.TargetW(w.Blocks[0], 0, 200));
        Assert.Equal(150, w.TargetW(w.Blocks[0], 30, 200));
        Assert.Equal(200, w.TargetW(w.Blocks[0], 60, 200));
        Assert.Equal(250, w.TargetW(w.Blocks[1], 10, 200));
        Assert.Null(w.TargetW(w.Blocks[2], 10, 200));
    }

    [Fact]
    public void Percent_is_computed_on_the_FTP()
    {
        var w = Sample(PowerUnit.PercentFtp);
        w.Blocks[1].Power = 90;
        Assert.Equal(225, w.TargetW(w.Blocks[1], 0, 250));
        Assert.Equal(180, w.TargetW(w.Blocks[1], 0, 200));
    }

    [Fact]
    public void Block_lookup_and_starts()
    {
        var w = Sample();
        Assert.Equal(210, w.TotalDurationS);
        Assert.Equal((0, 0.0), w.BlockAt(0));
        Assert.Equal((1, 5.0), w.BlockAt(65));
        Assert.Equal((2, 0.0), w.BlockAt(180));
        Assert.Equal(3, w.BlockAt(210).Index);
        Assert.Equal(180, w.StartOf(2));
    }

    [Fact]
    public void Progress_advances_skips_and_finishes()
    {
        var p = new WorkoutProgress(Sample(), 200);
        p.Advance(30);
        Assert.Equal(150, p.TargetW);
        Assert.Equal(0, p.TargetCadenceRpm);
        Assert.Equal(30, p.BlockRemainingS, 3);
        p.SkipBlock();
        Assert.Equal(1, p.BlockIndex);
        Assert.Equal(250, p.TargetW);
        Assert.Equal(95, p.TargetCadenceRpm);
        p.Advance(120);
        Assert.Null(p.TargetW); // bloc libre
        p.Advance(30);
        Assert.True(p.IsFinished);
        Assert.Null(p.TargetW);
        p.SkipBlock();
        Assert.True(p.IsFinished);
    }

    [Fact]
    public void Converting_units_keeps_the_watts()
    {
        var w = Sample();
        w.ConvertTo(PowerUnit.PercentFtp, 250);
        Assert.Equal(PowerUnit.PercentFtp, w.Unit);
        Assert.Equal(100, w.Blocks[1].Power);
        Assert.Equal(40, w.Blocks[0].Power);
        w.ConvertTo(PowerUnit.Watts, 250);
        Assert.Equal(250, w.Blocks[1].Power);
        Assert.Equal(100, w.Blocks[0].Power);
    }

    [Fact]
    public void Tss_estimate_one_hour_at_FTP_is_100()
    {
        var w = new Workout { Unit = PowerUnit.PercentFtp, Blocks = [new() { DurationS = 3600, Power = 100 }] };
        Assert.Equal(100, w.EstimatedTss(250), 1);
    }

    [Fact]
    public void Clamp_fixes_out_of_range_values()
    {
        var w = new Workout { Unit = PowerUnit.PercentFtp, Blocks = [new() { DurationS = 1, Power = 900, CadenceRpm = 10 }] };
        w.Clamp();
        Assert.Equal(WorkoutBlock.MinDurationS, w.Blocks[0].DurationS);
        Assert.Equal(300, w.Blocks[0].Power);
        Assert.Equal(WorkoutBlock.MinCadenceRpm, w.Blocks[0].CadenceRpm);
    }

    [Fact]
    public void Examples_are_valid()
    {
        var examples = Workout.Examples(DateTimeOffset.Now);
        Assert.Equal(2, examples.Select(e => e.Id).Distinct().Count());
        Assert.All(examples, e => Assert.True(e.TotalDurationS > 1800));
    }
}

public class WorkoutStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "velom3d-workouts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void First_listing_creates_examples_once()
    {
        var store = new WorkoutStore(_dir);
        var first = store.List();
        Assert.Equal(2, first.Count);
        foreach (var w in first) store.Delete(w.Id);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var store = new WorkoutStore(_dir);
        store.List();
        var w = Workout.CreateDefault(DateTimeOffset.Now.AddDays(1), "Mon entraînement");
        w.Blocks[1].Kind = BlockPower.Free;
        store.Save(w);
        var back = store.Load(w.Id)!;
        Assert.Equal("Mon entraînement", back.Name);
        Assert.Equal(PowerUnit.PercentFtp, back.Unit);
        Assert.Equal(BlockPower.Free, back.Blocks[1].Kind);
        Assert.Equal(w.Blocks.Select(b => b.DurationS), back.Blocks.Select(b => b.DurationS));
        Assert.Equal(w.Id, store.List()[0].Id);
    }

    [Fact]
    public void Broken_files_are_ignored()
    {
        var store = new WorkoutStore(_dir);
        store.List();
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ pas du json");
        Assert.Equal(2, store.List().Count);
        Assert.Null(store.Load("absent"));
    }
}

public class TargetHistoryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "velom3d-targets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Recorder_saves_power_and_cadence_targets()
    {
        var rec = new RideRecorder(RideMode.Workout, new RiderProfile(), DateTimeOffset.Now, 180, "Sweet spot");
        rec.TargetCadenceRpm = 90;
        rec.Tick(2.0, 175, 88, 120);
        rec.TargetPowerW = 0;
        rec.TargetCadenceRpm = 0;
        rec.Tick(1.0, 150, 80, 120);
        var store = new RideStore(_dir);
        store.Save(rec.Finish());
        var back = store.Load(rec.Ride.Info.Id)!;
        Assert.Equal("Sweet spot", back.Info.WorkoutName);
        Assert.Equal(RideMode.Workout, back.Info.Mode);
        Assert.Equal([180, 180, 0], back.Samples.Select(s => s.TargetPowerW));
        Assert.Equal([90, 90, 0], back.Samples.Select(s => s.TargetCadenceRpm));
    }

    [Fact]
    public void Old_csv_without_cadence_target_still_loads()
    {
        var store = new RideStore(_dir);
        var rec = new RideRecorder(RideMode.TargetPower, new RiderProfile(), DateTimeOffset.Now, 200);
        rec.Tick(2.0, 200, 90, 0);
        var ride = rec.Finish();
        store.Save(ride);
        var csv = Path.Combine(_dir, ride.Info.Id + ".csv");
        File.WriteAllLines(csv, File.ReadAllLines(csv).Select(l => l[..l.LastIndexOf(',')]));
        var back = store.Load(ride.Info.Id)!;
        Assert.Equal(2, back.Samples.Count);
        Assert.All(back.Samples, s => Assert.Equal((200, 0), (s.TargetPowerW, s.TargetCadenceRpm)));
    }
}
