using Godot;

/// <summary>
/// A single model on the table. Placeholder visuals: a base, a capsule body and a
/// selection ring. Moves along a path while hugging the terrain.
/// </summary>
public partial class Unit : Node3D
{
	[Signal] public delegate void MoveFinishedEventHandler(Unit unit);

	private static readonly Color[] TeamColors = { new(0.2f, 0.45f, 0.9f), new(0.85f, 0.25f, 0.2f) };

	[Export] public string DisplayName = "Trooper";
	[Export] public int Team = 0;
	[Export] public float MoveRange = 6.0f;      // inches per turn
	[Export] public float BaseRadius = 0.63f;    // 32mm base
	[Export] public float MoveSpeed = 6.0f;      // animation speed, inches per second

	public Terrain Terrain;
	public Vector2I GridCell = Vector2I.Zero;
	public float MovementLeft = 0.0f;
	public bool IsMoving = false;

	private Vector3[] _path = System.Array.Empty<Vector3>();
	private int _pathIndex = 0;
	private MeshInstance3D _ring;

	public override void _Ready()
	{
		MovementLeft = MoveRange;
		BuildVisuals();
	}

	public void PlaceAtCell(Vector2I cell)
	{
		GridCell = cell;
		GlobalPosition = Terrain.GridToWorld(cell);
	}

	public void ResetMovement()
	{
		MovementLeft = MoveRange;
	}

	public void SetSelected(bool value)
	{
		_ring.Visible = value;
	}

	public void FollowPath(Vector3[] points, float cost, Vector2I destination)
	{
		if (points.Length < 2)
			return;
		MovementLeft = Mathf.Max(0.0f, MovementLeft - cost);
		GridCell = destination;
		_path = points;
		_pathIndex = 1;
		IsMoving = true;
	}

	public override void _Process(double delta)
	{
		if (!IsMoving)
			return;
		float budget = MoveSpeed * (float)delta;
		var pos = GlobalPosition;
		while (budget > 0.0f && _pathIndex < _path.Length)
		{
			var target = _path[_pathIndex];
			var flat = new Vector3(target.X - pos.X, 0.0f, target.Z - pos.Z);
			float d = flat.Length();
			if (d <= budget)
			{
				pos = target;
				budget -= d;
				_pathIndex++;
			}
			else
			{
				pos += flat / d * budget;
				budget = 0.0f;
			}
			if (d > 0.001f)
			{
				var rot = Rotation;
				rot.Y = Mathf.Atan2(flat.X, flat.Z);
				Rotation = rot;
			}
		}
		pos.Y = Terrain.GetHeight(pos.X, pos.Z);
		GlobalPosition = pos;
		if (_pathIndex >= _path.Length)
		{
			IsMoving = false;
			EmitSignal(SignalName.MoveFinished, this);
		}
	}

	private void BuildVisuals()
	{
		var teamMat = new StandardMaterial3D();
		teamMat.AlbedoColor = TeamColors[Team % TeamColors.Length];

		var baseMat = new StandardMaterial3D();
		baseMat.AlbedoColor = new Color(0.12f, 0.12f, 0.12f);

		var baseMesh = new MeshInstance3D();
		var cyl = new CylinderMesh();
		cyl.TopRadius = BaseRadius;
		cyl.BottomRadius = BaseRadius;
		cyl.Height = 0.15f;
		baseMesh.Mesh = cyl;
		baseMesh.MaterialOverride = baseMat;
		baseMesh.Position = new Vector3(0.0f, 0.075f, 0.0f);
		AddChild(baseMesh);

		var body = new MeshInstance3D();
		var cap = new CapsuleMesh();
		cap.Radius = 0.35f;
		cap.Height = 1.5f;
		body.Mesh = cap;
		body.MaterialOverride = teamMat;
		body.Position = new Vector3(0.0f, 0.15f + 0.75f, 0.0f);
		AddChild(body);

		// Small "visor" so you can see which way the model faces.
		var visor = new MeshInstance3D();
		var box = new BoxMesh();
		box.Size = new Vector3(0.3f, 0.12f, 0.15f);
		visor.Mesh = box;
		visor.MaterialOverride = baseMat;
		visor.Position = new Vector3(0.0f, 1.35f, 0.32f);
		AddChild(visor);

		_ring = new MeshInstance3D();
		var torus = new TorusMesh();
		torus.InnerRadius = BaseRadius + 0.05f;
		torus.OuterRadius = BaseRadius + 0.18f;
		_ring.Mesh = torus;
		var ringMat = new StandardMaterial3D();
		ringMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		ringMat.AlbedoColor = new Color(1.0f, 0.85f, 0.2f);
		_ring.MaterialOverride = ringMat;
		_ring.Position = new Vector3(0.0f, 0.08f, 0.0f);
		_ring.Visible = false;
		AddChild(_ring);

		// Collision body on layer 2 used only for mouse picking.
		var pick = new StaticBody3D();
		pick.CollisionLayer = 2;
		pick.CollisionMask = 0;
		pick.SetMeta("unit", this);
		var cs = new CollisionShape3D();
		var shape = new CylinderShape3D();
		shape.Radius = BaseRadius;
		shape.Height = 1.8f;
		cs.Shape = shape;
		cs.Position = new Vector3(0.0f, 0.9f, 0.0f);
		pick.AddChild(cs);
		AddChild(pick);
	}
}
