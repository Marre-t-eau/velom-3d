using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Velom3d.World;

/// <summary>
/// Monde 3D d'une sortie : la route et le décor d'une map (<see cref="CourseMap"/>, par défaut
/// <c>Maps/boucle-5km.json</c>), le cycliste et la caméra qui le suit, comme dans Zwift. L'écran donne à chaque image la distance
/// parcourue, la vitesse et la cadence (<see cref="Follow"/>) ; tout le reste se fait ici.
/// Le monde se dessine dans la fenêtre, derrière l'interface de l'écran (qui doit laisser voir le fond).
/// Pensé pour le téléphone : décor en quelques gros maillages instanciés par tronçon de route,
/// éclairage par sommet, une seule ombre (celle du cycliste), brouillard et rendu 3D à résolution réduite.
/// </summary>
public partial class RideWorld : Node3D
{
    /// <summary>Le cycliste roule à droite de la route.</summary>
    const float LaneOffsetM = 1.6f;
    /// <summary>Le décor est regroupé par tronçons de cette longueur, pour que seuls les proches soient dessinés.</summary>
    const double ChunkM = 500.0;
    const float ViewDistanceM = 600f;
    /// <summary>Largeur visée du rendu 3D (pixels) : au-delà, la 3D est calculée plus petite puis agrandie.</summary>
    const float TargetRenderWidth = 1280f;

    static readonly Color SkyHorizon = new(0.74f, 0.84f, 0.92f);
    static readonly Color Grass = new(0.36f, 0.55f, 0.24f);

    public const string DefaultMapPath = "res://Maps/boucle-5km.json";

