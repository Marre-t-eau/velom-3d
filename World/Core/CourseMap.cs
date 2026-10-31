using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velom3d.World;

/// <summary>Élément de décor d'une map : type (<see cref="PropKind"/> en camelCase), position (m), rotation (degrés), taille.</summary>
public sealed class MapProp
{
    public string Kind { get; set; } = "";
    public double X { get; set; }
    public double Z { get; set; }
    public double Rotation { get; set; }
    public double Scale { get; set; } = 1.0;
}

/// <summary>Repère posé sur la route à une distance donnée (pour l'instant : <c>startArch</c>, l'arche de départ).</summary>
public sealed class MapLandmark
{
    public string Kind { get; set; } = "";
    public double DistanceM { get; set; }
}

/// <summary>
/// Une map, telle qu'enregistrée dans un fichier JSON (<c>Maps/*.json</c>) : la route (points x, z,
/// altitude en mètres), le décor et quelques repères. Toutes les sources donnent ce même fichier :
/// générateur procédural (<see cref="ProceduralLoop"/>), import d'une trace réelle (<see cref="GpxImport"/>)
/// ou map écrite à la main. Le format est décrit dans <c>docs/format-des-maps.md</c>.
/// </summary>
public sealed class CourseMap
{
    public const string FormatName = "velom3d-map";
    public const int CurrentVersion = 1;
    public const string StartArch = "startArch";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>D'où vient la map : <c>procedural</c>, <c>gpx</c> ou <c>handmade</c>.</summary>
    public string Source { get; set; } = "handmade";
    /// <summary>La route revient à son départ (on enchaîne les tours).</summary>
    public bool Loop { get; set; }
    /// <summary>Courbe lisse passant par les points (pratique pour une map faite à la main, avec peu de points).</summary>
    public bool Smooth { get; set; }
    public double RoadWidthM { get; set; } = 7.0;
    /// <summary>Points de la route, dans l'ordre : [x, z] ou [x, z, altitude], en mètres.</summary>
    public List<double[]> Road { get; set; } = new();
    public List<MapProp> Props { get; set; } = new();
    public List<MapLandmark> Landmarks { get; set; } = new();

    public Course BuildCourse()
    {
        var points = Road.Select(p => new RoadPoint(p[0], p[1], p.Length > 2 ? p[2] : 0.0)).ToList();
        return new Course(points, Loop, Smooth);
    }

    /// <summary>Décor prêt à dessiner ; les types inconnus (map d'une version plus récente) sont ignorés.</summary>
    public List<Prop> BuildProps(Course course)
    {
        var props = new List<Prop>(Props.Count);
        foreach (var p in Props)
            if (KindFromName(p.Kind) is { } kind)
                props.Add(new Prop(kind, p.X, p.Z, p.Rotation * Math.PI / 180.0, p.Scale, course.Project(p.X, p.Z)));
        return props;
    }

    public static string KindName(PropKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    public static PropKind? KindFromName(string name) =>
        Enum.TryParse<PropKind>(name, ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? kind : null;

    /// <summary>Lit une map ; refuse un fichier qui n'en est pas une ou une route inutilisable.</summary>
    public static CourseMap FromJson(string json)
    {
        var map = JsonSerializer.Deserialize(json, MapJson.Default.CourseMap)
                  ?? throw new FormatException("Fichier de map vide.");
        if (map.Format != FormatName) throw new FormatException($"Ce n'est pas une map Velom3d (format « {map.Format} »).");
        if (map.Version > CurrentVersion) throw new FormatException($"Map en version {map.Version}, trop récente pour cette appli.");
        if (map.Road.Count < 2 || map.Road.Any(p => p is null || p.Length < 2))
            throw new FormatException("La route doit avoir au moins deux points [x, z].");
        return map;
    }

    /// <summary>JSON lisible et facile à modifier à la main : un point de route ou un élément de décor par ligne.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        string S(string v) => JsonSerializer.Serialize(v, MapJson.Default.String);
        string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append("{\n");
        sb.Append($"  \"format\": {S(Format)},\n  \"version\": {Version},\n");
        sb.Append($"  \"id\": {S(Id)},\n  \"name\": {S(Name)},\n  \"description\": {S(Description)},\n  \"source\": {S(Source)},\n");
        sb.Append($"  \"loop\": {(Loop ? "true" : "false")},\n  \"smooth\": {(Smooth ? "true" : "false")},\n  \"roadWidthM\": {N(RoadWidthM)},\n");
        sb.Append("  \"road\": [\n");
        for (int i = 0; i < Road.Count; i++)
            sb.Append("    [").Append(string.Join(", ", Road[i].Select(N))).Append(i < Road.Count - 1 ? "],\n" : "]\n");
        sb.Append("  ],\n  \"props\": [\n");
        for (int i = 0; i < Props.Count; i++)
        {
            var p = Props[i];
            sb.Append($"    {{ \"kind\": {S(p.Kind)}, \"x\": {N(p.X)}, \"z\": {N(p.Z)}, \"rotation\": {N(p.Rotation)}, \"scale\": {N(p.Scale)} }}");
            sb.Append(i < Props.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ],\n  \"landmarks\": [\n");
        for (int i = 0; i < Landmarks.Count; i++)
            sb.Append($"    {{ \"kind\": {S(Landmarks[i].Kind)}, \"distanceM\": {N(Landmarks[i].DistanceM)} }}")
              .Append(i < Landmarks.Count - 1 ? ",\n" : "\n");
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    /// <summary>Décor généré le long d'une route (maps procédurales et traces importées).</summary>
    public void GenerateScenery(int seed = 7)
    {
        var course = BuildCourse();
        Props = Scenery.Generate(course, seed, RoadWidthM / 2).Select(p => new MapProp
        {
            Kind = KindName(p.Kind),
            X = p.X,
            Z = p.Z,
            Rotation = p.RotationY * 180.0 / Math.PI,
            Scale = p.Scale,
        }).ToList();
    }
}

/// <summary>Sérialisation générée à la compilation : pas de réflexion, sûr sur Android.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(CourseMap))]
[JsonSerializable(typeof(string))]
internal partial class MapJson : JsonSerializerContext;
