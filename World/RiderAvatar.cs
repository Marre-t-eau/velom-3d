using Godot;

namespace Velom3d.World;

/// <summary>
/// Cycliste et vélo en formes simples (cylindres, boules, tores). Les jambes suivent les pédales
/// (cinématique inverse hanche → genou → pied), le pédalier tourne à la cadence mesurée et les
/// roues à la vitesse. Repère local : avant vers -Z, sol à y = 0, mètres.
/// </summary>
public partial class RiderAvatar : Node3D
{
    const float WheelRadius = 0.34f;
    const float CrankLength = 0.17f;
    const float Thigh = 0.46f, Shin = 0.45f, UpperArm = 0.30f, Forearm = 0.30f;

    static readonly Vector3 RearAxle = new(0, WheelRadius, 0.50f);
    static readonly Vector3 FrontAxle = new(0, WheelRadius, -0.52f);
    static readonly Vector3 BottomBracket = new(0, 0.30f, 0.04f);
    static readonly Vector3 SeatTop = new(0, 0.82f, 0.17f);
    static readonly Vector3 HeadTop = new(0, 0.86f, -0.40f);
    static readonly Vector3 HeadBottom = new(0, 0.70f, -0.44f);
    static readonly Vector3 Hips = new(0, 0.95f, 0.20f);
    static readonly Vector3 Shoulders = new(0, 1.36f, -0.24f);
    static readonly Vector3 Bars = new(0, 0.92f, -0.50f);

    public static readonly Color Jersey = new(0.20f, 0.72f, 0.42f);
    static readonly Color Shorts = new(0.08f, 0.08f, 0.10f);
    static readonly Color Skin = new(0.87f, 0.68f, 0.55f);
    static readonly Color Helmet = new(0.95f, 0.95f, 0.97f);
    static readonly Color Frame = new(0.85f, 0.18f, 0.20f);
    static readonly Color Tyre = new(0.07f, 0.07f, 0.08f);
    static readonly Color Metal = new(0.55f, 0.57f, 0.60f);

    Node3D _rearWheel = null!, _frontWheel = null!, _crank = null!;
    readonly MeshInstance3D[] _thighs = new MeshInstance3D[2], _shins = new MeshInstance3D[2];
    readonly MeshInstance3D[] _feet = new MeshInstance3D[2];

    /// <summary>Angle du pédalier (rad) et des roues (rad).</summary>
    public float CrankAngle { get; private set; }
    public float WheelAngle { get; private set; }

