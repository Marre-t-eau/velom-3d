using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Velom3d.Rides;
using Xunit;

namespace Velom3d.Sensors.Tests;

public class TrainingPlanTests
{
    [Fact]
    public void Endurance_plan_has_8_weeks_of_3_sessions()
    {
        var plan = TrainingPlan.Endurance();
        Assert.Equal(8, plan.Weeks.Count);
        Assert.All(plan.Weeks, w => Assert.Equal(3, w.Sessions.Count));
        Assert.Equal(24, plan.SessionCount);
        Assert.Equal(plan.SessionCount, plan.Sessions.Select(s => s.Id).Distinct().Count());
        Assert.True(plan.Weeks[3].Recovery && plan.Weeks[7].Recovery);
    }

    [Fact]
    public void Sessions_have_the_duration_in_their_name_and_valid_blocks()
    {
        foreach (var s in TrainingPlan.Endurance().Sessions)
        {
            Assert.Equal(PowerUnit.PercentFtp, s.Unit);
            Assert.StartsWith("plan-endurance-s", s.Id);
            Assert.True(TrainingPlan.IsPlanSession(s.Id));
            Assert.All(s.Blocks, b => Assert.InRange(b.DurationS, WorkoutBlock.MinDurationS, WorkoutBlock.MaxDurationS));
            Assert.All(s.Blocks, b => Assert.InRange(b.Power, 40, 90));
            // Durée annoncée = durée réelle, à la minute près, pour les séances continues et longues.
            var m = System.Text.RegularExpressions.Regex.Match(s.Name, @"(\d+) h(?: (\d+))?|(\d+) min$");
            if (m.Success && !s.Name.Contains('×'))
            {
                int minutes = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 60 + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0)
                                                  : int.Parse(m.Groups[3].Value);
                Assert.Equal(minutes * 60, s.TotalDurationS);
            }
        }
    }

    [Fact]
    public void Plan_is_mostly_endurance()
    {
        var plan = TrainingPlan.Endurance();
        double total = 0, z2 = 0;
        foreach (var s in plan.Sessions)
            foreach (var b in s.Blocks)
            {
                total += b.DurationS;
                if (b.Kind == BlockPower.Ramp || b.Power <= 75) z2 += b.DurationS;
            }
        Assert.True(z2 / total > 0.8, $"zone 2 : {z2 / total:P0}");
        // La sortie longue s'allonge jusqu'à 2 h.
        Assert.Equal(7200, plan.Weeks[6].Sessions[2].TotalDurationS);
        Assert.True(plan.Weeks[0].Sessions[2].TotalDurationS < plan.Weeks[2].Sessions[2].TotalDurationS);
    }

    static RideInfo RideOf(Workout w, DateTimeOffset at, double ratio) => new()
    {
        Id = Ride.NewId(at), StartedAt = at, Mode = RideMode.Workout, WorkoutName = w.Name, WorkoutId = w.Id,
        Summary = new RideSummary { DurationS = w.TotalDurationS * ratio },
    };

    [Fact]
    public void Done_sessions_come_from_rides_since_the_start_of_the_plan()
    {
        var plan = TrainingPlan.Endurance();
        var s = plan.Sessions.ToList();
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var rides = new List<RideInfo>
        {
            RideOf(s[0], start.AddDays(1), 1.0),
            RideOf(s[1], start.AddDays(2), 0.5),   // arrêtée trop tôt
            RideOf(s[2], start.AddDays(-3), 1.0),  // avant le début du plan
            RideOf(s[3], start.AddDays(4), 0.85),
        };
        Assert.Empty(plan.DoneSessions(rides, null));
        var done = plan.DoneSessions(rides, start);
        Assert.Equal(new HashSet<string> { s[0].Id, s[3].Id }, done);
        var next = plan.NextSession(done);
        Assert.Equal(s[1].Id, next!.Id);
        Assert.Equal(SessionState.Done, plan.StateOf(s[0], done, next));
        Assert.Equal(SessionState.Next, plan.StateOf(s[1], done, next));
        Assert.Equal(SessionState.Todo, plan.StateOf(s[2], done, next));
        Assert.Null(plan.NextSession(plan.Sessions.Select(x => x.Id).ToHashSet()));
    }

    [Fact]
    public void Ride_and_profile_keep_plan_fields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "velom3d-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RideStore(dir);
            var profile = store.LoadProfile();
            var at = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
            profile.PlanStartedAt = at;
            store.SaveProfile(profile);
            Assert.Equal(at, store.LoadProfile().PlanStartedAt);

            var session = TrainingPlan.Endurance().Sessions.First();
            var rec = new RideRecorder(RideMode.Workout, profile, at, 130, session.Name, session.Id);
            Assert.Equal(session.Id, rec.Ride.Info.WorkoutId);
            var free = new RideRecorder(RideMode.Simulation, profile, at, 0, session.Name, session.Id);
            Assert.Equal("", free.Ride.Info.WorkoutId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
