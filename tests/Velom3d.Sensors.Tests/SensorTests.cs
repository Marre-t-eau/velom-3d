using System;
using System.Collections.Generic;
using System.Linq;
using Velom3d.Sensors;
using Xunit;

namespace Velom3d.Sensors.Tests;

public class DecoderTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void IndoorBikeData_reads_speed_cadence_power_and_heart_rate()
    {
        var m = BleDecoders.ParseIndoorBikeData(Hex("4402b80bb400fa008c"))!;
        Assert.Equal(30.0, m.SpeedKmh!.Value, 3);
        Assert.Equal(90.0, m.CadenceRpm!.Value, 3);
        Assert.Equal(250, m.PowerW);
        Assert.Equal(140, m.HeartRateBpm);
    }

    [Fact]
    public void IndoorBikeData_more_data_bit_means_no_speed()
    {
        var m = BleDecoders.ParseIndoorBikeData(Hex("4100c800"))!;
        Assert.Null(m.SpeedKmh);
        Assert.Equal(200, m.PowerW);
    }

    [Fact]
    public void IndoorBikeData_truncated_frame_keeps_what_was_read()
    {
        var m = BleDecoders.ParseIndoorBikeData(Hex("4402b80b"))!;
        Assert.NotNull(m.SpeedKmh);
        Assert.Null(m.PowerW);
    }

    [Fact]
    public void IndoorBikeData_power_is_signed() =>
        Assert.Equal(-10, BleDecoders.ParseIndoorBikeData(Hex("4100f6ff"))!.PowerW);

    [Fact]
    public void IndoorBikeData_all_fields_in_order()
    {
        // vitesse, vit. moy., cadence, cad. moy., distance (u24), résistance, puissance, puiss. moy.,
        // énergie (5 octets), FC, MET, temps écoulé
        var frame = Hex("FE0F" + "e803" + "dc05" + "a000" + "9c00" + "102700" + "0500" + "c800" + "be00"
                        + "2c01" + "ffff" + "ff" + "7d" + "32" + "1e00");
        var m = BleDecoders.ParseIndoorBikeData(frame)!;
        Assert.Equal(10.0, m.SpeedKmh!.Value, 3);
        Assert.Equal(15.0, m.AverageSpeedKmh!.Value, 3);
        Assert.Equal(80.0, m.CadenceRpm!.Value, 3);
        Assert.Equal(10000, m.DistanceM);
        Assert.Equal(5, m.ResistanceLevel);
        Assert.Equal(200, m.PowerW);
        Assert.Equal(190, m.AveragePowerW);
        Assert.Equal(300, m.EnergyKcal);
        Assert.Equal(125, m.HeartRateBpm);
        Assert.Equal(30, m.ElapsedS);
    }

    [Fact]
    public void CyclingPower_reads_power_and_crank_data()
    {
        var m = BleDecoders.ParseCyclingPower(Hex("20002c010a000004"))!;
        Assert.Equal(300, m.PowerW);
        Assert.Equal(10, m.CrankRevs);
        Assert.Equal(1024, m.CrankEventTime);
    }

    [Fact]
    public void CrankCadence_from_two_events() =>
        Assert.Equal(90.09, BleDecoders.CrankCadence(10, 1024, 11, 0x06aa)!.Value, 2);

    [Fact]
    public void CrankCadence_handles_16bit_wraparound() =>
        Assert.Equal(204.8, BleDecoders.CrankCadence(65535, 65000, 1, 64)!.Value, 3);

    [Fact]
    public void CrankCadence_null_without_new_revolution() =>
        Assert.Null(BleDecoders.CrankCadence(10, 1024, 10, 1024));

    [Theory]
    [InlineData("0048", 72)]
    [InlineData("014800", 72)]
    public void HeartRate_8_and_16_bit(string hex, int bpm) =>
        Assert.Equal(bpm, BleDecoders.ParseHeartRate(Hex(hex))!.HeartRateBpm);

    [Fact]
    public void HeartRate_rr_intervals()
    {
        var m = BleDecoders.ParseHeartRate(Hex("10480004"))!;
        Assert.Equal(1.0, Assert.Single(m.RrIntervalsS), 6);
    }

    [Fact]
    public void ControlPoint_response()
    {
        Assert.True(BleDecoders.ParseControlPointResponse(Hex("800501"))!.Ok);
        Assert.False(BleDecoders.ParseControlPointResponse(Hex("800505"))!.Ok);
        Assert.Null(BleDecoders.ParseControlPointResponse(Hex("0501")));
    }

    [Fact]
    public void Uuid_long_to_short() =>
        Assert.Equal("2ad2", BleUuids.ToShort("00002AD2-0000-1000-8000-00805F9B34FB"));
}

