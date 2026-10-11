using System;
using System.Linq;
using Velom3d.World;
using Xunit;

namespace Velom3d.Sensors.Tests;

public class LoopCourseTests
{
    static readonly LoopCourse Course = new();

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
        Assert.InRange(length, 4990, 5000.1);
        var start = Course.At(0);
        var lap = Course.At(5000);
        Assert.Equal(start.X, lap.X, 6);
        Assert.Equal(start.Z, lap.Z, 6);
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
    static readonly LoopCourse Course = new();

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
