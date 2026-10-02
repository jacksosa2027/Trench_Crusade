using Godot;

/// <summary>
/// Tabletop camera rig: this node is a ground pivot that yaws, a child node pitches,
/// and the Camera3D sits back along the pitch node's Z axis (zoom distance).
///
/// Controls: WASD / arrows pan, Shift = faster, Q / E rotate,
///           mouse wheel zoom, hold middle mouse to orbit.
/// </summary>
public partial class RTSCamera : Node3D
{
	[Export] public float PanSpeed = 30.0f;           // inches per second at default zoom
	[Export] public float RotateSpeed = 1.8f;         // radians per second (Q/E)
	[Export] public float OrbitSensitivity = 0.005f;
	[Export] public float ZoomMin = 6.0f;
	[Export] public float ZoomMax = 90.0f;
	[Export] public float ZoomStep = 1.12f;
	[Export] public float PitchMinDeg = -85.0f;
	[Export] public float PitchMaxDeg = -12.0f;
	[Export] public float Smoothing = 10.0f;

	public Terrain Terrain;
	public Rect2 Bounds = new(-30, -22, 60, 44);
	public Camera3D Camera;

	private Node3D _pitchNode;
	private Vector3 _targetPos = Vector3.Zero;
	private float _targetYaw = 0.0f;
	private float _targetPitch = Mathf.DegToRad(-50.0f);
	private float _targetZoom = 45.0f;
	private float _zoom = 45.0f;
	private bool _orbiting = false;

	public override void _Ready()
	{
		_pitchNode = new Node3D();
		AddChild(_pitchNode);
		Camera = new Camera3D();
		Camera.Far = 500.0f;
		_pitchNode.AddChild(Camera);
		Camera.Current = true;
		_targetPos = Position;
		_targetYaw = Rotation.Y;
		Apply(1.0f);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			switch (mb.ButtonIndex)
			{
				case MouseButton.WheelUp:
					if (mb.Pressed)
						_targetZoom = Mathf.Max(ZoomMin, _targetZoom / ZoomStep);
					break;
				case MouseButton.WheelDown:
					if (mb.Pressed)
						_targetZoom = Mathf.Min(ZoomMax, _targetZoom * ZoomStep);
					break;
				case MouseButton.Middle:
					_orbiting = mb.Pressed;
					break;
			}
		}
		else if (@event is InputEventMouseMotion mm && _orbiting)
		{
			_targetYaw -= mm.Relative.X * OrbitSensitivity;
			_targetPitch = Mathf.Clamp(_targetPitch - mm.Relative.Y * OrbitSensitivity,
				Mathf.DegToRad(PitchMinDeg), Mathf.DegToRad(PitchMaxDeg));
		}
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		var move = Vector2.Zero;   // x = right, y = forward
		if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up))
			move.Y += 1.0f;
		if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down))
			move.Y -= 1.0f;
		if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right))
			move.X += 1.0f;
		if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left))
			move.X -= 1.0f;
		if (Input.IsKeyPressed(Key.Q))
			_targetYaw += RotateSpeed * dt;
		if (Input.IsKeyPressed(Key.E))
			_targetYaw -= RotateSpeed * dt;

		if (move != Vector2.Zero)
		{
			float speed = PanSpeed * (_zoom / 45.0f);
			if (Input.IsKeyPressed(Key.Shift))
				speed *= 2.0f;
			var forward = new Vector3(-Mathf.Sin(_targetYaw), 0.0f, -Mathf.Cos(_targetYaw));
			var right = new Vector3(Mathf.Cos(_targetYaw), 0.0f, -Mathf.Sin(_targetYaw));
			_targetPos += (right * move.X + forward * move.Y).Normalized() * speed * dt;
		}

		_targetPos.X = Mathf.Clamp(_targetPos.X, Bounds.Position.X, Bounds.End.X);
		_targetPos.Z = Mathf.Clamp(_targetPos.Z, Bounds.Position.Y, Bounds.End.Y);
		if (Terrain != null)
			_targetPos.Y = Terrain.GetHeight(_targetPos.X, _targetPos.Z);

		Apply(1.0f - Mathf.Exp(-Smoothing * dt));
	}

	private void Apply(float weight)
	{
		Position = Position.Lerp(_targetPos, weight);
		var rot = Rotation;
		rot.Y = Mathf.LerpAngle(rot.Y, _targetYaw, weight);
		Rotation = rot;
		var pitchRot = _pitchNode.Rotation;
		pitchRot.X = Mathf.Lerp(pitchRot.X, _targetPitch, weight);
		_pitchNode.Rotation = pitchRot;
		_zoom = Mathf.Lerp(_zoom, _targetZoom, weight);
		Camera.Position = new Vector3(0.0f, 0.0f, _zoom);
	}
}
