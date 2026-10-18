using System.Collections.Generic;
using Godot;

/// <summary>
/// A single model on the table. Placeholder visuals: a base, a capsule body and a
/// selection ring. Moves along a path while hugging the terrain.
/// </summary>
public partial class Unit : Node3D
{
	[Signal] public delegate void MoveFinishedEventHandler(Unit unit);

	private static readonly Color[] TeamColors = { new(0.2f, 0.45f, 0.9f), new(0.85f, 0.25f, 0.2f) };
	private static readonly string[] TeamModels = {"res://models/paladin.glb", "res://models/lord_of_tumors.glb"};
	
	[Export] public string DisplayName = "Trooper";
	[Export] public int Team = 0;
	[Export] public float MoveRange = 6.0f;      // meters per turn
	[Export] public float BaseRadius = 0.63f;    // meters
	[Export] public float MoveSpeed = 10.0f;      // animation speed, meters per second

	public enum ActionType {Move, Charge, Retreat, Dash, Shoot, Fight, Other};
	public bool HasActivated;
	public HashSet<ActionType> ActionRecord = new();
	public Terrain Terrain;
	public Vector2I GridCell = Vector2I.Zero;
	public float MovementLeft = 0.0f;
	public bool IsMoving = false;

	private Vector3[] _path = System.Array.Empty<Vector3>();
	private int _pathIndex = 0;
	private MeshInstance3D _ring;

	/// <summary>
	/// Resets the unit's movement to its max range and builds visuals.
	/// </summary>
	public override void _Ready()
	{
		MovementLeft = MoveRange;
		BuildVisuals();
	}

	/// <summary>
	/// Places the unit at the given cell.
	/// </summary>
	public void PlaceAtCell(Vector2I cell)
	{
		GridCell = cell;
		GlobalPosition = Terrain.GridToWorld(cell);
	}

	/// <summary>
	/// Resets the unit's movement to its max range.
	/// </summary>
	public void ResetMovement()
	{
		MovementLeft = MoveRange;
	}

	/// <summary>
	/// Resets the activation flag and action record.
	/// </summary>
	public void ResetActivation()
	{
		HasActivated = false;
		ActionRecord.Clear();
	}

	/// <summary>
	/// Returns whether or not a unit has used an action.
	/// </summary>
	public bool HasUsed(ActionType a)
	{
		if (ActionRecord.Contains(a))
		{
			return true;
		}
		return false;
	}

	/// <summary>
	/// Returns whether or not unit has used a movement action.
	/// </summary>
	public bool HasUsedMovementAction()
	{
		if (ActionRecord.Contains(ActionType.Move) || ActionRecord.Contains(ActionType.Charge) || ActionRecord.Contains(ActionType.Retreat))
		{
			return true;
		}
		return false;
	}

	/// <summary>
	/// Sets the unit's ring mesh to be visible.
	/// </summary>
	public void SetSelected(bool value)
	{
		_ring.Visible = value;
	}

	/// <summary>
	/// Sets the necessary states so unit can follow path to destination.
	/// </summary>
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

	/// <summary>
	/// Iteratively calculates the path to reach the destination.
	/// </summary>
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

	/// <summary>
	/// Builds Visuals of the unit.
	/// </summary>
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
		baseMesh.MaterialOverride = teamMat;
		baseMesh.Position = new Vector3(0.0f, 0.075f, 0.0f);
		AddChild(baseMesh);

		//new model code:
		var scene = GD.Load<PackedScene>(TeamModels[Team % TeamModels.Length]);
		var model = scene.Instantiate<Node3D>();
		model.Scale = Vector3.One * 0.66f; //CHANGE THE SCALE LATER
		model.Position = new Vector3(0.0f, 0.15f, 0.0f); //stand on top of the base
		AddChild(model);

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
		shape.Height = 1.95f;
		cs.Shape = shape;
		cs.Position = new Vector3(0.0f, 0.975f, 0.0f);
		pick.AddChild(cs);
		AddChild(pick);
	}
}