public class CommandTests
{
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact] public void Erg_250W() => Assert.Equal("05fa00", Hex(FtmsCommands.SetTargetPower(250)));
    [Fact] public void Grade_5pct() => Assert.Equal("110000f4012833", Hex(FtmsCommands.SetSimulation(5.0)));
    [Fact] public void Grade_minus_2_5pct() => Assert.Equal("11000006ff2833", Hex(FtmsCommands.SetSimulation(-2.5)));
    [Fact] public void Resistance_3_5() => Assert.Equal("0423", Hex(FtmsCommands.SetTargetResistance(3.5)));
    [Fact] public void Resistance_is_clamped() => Assert.Equal("04ff", Hex(FtmsCommands.SetTargetResistance(99)));
}

public class FusionTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void Dedicated_power_meter_wins_while_it_transmits()
    {
        var f = new SensorFusion();
        f.Feed("pm", BleUuids.CyclingPowerMeasurement, Hex("0000fa00"), now: 0.0);  // 250 W pédales
        f.Feed("kickr", BleUuids.IndoorBikeData, Hex("4100c800"), now: 0.5);       // 200 W trainer
        Assert.Equal(250, f.Power);
    }

    [Fact]
    public void Trainer_takes_over_when_power_meter_goes_silent()
    {
        var f = new SensorFusion();
        f.Feed("pm", BleUuids.CyclingPowerMeasurement, Hex("0000fa00"), now: 0.0);
        f.Feed("kickr", BleUuids.IndoorBikeData, Hex("4100c800"), now: SensorFusion.StaleAfterS + 0.1);
        Assert.Equal(200, f.Power);
    }

    [Fact]
    public void Cadence_drops_to_zero_when_cranks_stop()
    {
        var f = new SensorFusion();
        f.Feed("pm", BleUuids.CyclingPowerMeasurement, Hex("20000000" + "0a00" + "0004"), 0.0);
        f.Feed("pm", BleUuids.CyclingPowerMeasurement, Hex("20000000" + "0b00" + "aa06"), 0.7);
        Assert.Equal(90.09, f.Cadence, 2);
        f.Feed("pm", BleUuids.CyclingPowerMeasurement, Hex("20000000" + "0b00" + "aa06"), 4.0);
        Assert.Equal(0.0, f.Cadence);
    }

    [Fact]
    public void Long_uuids_are_accepted()
    {
        var f = new SensorFusion();
        f.Feed("hrm", BleUuids.ToLong(BleUuids.HeartRateMeasurement), Hex("0048"), 0.0);
        Assert.Equal(72, f.HeartRate);
    }
}

/// <summary>Boucle complète trainer simulé -> octets -> fusion, à 60 images/s en temps simulé.</summary>
public class SimulatedTrainerTests
{
    static void Ride(SimulatedTrainer t, SensorFusion f, ref double now, double seconds)
    {
        const double dt = 1.0 / 60.0;
        int frames = 0;
        for (double end = now + seconds; now < end; now += dt)
        {
            t.Step(dt);
            if (++frames % 15 != 0) continue; // 4 Hz, comme un vrai trainer
            f.Feed("sim", BleUuids.IndoorBikeData, t.IndoorBikeFrame(true), now);
            f.Feed("sim", BleUuids.CyclingPowerMeasurement, t.CyclingPowerFrame(), now);
            f.Feed("sim", BleUuids.HeartRateMeasurement, t.HeartRateFrame(), now);
        }
    }

    [Fact]
    public void Flat_then_climb_then_erg()
    {
        var t = new SimulatedTrainer();
        var f = new SensorFusion();
        var responses = new List<ControlPointResponse>();
        f.ControlResponse += responses.Add;
        double now = 0;

        foreach (var cmd in new[] { FtmsCommands.RequestControl(), FtmsCommands.Start() })
            f.Feed("sim", BleUuids.FitnessMachineControlPoint, t.HandleControlPoint(cmd), now);
        Assert.All(responses, r => Assert.True(r.Ok));

        Ride(t, f, ref now, 5);
        Assert.InRange(f.Power, 170, 190);
        Assert.InRange(f.Cadence, 84, 92);
        Assert.InRange(f.Speed, 20, 30);
        double flat = f.Speed;

        t.HandleControlPoint(FtmsCommands.SetSimulation(8.0));
        Ride(t, f, ref now, 2);
        Assert.Equal(8.0, t.GradePct, 3);
        Assert.True(f.Speed < flat * 0.6, $"montée : {f.Speed:0.0} km/h");

        t.HandleControlPoint(FtmsCommands.SetTargetPower(300));
        Ride(t, f, ref now, 3);
        Assert.True(t.ErgMode);
        Assert.InRange(f.Power, 290, 310);
    }

