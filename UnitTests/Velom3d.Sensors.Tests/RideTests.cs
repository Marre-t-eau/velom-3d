using System;
using System.IO;
using System.Linq;
using Velom3d.Rides;
using Velom3d.Sensors;
using Xunit;

namespace Velom3d.Sensors.Tests;

public class PhysicsTests
{
    [Fact]
    public void Flat_200W_rides_around_32_kmh()
    {
        var bike = new BikePhysics { MassKg = 83.0 };
        double kmh = bike.EquilibriumSpeed(200) * 3.6;
        Assert.InRange(kmh, 30.0, 34.0);
    }

    [Fact]
    public void Step_converges_to_equilibrium_speed()
    {
        var bike = new BikePhysics { MassKg = 83.0 };
        for (int i = 0; i < 600; i++) bike.Step(250, 1.0);
        Assert.Equal(bike.EquilibriumSpeed(250), bike.SpeedMps, 2);
    }

    [Fact]
    public void Coasting_slows_down_and_stops_at_zero()
    {
        var bike = new BikePhysics();
        bike.Reset(10.0);
        bike.Step(0, 5.0);
        Assert.InRange(bike.SpeedMps, 0.1, 9.0);
        bike.Step(0, 600.0);
        Assert.Equal(0.0, bike.SpeedMps, 3);
    }

    [Fact]
    public void Heavier_rider_is_slower_on_a_climb()
    {
        var light = new BikePhysics { MassKg = 65, GradePct = 6 };
        var heavy = new BikePhysics { MassKg = 95, GradePct = 6 };
        Assert.True(heavy.EquilibriumSpeed(250) < light.EquilibriumSpeed(250));
    }

    [Fact]
    public void Ftms_wind_coefficient_matches_CdA() =>
        Assert.Equal(0.245, new BikePhysics().WindResistanceCoefficient, 3);
}

public class MetricsTests
{
    static RideSample[] Constant(int seconds, int watts, int hr = 0, double cadence = 90) =>
        Enumerable.Range(1, seconds).Select(t => new RideSample(t, watts, cadence, hr, 30, t * 8.33, 0, 0)).ToArray();

    [Fact]
    public void One_hour_at_FTP_is_100_TSS_and_IF_1()
    {
        var s = RideMetrics.Compute(Constant(3600, 250), 250);
        Assert.Equal(250.0, s.NormalizedPowerW, 6);
        Assert.Equal(1.0, s.IntensityFactor, 6);
        Assert.Equal(100.0, s.Tss, 6);
        Assert.Equal(900.0, s.WorkKj, 6);
        Assert.InRange(s.Kcal, 890, 900); // ≈ 1 kcal par kJ
    }

    [Fact]
    public void Normalized_power_exceeds_average_for_intervals()
    {
        // 1 min à 300 W, 1 min à 100 W, pendant 20 min.
        var samples = Enumerable.Range(1, 1200)
            .Select(t => new RideSample(t, (t / 60) % 2 == 0 ? 300 : 100, 90, 0, 30, 0, 0, 0)).ToArray();
        var s = RideMetrics.Compute(samples, 250);
        Assert.Equal(200.0, s.AvgPowerW, 0);
        Assert.True(s.NormalizedPowerW > 220, $"NP {s.NormalizedPowerW}");
    }

    [Fact]
    public void Missing_heart_rate_and_freewheeling_are_left_out_of_averages()
    {
        var samples = Constant(60, 200, hr: 0).Concat(Constant(60, 0, hr: 0, cadence: 0)
            .Select(x => x with { TimeS = x.TimeS + 60 })).ToArray();
        var s = RideMetrics.Compute(samples, 200);
        Assert.Equal(0.0, s.AvgHeartRateBpm);
        Assert.Equal(90.0, s.AvgCadenceRpm, 6);
        Assert.Equal(100.0, s.AvgPowerW, 6);
    }

    [Theory]
    [InlineData(100, 0)] [InlineData(150, 1)] [InlineData(200, 2)] [InlineData(250, 3)]
    [InlineData(280, 4)] [InlineData(350, 5)] [InlineData(500, 6)]
    public void Power_zones_follow_Coggan(int watts, int zone) => Assert.Equal(zone, RideMetrics.ZoneOf(watts, 250));

    [Fact]
    public void Empty_ride_has_empty_summary() => Assert.Equal(0.0, RideMetrics.Compute([], 250).Tss);
}

