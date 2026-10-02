using System.Collections.Generic;
using Godot;

/// <summary>
/// Entry point, attached to the root of main.tscn. Builds the world, spawns two
/// armies and handles selection, move orders and turns.
///
/// Left click: select a model (your own to move it, an enemy to see its threat range)
/// Right click: move the selected model to the hovered point (if in range)
/// Space / Enter: end turn     Esc: deselect
/// </summary>
public partial class Game : Node3D
{
	private readonly record struct UnitProfile(string Name, float Move);

	private static readonly string[] TeamNames = { "Blue", "Red" };
	private static readonly UnitProfile[] Army =
	{
		new("Trooper", 6.0f),
		new("Trooper", 6.0f),
		new("Trooper", 6.0f),
		new("Heavy", 5.0f),
		new("Scout", 8.0f),
	};

	[Export] public float MaxSlopeDeg = 50.0f;

	public Terrain Terrain;
	public RTSCamera CameraRig;
	public GeodesicPathfinder Pathfinder;
	public readonly List<Unit> Units = new();
	public Unit Selected;
	public int CurrentTeam = 0;
	public int Turn = 1;

	private GeodesicPathfinder.DistanceField _field;
	private readonly ImmediateMesh _previewMesh = new();
	private StandardMaterial3D _previewMat;
	private Label _hud;
	private string _hoverText = "";

	public override void _Ready()
	{
		GD.Randomize();
		BuildEnvironment();

		Terrain = new Terrain();
		AddChild(Terrain);
		Pathfinder = new GeodesicPathfinder(Terrain, MaxSlopeDeg);

		CameraRig = new RTSCamera();
		CameraRig.Terrain = Terrain;
		CameraRig.Bounds = new Rect2(Terrain.Origin.X, Terrain.Origin.Z, Terrain.BoardSize.X, Terrain.BoardSize.Y);
		AddChild(CameraRig);

		BuildPreview();
		BuildHud();
		SpawnArmies();
		UpdateHud();
	}