    public override void _Ready()
    {
        // Vélo.
        _rearWheel = Wheel(RearAxle);
        _frontWheel = Wheel(FrontAxle);
        Tube(BottomBracket, SeatTop, 0.025f, Frame);
        Tube(BottomBracket, HeadBottom, 0.03f, Frame);
        Tube(SeatTop + new Vector3(0, -0.04f, 0), HeadTop, 0.025f, Frame);
        Tube(HeadBottom, HeadTop, 0.03f, Frame);
        Tube(BottomBracket, RearAxle, 0.018f, Frame);
        Tube(SeatTop + new Vector3(0, -0.06f, 0), RearAxle, 0.016f, Frame);
        Tube(HeadBottom, FrontAxle, 0.02f, Frame);
        Tube(HeadTop, Bars, 0.02f, Metal);
        Tube(Bars + new Vector3(-0.21f, 0, 0), Bars + new Vector3(0.21f, 0, 0), 0.016f, Metal);
        Tube(SeatTop, SeatTop + new Vector3(0, 0.06f, 0.02f), 0.015f, Metal);
        Box(SeatTop + new Vector3(0, 0.07f, 0.0f), new Vector3(0.12f, 0.03f, 0.26f), Shorts);

        _crank = new Node3D { Position = BottomBracket };
        AddChild(_crank);
        var chainring = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.10f, BottomRadius = 0.10f, Height = 0.01f, RadialSegments = 12, Rings = 1 },
            MaterialOverride = Mat(Metal),
            Position = new Vector3(0.06f, 0, 0),
            RotationDegrees = new Vector3(0, 0, 90),
        };
        _crank.AddChild(chainring);
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? 0.07f : -0.07f;
            float sign = side == 0 ? 1 : -1;
            Tube(new Vector3(x, 0, 0), new Vector3(x, 0, -CrankLength * sign), 0.012f, Metal, _crank);
        }

        // Cycliste : buste penché, tête casquée, bras vers le cintre.
        Tube(Hips, Shoulders, 0.14f, Jersey);
        Ball(Hips, new Vector3(0.17f, 0.12f, 0.14f), Shorts);
        Ball(Shoulders + new Vector3(0, 0.02f, 0), new Vector3(0.21f, 0.09f, 0.11f), Jersey);
        Tube(Shoulders, Shoulders + new Vector3(0, 0.1f, -0.06f), 0.05f, Skin);
        var head = Shoulders + new Vector3(0, 0.20f, -0.12f);
        Ball(head, new Vector3(0.10f, 0.11f, 0.11f), Skin);
        Ball(head + new Vector3(0, 0.04f, 0.01f), new Vector3(0.12f, 0.09f, 0.15f), Helmet);
        foreach (float x in new[] { -0.19f, 0.19f })
        {
            var shoulder = Shoulders + new Vector3(x, 0, 0);
            var hand = Bars + new Vector3(x * 1.05f, 0.01f, 0);
            var elbow = Joint(shoulder, hand, UpperArm, Forearm, new Vector3(Mathf.Sign(x) * 0.6f, -0.5f, 0.6f));
            Tube(shoulder, elbow, 0.045f, Jersey);
            Tube(elbow, hand, 0.038f, Skin);
            Ball(hand, new Vector3(0.04f, 0.04f, 0.05f), Shorts);
        }

        for (int side = 0; side < 2; side++)
        {
            _thighs[side] = Tube(Vector3.Zero, Vector3.Up, 0.07f, Shorts);
            _shins[side] = Tube(Vector3.Zero, Vector3.Up, 0.05f, Skin);
            _feet[side] = Box(Vector3.Zero, new Vector3(0.09f, 0.07f, 0.24f), Shorts);
        }
        Pose();
    }

    /// <summary>Fait tourner pédalier et roues.</summary>
    public void Animate(double delta, double cadenceRpm, double speedMps)
    {
        CrankAngle = Mathf.Wrap(CrankAngle + (float)(cadenceRpm / 60.0 * Mathf.Tau * delta), 0f, Mathf.Tau);
        WheelAngle = Mathf.Wrap(WheelAngle + (float)(speedMps / WheelRadius * delta), 0f, Mathf.Tau);
        Pose();
    }

    void Pose()
    {
        _crank.Rotation = new Vector3(-CrankAngle, 0, 0);
        _rearWheel.Rotation = new Vector3(-WheelAngle, 0, 0);
        _frontWheel.Rotation = new Vector3(-WheelAngle, 0, 0);
        for (int side = 0; side < 2; side++)
        {
            // Pédale droite (côté +X) et gauche, en opposition.
            float a = CrankAngle + (side == 0 ? 0f : Mathf.Pi);
            float x = side == 0 ? 0.11f : -0.11f;
            var pedal = BottomBracket + new Vector3(x, -Mathf.Sin(a) * CrankLength, -Mathf.Cos(a) * CrankLength);
            var hip = Hips + new Vector3(x, 0, 0);
            var ankle = pedal + new Vector3(0, 0.07f, 0.03f);
            var knee = Joint(hip, ankle, Thigh, Shin, new Vector3(0, 0.3f, -1f));
            Place(_thighs[side], hip, knee);
            Place(_shins[side], knee, ankle);
            _feet[side].Position = pedal + new Vector3(0, 0.03f, -0.03f);
        }
    }

    /// <summary>Articulation intermédiaire (genou, coude) de deux segments de longueurs données,
    /// pliée du côté de <paramref name="pole"/>.</summary>
    public static Vector3 Joint(Vector3 from, Vector3 to, float l1, float l2, Vector3 pole)
    {
        var d = to - from;
        float len = Mathf.Clamp(d.Length(), Mathf.Abs(l1 - l2) + 0.001f, l1 + l2 - 0.001f);
        var dir = d.Normalized();
        float along = (l1 * l1 - l2 * l2 + len * len) / (2f * len);
        float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - along * along));
        var bend = (pole - dir * pole.Dot(dir)).Normalized();
        return from + dir * along + bend * h;
    }

    /// <summary>Oriente un cylindre de hauteur 1 entre deux points.</summary>
    static void Place(MeshInstance3D tube, Vector3 a, Vector3 b)
    {
        var y = b - a;
        var axis = y.Normalized();
        var x = axis.Cross(Mathf.Abs(axis.X) < 0.9f ? Vector3.Right : Vector3.Forward).Normalized();
        var z = x.Cross(axis);
        tube.Transform = new Transform3D(new Basis(x, y, z), (a + b) / 2f);
    }

    Node3D Wheel(Vector3 axle)
    {
        var pivot = new Node3D { Position = axle };
        AddChild(pivot);
        var tyre = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = WheelRadius - 0.035f, OuterRadius = WheelRadius, Rings = 20, RingSegments = 6 },
            MaterialOverride = Mat(Tyre),
            RotationDegrees = new Vector3(0, 0, 90),
        };
        pivot.AddChild(tyre);
        // Rayons : deux barres croisées, pour voir la roue tourner.
        foreach (float angle in new[] { 0f, 60f, 120f })
        {
            var spoke = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.01f, (WheelRadius - 0.03f) * 2f, 0.025f) },
                MaterialOverride = Mat(Metal),
                RotationDegrees = new Vector3(angle, 0, 0),
            };
            pivot.AddChild(spoke);
        }
        return pivot;
    }

    MeshInstance3D Tube(Vector3 a, Vector3 b, float radius, Color color, Node3D? parent = null)
    {
        var tube = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 1f, RadialSegments = 8, Rings = 1 },
            MaterialOverride = Mat(color),
        };
        (parent ?? this).AddChild(tube);
        Place(tube, a, b);
        return tube;
    }

    MeshInstance3D Ball(Vector3 center, Vector3 radii, Color color)
    {
        var ball = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 10, Rings = 6 },
            MaterialOverride = Mat(color),
            Position = center,
            Scale = radii,
        };
        AddChild(ball);
        return ball;
    }

    MeshInstance3D Box(Vector3 center, Vector3 size, Color color)
    {
        var box = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Mat(color), Position = center };
        AddChild(box);
        return box;
    }

    static readonly System.Collections.Generic.Dictionary<Color, StandardMaterial3D> Materials = new();

    static StandardMaterial3D Mat(Color color)
    {
        if (!Materials.TryGetValue(color, out var m))
            Materials[color] = m = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.8f };
        return m;
    }
}
