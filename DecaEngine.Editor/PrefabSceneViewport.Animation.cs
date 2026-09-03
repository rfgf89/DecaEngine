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
	/// <summary>Scene animation drive: per-frame character step and humanoid avatar mapping.</summary>
	public partial class PrefabSceneViewport
	{
		private void UpdateAnimation(float deltaSeconds)
		{
			// Diagnostics run before the early-out. Fully qualified System.Environment: the
			// viewport has its own Environment property that the short name would resolve to.
			if (System.Environment.GetEnvironmentVariable("DECA_ANIM_DIAG") == "1" && (_animDiagFrame++ % 10) == 0)
			{
				Console.WriteLine($"[animdiag] frame {_animDiagFrame}: " +
					$"{((DiligentBatchRenderer)_env.BatchRenderer).DiagCounters}, " +
					$"entries={_rendered.Count}, characters={_animation?.CharacterCount ?? 0}");
			}

			if (_animation == null || _animation.CharacterCount == 0)
			{
				return;
			}

			// Prefab store, not the environment's: it is recreated on every prefab reload.
			var store = _lastStore;
			if (store == null)
			{
				return;
			}

			// Rewired every frame: both are toggled on a live scene, long after the driver was made.
			_animation.Physics = _physics;
			_animation.Debug = _debugDraw;
			_animation.DebugOptions = _editorSettings.AnimationDebug;
			_animation.HighlightJoint = HighlightJoint;
			_animation.BeginFrame();

			foreach (var record in _rendered.Values)
			{
				if (!store.TryGetEntityById(record.EntityId, out var entity))
				{
					continue;
				}

				// Re-applied every frame because the Humanoid window edits it on a live scene;
				// SetAvatar compares by reference, so repeat calls are free.
				if (!string.IsNullOrEmpty(record.ResolvedPath) &&
					_models.TryGetValue(record.ResolvedPath, out var avatarState) &&
					avatarState.Model?.Skeleton != null)
				{
					_animation.SetAvatar(record.EntityId,
						AvatarFor(record.ResolvedPath, avatarState.Model.Skeleton));
				}

				// The pose is in model space while physics is in world space: foot IK and the
				// ragdoll need the entity world transform to convert between them.
				_animation.Update(entity, record.LastWorld, deltaSeconds);
			}

			_env.BatchRenderer.ExecuteSkinning();
		}

		// --- Humanoid mapping (see HumanoidAvatar) ------------------------------------------------

		// Keyed by model path, not entity: the mapping is a property of the rig.
		private readonly Dictionary<string, HumanoidAvatar> _avatars = new();

		// Models whose mapping was auto-built rather than loaded: the Humanoid window says so.
		private readonly HashSet<string> _autoAvatars = new(StringComparer.OrdinalIgnoreCase);

		// Falls back to an auto-built mapping so foot IK and ragdolls work without manual setup.
		private HumanoidAvatar AvatarFor(string modelPath, PreparedSkeleton skeleton)
		{
			if (_avatars.TryGetValue(modelPath, out var cached))
			{
				return cached;
			}

			var avatar = HumanoidAvatarAsset.Load(modelPath);

			if (avatar == null)
			{
				avatar = HumanoidAutoMap.Build(skeleton);
				_autoAvatars.Add(modelPath);
			}
			else
			{
				_autoAvatars.Remove(modelPath);
			}

			_avatars[modelPath] = avatar;
			return avatar;
		}

		/// <summary>True when this model's humanoid mapping was auto-built.</summary>
		public bool IsAvatarAuto(string modelPath) => _autoAvatars.Contains(modelPath);

		/// <summary>Drops the cached mapping; call after saving an avatar or the scene keeps the old one.</summary>
		public void InvalidateAvatar(string modelPath)
		{
			if (!string.IsNullOrEmpty(modelPath))
			{
				_avatars.Remove(modelPath);
				_autoAvatars.Remove(modelPath);
			}
		}

		/// <summary>Skinned model of the selected entity; input for the Humanoid window.</summary>
		public (PreparedSkeleton? Skeleton, string? ModelPath, string Name) SelectedSkinnedModel
		{
			get
			{
				if (_highlightedId < 0 || !_rendered.TryGetValue(_highlightedId, out var record) ||
					string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) ||
					state.Model?.Skeleton == null)
				{
					return (null, null, string.Empty);
				}

				return (state.Model.Skeleton, record.ResolvedPath,
					System.IO.Path.GetFileName(record.ResolvedPath));
			}
		}

		/// <summary>Joint highlighted by the Humanoid window; empty means no highlight.</summary>
		public string HighlightJoint { get; set; } = string.Empty;
	}
}