public class RecorderTests
{
    static RideRecorder NewRecorder(RideMode mode = RideMode.Simulation) =>
        new(mode, new RiderProfile { FtpW = 200 }, new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero), 180);

    [Fact]
    public void One_sample_per_second_even_with_uneven_frames()
    {
        var rec = NewRecorder();
        var rng = new Random(3);
        double t = 0;
        while (t < 120.0) { double dt = 0.005 + rng.NextDouble() * 0.3; rec.Tick(dt, 200, 90, 140); t += dt; }
        Assert.Equal(120, rec.Ride.Samples.Count);
        Assert.Equal(Enumerable.Range(1, 120).Select(i => (double)i), rec.Ride.Samples.Select(s => Math.Round(s.TimeS, 6)));
    }

    [Fact]
    public void Pause_stops_time_and_distance()
    {
        var rec = NewRecorder();
        for (int i = 0; i < 60; i++) rec.Tick(0.5, 200, 90, 140);
        double distance = rec.DistanceM;
        rec.Pause();
        for (int i = 0; i < 60; i++) rec.Tick(0.5, 200, 90, 140);
        Assert.Equal(30.0, rec.ElapsedS, 6);
        Assert.Equal(distance, rec.DistanceM);
        rec.Resume();
        rec.Tick(1.0, 200, 90, 140);
        Assert.Equal(31, rec.Ride.Samples.Count);
    }

    [Fact]
    public void Distance_matches_speed_and_summary_uses_ride_FTP()
    {
        var rec = NewRecorder(RideMode.TargetPower);
        for (int i = 0; i < 600; i++) rec.Tick(1.0, 200, 90, 140);
        var ride = rec.Finish();
        Assert.Equal(180, ride.Info.TargetPowerW);
        Assert.Equal(180, ride.Samples[^1].TargetPowerW);
        Assert.Equal(1.0, ride.Info.Summary.IntensityFactor, 6);
        Assert.InRange(ride.Info.Summary.DistanceM, 4500, 5400); // ~10 min à ~31 km/h, départ arrêté
        Assert.Equal(ride.Info.Summary.DistanceM, rec.DistanceM, 0);
    }
}

public class StoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "velom3d-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static Ride MakeRide(DateTimeOffset at, int watts)
    {
        var rec = new RideRecorder(RideMode.TargetPower, new RiderProfile(), at, watts);
        for (int i = 0; i < 90; i++) rec.Tick(1.0, watts, 88.5, 150);
        return rec.Finish();
    }

    [Fact]
    public void Save_load_and_list_newest_first()
    {
        var store = new RideStore(_dir);
        var older = MakeRide(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), 150);
        var newer = MakeRide(new DateTimeOffset(2026, 10, 9, 18, 30, 0, TimeSpan.FromHours(2)), 220);
        store.Save(older);
        store.Save(newer);

        var list = store.List();
        Assert.Equal([newer.Info.Id, older.Info.Id], list.Select(r => r.Id));
        Assert.Equal(RideMode.TargetPower, list[0].Mode);
        Assert.Equal(newer.Info.Summary.Tss, list[0].Summary.Tss, 6);
        Assert.Equal(7, list[0].Summary.PowerZonesS.Length);

        var loaded = store.Load(newer.Info.Id)!;
        Assert.Equal(90, loaded.Samples.Count);
        Assert.Equal(newer.Samples[42].PowerW, loaded.Samples[42].PowerW);
        Assert.Equal(88.5, loaded.Samples[42].CadenceRpm, 6);
        Assert.Equal(newer.Samples[42].DistanceM, loaded.Samples[42].DistanceM, 0);
        Assert.Equal(newer.Info.StartedAt, loaded.Info.StartedAt);
    }

    [Fact]
    public void Delete_removes_both_files()
    {
        var store = new RideStore(_dir);
        var ride = MakeRide(DateTimeOffset.UtcNow, 200);
        store.Save(ride);
        store.Delete(ride.Info.Id);
        Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Profile_round_trip_and_default_and_corrupt_files_are_ignored()
    {
        var store = new RideStore(_dir);
        Assert.Equal(200, store.LoadProfile().FtpW);
        store.SaveProfile(new RiderProfile { FtpW = 265, RiderMassKg = 68.5, LastMode = RideMode.TargetPower });
        var p = store.LoadProfile();
        Assert.Equal(265, p.FtpW);
        Assert.Equal(68.5, p.RiderMassKg);
        Assert.Equal(RideMode.TargetPower, p.LastMode);

        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ pas du json");
        Assert.Empty(store.List());
    }
}

public class TrainerModeTests
{
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Entering_simulation_resets_then_takes_control_again()
    {
        var frames = FtmsCommands.EnterSimulation(0.0, 0.0, 0.004, 0.25);
        Assert.Equal(["01", "00", "07", "11000000002819"], frames.Select(Hex));
    }

    [Fact]
    public void Entering_erg_takes_control_before_the_target() =>
        Assert.Equal(["00", "07", "05dc00"], FtmsCommands.EnterErg(220).Select(Hex));

    [Fact]
    public void Simulation_after_erg_really_leaves_erg()
    {
        var t = new SimulatedTrainer();
        foreach (var f in FtmsCommands.EnterErg(250)) t.HandleControlPoint(f);
        Assert.True(t.ErgMode);
        foreach (var f in FtmsCommands.EnterSimulation(0.0, 0.0, 0.004, 0.25))
            Assert.Equal(0x01, t.HandleControlPoint(f)[2]);
        Assert.False(t.ErgMode);
        Assert.Equal(0, t.TargetPowerW);
        Assert.Equal(0.25, t.Cw, 6);
    }

    [Fact]
    public void Refusals_are_explained_in_plain_words() =>
        Assert.Equal("mode simulation / non prise en charge par ce trainer",
            FtmsCommands.Describe(0x11) + " / " + FtmsCommands.DescribeResult(0x02));

