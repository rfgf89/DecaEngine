using System;
using System.IO;
using System.Numerics;
using DecaEngine.Core.Assets;
using DecaEngine.Core.Entities;
using DecaEngine.Core.Prefabs;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor;

/// <summary>Builds the demo scene (--make-sample-prefab): ground, four animated characters, two lights.
/// Built in code and saved via PrefabAsset.SaveJson because the .prefab.json field layout is
/// undocumented Friflo serialization; a hand-written file would silently drift.</summary>
public static class SamplePrefabBuilder
{
	/// <summary>Creates a whole project (--make-sample-project) via EditorBuilder, the same path as File - New Project.</summary>
	public static void RunProject(string[] args)
	{
		string outputPath = args.Length > 1 ? args[1] : ".";
		string projectName = args.Length > 2 ? args[2] : "AnimationSample";

		if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
		{
			// EditorBuilder reads csproj via MSBuild; without registration it fails mid-generation.
			Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
		}

		Console.WriteLine($"[sample] creating project '{projectName}' in {Path.GetFullPath(outputPath)} ...");

		string slnPath = new EditorBuilder().Build(projectName, outputPath,
			ProjectTemplate.AnimationSample, Console.WriteLine);

		Console.WriteLine($"[sample] done. To open: File -> Open Project -> {slnPath}");
	}

	public static void Run(string[] args) => WriteScene(args.Length > 1 ? args[1] : "Assets");

	/// <summary>Khronos Fox is authored in centimeters; 0.01 puts it at ~1.5 m in the scene's meters.</summary>
	private const float FoxScale = 0.01f;

	/// <summary>Writes the demo scene: one character per animation-stack layer, since the layers
	/// override each other's pose and a single character would only show the last one.</summary>
	public static void WriteScene(string assetsDirectory, Action<string>? log = null)
	{
		log ??= Console.WriteLine;

		Directory.CreateDirectory(assetsDirectory);

		string prefabPath = Path.Combine(assetsDirectory, "Animation Sample.prefab.json");

		CopyFoxModel(assetsDirectory, log);
		WriteGround(assetsDirectory, log);
		WriteFoxAvatar(assetsDirectory, log);

		var store = new EntityStore();
		var root = store.CreateEntity();

		root.AddComponent(new EntityName("Animation Sample"));
		root.AddComponent(new Position(0f, 0f, 0f));
		root.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		root.AddComponent(new Scale3(1f, 1f, 1f));

		root.AddChild(CreateGround(store));
		root.AddChild(CreateClipFox(store));
		root.AddChild(CreateFootIkFox(store));
		root.AddChild(CreateRagdollFox(store, active: false));
		root.AddChild(CreateRagdollFox(store, active: true));
		root.AddChild(CreateCircleFox(store));
		root.AddChild(CreatePlayerFox(store));
		root.AddChild(CreateKeyLight(store));
		root.AddChild(CreateFillLight(store));

		PrefabAsset.SaveJson(root, prefabPath);

		log($"[sample] prefab written: {Path.GetFullPath(prefabPath)}");
		VerifyRoundTrip(prefabPath, log);
	}

	private static Entity CreateGround(EntityStore store)
	{
		var ground = store.CreateEntity();

		ground.AddComponent(new EntityName("Ground"));
		ground.AddComponent(new Position(0f, 0f, 0f));
		ground.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		ground.AddComponent(new Scale3(1f, 1f, 1f));
		ground.AddComponent(new ModelRenderer { modelRef = new AssetRef("Ground.glb") });

		return ground;
	}

	/// <summary>Pure animation character: clip, spring-bone tail, head look-at; flat ground on purpose.</summary>
	private static Entity CreateClipFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Run (clip + spring + look-at)", new Vector3(0f, 0f, 3.5f));