	// --- Input -------------------------------------------------------------------

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			if (mb.ButtonIndex == MouseButton.Left)
				OnLeftClick(mb.Position);
			else if (mb.ButtonIndex == MouseButton.Right)
				OnRightClick(mb.Position);
		}
		else if (@event is InputEventMouseMotion mm)
		{
			UpdateHover(mm.Position);
		}
		else if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			switch (key.Keycode)
			{
				case Key.Space:
				case Key.Enter:
					EndTurn();
					break;
				case Key.Escape:
					Select(null);
					break;
			}
		}
	}

	private void OnLeftClick(Vector2 mouse)
	{
		if (AnyMoving())
			return;
		var hit = Raycast(mouse, 2);
		if (hit.Count > 0 && hit["collider"].AsGodotObject() is GodotObject collider && collider.HasMeta("unit"))
			Select(collider.GetMeta("unit").As<Unit>());
		else
			Select(null);
	}

	private void OnRightClick(Vector2 mouse)
	{
		if (Selected == null || Selected.Team != CurrentTeam || _field == null || AnyMoving())
			return;
		var hit = Raycast(mouse, 1);
		if (hit.Count == 0)
			return;
		var cell = Terrain.WorldToGrid(hit["position"].AsVector3());
		if (cell == Selected.GridCell || !_field.IsReachable(cell))
			return;
		Selected.FollowPath(_field.PathTo(cell), _field.CostTo(cell), cell);
		_field = null;
		Terrain.HideRange();
		_previewMesh.ClearSurfaces();
		_hoverText = "";
		UpdateHud();
	}

	private void UpdateHover(Vector2 mouse)
	{
		_previewMesh.ClearSurfaces();
		_hoverText = "";
		if (Selected == null || _field == null || Selected.IsMoving)
		{
			UpdateHud();
			return;
		}
		var hit = Raycast(mouse, 1);
		if (hit.Count == 0)
		{
			UpdateHud();
			return;
		}
		var cell = Terrain.WorldToGrid(hit["position"].AsVector3());
		float straight = FlatDistance(Selected.GlobalPosition, Terrain.GridToWorld(cell));
		if (_field.IsReachable(cell) && cell != Selected.GridCell)
		{
			DrawPath(_field.PathTo(cell));
			_hoverText = $"Path: {_field.CostTo(cell):0.0}\"  (straight line {straight:0.0}\")";
		}
		else
		{
			_hoverText = $"Out of range  (straight line {straight:0.0}\")";
		}
		UpdateHud();
	}

	// --- Game flow ---------------------------------------------------------------

	private void Select(Unit u)
	{
		Selected?.SetSelected(false);
		Selected = u;
		_previewMesh.ClearSurfaces();
		_hoverText = "";
		if (Selected != null)
		{
			Selected.SetSelected(true);
			RefreshField();
		}
		else
		{
			_field = null;
			Terrain.HideRange();
		}
		UpdateHud();
	}

	private void RefreshField()
	{
		// Own models show remaining movement; enemies show their full move (threat range).
		float budget = Selected.Team == CurrentTeam ? Selected.MovementLeft : Selected.MoveRange;
		_field = Pathfinder.Compute(Selected.GridCell, budget, ObstaclesFor(Selected), Selected.BaseRadius);
		if (budget > 0.0f)
			Terrain.ShowRange(_field.Dist, budget);
		else
			Terrain.HideRange();
	}

	private void OnUnitMoveFinished(Unit u)
	{
		if (u == Selected)
			RefreshField();
		UpdateHud();
	}

	private void EndTurn()
	{
		if (AnyMoving())
			return;
		CurrentTeam = 1 - CurrentTeam;
		if (CurrentTeam == 0)
			Turn++;
		foreach (var u in Units)
		{
			if (u.Team == CurrentTeam)
				u.ResetMovement();
		}
		Select(null);
	}

	private List<Obstacle> ObstaclesFor(Unit mover)
	{
		var output = new List<Obstacle>();
		foreach (var u in Units)
		{
			if (u != mover)
				output.Add(new Obstacle(u.GlobalPosition, u.BaseRadius));
		}
		return output;
	}

	private bool AnyMoving()
	{
		foreach (var u in Units)
		{
			if (u.IsMoving)
				return true;
		}
		return false;
	}

	// --- Spawning ----------------------------------------------------------------

	private void SpawnArmies()
	{
		float halfX = Terrain.BoardSize.X * 0.5f;
		float halfZ = Terrain.BoardSize.Y * 0.5f;
		for (int team = 0; team < 2; team++)
		{
			// 9" deep deployment zones on the short edges.
			float xMin = team == 0 ? -halfX + 1.0f : halfX - 10.0f;
			float xMax = team == 0 ? -halfX + 10.0f : halfX - 1.0f;
			int placed = 0;
			int attempts = 0;
			while (placed < Army.Length && attempts < 1000)
			{
				attempts++;
				var p = new Vector3((float)GD.RandRange(xMin, xMax), 0.0f, (float)GD.RandRange(-halfZ * 0.6f, halfZ * 0.6f));
				var cell = Terrain.WorldToGrid(p);
				if (Terrain.SlopeDeg(cell) > 20.0f)
					continue;
				var wp = Terrain.GridToWorld(cell);
				if (TooClose(wp, 0.63f * 2.0f + 0.5f))
					continue;
				var profile = Army[placed];
				var u = new Unit();
				u.Team = team;
				u.DisplayName = $"{TeamNames[team]} {profile.Name} {placed + 1}";
				u.MoveRange = profile.Move;
				u.Terrain = Terrain;
				AddChild(u);
				u.PlaceAtCell(cell);
				var rot = u.Rotation;
				rot.Y = team == 0 ? Mathf.Pi * 0.5f : -Mathf.Pi * 0.5f;   // face the enemy
				u.Rotation = rot;
				u.MoveFinished += OnUnitMoveFinished;
				Units.Add(u);
				placed++;
			}
		}
	}

	private bool TooClose(Vector3 p, float minDist)
	{
		foreach (var u in Units)
		{
			if (FlatDistance(u.GlobalPosition, p) < minDist)
				return true;
		}
		return false;
	}

	// --- Visual helpers ----------------------------------------------------------

	private void DrawPath(Vector3[] path)
	{
		_previewMesh.ClearSurfaces();
		if (path.Length < 2)
			return;
		var lift = Vector3.Up * 0.1f;
		// Path line, resampled so it follows the ground between grid points.
		_previewMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, _previewMat);
		for (int k = 0; k < path.Length - 1; k++)
		{
			var a = path[k];
			var b = path[k + 1];
			int n = Mathf.Max(1, Mathf.CeilToInt(FlatDistance(a, b) / 0.2f));
			for (int s = 0; s < n; s++)
			{
				var p = a.Lerp(b, (float)s / n);
				p.Y = Terrain.GetHeight(p.X, p.Z);
				_previewMesh.SurfaceAddVertex(p + lift);
			}
		}
		var end = path[^1];
		_previewMesh.SurfaceAddVertex(end + lift);
		_previewMesh.SurfaceEnd();
		// Ghost base outline at the destination.
		_previewMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, _previewMat);
		for (int s = 0; s < 33; s++)
		{
			float ang = Mathf.Tau * s / 32.0f;
			var p = end + new Vector3(Mathf.Cos(ang), 0.0f, Mathf.Sin(ang)) * Selected.BaseRadius;
			p.Y = Terrain.GetHeight(p.X, p.Z);
			_previewMesh.SurfaceAddVertex(p + lift);
		}
		_previewMesh.SurfaceEnd();
	}

	private Godot.Collections.Dictionary Raycast(Vector2 mouse, uint mask)
	{
		var cam = CameraRig.Camera;
		var from = cam.ProjectRayOrigin(mouse);
		var to = from + cam.ProjectRayNormal(mouse) * 1000.0f;
		var query = PhysicsRayQueryParameters3D.Create(from, to, mask);
		return GetWorld3D().DirectSpaceState.IntersectRay(query);
	}

	private static float FlatDistance(Vector3 a, Vector3 b)
	{
		return new Vector2(a.X - b.X, a.Z - b.Z).Length();
	}

	private void UpdateHud()
	{
		string t = $"Turn {Turn}  -  {TeamNames[CurrentTeam]} player's move\n";
		if (Selected != null)
		{
			if (Selected.Team == CurrentTeam)
				t += $"{Selected.DisplayName}   Move left: {Selected.MovementLeft:0.0}\" / {Selected.MoveRange:0.0}\"\n";
			else
				t += $"{Selected.DisplayName} (enemy)   Threat range: {Selected.MoveRange:0.0}\"\n";
		}
		t += _hoverText;
		_hud.Text = t;
	}

	private void BuildPreview()
	{
		_previewMat = new StandardMaterial3D();
		_previewMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
		_previewMat.AlbedoColor = new Color(1.0f, 0.9f, 0.3f);
		_previewMat.NoDepthTest = true;
		var mi = new MeshInstance3D();
		mi.Mesh = _previewMesh;
		mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		AddChild(mi);
	}

	private void BuildHud()
	{
		var layer = new CanvasLayer();
		AddChild(layer);
		_hud = new Label();
		_hud.Position = new Vector2(12, 10);
		_hud.AddThemeFontSizeOverride("font_size", 18);
		_hud.AddThemeColorOverride("font_outline_color", Colors.Black);
		_hud.AddThemeConstantOverride("outline_size", 6);
		layer.AddChild(_hud);

		var help = new Label();
		help.Text = "WASD/arrows pan  |  Q/E rotate  |  Wheel zoom  |  Middle-drag orbit  |  LMB select  |  RMB move  |  Space end turn";
		help.AnchorTop = 1.0f;
		help.AnchorBottom = 1.0f;
		help.OffsetLeft = 12;
		help.OffsetTop = -34;
		help.AddThemeColorOverride("font_outline_color", Colors.Black);
		help.AddThemeConstantOverride("outline_size", 5);
		layer.AddChild(help);
	}

	private void BuildEnvironment()
	{
		var env = new Godot.Environment();
		env.BackgroundMode = Godot.Environment.BGMode.Sky;
		var sky = new Sky();
		sky.SkyMaterial = new ProceduralSkyMaterial();
		env.Sky = sky;
		env.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
		env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
		var we = new WorldEnvironment();
		we.Environment = env;
		AddChild(we);

		var sun = new DirectionalLight3D();
		sun.RotationDegrees = new Vector3(-55.0f, 35.0f, 0.0f);
		sun.ShadowEnabled = true;
		AddChild(sun);
	}
}
