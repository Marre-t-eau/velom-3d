using Godot;

namespace Velom3d.World;

/// <summary>
/// Petit constructeur de maillages « low poly » à facettes, en couleurs de sommet : arbres,
/// maisons et route sont générés ici, sans fichier 3D. Une seule matière pour tout le décor.
/// </summary>
public sealed class LowPoly
{
    readonly SurfaceTool _st = new();

    public LowPoly() => _st.Begin(Mesh.PrimitiveType.Triangles);

    /// <summary>Matière du décor : couleur des sommets (× couleur de l'instance), éclairage par sommet (léger sur téléphone).</summary>
    public static StandardMaterial3D Material(bool perVertex = true) => new()
    {
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Roughness = 1.0f,
        ShadingMode = perVertex ? BaseMaterial3D.ShadingModeEnum.PerVertex : BaseMaterial3D.ShadingModeEnum.PerPixel,
    };

    public ArrayMesh Commit(Material material)
    {
        _st.SetMaterial(material);
        return _st.Commit();
    }

    /// <summary>Triangle à facette plate ; l'ordre des sommets est corrigé pour que la face regarde vers <paramref name="outward"/>.</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Color color)
    {
        var n = (b - a).Cross(c - a);
        if (n.Dot(outward) < 0) { (b, c) = (c, b); n = -n; }
        n = n.Normalized();
        _st.SetColor(color);
        _st.SetNormal(n);
        // Godot affiche les faces dont les sommets tournent dans le sens horaire.
        _st.AddVertex(a);
        _st.AddVertex(c);
        _st.AddVertex(b);
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Color color)
    {
        Triangle(a, b, c, outward, color);
        Triangle(a, c, d, outward, color);
    }

    /// <summary>Tronc de cône vertical (cône si le rayon du haut est nul).</summary>
    public void Cone(Vector3 bottom, float radiusBottom, float radiusTop, float height, int sides, Color color, bool caps = false)
    {
        var top = bottom + Vector3.Up * height;
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
            var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            var out_ = (d0 + d1).Normalized();
            var b0 = bottom + d0 * radiusBottom;
            var b1 = bottom + d1 * radiusBottom;
            if (radiusTop <= 0.001f)
                Triangle(b0, b1, top, out_ + Vector3.Up * (radiusBottom / height), color);
            else
                Quad(b0, b1, top + d1 * radiusTop, top + d0 * radiusTop, out_, color);
            if (caps)
            {
                Triangle(bottom, b0, b1, Vector3.Down, color);
                if (radiusTop > 0.001f) Triangle(top, top + d0 * radiusTop, top + d1 * radiusTop, Vector3.Up, color);
            }
        }
    }

    /// <summary>Boule à facettes (ellipsoïde), pour les houppiers et les arbustes.</summary>
    public void Blob(Vector3 center, Vector3 radii, int slices, int stacks, Color color, float jitter = 0f, int seed = 1)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        var pts = new Vector3[stacks + 1, slices];
        for (int s = 0; s <= stacks; s++)
        {
            float phi = Mathf.Pi * s / stacks;
            for (int i = 0; i < slices; i++)
            {
                float th = Mathf.Tau * (i + 0.5f * (s % 2)) / slices;
                var dir = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                float k = s == 0 || s == stacks ? 1f : 1f + rng.RandfRange(-jitter, jitter);
                pts[s, i] = center + dir * radii * k;
            }
        }
        for (int s = 0; s < stacks; s++)
            for (int i = 0; i < slices; i++)
            {
                int j = (i + 1) % slices;
                var a = pts[s, i]; var b = pts[s, j]; var c = pts[s + 1, j]; var d = pts[s + 1, i];
                var mid = (a + b + c + d) / 4f - center;
                // Teinte un peu plus sombre vers le bas : du volume sans texture.
                var shade = color.Darkened(0.18f * s / stacks);
                if (s == 0) Triangle(a, c, d, mid, shade);
                else if (s == stacks - 1) Triangle(a, b, c, mid, shade);
                else Quad(a, b, c, d, mid, shade);
            }
    }

    /// <summary>Pavé aligné sur les axes.</summary>
    public void Box(Vector3 min, Vector3 max, Color color, bool bottom = false)
    {
        Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1), Vector3.Left, color);
        Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1), Vector3.Right, color);
        Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0), Vector3.Forward, color);
        Quad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), Vector3.Back, color);
        Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1), Vector3.Up, color);
        if (bottom) Quad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), Vector3.Down, color);
    }

    /// <summary>Toit à deux pans au-dessus d'un rectangle (faîtage selon l'axe X).</summary>
    public void Roof(Vector3 min, Vector3 max, float height, float overhang, Color roof, Color gable)
    {
        float x0 = min.X - overhang, x1 = max.X + overhang, z0 = min.Z - overhang, z1 = max.Z + overhang;
        float y = max.Y, zm = (min.Z + max.Z) / 2f;
        var r0 = new Vector3(x0, y + height, zm);
        var r1 = new Vector3(x1, y + height, zm);
        Quad(new Vector3(x0, y, z0), new Vector3(x1, y, z0), r1, r0, new Vector3(0, 1, -1), roof);
        Quad(new Vector3(x0, y, z1), new Vector3(x1, y, z1), r1, r0, new Vector3(0, 1, 1), roof);
        // Pignons, dans le plan des murs.
        Triangle(new Vector3(min.X, y, min.Z), new Vector3(min.X, y, max.Z), new Vector3(min.X, y + height, zm), Vector3.Left, gable);
        Triangle(new Vector3(max.X, y, min.Z), new Vector3(max.X, y, max.Z), new Vector3(max.X, y + height, zm), Vector3.Right, gable);
    }
}