    [Fact]
    public void Debug_effort_varies_but_stays_realistic()
    {
        var t = new SimulatedTrainer { VaryEffort = true };
        var f = new SensorFusion();
        var powers = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 600 * 4; i++)
        {
            t.Step(0.25);
            f.Feed("sim", BleUuids.IndoorBikeData, t.IndoorBikeFrame(true), i * 0.25);
            if (i % 4 == 0) powers.Add(f.Power);
        }
        Assert.True(powers.Max() - powers.Min() > 60, $"{powers.Min()}..{powers.Max()}");
        Assert.InRange(powers.Average(), 150, 210);
        Assert.InRange(f.HeartRate, 100, 190);
    }
}

public class GearingTests
{
    [Fact]
    public void Every_preset_cassette_has_its_speed_count_and_increasing_teeth()
    {
        foreach (var (speeds, cassettes) in Gearing.Cassettes)
            foreach (var c in cassettes)
            {
                Assert.Equal(speeds, c.Teeth.Length);
                Assert.True(c.Teeth.Zip(c.Teeth.Skip(1)).All(p => p.Second > p.First), c.Label);
                Assert.Equal(c.Label, $"{c.Teeth[0]}-{c.Teeth[^1]}");
            }
    }

    [Fact]
    public void Speed_and_development_follow_the_gear()
    {
        var d = new Drivetrain([34, 50], [11, 12, 13, 14, 15, 17, 19, 21, 24, 27, 30], 2.105);
        Assert.Equal(50, d.Chainring); // grand plateau d'abord
        Assert.Equal(17, d.Cog);       // pignon du milieu
        Assert.Equal(50.0 / 17 * 2.105, d.Development, 6);
        Assert.Equal(90.0 / 60 * d.Development * 3.6, d.SpeedMpsAt(90) * 3.6, 6); // ≈ 33,4 km/h
        Assert.Equal(6, d.RearGearNumber);
    }

    [Fact]
    public void Shifting_stops_at_both_ends_and_front_cycles()
    {
        var d = new Drivetrain([50, 34], [11, 13, 15, 17, 19]);
        while (d.ShiftHarder()) { }
        Assert.Equal(11, d.Cog);
        Assert.False(d.ShiftHarder());
        while (d.ShiftEasier()) { }
        Assert.Equal(19, d.Cog);
        Assert.Equal(1, d.RearGearNumber);
        d.ShiftFront();
        Assert.Equal(34, d.Chainring);
        d.ShiftFront();
        Assert.Equal(50, d.Chainring);
    }

    [Fact]
    public void Harder_gear_or_faster_cadence_needs_more_power()
    {
        var physics = new BikePhysics { MassKg = 83 };
        var d = new Drivetrain([50, 34], Gearing.DefaultCogs);
        double p = d.RequiredPowerW(physics, 90);
        // La puissance demandée est celle qui tient cette vitesse sur le plat.
        Assert.Equal(d.SpeedMpsAt(90), physics.EquilibriumSpeed(p), 3);
        Assert.True(d.RequiredPowerW(physics, 100) > p);
        d.ShiftHarder();
        Assert.True(d.RequiredPowerW(physics, 90) > p);
        Assert.Equal(0.0, d.RequiredPowerW(physics, 0));
    }

    [Theory]
    [InlineData("52/36", new[] { 52, 36 })]
    [InlineData("50 34", new[] { 50, 34 })]
    [InlineData("42", new[] { 42 })]
    public void Custom_chainrings_are_parsed(string text, int[] expected) =>
        Assert.Equal(expected, Gearing.ParseChainrings(text)!);

    [Theory]
    [InlineData("53/39/30/24")] // 4 plateaux
    [InlineData("52/abc")]
    [InlineData("52/52")]
    [InlineData("70/50")]
    public void Invalid_chainrings_are_refused(string text) => Assert.Null(Gearing.ParseChainrings(text));

    [Fact]
    public void Custom_cassette_is_parsed() =>
        Assert.Equal([11, 12, 13, 14, 16, 18, 20, 22, 25, 28, 32], Gearing.ParseCassette("11,12,13,14,16,18,20,22,25,28,32")!);

    [Fact]
    public void Describe_reads_like_a_bike_shop() =>
        Assert.Equal("52/36 × 11-30 (12 v.)", Gearing.Describe([36, 52], [11, 12, 13, 14, 15, 16, 17, 19, 21, 24, 27, 30]));

    [Fact]
    public void Profile_keeps_gears_and_ride_remembers_them()
    {
        var p = new RiderProfile { VirtualGears = true, Chainrings = [52, 36], Cogs = [11, 12, 13, 14, 15, 17, 19, 21, 24, 27, 30] };
        var rec = new RideRecorder(RideMode.Simulation, p, DateTimeOffset.Now);
        Assert.Equal("52/36 × 11-30 (11 v.)", rec.Ride.Info.VirtualGears);
        Assert.Equal("", new RideRecorder(RideMode.TargetPower, p, DateTimeOffset.Now, 200).Ride.Info.VirtualGears);
    }
}
