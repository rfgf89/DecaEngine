using Engine.ImGui.Core;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

/// <summary>Animation and physics debug window: debug layer toggles, simulation knobs, counters.
/// Owns nothing - toggles live in <see cref="EditorSettings"/>, so closing it disables nothing.</summary>
public class AnimationPhysicsDebugWindow : ImGuiDockingWindow
{
	private readonly EditorSettings _settings;
	private readonly PrefabSceneViewport _viewport;

	private bool _changed;

	public AnimationPhysicsDebugWindow(string title, EditorSettings settings, PrefabSceneViewport viewport,
		ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_settings = settings;
		_viewport = viewport;
	}

	protected override void OnRender(uint dockId)
	{
		_changed = false;

		if (ImGui.CollapsingHeader("Animation", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawAnimationSection();
		}

		if (ImGui.CollapsingHeader("Physics", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawPhysicsSection();
		}

		if (ImGui.CollapsingHeader("Simulation", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSimulationSection();
		}

		if (ImGui.CollapsingHeader("State", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawStateSection();
		}

		if (ImGui.CollapsingHeader("Legend"))
		{
			DrawLegendSection();
		}

		// Save once the control is released, not on every drag tick.
		if (_changed && !ImGui.IsAnyItemActive())
		{
			_settings.Save();
		}
	}

	private void DrawAnimationSection()
	{
		var options = _settings.AnimationDebug;

		_changed |= ImGui.Checkbox("Skeleton", ref options.Skeleton);
		Hint("Bones as octahedra. Orange bone - its pose is driven by physics, cyan - by animation.");

		_changed |= ImGui.Checkbox("Joint axes", ref options.JointAxes);
		Hint("X red, Y green, Z blue. They show a bone twisted around its own axis - " +
			"an error the skeleton's \"sticks\" cannot reveal.");

		_changed |= ImGui.Checkbox("Bone names", ref options.JointNames);
		Hint("Labels over the viewport - use them to fill component fields that name bones as strings.");

		_changed |= ImGui.Checkbox("Bind pose", ref options.BindPose);
		Hint("Grey over the current pose: tells \"pose was not applied\" from \"applied, but the wrong one\".");

		_changed |= ImGui.Checkbox("Foot IK", ref options.FootIk);
		Hint("Leg chains and soles. Red - the solver did NOT run this frame " +
			"(no native ozz, no physics, or bones not found).");

		_changed |= ImGui.Checkbox("Spring bones", ref options.SpringChains);
		_changed |= ImGui.Checkbox("Look-at", ref options.LookAt);

		_changed |= ImGui.Checkbox("On top of geometry##anim", ref options.OnTop);
		Hint("No depth test. Almost always what you want: the skeleton sits entirely inside the mesh.");

		_settings.AnimationDebug = options;
	}

	private void DrawPhysicsSection()
	{
		var options = _settings.PhysicsDebug;

		_changed |= ImGui.Checkbox("Body colliders", ref options.Colliders);
		Hint("Wireframes of the ACTUAL shapes from the simulation registry, not of what was requested.");

		_changed |= ImGui.Checkbox("Scene statics", ref options.Statics);
		Hint("A static mesh is drawn as its bounding box: a wireframe over level triangles would fill the screen.");

		_changed |= ImGui.Checkbox("Contacts", ref options.Contacts);
		Hint("Costs narrow-phase work - only this checkbox enables it. Arrow length is penetration depth.");

		_changed |= ImGui.Checkbox("Raycasts", ref options.Rays);
		Hint("This frame's rays, foot IK rays above all. A grey ray is a miss.");

		_changed |= ImGui.Checkbox("Velocities", ref options.Velocities);
		Hint("Green arrow - linear velocity, purple - angular. Length in world units per second.");

		_changed |= ImGui.Checkbox("Ragdoll joints", ref options.RagdollJoints);

		// The stored field is inverted so its default(false) is the useful behaviour.
		bool collidersOnTop = !options.CollidersDepthTested;
		if (ImGui.Checkbox("Colliders on top of geometry##collidersontop", ref collidersOnTop))
		{
			options.CollidersDepthTested = !collidersOnTop;
			_changed = true;
		}

		Hint("No depth test, same as the skeleton. A character's collider sits entirely INSIDE the mesh - " +
			"with depth testing it is invisible, and this checkbox is usually asked about capsules.");

		_changed |= ImGui.Checkbox("Everything else on top of geometry##phys", ref options.OnTop);
		Hint("Statics, contacts, rays, velocities, joints. Separate from colliders: these live outside " +
			"meshes, and \"on top of everything\" turns them into a mesh covering the whole screen.");

		_settings.PhysicsDebug = options;
	}

	private void DrawSimulationSection()
	{
		bool enabled = _settings.ScenePhysicsEnabled;
		if (ImGui.Checkbox("Physics in scene", ref enabled))
		{
			_settings.ScenePhysicsEnabled = enabled;
			_changed = true;
		}

		Hint("The world is created lazily anyway - for a character with foot IK/ragdoll/Character Body, or " +
			"for enabled physics debug. This checkbox disables it for good.");

		ImGui.TextDisabled(_viewport.ScriptCharacterStatus.Playing
			? "Play is running: simulation and animation are live."
			: "Play is not running: the world exists but does NOT STEP, and animation stands still.");

		bool paused = _settings.ScenePhysicsPaused;
		if (ImGui.Checkbox("Pause", ref paused))
		{
			_settings.ScenePhysicsPaused = paused;
			_changed = true;
		}

		float timeScale = _settings.ScenePhysicsTimeScale;
		if (ImGui.SliderFloat("Time scale", ref timeScale, 0.01f, 2f))
		{
			_settings.ScenePhysicsTimeScale = timeScale;
			_changed = true;
		}

		float gravity = _settings.SceneGravity;
		if (ImGui.SliderFloat("Gravity (Y)", ref gravity, -200f, 0f))
		{
			_settings.SceneGravity = gravity;
			_changed = true;
		}

		Hint("In WORLD units, not metres: model scale is arbitrary, and -9.81 is meaningful exactly " +
			"for a one-metre character. Applied the next time the world is created.");

		float intensity = _settings.DebugLineIntensity;
		if (ImGui.SliderFloat("Line brightness", ref intensity, 0.5f, 20f))
		{
			_settings.DebugLineIntensity = intensity;
			_changed = true;
		}

		Hint("Lines are written to the HDR target BEFORE tonemapping, whose exposure is not known in advance - " +
			"on a very bright or very dark scene, adjust brightness here.");
	}

	private void DrawStateSection()
	{
		var physics = _viewport.DebugPhysics;

		if (physics == null)
		{
			ImGui.TextDisabled("No physics in the scene.");
			ImGui.TextWrapped("The world is created when the scene gets a character with a " +
				"Foot IK, Ragdoll or Character Body component, or when any physics debug layer is enabled.");
		}
		else
		{
			ImGui.Text($"Bodies: {physics.BodyCount} ({physics.SleepingBodyCount} asleep)");
			ImGui.Text($"Static triangles: {physics.StaticTriangleCount}");
			ImGui.Text($"Steps per frame: {physics.LastStepCount} in {physics.LastStepMilliseconds:0.00} ms");
			ImGui.Text($"Raycasts per frame: {physics.RayCastsThisFrame}");

			var contacts = physics.World.Contacts;
			ImGui.Text(contacts.Enabled
				? $"Contacts: {contacts.Contacts.Count}" + (contacts.Dropped > 0 ? $" (+{contacts.Dropped} dropped)" : "")
				: "Contacts are not collected");
		}

		DrawScriptCharacters();

		ImGui.Separator();

		var stats = _viewport.DebugLineStats;
		ImGui.Text($"Debug lines (vertices): {stats.Vertices}");
		if (stats.Overflowed)
		{
			ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0.2f, 1f),
				"Hit the vertex ceiling - NOT everything is shown.");
		}

		ImGui.Separator();

		var characters = _viewport.DebugCharacters;
		if (characters.Count == 0)
		{
			ImGui.TextDisabled("No skinned characters in the scene.");
			return;
		}

		for (int i = 0; i < characters.Count; i++)
		{
			var character = characters[i];

			if (!ImGui.TreeNodeEx($"Entity {character.EntityId}##character{i}",
				ImGuiTreeNodeFlags.DefaultOpen))
			{
				continue;
			}

			ImGui.Text($"Clip: {character.Clip}{(character.Playing ? "" : " (not found)")}");
			ImGui.Text(character.Locomotion
				? $"Locomotion: {character.LocoSpeed:0.00} m/s - idle {character.LocoIdleWeight:0.00} / " +
					$"walk {character.LocoWalkWeight:0.00} / run {character.LocoRunWeight:0.00}, " +
					$"gait phases {character.LocoWalkPhaseOffset:0.00}/{character.LocoRunPhaseOffset:0.00}"
				: "Locomotion: none (pose driven by Animator)");
			ImGui.Text($"Time: {character.Time:0.000} s");
			ImGui.Text($"Joints: {character.JointCount}");
			ImGui.Text($"IK legs: {character.LegCount} - {(character.IkApplied ? "applied" : "not applied")}");
			ImGui.Text($"Spring bone chains: {character.ChainCount}");
			ImGui.Text(character.RagdollBones > 0
				? $"Ragdoll: {character.RagdollBones} bones, {(character.RagdollPhysical ? "physics" : "animation")}"
				: "Ragdoll: none");
			if (character.ReactionWeight > 0f)
			{
				ImGui.Text($"Hit reaction: weight {character.ReactionWeight:0.00}, " +
					$"deviation {character.ReactionDeviation:0.##} model units");
			}

			ImGui.TreePop();
		}
	}

	private void DrawScriptCharacters()
	{
		ImGui.Separator();

		var status = _viewport.ScriptCharacterStatus;

		ImGui.Text($"Motion scripts: {status.Scripts}, of them with Character Body: {status.WithBody}");
		ImGui.Text($"Bodies created: {status.Bodies}, Play: {(status.Playing ? "running" : "not running")}");

		if (status.FloorRescues > 0)
		{
			ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.35f, 1f),
				$"Rescues from below the floor: {status.FloorRescues} (see Console)");
		}

		if (status.Scripts == 0)
		{
			ImGui.TextDisabled("No entities with a motion script in the scene.");
			return;
		}

		if (!status.Playing)
		{
			ImGui.TextDisabled("Scripts only run in Play Mode - hit Play in the inspector.");
		}
		else if (status.WithBody == 0)
		{
			ImGui.TextDisabled("No body - the character moves by transform and passes through geometry. " +
				"Add a Character Body (or recreate the scene if it is an old one).");
		}
		else if (!status.HasPhysics)
		{
			ImGui.TextDisabled("No physics in the scene - a character with a body will just stand there. " +
				"Enable \"Physics in scene\" above.");
		}
		else if (status.Paused)
		{
			ImGui.TextDisabled("Simulation is PAUSED - velocity is set on the body, but the world does not step. " +
				"Clear the \"Pause\" checkbox above.");
		}
		else if (status.Bodies < status.WithBody)
		{
			ImGui.TextDisabled("Not everyone got a body: check the circle radius (zero disables the script).");
		}
	}

	private static void DrawLegendSection()
	{
		ImGui.BulletText("Orange - dynamic body (and a bone driven by physics)");
		ImGui.BulletText("Cyan - kinematic; a bone driven by animation; surface normal");
		ImGui.BulletText("Grey - body asleep; bind pose; a ray that hit nothing");
		ImGui.BulletText("Blue - scene statics");
		ImGui.BulletText("Yellow - contact with statics; ragdoll link; skeleton root");
		ImGui.BulletText("Red - contact between two bodies; foot IK not applied");
		ImGui.BulletText("Green - spring bone chain; linear velocity; the hit part of a ray");
		ImGui.BulletText("Purple - look-at target; angular velocity; a shape the debug draw cannot render");
	}

	private static void Hint(string text)
	{
		ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.6f, 0.62f, 1f));
		ImGui.TextWrapped(text);
		ImGui.PopStyleColor();
		ImGui.Spacing();
	}
}
