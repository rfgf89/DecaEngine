using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Scene physics: lazy Bepu world, statics, debug line overlay.</summary>
	public partial class PrefabSceneViewport
	{
		// Must run BEFORE animation: foot IK rays need the world as it will be drawn, and the
		// ragdoll reads poses from bodies integrated here.
		private void PollScenePhysics(float deltaSeconds)
		{
			if (!ScenePhysicsWanted())
			{
				if (_physics != null)
				{
					// Detach, not Clear: ragdoll handles die with the world, but characters and
					// their skinning palettes must survive physics being switched off.
					_animation?.DetachPhysics();
					_motion.Clear(_physics);
					_physics.Dispose();
					_physics = null;
				}

				return;
			}

			if (_physics == null)
			{
				_physics = new ScenePhysics(new Vector3(0f, _editorSettings.SceneGravity, 0f));
				_physicsStaticsDirty = true;
			}

			// The world exists outside Play (statics BVH is too slow to build on button press)
		// but only steps in Play; the manual pause sits on top of that.
		_physics.Paused = _editorSettings.ScenePhysicsPaused || !IsPlaying;
			_physics.TimeScale = _editorSettings.ScenePhysicsTimeScale;
			_physics.RecordRays = _editorSettings.PhysicsDebug.Rays;

			bool recordContacts = _editorSettings.PhysicsDebug.NeedsContactRecording;
			if (_physics.World.Contacts.Enabled != recordContacts)
			{
				_physics.World.Contacts.Enabled = recordContacts;

				// Disabling must also clear, or the last recorded step stays on screen forever.
				if (!recordContacts)
				{
					_physics.World.Contacts.Clear();
				}
			}

			if (_physicsStaticsDirty)
			{
				_physicsStaticsDirty = false;
				RebuildPhysicsStatics();
			}

			// Velocity is set BEFORE the step and the pose read AFTER; merging the two calls
			// would cost a frame of lag in one direction or the other.
			_motion.Input = _playerInput;
			_playerInput = default;
			_motion.Steer(_lastStore, _physics, IsPlaying, deltaSeconds, _animation);

			_physics.Update(deltaSeconds);

			// SyncScene ran earlier this frame, so the picture trails physics by exactly one frame.
			_motion.Apply(_lastStore, _physics);
		}

		// The world is created only for a real consumer: building statics is a whole-scene BVH.
		private bool ScenePhysicsWanted()
		{
			if (!_editorSettings.ScenePhysicsEnabled)
			{
				return false;
			}

			if (_editorSettings.PhysicsDebug.AnyEnabled)
			{
				return true;
			}

			var store = _lastStore;
			if (store == null)
			{
				return false;
			}

			foreach (var record in _rendered.Values)
			{
				if (store.TryGetEntityById(record.EntityId, out var entity) &&
					(entity.HasComponent<FootIkComponent>() || entity.HasComponent<RagdollComponent>() ||
						IsPhysicalCharacter(entity)))
				{
					return true;
				}
			}

			return false;
		}

		private static bool IsPhysicalCharacter(Entity entity) => entity.HasComponent<CharacterBodyComponent>();

		/// <summary>Character driver state for the debug window.</summary>
		public (bool Playing, bool HasPhysics, bool Paused, int Scripts, int WithBody, int Bodies, int FloorRescues) ScriptCharacterStatus
		{
			get
			{
				int scripts = 0;
				int withBody = 0;
				var store = _lastStore;

				if (store != null)
				{
					// Scripts and bodies are counted separately: old scenes have scripts but no body.
					foreach (var entity in store.Query<CircleMoveComponent>().Entities)
					{
						scripts++;
						withBody += entity.HasComponent<CharacterBodyComponent>() ? 1 : 0;
					}

					foreach (var entity in store.Query<PlayerMoveComponent>().Entities)
					{
						scripts++;
						withBody += entity.HasComponent<CharacterBodyComponent>() ? 1 : 0;
					}
				}

				return (IsPlaying, _physics != null, _physics?.Paused ?? false, scripts, withBody,
					_motion.CharacterCount, _motion.FloorRescues);
			}
		}

		// Skinned models are excluded: a character must not be its own floor, or foot IK rays
		// would hit its own leg.
		private void RebuildPhysicsStatics()
		{
			if (_physics == null)
			{
				return;
			}

			_physics.BeginStatics();

			foreach (var record in _rendered.Values)
			{
				if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null ||
					state.Model.Skeleton != null)
				{
					continue;
				}

				_physicsPositions.Clear();
				_physicsIndices.Clear();
				AppendRecordGeometry(record, state.Model, _physicsPositions, _physicsIndices);

				_physics.AddStaticMesh(
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_physicsPositions),
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_physicsIndices));
			}

			_physics.EndStatics();

			// Scratch holds a world copy of the whole scene: tens of MB on a large level.
			_physicsPositions.Clear();
			_physicsIndices.Clear();
			_physicsPositions.TrimExcess();
			_physicsIndices.TrimExcess();
		}

		// Call before any stage that draws: the line list is one frame, not an accumulator.
		private void BeginDebugFrame()
		{
			// Joint highlight turns debug on by itself; it is asked for with all layers off.
			_debugDraw.Enabled = _editorSettings.AnimationDebug.AnyEnabled ||
				_editorSettings.PhysicsDebug.AnyEnabled ||
				!string.IsNullOrEmpty(HighlightJoint);

			_debugDraw.Clear();
		}

		// Call after animation and before the graph runs.
		private void EndDebugFrame()
		{
			if (_debugDraw.Enabled && _physics != null)
			{
				var options = _editorSettings.PhysicsDebug;
				PhysicsDebugDraw.Draw(_debugDraw, _physics, options);

				if (options.RagdollJoints)
				{
					_animation?.DrawRagdollJoints(_debugDraw, options.OnTop);
				}
			}

			_animation?.DescribeCharacters(_debugCharacters);
			PollDebugLineOverlay();
		}

		// Creating or dropping the overlay rebuilds the graph (commands are frozen), so the
		// "anything to draw" test must stay cheap.
		private void PollDebugLineOverlay()
		{
			if (!_debugDraw.Enabled || _debugDraw.TotalCount == 0)
			{
				if (_env.Pipeline.DebugOverlay != null)
				{
					_env.Pipeline.DebugOverlay = null;
					_env.Pipeline.InvalidateGraph();
				}

				return;
			}

			if (_debugLineOverlay == null)
			{
				if (_debugOverlayFailed)
				{
					return;
				}

				try
				{
					_debugLineOverlay = new DebugLineOverlay(_env.DilApi, _graphicsApi, _env.BatchRenderer,
						_env.Pipeline.Targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm);
				}
				catch (Exception ex)
				{
					// Try once: a shader that failed to compile will fail again next frame.
					_debugOverlayFailed = true;
					EngineLog.Add(LogLevel.Error, $"Debug draw: overlay unavailable: {ex.Message}");
					return;
				}
			}

			_debugLineOverlay.Intensity = _editorSettings.DebugLineIntensity;

			bool commandsDirty = _debugLineOverlay.Upload(_debugDraw);

			if (_env.Pipeline.DebugOverlay == null)
			{
				_env.Pipeline.DebugOverlay = _debugLineOverlay.Draw;
				commandsDirty = true;
			}

			if (commandsDirty)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		// Call before recreating the environment: the overlay holds this pipeline's buffers and PSO.
		private void ReleaseDebugOverlay()
		{
			if (_debugLineOverlay == null)
			{
				return;
			}

			_env.Pipeline.DebugOverlay = null;
			_env.Pipeline.InvalidateGraph();
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			_debugLineOverlay.Dispose();
			_debugLineOverlay = null;
		}

		private readonly HashSet<int> _visitedThisSync = new();
		private readonly List<int> _removeScratch = new();

		// Prefab punctual lights mirrored into the env render store, keyed by prefab entity id:
		// the culling system reads _env.Store, and mirrors carry world transforms, not local.
		private readonly Dictionary<int, Entity> _lightMirrors = new();

		private readonly List<PunctualLight> _probeBakeLightsScratch = new();

		// TLAS for ray-traced shadows, separate from the probe _sceneAccel: BLASes are cached
		// per mesh and survive movement, poses are handled by rebuilding the TLAS.
		private DiligentRayTracingScene? _rtShadowScene;
		private readonly List<DiligentRayTracingScene.Instance> _rtShadowInstances = new();
		private bool _appliedRtShadows;

	}
}