    public CourseMap Map { get; }
    public Course Course { get; }
    public RiderAvatar Rider { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    /// <summary>Distance affichée (m), tours compris.</summary>
    public double DistanceM { get; private set; }
    /// <summary>Nombre d'éléments de décor posés (pour les tests).</summary>
    public int PropCount { get; private set; }

    double _speedMps, _cadenceRpm;
    bool _cameraPlaced;
    float _previousScale = 1f;

    public RideWorld() : this(LoadMap(DefaultMapPath)) { }

    public RideWorld(CourseMap map)
    {
        Map = map;
        Course = map.BuildCourse();
    }

    /// <summary>Lit une map (res:// ou user://). Si le fichier manque ou est abîmé, la boucle de 5 km est régénérée.</summary>
    public static CourseMap LoadMap(string path)
    {
        try
        {
            return CourseMap.FromJson(FileAccess.GetFileAsString(path));
        }
        catch (System.Exception e)
        {
            GD.PushWarning($"Map {path} illisible ({e.Message}) : boucle de 5 km générée à la place.");
            return ProceduralLoop.Generate("boucle-5km", "Boucle de 5 km");
        }
    }

    public override void _Ready()
    {
        var vp = GetViewport();
        _previousScale = vp.Scaling3DScale;
        float width = DisplayServer.WindowGetSize().X;
        vp.Scaling3DScale = Mathf.Clamp(TargetRenderWidth / Mathf.Max(width, 1f), 0.5f, 1f);

        BuildEnvironment();
        var material = LowPoly.Material();
        BuildGround();
        BuildRoad(material);
        foreach (var landmark in Map.Landmarks)
            if (landmark.Kind == CourseMap.StartArch) BuildStartArch(material, landmark.DistanceM);
        BuildScenery(material);

        Rider = new RiderAvatar();
        AddChild(Rider);
        Camera = new Camera3D { Fov = 62f, Near = 0.2f, Far = ViewDistanceM + 50f, Current = true };
        AddChild(Camera);
        Follow(0, 0, 0);
        PlaceRider();
    }

    public override void _ExitTree() => GetViewport().Scaling3DScale = _previousScale;

    /// <summary>Position du cycliste sur le circuit (distance de la sortie), vitesse (m/s) et cadence (tr/min).</summary>
    public void Follow(double distanceM, double speedMps, double cadenceRpm)
    {
        DistanceM = distanceM;
        _speedMps = speedMps;
        _cadenceRpm = cadenceRpm;
    }

    public override void _Process(double delta)
    {
        Rider.Animate(delta, _cadenceRpm, _speedMps);
        PlaceRider();
        PlaceCamera(delta);
    }

    void PlaceRider()
    {
        var p = Course.At(DistanceM);
        var (x, z) = p.Offset(LaneOffsetM);
        var forward = new Vector3((float)p.DirX, 0, (float)p.DirZ);
        Rider.Transform = new Transform3D(Basis.LookingAt(forward, Vector3.Up), new Vector3((float)x, 0.06f, (float)z));
    }

    /// <summary>Caméra derrière et au-dessus du cycliste, qui le rattrape en douceur dans les virages.</summary>
    void PlaceCamera(double delta)
    {
        var rider = Rider.Position;
        var here = Course.At(DistanceM);
        var target = OnRoad(DistanceM - 4.5, here, 1.8f);
        var look = OnRoad(DistanceM + 10.0, here, 0.2f);

        if (!_cameraPlaced)
        {
            Camera.Position = target;
            _cameraPlaced = true;
        }
        else
        {
            float k = 1f - Mathf.Exp(-6f * (float)delta);
            Camera.Position = Camera.Position.Lerp(target, k);
        }
        // Toujours à bonne distance, même si l'image a sauté.
        var flat = new Vector3(Camera.Position.X - rider.X, 0, Camera.Position.Z - rider.Z);
        if (flat.Length() > 7f) Camera.Position = new Vector3(rider.X, Camera.Position.Y, rider.Z) + flat.Normalized() * 7f;
        Camera.LookAt(look, Vector3.Up);
    }

    /// <summary>Point du couloir du cycliste à une distance donnée ; au-delà du départ ou de l'arrivée
    /// d'un aller simple, dans le prolongement de la route.</summary>
    Vector3 OnRoad(double distanceM, CoursePoint here, float y)
    {
        double wrapped = Course.Wrap(distanceM);
        var p = Course.At(distanceM);
        var (x, z) = p.Offset(LaneOffsetM);
        double beyond = Course.IsLoop ? 0 : distanceM - wrapped;
        if (Mathf.Abs((float)beyond) < 0.01) return new Vector3((float)x, y, (float)z);
        var (hx, hz) = here.Offset(LaneOffsetM);
        double along = distanceM - Course.Wrap(DistanceM);
        return new Vector3((float)(hx + here.DirX * along), y, (float)(hz + here.DirZ * along));
    }

    void BuildEnvironment()
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.33f, 0.55f, 0.85f),
            SkyHorizonColor = SkyHorizon,
            GroundHorizonColor = SkyHorizon,
            GroundBottomColor = Grass.Darkened(0.3f),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = sky, ProcessMode = Sky.ProcessModeEnum.Quality },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.7f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogLightColor = SkyHorizon,
            FogDensity = 1f,
            FogDepthBegin = 120f,
            FogDepthEnd = ViewDistanceM,
            FogSkyAffect = 0f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55f, 35f, 0f),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 25f,
        };
        AddChild(sun);
    }

    void BuildGround()
    {
        // Herbe sous toute la map, avec de la marge pour l'horizon.
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < Course.PointCount; i++)
        {
            var (x, z) = Course.Point(i);
            minX = Mathf.Min(minX, (float)x); maxX = Mathf.Max(maxX, (float)x);
            minZ = Mathf.Min(minZ, (float)z); maxZ = Mathf.Max(maxZ, (float)z);
        }
        float margin = 2f * ViewDistanceM;
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(maxX - minX + margin, maxZ - minZ + margin) },
            Position = new Vector3((minX + maxX) / 2f, 0, (minZ + maxZ) / 2f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Grass, Roughness = 1f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(ground);
    }

    /// <summary>Route en un seul maillage : bas-côtés, asphalte, lignes de rive et pointillés au centre.</summary>
    void BuildRoad(Material material)
    {
        var asphalt = new Color(0.28f, 0.28f, 0.29f);
        var gravel = new Color(0.55f, 0.50f, 0.42f);
        var paint = new Color(0.93f, 0.93f, 0.90f);
        float half = (float)Map.RoadWidthM / 2f;
        var mesh = new LowPoly();
        int n = Course.IsLoop ? Course.PointCount : Course.PointCount - 1;
        for (int i = 0; i < n; i++)
        {
            var a = Course.At(i * Course.StepM);
            var b = Course.At((i + 1) * Course.StepM);
            Strip(mesh, a, b, half, half + 0.8f, 0.04f, gravel);
            Strip(mesh, a, b, -half - 0.8f, -half, 0.04f, gravel);
            Strip(mesh, a, b, -half, half, 0.06f, asphalt);
            Strip(mesh, a, b, half - 0.35f, half - 0.2f, 0.08f, paint);
            Strip(mesh, a, b, -half + 0.2f, -half + 0.35f, 0.08f, paint);
        }
        // Pointillés : 4 m de trait tous les 10 m.
        for (double d = 0; d < Course.LengthM - 4; d += 10.0)
            Strip(mesh, Course.At(d), Course.At(d + 4.0), -0.07f, 0.07f, 0.08f, paint);
        AddChild(new MeshInstance3D { Mesh = mesh.Commit(material), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    static void Strip(LowPoly mesh, CoursePoint a, CoursePoint b, float from, float to, float y, Color color)
    {
        Vector3 V(CoursePoint p, float right)
        {
            var (x, z) = p.Offset(right);
            return new Vector3((float)x, y, (float)z);
        }
        mesh.Quad(V(a, from), V(a, to), V(b, to), V(b, from), Vector3.Up, color);
    }

    /// <summary>Arche de départ et ligne d'arrivée, pour voir passer les tours.</summary>
    void BuildStartArch(Material material, double distanceM)
    {
        var accent = new Color(0.22f, 0.78f, 0.47f);
        var white = new Color(0.95f, 0.95f, 0.95f);
        var dark = new Color(0.12f, 0.12f, 0.14f);
        var mesh = new LowPoly();
        float half = (float)Map.RoadWidthM / 2f;
        float w = half + 1.2f;
        mesh.Box(new Vector3(-w - 0.3f, 0, -0.3f), new Vector3(-w + 0.3f, 5.5f, 0.3f), white);
        mesh.Box(new Vector3(w - 0.3f, 0, -0.3f), new Vector3(w + 0.3f, 5.5f, 0.3f), white);
        mesh.Box(new Vector3(-w - 0.3f, 4.4f, -0.25f), new Vector3(w + 0.3f, 5.6f, 0.25f), accent, bottom: true);
        // Damier au sol.
        for (int i = 0; i < (int)(2 * half / 0.5f); i++)
            for (int j = 0; j < 2; j++)
            {
                float x0 = -half + i * 0.5f;
                mesh.Quad(new Vector3(x0, 0.085f, j * 0.5f - 0.5f), new Vector3(x0 + 0.5f, 0.085f, j * 0.5f - 0.5f),
                    new Vector3(x0 + 0.5f, 0.085f, j * 0.5f), new Vector3(x0, 0.085f, j * 0.5f), Vector3.Up, (i + j) % 2 == 0 ? white : dark);
            }
        var start = Course.At(distanceM);
        var forward = new Vector3((float)start.DirX, 0, (float)start.DirZ);
        AddChild(new MeshInstance3D
        {
            Mesh = mesh.Commit(material),
            Transform = new Transform3D(Basis.LookingAt(forward, Vector3.Up), new Vector3((float)start.X, 0, (float)start.Z)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    void BuildScenery(Material material)
    {
        var props = Map.BuildProps(Course);
        PropCount = props.Count;
        var meshes = new Dictionary<PropKind, ArrayMesh>
        {
            [PropKind.Bush] = BushMesh(material),
            [PropKind.LeafyTree] = LeafyTreeMesh(material),
            [PropKind.Conifer] = ConiferMesh(material),
            [PropKind.House] = HouseMesh(material, 4f, 3f, 2.9f),
            [PropKind.Farmhouse] = HouseMesh(material, 7f, 3.6f, 3.4f),
        };
        var rng = new RandomNumberGenerator { Seed = 11 };
        Color[] walls = [new(1f, 1f, 1f), new(1f, 0.93f, 0.80f), new(1f, 0.86f, 0.78f), new(0.92f, 0.92f, 0.95f), new(1f, 0.97f, 0.88f)];

        foreach (var group in props.GroupBy(p => (p.Kind, Chunk: (int)(p.CourseDistanceM / ChunkM))))
        {
            var list = group.ToList();
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = meshes[group.Key.Kind],
                InstanceCount = list.Count,
            };
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                float s = (float)p.Scale;
                var basis = new Basis(Vector3.Up, (float)p.RotationY).Scaled(new Vector3(s, s, s));
                multi.SetInstanceTransform(i, new Transform3D(basis, new Vector3((float)p.X, 0, (float)p.Z)));
                multi.SetInstanceColor(i, p.Kind switch
                {
                    PropKind.House or PropKind.Farmhouse => walls[rng.RandiRange(0, walls.Length - 1)],
                    // Quelques arbres aux couleurs d'automne.
                    PropKind.LeafyTree when rng.Randf() < 0.18f => new Color(1.7f, 1.05f, 0.45f),
                    _ => new Color(rng.RandfRange(0.8f, 1.1f), rng.RandfRange(0.85f, 1.1f), rng.RandfRange(0.8f, 1.05f)),
                });
            }
            AddChild(new MultiMeshInstance3D
            {
                Multimesh = multi,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = ViewDistanceM + (float)ChunkM,
            });
        }
    }

    static ArrayMesh BushMesh(Material material)
    {
        var m = new LowPoly();
        var green = new Color(0.24f, 0.48f, 0.18f);
        m.Blob(new Vector3(0, 0.45f, 0), new Vector3(0.9f, 0.6f, 0.9f), 6, 3, green, 0.15f, 3);
        m.Blob(new Vector3(0.6f, 0.35f, 0.3f), new Vector3(0.55f, 0.45f, 0.55f), 6, 3, green.Lightened(0.08f), 0.15f, 4);
        return m.Commit(material);
    }

    static ArrayMesh LeafyTreeMesh(Material material)
    {
        var m = new LowPoly();
        m.Cone(Vector3.Zero, 0.2f, 0.14f, 2.4f, 5, new Color(0.40f, 0.28f, 0.18f));
        m.Blob(new Vector3(0, 3.7f, 0), new Vector3(1.9f, 1.8f, 1.9f), 7, 4, new Color(0.30f, 0.56f, 0.22f), 0.12f, 1);
        m.Blob(new Vector3(0.7f, 4.6f, -0.3f), new Vector3(1.1f, 1.0f, 1.1f), 6, 3, new Color(0.34f, 0.60f, 0.24f), 0.12f, 2);
        return m.Commit(material);
    }

    static ArrayMesh ConiferMesh(Material material)
    {
        var m = new LowPoly();
        var green = new Color(0.14f, 0.38f, 0.20f);
        m.Cone(Vector3.Zero, 0.17f, 0.12f, 1.2f, 5, new Color(0.36f, 0.25f, 0.16f));
        m.Cone(new Vector3(0, 0.9f, 0), 1.7f, 0f, 3.0f, 7, green, caps: true);
        m.Cone(new Vector3(0, 2.4f, 0), 1.3f, 0f, 2.6f, 7, green.Lightened(0.05f), caps: true);
        m.Cone(new Vector3(0, 3.8f, 0), 0.9f, 0f, 2.3f, 7, green.Lightened(0.1f), caps: true);
        return m.Commit(material);
    }

    /// <summary>Maison : murs (teintés par instance), toit à deux pans, porte et fenêtres en façade (côté -Z).</summary>
    static ArrayMesh HouseMesh(Material material, float halfLength, float halfDepth, float wallHeight)
    {
        var m = new LowPoly();
        var wall = new Color(0.93f, 0.90f, 0.84f);
        var roof = new Color(0.66f, 0.27f, 0.20f);
        var wood = new Color(0.38f, 0.24f, 0.15f);
        var glass = new Color(0.22f, 0.32f, 0.42f);
        var min = new Vector3(-halfLength, 0, -halfDepth);
        var max = new Vector3(halfLength, wallHeight, halfDepth);
        m.Box(min, max, wall);
        m.Roof(min, max, halfDepth * 0.75f, 0.4f, roof, wall);
        float z = -halfDepth - 0.04f;
        m.Box(new Vector3(-0.5f, 0, z), new Vector3(0.5f, 2.1f, -halfDepth + 0.01f), wood);
        for (float x = 1.6f; x < halfLength - 0.8f; x += 2.4f)
            foreach (float sx in new[] { -1f, 1f })
            {
                m.Box(new Vector3(sx * x - 0.55f, 1.1f, z), new Vector3(sx * x + 0.55f, 2.1f, -halfDepth + 0.01f), glass);
                m.Box(new Vector3(sx * x - 0.55f, 1.1f, halfDepth - 0.01f), new Vector3(sx * x + 0.55f, 2.1f, halfDepth + 0.04f), glass);
            }
        // Cheminée.
        m.Box(new Vector3(halfLength * 0.5f, wallHeight, -0.3f), new Vector3(halfLength * 0.5f + 0.6f, wallHeight + halfDepth * 0.75f + 0.6f, 0.3f), wall.Darkened(0.2f));
        return m.Commit(material);
    }
}
