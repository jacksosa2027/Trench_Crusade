using System.Runtime.InteropServices;
using Godot;

/// <summary>
/// Heightmap battlefield. Scale convention: 1 world unit = 1 inch (tabletop units).
///
/// The terrain is a regular grid of height samples ("grid points"). The same grid is
/// used for rendering, collision, and the geodesic pathfinder, so what you see is
/// exactly what movement is measured over.
/// </summary>
public partial class Terrain : StaticBody3D
{
	public const float Unreachable = 1.0e4f;

	[Export] public Vector2 BoardSize = new(100.0f, 100.0f);   // 60" x 44" = standard Strike Force board
	[Export] public float CellSize = 0.5f;                      // spacing between grid points, in inches
	[Export] public float HeightScale = 5.0f;                   // max hill height, in inches
	[Export] public float NoiseFrequency = 0.045f;
	[Export] public int NoiseSeed = 0;                          // 0 = random each run
	[Export] public bool UseTerraces = true;                    // plateaus + steep risers (some impassable)
	[Export] public float TerraceHeight = 1.5f;

	public Vector2I GridSize { get; private set; }             // number of grid points in x / z
	public Vector3 Origin { get; private set; }                // world position of grid point (0, 0)
	public float[] Heights = System.Array.Empty<float>();

	private Vector3[] _normals = System.Array.Empty<Vector3>();
	private ShaderMaterial _material;
	private ImageTexture _rangeTexture;

	private const string ShaderCode = """
		shader_type spatial;

		uniform sampler2D range_tex : filter_linear, repeat_disable;
		uniform bool show_range = false;
		uniform float max_range = 6.0;
		uniform float height_scale = 5.0;
		uniform vec4 range_color : source_color = vec4(0.25, 0.65, 1.0, 1.0);

		varying vec3 world_pos;

		void vertex() {
			world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		}

		void fragment() {
			float t = clamp(world_pos.y / height_scale, 0.0, 1.0);
			vec3 col = mix(vec3(0.30, 0.34, 0.20), vec3(0.55, 0.49, 0.40), t);

			// 1-inch grid.
			vec2 gw = max(fwidth(world_pos.xz), vec2(1e-4));
			vec2 g = abs(fract(world_pos.xz + 0.5) - 0.5) / gw;
			float grid = 1.0 - clamp(min(g.x, g.y), 0.0, 1.0);
			col = mix(col, col * 0.75, grid * 0.35);

			// Contour lines every 0.5" of elevation, so height is readable from above.
			float hy = world_pos.y / 0.5;
			float c = abs(fract(hy + 0.5) - 0.5) / max(fwidth(hy), 1e-4);
			float contour = 1.0 - clamp(c, 0.0, 1.0);
			col = mix(col, col * 0.55, contour * 0.5);

			// Movement range overlay (distance field written by the pathfinder).
			if (show_range) {
				float d = texture(range_tex, UV).r;
				if (d <= max_range) {
					col = mix(col, range_color.rgb, 0.35);
					float rim = smoothstep(max_range - 0.3, max_range, d);
					col = mix(col, range_color.rgb, rim * 0.7);
				}
			}

			ALBEDO = col;
			ROUGHNESS = 0.95;
		}
		""";

	public override void _Ready()
	{
		Generate();
	}

	public void Generate()
	{
		GridSize = new Vector2I(
			Mathf.RoundToInt(BoardSize.X / CellSize) + 1,
			Mathf.RoundToInt(BoardSize.Y / CellSize) + 1);
		Origin = new Vector3(-BoardSize.X * 0.5f, 0.0f, -BoardSize.Y * 0.5f);
		GenerateHeights();
		var mesh = BuildMesh();
		BuildCollision(mesh);
	}

	// --- Grid helpers ------------------------------------------------------------

	public bool InBounds(Vector2I c)
	{
		return c.X >= 0 && c.Y >= 0 && c.X < GridSize.X && c.Y < GridSize.Y;
	}

	public int CellIndex(Vector2I c)
	{
		return c.Y * GridSize.X + c.X;
	}

	public float HeightAt(int i, int j)
	{
		return Heights[j * GridSize.X + i];
	}

	public Vector3 GridToWorld(Vector2I c)
	{
		return new Vector3(Origin.X + c.X * CellSize, HeightAt(c.X, c.Y), Origin.Z + c.Y * CellSize);
	}

	public Vector2I WorldToGrid(Vector3 p)
	{
		return new Vector2I(
			Mathf.Clamp(Mathf.RoundToInt((p.X - Origin.X) / CellSize), 0, GridSize.X - 1),
			Mathf.Clamp(Mathf.RoundToInt((p.Z - Origin.Z) / CellSize), 0, GridSize.Y - 1));
	}