		fox.AddComponent(new Animator
		{
			ClipName = "Run",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		// Gravity is in MODEL space and model units (not meters), hence the large value.
		fox.AddComponent(new SpringBoneComponent
		{
			Enabled = true,
			RootJoint = "b_Tail01_012",
			Length = 3,
			Stiffness = 0.08f,
			Drag = 0.2f,
			TailLength = 10f,
			Gravity = new Vector3(0f, -20f, 0f),
		});

		// Look-at target is currently evaluated in model space (see AnimationDriver), so it is
		// given in model units. Gaze axes are rig-specific: Fox's head bone looks along +Z.
		fox.AddComponent(new LookAtComponent
		{
			Enabled = true,
			Joint = "b_Head_05",
			Target = new Vector3(0f, 40f, 120f),
			Forward = Vector3.UnitZ,
			Up = Vector3.UnitY,
			Weight = 0.6f,
		});

		return fox;
	}

	/// <summary>Foot IK character placed ON THE STAIRS: on flat ground the solver is indistinguishable from off.</summary>
	private static Entity CreateFootIkFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Walk (foot IK on stairs)", new Vector3(2.6f, 0.35f, 0f));

		fox.AddComponent(new Animator
		{
			ClipName = "Walk",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		// Bones deliberately unset: they come from the model's humanoid avatar mapping.
		// All sizes are in MODEL units (joint-position units), not scene meters: the solver
		// works in model space.
		fox.AddComponent(new FootIkComponent
		{
			Enabled = true,
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			Smoothing = 12f,
			Weight = 1f,
			AlignToNormal = true,

			// Quadruped: front legs from the arm slots + body tilt to terrain.
			FrontLegs = true,
		});

		return fox;
	}

	/// <summary>Ragdolls above the ramp, limp and active (servo); both drop on the first simulated frame.</summary>
	private static Entity CreateRagdollFox(EntityStore store, bool active)
	{
		var fox = CreateFox(store,
			active ? "Fox Active Ragdoll (servo)" : "Fox Ragdoll (limp)",
			new Vector3(-3.2f, 1.8f, active ? 2.2f : -2.2f));

		// The clip keeps playing: invisible in limp mode, but in active mode it is the servo target.
		fox.AddComponent(new Animator
		{
			ClipName = "Survey",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		fox.AddComponent(new RagdollComponent
		{
			Enabled = true,
			Physical = true,
			ServoStrength = active ? 60f : 0f,

			// Root unset: pelvis comes from the humanoid mapping, like the foot IK bones above.
			MaxDepth = 4,

			// Zero = capsule radii measured from the mesh per body part; a single authored
			// radius is wrong by construction (torso is 3x thicker than a leg).
			BoneRadius = 0f,
			TotalMass = 12f,
		});

		return fox;
	}

	/// <summary>Circle center/radius chosen on the FLAT part of the ground so the path avoids stairs and ramp.</summary>
	private static readonly Vector3 CircleCenter = new(0f, 0f, -4.3f);

	private const float CircleRadius = 2f;

	/// <summary>Meters per second.</summary>
	private const float CircleSpeed = 1f;

	/// <summary>Character walking in a circle: the first gameplay script in the scene.
	/// Start position and rotation match phase zero so Play does not begin with a visible jump.</summary>
	private static Entity CreateCircleFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Circle (gameplay script)",
			CircleCenter + new Vector3(CircleRadius, 0f, 0f));

		// Same rotation the system will set on frame one at phase zero; computed, not hardcoded,
		// so it cannot drift from the system and jerk at Play.
		var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY,
			MathF.Atan2(0f, 1f) - MathF.Atan2(FoxForward.X, FoxForward.Z));
		fox.AddComponent(new Rotation(facing.X, facing.Y, facing.Z, facing.W));

		// Animator stays as the locomotion fallback (no ozz / clips missing) - see LocomotionComponent.
		fox.AddComponent(new Animator
		{
			ClipName = "Walk",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		fox.AddComponent(new LocomotionComponent
		{
			IdleClip = "Survey",
			WalkSpeed = 1f,
			RunSpeed = 3f,
		});

		// Partial blend rooted at the neck BY NAME: the default chest slot carries the front
		// legs on a quadruped, and an overlay there would freeze their walk.
		fox.AddComponent(new OverlayClipComponent
		{
			Enabled = true,
			ClipName = "Survey",
			RootJoint = "b_Neck_04",
			Weight = 1f,
			Speed = 1f,
			Loop = true,
		});

		fox.AddComponent(new CircleMoveComponent
		{
			Enabled = true,
			Center = CircleCenter,
			Radius = CircleRadius,
			Speed = CircleSpeed,
			Angle = 0f,
			FaceMotion = true,
			Forward = FoxForward,

			// Turn limit matters after ragdoll recovery: the body turns toward the tangent
			// instead of snapping to it.
			TurnSpeed = 360f,
		});

		// Body sizes are in scene METERS, not model units: the body lives in world space.
		// Fox pelvis measured at y=~41 model units = 0.41 m at scale 0.01.
		fox.AddComponent(new CharacterBodyComponent
		{
			Radius = 0.18f,
			Height = 0.5f,
			Mass = 12f,

			// Along the body: the fox is ~1 m nose to rump; a vertical capsule would leave the
			// front half clipping into stairs and walls (see CharacterBodyComponent.Length).
			Length = 0.8f,
		});

		// Same numbers as the stairs fox: one model, diverging values would mean one of them is wrong.
		fox.AddComponent(new FootIkComponent
		{
			Enabled = true,
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			Smoothing = 12f,
			Weight = 1f,
			AlignToNormal = true,

			// Quadruped: front legs from the arm slots + body tilt to terrain.
			FrontLegs = true,
		});

		// Disabled and non-physical: the fall cycle enables it on demand; building it up front
		// costs ~20 bodies and joints for a character that mostly just walks.
		fox.AddComponent(new RagdollComponent
		{
			Enabled = false,
			Physical = false,
			MaxDepth = 4,
			BoneRadius = 0f,
			TotalMass = 12f,
		});

		// Fall every 6 s = twice per 12.6 s lap: frequent enough to see soon after Play,
		// rare enough to still watch the walking.
		fox.AddComponent(new FallRecoverComponent
		{
			// Custom get-up clips added to Fox.glb; chosen by lying pose, snapshot blended
			// into the clip start over GetUpDuration.
			GetUpBackClip = "GetUp_FromBack",
			GetUpBellyClip = "GetUp_FromBelly",
			GetUpDuration = 0.7f,

			FallEvery = 6f,
			MinFallTime = 1.2f,
			SettleTimeout = 4f,
			SettleSpeed = 0.05f,
		});

		return fox;
	}

	/// <summary>Player-controlled character (WASD/arrows in Play, Shift = run), full modern setup.</summary>
	private static Entity CreatePlayerFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Player (WASD in Play)", new Vector3(3.5f, 0f, -4.3f));

		// Face world +Z before the first input; Khronos Fox's muzzle points -Z (see FoxForward).
		var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY,
			MathF.Atan2(0f, 1f) - MathF.Atan2(FoxForward.X, FoxForward.Z));
		fox.AddComponent(new Rotation(facing.X, facing.Y, facing.Z, facing.W));

		// Additive layer: Survey delta on top of any gait; unlike Overlay Clip the walk on
		// these bones is not overwritten, only nudged. Half weight = subtle motion.
		fox.AddComponent(new AdditiveClipComponent
		{
			Enabled = true,
			ClipName = "Survey",
			Weight = 0.5f,
			Speed = 1f,
			Loop = true,
		});

		fox.AddComponent(new Animator
		{
			ClipName = "Walk",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		fox.AddComponent(new LocomotionComponent
		{
			IdleClip = "Survey",
			WalkSpeed = 1f,
			RunSpeed = 3f,
		});

		fox.AddComponent(new CharacterBodyComponent
		{
			Radius = 0.18f,
			Height = 0.5f,
			Mass = 12f,

			// Along the body: the fox is ~1 m nose to rump; a vertical capsule would leave the
			// front half clipping into stairs and walls (see CharacterBodyComponent.Length).
			Length = 0.8f,
		});

		fox.AddComponent(new PlayerMoveComponent
		{
			WalkSpeed = 1f,
			RunSpeed = 3f,
			FaceMotion = true,
			Forward = FoxForward,
		});

		fox.AddComponent(new FootIkComponent
		{
			Enabled = true,
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			Smoothing = 12f,
			Weight = 1f,
			AlignToNormal = true,

			// Quadruped: front legs from the arm slots + body tilt to terrain.
			FrontLegs = true,
		});

		// Disabled ragdoll is required for hit reactions (being rammed): the reaction builds
		// bodies for the push and tears them down after.
		fox.AddComponent(new RagdollComponent
		{
			Enabled = false,
			Physical = false,
			MaxDepth = 4,
			BoneRadius = 0f,
			TotalMass = 12f,
		});

		return fox;
	}

	/// <summary>Khronos Fox muzzle direction in model space; NOT the engine's forward = rotation * +Z convention.</summary>
	private static readonly Vector3 FoxForward = -Vector3.UnitZ;

	private static Entity CreateFox(EntityStore store, string name, Vector3 position)
	{
		var fox = store.CreateEntity();

		fox.AddComponent(new EntityName(name));
		fox.AddComponent(new Position(position.X, position.Y, position.Z));
		fox.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		fox.AddComponent(new Scale3(FoxScale, FoxScale, FoxScale));

		// Path is relative to the Assets folder: that is how the scene resolves it
		// (see PrefabSceneViewport.ResolveAssetPath).
		fox.AddComponent(new ModelRenderer { modelRef = new AssetRef("Fox.glb") });

		return fox;
	}

	/// <summary>Key spot from above; light direction comes from the entity ROTATION (forward = rotation * +Z).</summary>
	private static Entity CreateKeyLight(EntityStore store)
	{
		var light = store.CreateEntity();
		var down = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);

		light.AddComponent(new EntityName("Key Spot"));
		light.AddComponent(new Position(1.5f, 4.5f, -1.5f));
		light.AddComponent(new Rotation(down.X, down.Y, down.Z, down.W));
		light.AddComponent(new Scale3(1f, 1f, 1f));

		// Intensity is in ENGINE units, not lumens (existing lights use 1..8); "physical" 120
		// blew out the frame via probe GI and auto-exposure.
		light.AddComponent(new LightComponent
		{
			Type = LightType.Spot,
			Color = new Vector3(1f, 0.93f, 0.82f),
			Intensity = 8f,
			Range = 14f,
			SpotAngle = 70f,
			InnerSpotAngle = 40f,
			ShadowStrength = 1f,

			// Emitter radius drives PCSS penumbra width; 10 cm keeps shadows readable.
			SourceRadius = 0.1f,
		});

		return light;
	}

	/// <summary>Fill point near the red wall so probe GI shows a color difference, not just brightness.</summary>
	private static Entity CreateFillLight(EntityStore store)
	{
		var light = store.CreateEntity();

		light.AddComponent(new EntityName("Fill Point"));
		light.AddComponent(new Position(-5.5f, 1.8f, 0f));
		light.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		light.AddComponent(new Scale3(1f, 1f, 1f));

		light.AddComponent(new LightComponent
		{
			Type = LightType.Point,
			Color = new Vector3(0.75f, 0.85f, 1f),
			Intensity = 3f,
			Range = 8f,
			ShadowStrength = 1f,
			SourceRadius = 0.15f,
		});

		return light;
	}

	/// <summary>Writes the humanoid avatar file next to the model; without the file the mapping is
	/// recomputed on every load and silently changes with the mapping code. Reads the skeleton
	/// only: a full model load would require a GPU the prefab generator does not have.</summary>
	private static void WriteFoxAvatar(string assetsDirectory, Action<string> log)
	{
		string modelPath = Path.Combine(assetsDirectory, "Fox.glb");

		if (!File.Exists(modelPath))
		{
			return;
		}

		try
		{
			var skeleton = SkinningImport.BuildSkeleton(
				SharpGLTF.Schema2.ModelRoot.Load(modelPath), out _);
			if (skeleton == null || skeleton.JointCount == 0)
			{
				log("[sample] WARNING: Fox.glb skeleton could not be read - avatar not written");
				return;
			}

			var avatar = HumanoidAutoMap.Build(skeleton);
			var issues = HumanoidValidation.Validate(avatar, skeleton);

			// Reference pose comes from bind; the fox is a quadruped so its "arms" point down,
			// which is a property of the model, not a mapping error.
			HumanoidReferencePose.CaptureFromBind(avatar, skeleton);
			var pose = HumanoidReferencePose.Evaluate(avatar, skeleton);

			HumanoidAvatarAsset.Save(avatar, modelPath);

			log($"[sample] avatar written: {HumanoidAvatarAsset.PathFor(modelPath)} " +
				$"(bones {skeleton.JointCount}, mapping issues {issues.Count}, " +
				$"deviation from T-pose up to {pose.Worst:0.#}°)");
		}
		catch (Exception ex)
		{
			// The scene still works without an avatar - the mapping just becomes automatic.
			log($"[sample] WARNING: avatar not written ({ex.Message}) - mapping will be automatic");
		}
	}

	private static void WriteGround(string assetsDirectory, Action<string> log)
	{
		string path = Path.Combine(assetsDirectory, "Ground.glb");

		try
		{
			SampleGroundBuilder.Write(path);
			log($"[sample] ground generated: {Path.GetFullPath(path)}");
		}
		catch (Exception ex)
		{
			log($"[sample] WARNING: ground not generated ({ex.Message}) - " +
				"characters will be in empty space, with nothing to test foot IK and ragdoll against");
		}
	}

	/// <summary>Round-trips the saved prefab: a component Friflo fails to restore arrives with
	/// zeroed fields, not an error, so counts are checked explicitly.</summary>
	private static void VerifyRoundTrip(string prefabPath, Action<string> log)
	{
		var store = new EntityStore();
		var loaded = PrefabAsset.Instantiate(store, prefabPath);

		int children = 0;
		int models = 0;
		int animators = 0;
		int springs = 0;
		int lookAts = 0;
		int footIk = 0;
		int ragdolls = 0;
		int lights = 0;
		int circles = 0;
		int bodies = 0;
		int falls = 0;
		int locomotions = 0;
		int players = 0;

		foreach (var child in loaded.ChildEntities)
		{
			children++;

			models += child.HasComponent<ModelRenderer>() ? 1 : 0;
			animators += child.HasComponent<Animator>() ? 1 : 0;
			springs += child.HasComponent<SpringBoneComponent>() ? 1 : 0;
			lookAts += child.HasComponent<LookAtComponent>() ? 1 : 0;
			footIk += child.HasComponent<FootIkComponent>() ? 1 : 0;
			ragdolls += child.HasComponent<RagdollComponent>() ? 1 : 0;
			lights += child.HasComponent<LightComponent>() ? 1 : 0;
			circles += child.HasComponent<CircleMoveComponent>() ? 1 : 0;
			bodies += child.HasComponent<CharacterBodyComponent>() ? 1 : 0;
			falls += child.HasComponent<FallRecoverComponent>() ? 1 : 0;
			locomotions += child.HasComponent<LocomotionComponent>() ? 1 : 0;
			players += child.HasComponent<PlayerMoveComponent>() ? 1 : 0;
		}

		bool ok = children == 9 && models == 7 && animators == 6 && springs == 1 && lookAts == 1 &&
			footIk == 3 && ragdolls == 4 && lights == 2 && circles == 1 && bodies == 2 && falls == 1 &&
			locomotions == 2 && players == 1;

		log($"[sample] round-trip: children={children}/9, ModelRenderer={models}/7, " +
			$"Animator={animators}/6, SpringBone={springs}/1, LookAt={lookAts}/1, " +
			$"FootIk={footIk}/3, Ragdoll={ragdolls}/4, Light={lights}/2, CircleMove={circles}/1, " +
			$"CharacterBody={bodies}/2, FallRecover={falls}/1, Locomotion={locomotions}/2, " +
			$"PlayerMove={players}/1 {(ok ? "OK" : "COMPONENTS LOST")}");
	}

	/// <summary>Copies the model next to the prefab; without it ModelRenderer resolves to an empty entity.</summary>
	private static void CopyFoxModel(string assetsDirectory, Action<string> log)
	{
		string source = Path.Combine(AppContext.BaseDirectory, "EditorAssets", "models", "Fox.glb");
		string destination = Path.Combine(assetsDirectory, "Fox.glb");

		if (!File.Exists(source))
		{
			log($"[sample] WARNING: {source} not found - put Fox.glb into {assetsDirectory} manually");
			return;
		}

		File.Copy(source, destination, overwrite: true);
	}
}
