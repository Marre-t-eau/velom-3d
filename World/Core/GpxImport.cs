using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Velom3d.World;

/// <summary>
/// Transforme une trace GPX (Strava, Komoot, Garmin, OpenStreetMap…) en map : latitude, longitude
/// et altitude deviennent des mètres autour du premier point (nord vers -z), puis le décor est
/// généré le long de la route. Une trace qui revient à moins de 30 m de son départ devient une boucle.
/// </summary>
public static class GpxImport
{
    const double EarthRadiusM = 6371000.0;
    /// <summary>Points GPS gardés au moins tous les … m : le bruit du GPS disparaît, la route est lissée.</summary>
    const double MinSpacingM = 10.0;
    const double LoopCloseM = 30.0;

    public static CourseMap ToMap(string gpxXml, string id, string? name = null, int seed = 7)
    {
        var doc = XDocument.Parse(gpxXml);
        var points = doc.Descendants().Where(e => e.Name.LocalName == "trkpt").ToList();
        if (points.Count == 0) points = doc.Descendants().Where(e => e.Name.LocalName == "rtept").ToList();
        if (points.Count < 2) throw new FormatException("La trace GPX n'a pas assez de points.");
        string? gpxName = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "name")?.Value.Trim();

        double Attr(XElement e, string n) => double.Parse(e.Attribute(n)?.Value ?? throw new FormatException($"Point GPX sans {n}."), CultureInfo.InvariantCulture);
        double lat0 = Attr(points[0], "lat") * Math.PI / 180.0, lon0 = Attr(points[0], "lon") * Math.PI / 180.0;
        double cos0 = Math.Cos(lat0);

        var map = new CourseMap
        {
            Id = id,
            Name = name ?? (string.IsNullOrEmpty(gpxName) ? id : gpxName),
            Source = "gpx",
            Smooth = true,
        };
        foreach (var e in points)
        {
            double lat = Attr(e, "lat") * Math.PI / 180.0, lon = Attr(e, "lon") * Math.PI / 180.0;
            double x = EarthRadiusM * cos0 * (lon - lon0), z = -EarthRadiusM * (lat - lat0);
            var ele = e.Elements().FirstOrDefault(c => c.Name.LocalName == "ele");
            double alt = ele is null ? 0.0 : double.Parse(ele.Value, CultureInfo.InvariantCulture);
            if (map.Road.Count > 0)
            {
                var last = map.Road[^1];
                if (Math.Sqrt((x - last[0]) * (x - last[0]) + (z - last[1]) * (z - last[1])) < MinSpacingM) continue;
            }
            map.Road.Add([x, z, alt]);
        }
        if (map.Road.Count < 2) throw new FormatException("La trace GPX est trop courte.");

        var first = map.Road[0];
        var end = map.Road[^1];
        if (map.Road.Count > 3 && Math.Sqrt((end[0] - first[0]) * (end[0] - first[0]) + (end[1] - first[1]) * (end[1] - first[1])) < LoopCloseM)
        {
            map.Loop = true;
            map.Road.RemoveAt(map.Road.Count - 1);
        }
        var course = map.BuildCourse();
        map.Description = $"Trace réelle de {course.LengthM / 1000:0.0} km{(map.Loop ? " en boucle" : "")}, importée d'un GPX.";
        map.Landmarks = [new MapLandmark { Kind = CourseMap.StartArch, DistanceM = 0 }];
        map.GenerateScenery(seed);
        return map;
    }
}
