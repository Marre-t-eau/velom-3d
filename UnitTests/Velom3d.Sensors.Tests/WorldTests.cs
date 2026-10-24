using System;
using System.IO;
using System.Linq;
using Velom3d.World;
using Xunit;

namespace Velom3d.Sensors.Tests;

public class LoopCourseTests
{
    static readonly Course Course = MapFile.Default.BuildCourse();

    [Fact]
    public void Loop_is_5_km_and_closes_on_itself()
    {
        double length = 0;
        for (int i = 0; i < Course.PointCount; i++)
        {
            var (ax, az) = Course.Point(i);
            var (bx, bz) = Course.Point(i + 1);
            length += Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        }
        Assert.InRange(length, 4995, 5005);
        var start = Course.At(0);
        var lap = Course.At(5000);
        Assert.Equal(start.X, lap.X, 2);
        Assert.Equal(start.Z, lap.Z, 2);
    }

    [Fact]
    public void Riding_moves_along_the_road_by_the_distance_ridden()
    {
        for (double d = 0; d < 10000; d += 137)
        {
            var a = Course.At(d);
            var b = Course.At(d + 2);
            double moved = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            Assert.InRange(moved, 1.95, 2.01);
            // La direction donnée est bien celle du déplacement.
            Assert.True(((b.X - a.X) * a.DirX + (b.Z - a.Z) * a.DirZ) / moved > 0.99);
        }
    }

    [Fact]
    public void Offset_goes_to_the_right_of_the_road()
    {
        var p = Course.At(1234);
        var (x, z) = p.Offset(3);
        Assert.Equal(3, Course.DistanceToCenterline(x, z), 1);
        // Côté droit du cycliste dans Godot (avant = -Z local, droite = +X local).
        double cross = p.DirX * (z - p.Z) - p.DirZ * (x - p.X);
        Assert.True(cross > 0);
    }

    [Fact]
    public void Curves_are_gentle_enough_for_a_bike()
    {
        for (double d = 0; d < Course.LengthM; d += 10)
        {
            var a = Course.At(d);
            var b = Course.At(d + 10);
            double turn = Math.Acos(Math.Clamp(a.DirX * b.DirX + a.DirZ * b.DirZ, -1, 1));
            Assert.True(10 / Math.Max(turn, 1e-9) > 100, $"virage trop serré à {d} m");
        }
    }
}

public class SceneryTests
{
    static readonly Course Course = MapFile.Default.BuildCourse();

    [Fact]
    public void Scenery_has_bushes_trees_and_houses_off_the_road()
    {
        var props = Scenery.Generate(Course);
        foreach (PropKind kind in Enum.GetValues<PropKind>())
            Assert.True(props.Count(p => p.Kind == kind) >= 8, $"{kind} : {props.Count(p => p.Kind == kind)}");
        Assert.InRange(props.Count, 800, 4000);
        foreach (var p in props)
            Assert.True(Course.DistanceToCenterline(p.X, p.Z) > Scenery.RoadHalfWidthM + 1.5, $"{p.Kind} sur la route");
    }

    [Fact]
    public void Scenery_is_the_same_every_ride()
    {
        Assert.Equal(Scenery.Generate(Course), Scenery.Generate(Course));
    }

    [Fact]
    public void Houses_do_not_overlap()
    {
        var houses = Scenery.Generate(Course).Where(p => p.Kind is PropKind.House or PropKind.Farmhouse).ToList();
        for (int i = 0; i < houses.Count; i++)
            for (int j = i + 1; j < houses.Count; j++)
                Assert.True(Math.Sqrt(Math.Pow(houses[i].X - houses[j].X, 2) + Math.Pow(houses[i].Z - houses[j].Z, 2)) >= 16);
    }
}

/// <summary>La map livrée avec l'appli, lue depuis Maps/.</summary>
static class MapFile
{
    public static string Path(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(System.IO.Path.Combine(dir.FullName, "Maps"))) dir = dir.Parent;
        return System.IO.Path.Combine(dir!.FullName, "Maps", name);
    }

    public static readonly CourseMap Default = CourseMap.FromJson(File.ReadAllText(Path("boucle-5km.json")));
}

