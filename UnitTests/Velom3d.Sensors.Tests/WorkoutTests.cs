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
        var start = DateTimeOffset.UnixEpoch;
        double now = 0;
        var rec = new RideRecorder(RideMode.Workout, new RiderProfile(), start, 180, "Sweet spot") { Clock = () => start.AddSeconds(now) };
        rec.TargetCadenceRpm = 90;
        rec.Tick(2.0, 175, 88, 120);
        now = 2;
        rec.TargetPowerW = 0;
        rec.TargetCadenceRpm = 0;
        rec.Tick(1.0, 150, 80, 120);
        now = 3;
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
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "old.json"), """{ "Id": "old", "Mode": "TargetPower", "FtpW": 200 }""");
        File.WriteAllText(Path.Combine(_dir, "old.csv"),
            "time_s,power_w,cadence_rpm,heart_rate_bpm,speed_kmh,distance_m,grade_pct,target_power_w\n1,200,90,0,30,8,0,200\n2,200,90,0,31,17,0,200\n");
        var back = store.Load("old")!;
        Assert.Equal(2, back.Samples.Count);
        Assert.All(back.Samples, s => Assert.Equal((200, 0), (s.TargetPowerW, s.TargetCadenceRpm)));
    }
}

public class FtpTestTests
{
    [Fact]
    public void Test_workout_has_one_20_minute_free_effort()
    {
        var w = Workout.FtpTestWorkout();
        var effort = Assert.Single(w.Blocks, b => b.FtpTest);
        Assert.Equal(BlockPower.Free, effort.Kind);
        Assert.Equal(Workout.FtpTestEffortS, effort.DurationS);
    }

    [Fact]
    public void Ftp_is_95_percent_of_the_effort_average()
    {
        var w = Workout.FtpTestWorkout();
        var p = new WorkoutProgress(w, 200);
        int start = w.StartOf(w.Blocks.FindIndex(b => b.FtpTest));
        p.Advance(start, 150); // échauffement : ne compte pas
        Assert.Null(p.FtpTestResultW);
        for (int t = 0; t < Workout.FtpTestEffortS; t++) p.Advance(1, t % 2 == 0 ? 240 : 260);
        Assert.Equal(250, p.FtpTestAveragePowerW, 3);
        Assert.Equal(238, p.FtpTestResultW);
        p.Advance(60, 100); // retour au calme : ne change rien
        Assert.Equal(238, p.FtpTestResultW);
    }

    [Fact]
    public void Skipped_or_cut_short_test_gives_no_ftp()
    {
        var w = Workout.FtpTestWorkout();
        int index = w.Blocks.FindIndex(b => b.FtpTest);
        var p = new WorkoutProgress(w, 200);
        p.Advance(w.StartOf(index), 150);
        p.Advance(600, 300); // 10 min sur 20
        p.SkipBlock();
        Assert.Null(p.FtpTestResultW);
        Assert.Null(new WorkoutProgress(Examples()[0], 200).FtpTestResultW);
    }

    static System.Collections.Generic.List<Workout> Examples() => Workout.Examples(System.DateTimeOffset.Now);
}

public class RampTestTests
{
    [Fact]
    public void Ramp_test_climbs_6_percent_per_minute()
    {
        var w = Workout.RampTestWorkout();
        var steps = w.Blocks.FindAll(b => b.RampTest);
        Assert.Equal(Workout.RampSteps, steps.Count);
        Assert.All(steps, s => Assert.Equal(60, s.DurationS));
        Assert.Equal(100, w.TargetW(steps[0], 0, 200));
        Assert.Equal(112, w.TargetW(steps[1], 0, 200));
    }

    [Fact]
    public void Ftp_is_75_percent_of_best_minute_and_stops_on_failure()
    {
        var w = Workout.RampTestWorkout();
        var p = new WorkoutProgress(w, 200);
        p.Advance(300, 90); // échauffement : ne compte pas
        // Le cycliste tient les paliers (à la consigne) pendant 10 min, par secondes.
        for (int t = 0; t < 600; t++) p.Advance(1, p.TargetW ?? 0);
        Assert.Equal(11, p.BlockIndex); // échauffement + 10 paliers passés : 11e palier
        double best = p.RampBestMinuteW;
        Assert.Equal(208, best, 0); // palier 10 : 50 + 9 × 6 = 104 % de 200 W
        p.EndRampTest();
        Assert.False(p.Current!.RampTest);
        Assert.Equal(156, p.FtpTestResultW);
        p.Advance(300, 50);
        Assert.Equal(156, p.FtpTestResultW);
    }

    [Fact]
    public void Too_short_ramp_gives_no_ftp()
    {
        var p = new WorkoutProgress(Workout.RampTestWorkout(), 200);
        p.Advance(300, 90);
        for (int t = 0; t < 120; t++) p.Advance(1, 400);
        p.EndRampTest();
        Assert.Null(p.FtpTestResultW);
    }
}