    [Fact]
    public void Unknown_opcode_is_rejected_as_not_supported() =>
        Assert.Equal(new byte[] { 0x80, 0x42, 0x02 }, new SimulatedTrainer().HandleControlPoint(new byte[] { 0x42 }));
}

public class DeviceListTests
{
    [Theory]
    [InlineData(DeviceKind.Trainer, "1826", "1818")]
    [InlineData(DeviceKind.PowerMeter, "1818")]
    [InlineData(DeviceKind.HeartRate, "0000180D-0000-1000-8000-00805F9B34FB")]
    [InlineData(DeviceKind.SpeedCadence, "1816")]
    [InlineData(DeviceKind.Unknown, "fe95")]
    public void Classify_by_advertised_services(DeviceKind expected, params string[] services) =>
        Assert.Equal(expected, DeviceList.Classify(services));

    [Fact]
    public void Repeated_advertisements_update_one_entry_and_keep_name()
    {
        var l = new DeviceList();
        l.Found("aa", "KICKR CORE", ["1826"], -70, 0);
        l.Found("aa", "", [], -55, 1); // réponse de scan sans nom ni services
        var d = Assert.Single(l.Sorted);
        Assert.Equal("KICKR CORE", d.Name);
        Assert.Equal(DeviceKind.Trainer, d.Kind);
        Assert.Equal(-55, d.Rssi);
    }

    [Fact]
    public void Sorted_puts_connected_first_then_role_then_signal()
    {
        var l = new DeviceList();
        l.Found("hr", "HRM", ["180d"], -40, 0);
        l.Found("pm-far", "Pédales", ["1818"], -80, 0);
        l.Found("pm-near", "Pédales 2", ["1818"], -50, 0);
        l.Found("kickr", "KICKR", ["1826"], -90, 0);
        l.MarkConnected("hr", []);
        Assert.Equal(["hr", "kickr", "pm-near", "pm-far"], l.Sorted.Select(d => d.Address));
    }

    [Fact]
    public void Connecting_twice_is_refused_and_times_out()
    {
        var l = new DeviceList();
        l.Found("aa", "KICKR", ["1826"], -60, 0);
        Assert.True(l.MarkConnecting("aa", 10));
        Assert.False(l.MarkConnecting("aa", 11));
        Assert.Empty(l.ExpireConnecting(10 + DeviceList.ConnectTimeoutS - 1));
        Assert.Equal(["aa"], l.ExpireConnecting(10 + DeviceList.ConnectTimeoutS + 1));
        Assert.Equal(LinkState.Available, l.Get("aa")!.State);
    }

    [Fact]
    public void New_scan_forgets_only_devices_not_connected()
    {
        var l = new DeviceList();
        l.Found("aa", "KICKR", ["1826"], -60, 0);
        l.Found("bb", "HRM", ["180d"], -60, 0);
        l.MarkConnected("aa", []);
        l.ClearAvailable();
        Assert.Equal("aa", Assert.Single(l.Sorted).Address);
    }

    [Fact]
    public void Power_slot_prefers_dedicated_meter_and_falls_back_to_trainer()
    {
        var l = new DeviceList();
        l.Found("kickr", "KICKR", ["1826"], -60, 0);
        l.MarkConnected("kickr", ["1826"]);
        Assert.Equal("kickr", l.ConnectedFor(DeviceKind.PowerMeter)?.Address);
        l.Found("pm", "Pédales", ["1818"], -60, 0);
        l.MarkConnected("pm", ["1818"]);
        Assert.Equal("pm", l.ConnectedFor(DeviceKind.PowerMeter)?.Address);
        Assert.Equal("kickr", l.ConnectedFor(DeviceKind.Trainer)?.Address);
        Assert.Null(l.ConnectedFor(DeviceKind.HeartRate));
    }

    [Fact]
    public void Changed_fires_on_every_visible_change()
    {
        var l = new DeviceList();
        int n = 0;
        l.Changed += () => n++;
        l.Found("aa", "KICKR", ["1826"], -60, 0);
        l.MarkConnecting("aa", 0);
        l.MarkConnected("aa", []);
        l.MarkDisconnected("aa");
        l.MarkDisconnected("aa"); // déjà déconnecté : rien
        Assert.Equal(4, n);
    }
}