public class CourseMapTests
{
    [Fact]
    public void Default_map_file_is_the_5_km_loop_with_its_scenery()
    {
        var map = MapFile.Default;
        Assert.Equal("boucle-5km", map.Id);
        Assert.True(map.Loop);
        var course = map.BuildCourse();
        Assert.InRange(course.LengthM, 4995, 5005);
        var props = map.BuildProps(course);
        Assert.Equal(map.Props.Count, props.Count);
        foreach (PropKind kind in Enum.GetValues<PropKind>()) Assert.Contains(props, p => p.Kind == kind);
        Assert.Contains(map.Landmarks, l => l.Kind == CourseMap.StartArch);
    }

    [Fact]
    public void Default_map_file_is_what_the_generator_gives()
    {
        var generated = ProceduralLoop.Generate("boucle-5km", "Boucle de 5 km");
        Assert.Equal(generated.ToJson(), File.ReadAllText(MapFile.Path("boucle-5km.json")).Replace("\r\n", "\n"));
    }

    [Fact]
    public void Json_round_trip_keeps_the_map()
    {
        var map = MapFile.Default;
        var again = CourseMap.FromJson(map.ToJson());
        Assert.Equal(map.ToJson(), again.ToJson());
    }

    [Fact]
    public void Handmade_map_with_few_points_is_smoothed_and_can_be_open()
    {
        var map = CourseMap.FromJson("""
            {
              "format": "velom3d-map", "version": 1, "id": "test", "name": "Test",
              // Aller simple de quatre points, lissé, qui monte de 20 m.
              "smooth": true,
              "road": [[0, 0, 0], [0, -500, 5], [300, -800, 15], [300, -1200, 20]],
              "props": [{ "kind": "house", "x": 20, "z": -250 }, { "kind": "castle", "x": 0, "z": 0 }],
            }
            """);
        var course = map.BuildCourse();
        Assert.False(course.IsLoop);
        Assert.InRange(course.LengthM, 1300, 1450);
        Assert.Equal(course.At(course.LengthM).X, course.At(course.LengthM + 500).X, 6);
        Assert.Equal(20, course.AltitudeAt(course.LengthM), 3);
        Assert.True(course.GradePctAt(200) > 0);
        var props = map.BuildProps(course);
        Assert.Single(props); // le château est inconnu : ignoré
        Assert.InRange(props[0].CourseDistanceM, 200, 300);
    }

    [Fact]
    public void Not_a_map_is_refused()
    {
        Assert.Throws<FormatException>(() => CourseMap.FromJson("""{ "format": "autre", "road": [[0,0],[1,1]] }"""));
        Assert.Throws<FormatException>(() => CourseMap.FromJson("""{ "format": "velom3d-map", "road": [[0,0]] }"""));
    }
}

public class GpxImportTests
{
    static string Gpx(params (double Lat, double Lon, double Ele)[] pts) =>
        "<?xml version=\"1.0\"?><gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><name>Col test</name><trkseg>"
        + string.Concat(pts.Select(p => FormattableString.Invariant($"<trkpt lat=\"{p.Lat}\" lon=\"{p.Lon}\"><ele>{p.Ele}</ele></trkpt>")))
        + "</trkseg></trk></gpx>";

    [Fact]
    public void Gpx_track_becomes_a_map_in_meters_with_altitude()
    {
        // Vers le nord sur ~2,2 km (0,02° de latitude), en montant de 100 m.
        var pts = Enumerable.Range(0, 201).Select(i => (45.0 + i * 0.0001, 6.0, 500.0 + i * 0.5)).ToArray();
        var map = GpxImport.ToMap(Gpx(pts), "col-test");
        Assert.Equal("Col test", map.Name);
        Assert.Equal("gpx", map.Source);
        Assert.False(map.Loop);
        var course = map.BuildCourse();
        Assert.InRange(course.LengthM, 2200, 2250);
        Assert.True(course.At(1000).DirZ < -0.99); // le nord est vers -z
        Assert.InRange(course.GradePctAt(1000), 4.0, 5.0);
        Assert.NotEmpty(map.Props);
        Assert.Equal(map.ToJson(), CourseMap.FromJson(map.ToJson()).ToJson());
    }

    [Fact]
    public void Gpx_track_back_at_its_start_is_a_loop()
    {
        var pts = Enumerable.Range(0, 360).Select(i =>
        {
            double a = i * Math.PI / 180;
            return (45.0 + 0.005 * Math.Sin(a), 6.0 + 0.007 * Math.Cos(a), 300.0);
        }).Append((45.0, 6.007, 300.0)).ToArray();
        var map = GpxImport.ToMap(Gpx(pts), "boucle-test");
        Assert.True(map.Loop);
        Assert.InRange(map.BuildCourse().LengthM, 3300, 3600);
    }
}
