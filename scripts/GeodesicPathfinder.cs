using System.Collections.Generic;
using Godot;

/// <summary>An obstacle for the pathfinder: another model's base.</summary>
public readonly record struct Obstacle(Vector3 Position, float Radius);

/// <summary>
/// Computes surface (geodesic) distances across the terrain with Dijkstra's algorithm.
///
/// Every edge cost is the true 3D length of the ground between two grid points,
/// sampled along the way, so climbing a hill costs more than crossing flat ground.
/// Steps steeper than MaxSlopeDeg are impassable (cliffs).
///
/// A 16-neighbour stencil (8 king moves + 8 knight moves) is used. That keeps the
/// grid's direction bias to about 2-3% vs. true Euclidean distance (8-neighbour
/// grids are off by up to ~8%).
/// </summary>
public class GeodesicPathfinder
{
	private static readonly Vector2I[] Steps =
	{
		new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
		new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
		new(2, 1), new(2, -1), new(-2, 1), new(-2, -1),
		new(1, 2), new(1, -2), new(-1, 2), new(-1, -2),
	};
	private const int Subsamples = 3;   // height samples per grid spacing when measuring an edge

	public readonly Terrain Terrain;
	public readonly float MaxSlopeDeg;
	private readonly float _maxRise;    // tan(max slope): max vertical rise per unit horizontal

	public GeodesicPathfinder(Terrain terrain, float slopeLimitDeg = 50.0f)
	{
		Terrain = terrain;
		MaxSlopeDeg = slopeLimitDeg;
		_maxRise = Mathf.Tan(Mathf.DegToRad(slopeLimitDeg));
	}

	/// <param name="obstacles">Other models' bases.</param>
	/// <param name="moverRadius">Base radius of the moving model, so bases never overlap.</param>
	public DistanceField Compute(Vector2I start, float maxCost, IReadOnlyList<Obstacle> obstacles = null, float moverRadius = 0.0f)
	{
		int w = Terrain.GridSize.X;
		int h = Terrain.GridSize.Y;
		int n = w * h;
		var dist = new float[n];
		System.Array.Fill(dist, Terrain.Unreachable);
		var parent = new int[n];
		System.Array.Fill(parent, -1);
		var blocked = BuildBlockedMask(obstacles ?? System.Array.Empty<Obstacle>(), moverRadius);

		int s = start.Y * w + start.X;
		dist[s] = 0.0f;
		var heap = new PriorityQueue<int, float>();
		heap.Enqueue(s, 0.0f);

		while (heap.TryDequeue(out int idx, out float cost))
		{
			if (cost > dist[idx])
				continue;   // stale heap entry
			int i = idx % w;
			int j = idx / w;
			foreach (var step in Steps)
			{
				int ni = i + step.X;
				int nj = j + step.Y;
				if (ni < 0 || nj < 0 || ni >= w || nj >= h)
					continue;
				int nidx = nj * w + ni;
				if (blocked[nidx])
					continue;
				float edge = EdgeCost(new Vector2I(i, j), step);
				if (edge < 0.0f)
					continue;   // too steep
				float nc = cost + edge;
				if (nc > maxCost || nc >= dist[nidx])
					continue;
				dist[nidx] = nc;
				parent[nidx] = idx;
				heap.Enqueue(nidx, nc);
			}
		}

		return new DistanceField(Terrain, start, dist, parent, maxCost);
	}

	/// <summary>Surface length of the straight step from `from` to `from + step`, or -1 if too steep.</summary>
	private float EdgeCost(Vector2I from, Vector2I step)
	{
		var a = Terrain.GridToWorld(from);
		var b = Terrain.GridToWorld(from + step);
		int samples = Mathf.Max(Mathf.Abs(step.X), Mathf.Abs(step.Y)) * Subsamples;
		float total = 0.0f;
		var prev = a;
		for (int k = 1; k <= samples; k++)
		{
			var p = a.Lerp(b, (float)k / samples);
			p.Y = Terrain.GetHeight(p.X, p.Z);
			var seg = p - prev;
			float horiz = new Vector2(seg.X, seg.Z).Length();
			if (Mathf.Abs(seg.Y) > horiz * _maxRise)
				return -1.0f;
			total += seg.Length();
			prev = p;
		}
		return total;
	}

	private bool[] BuildBlockedMask(IReadOnlyList<Obstacle> obstacles, float moverRadius)
	{
		var mask = new bool[Terrain.GridSize.X * Terrain.GridSize.Y];
		foreach (var o in obstacles)
		{
			float r = o.Radius + moverRadius;
			var centre = Terrain.WorldToGrid(o.Position);
			int rc = Mathf.CeilToInt(r / Terrain.CellSize);
			for (int dj = -rc; dj <= rc; dj++)
			{
				for (int di = -rc; di <= rc; di++)
				{
					var c = centre + new Vector2I(di, dj);
					if (!Terrain.InBounds(c))
						continue;
					var wp = Terrain.GridToWorld(c);
					if (new Vector2(wp.X - o.Position.X, wp.Z - o.Position.Z).Length() < r)
						mask[Terrain.CellIndex(c)] = true;
				}
			}
		}
		return mask;
	}

	// --- Result ------------------------------------------------------------------

	public class DistanceField
	{
		public readonly Terrain Terrain;
		public readonly Vector2I Start;
		public readonly float[] Dist;
		public readonly int[] Parent;
		public readonly float MaxCost;

		public DistanceField(Terrain terrain, Vector2I start, float[] dist, int[] parent, float maxCost)
		{
			Terrain = terrain;
			Start = start;
			Dist = dist;
			Parent = parent;
			MaxCost = maxCost;
		}

		public float CostTo(Vector2I cell)
		{
			if (!Terrain.InBounds(cell))
				return float.PositiveInfinity;
			return Dist[Terrain.CellIndex(cell)];
		}

		public bool IsReachable(Vector2I cell)
		{
			return CostTo(cell) <= MaxCost;
		}

		/// <summary>World-space points (grid points) from the start to `cell`, inclusive.</summary>
		public Vector3[] PathTo(Vector2I cell)
		{
			if (!IsReachable(cell))
				return System.Array.Empty<Vector3>();
			int w = Terrain.GridSize.X;
			int idx = Terrain.CellIndex(cell);
			var chain = new List<int>();
			while (idx != -1)
			{
				chain.Add(idx);
				idx = Parent[idx];
			}
			chain.Reverse();
			var output = new Vector3[chain.Count];
			for (int k = 0; k < chain.Count; k++)
				output[k] = Terrain.GridToWorld(new Vector2I(chain[k] % w, chain[k] / w));
			return output;
		}
	}
}