	public float SlopeDeg(Vector2I c)
	{
		return Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(_normals[CellIndex(c)].Y, -1.0f, 1.0f)));
	}

	/// <summary>
	/// Exact surface height at any world x/z. Matches the mesh triangulation exactly,
	/// so units and path previews sit precisely on the rendered ground.
	/// </summary>
	public float GetHeight(float x, float z)
	{
		float gx = Mathf.Clamp((x - Origin.X) / CellSize, 0.0f, GridSize.X - 1.0001f);
		float gz = Mathf.Clamp((z - Origin.Z) / CellSize, 0.0f, GridSize.Y - 1.0001f);
		int i = (int)gx;
		int j = (int)gz;
		float fx = gx - i;
		float fz = gz - j;
		float h00 = HeightAt(i, j);
		float h10 = HeightAt(i + 1, j);
		float h01 = HeightAt(i, j + 1);
		float h11 = HeightAt(i + 1, j + 1);
		if (fx > fz)   // triangle (00, 10, 11)
			return h00 + (h10 - h00) * fx + (h11 - h10) * fz;
		else           // triangle (00, 11, 01)
			return h00 + (h11 - h01) * fx + (h01 - h00) * fz;
	}

	// --- Range overlay -----------------------------------------------------------

	/// <summary>dist: one float per grid point (inches travelled), Unreachable where unreachable.</summary>
	public void ShowRange(float[] dist, float maxRange)
	{
		byte[] bytes = MemoryMarshal.AsBytes<float>(dist).ToArray();
		var img = Image.CreateFromData(GridSize.X, GridSize.Y, false, Image.Format.Rf, bytes);
		if (_rangeTexture == null)
			_rangeTexture = ImageTexture.CreateFromImage(img);
		else
			_rangeTexture.Update(img);
		_material.SetShaderParameter("range_tex", _rangeTexture);
		_material.SetShaderParameter("max_range", maxRange);
		_material.SetShaderParameter("show_range", true);
	}

	public void HideRange()
	{
		_material.SetShaderParameter("show_range", false);
	}

	// --- Generation --------------------------------------------------------------

	private void GenerateHeights()
	{
		var noise = new FastNoiseLite();
		noise.Seed = NoiseSeed != 0 ? NoiseSeed : (int)GD.Randi();
		noise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
		noise.Frequency = NoiseFrequency;
		noise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
		noise.FractalOctaves = 4;

		Heights = new float[GridSize.X * GridSize.Y];
		for (int j = 0; j < GridSize.Y; j++)
		{
			for (int i = 0; i < GridSize.X; i++)
			{
				float x = Origin.X + i * CellSize;
				float z = Origin.Z + j * CellSize;
				float n = noise.GetNoise2D(x, z) * 0.5f + 0.5f;
				float h = Mathf.SmoothStep(0.4f, 0.85f, n) * HeightScale;   // flat lowlands, distinct hills
				if (UseTerraces)
				{
					float s = h / TerraceHeight;
					h = TerraceHeight * (Mathf.Floor(s) + Mathf.SmoothStep(0.65f, 1.0f, s - Mathf.Floor(s)));
				}
				Heights[j * GridSize.X + i] = h;
			}
		}
	}

	private ArrayMesh BuildMesh()
	{
		int w = GridSize.X;
		int h = GridSize.Y;
		var verts = new Vector3[w * h];
		var uvs = new Vector2[w * h];
		var indices = new int[(w - 1) * (h - 1) * 6];
		_normals = new Vector3[w * h];

		for (int j = 0; j < h; j++)
		{
			for (int i = 0; i < w; i++)
			{
				int idx = j * w + i;
				verts[idx] = GridToWorld(new Vector2I(i, j));
				// UVs hit texel centres so the range texture lines up with grid points.
				uvs[idx] = new Vector2((i + 0.5f) / w, (j + 0.5f) / h);
				float hl = HeightAt(Mathf.Max(i - 1, 0), j);
				float hr = HeightAt(Mathf.Min(i + 1, w - 1), j);
				float hd = HeightAt(i, Mathf.Max(j - 1, 0));
				float hu = HeightAt(i, Mathf.Min(j + 1, h - 1));
				_normals[idx] = new Vector3(hl - hr, 2.0f * CellSize, hd - hu).Normalized();
			}
		}

		// Godot uses clockwise winding for front faces (seen from above).
		int k = 0;
		for (int j = 0; j < h - 1; j++)
		{
			for (int i = 0; i < w - 1; i++)
			{
				int a = j * w + i;   // 00
				int b = a + 1;       // 10
				int c = a + w;       // 01
				int d = c + 1;       // 11
				indices[k++] = a; indices[k++] = b; indices[k++] = d;
				indices[k++] = a; indices[k++] = d; indices[k++] = c;
			}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = verts;
		arrays[(int)Mesh.ArrayType.Normal] = _normals;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.Index] = indices;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

		var shader = new Shader();
		shader.Code = ShaderCode;
		_material = new ShaderMaterial();
		_material.Shader = shader;
		_material.SetShaderParameter("height_scale", HeightScale);

		var mi = new MeshInstance3D();
		mi.Mesh = mesh;
		mi.MaterialOverride = _material;
		AddChild(mi);
		return mesh;
	}

	private void BuildCollision(ArrayMesh mesh)
	{
		var shape = new ConcavePolygonShape3D();
		shape.SetFaces(mesh.GetFaces());
		shape.BackfaceCollision = true;
		var cs = new CollisionShape3D();
		cs.Shape = shape;
		AddChild(cs);
		CollisionLayer = 1;
		CollisionMask = 0;
	}
}
