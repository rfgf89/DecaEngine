using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;
using Engine.ImGui.Core;
using Hexa.NET.ImGui;
using DecaEngine.Animation;

namespace DecaEngine.Editor;

/// <summary>Binds <see cref="HumanoidAvatar"/> slots to the selected rig's joints.</summary>
// Edits stay in a working copy until Save: the mapping is shared by every character of the model.
public class HumanoidWindow : ImGuiDockingWindow
{
	private readonly PrefabSceneViewport _viewport;

	private HumanoidAvatar _avatar = new();
	private string _modelPath = string.Empty;
	private PreparedSkeleton? _skeleton;

	private List<HumanoidIssue> _issues = new();
	private bool _dirty;
	private string _status = string.Empty;

	public HumanoidWindow(string title, PrefabSceneViewport viewport, ImGuiRender imGuiRender)
		: base(title, imGuiRender)
	{
		_viewport = viewport;
	}

	protected override void OnRender(uint dockId)
	{
		var (skeleton, modelPath, name) = _viewport.SelectedSkinnedModel;

		// Cleared each frame and re-set by hover below, else it sticks to a joint the mouse left.
		_viewport.HighlightJoint = string.Empty;

		if (skeleton == null || string.IsNullOrEmpty(modelPath))
		{
			_skeleton = null;
			ImGui.TextDisabled("Select an entity with a skinned model in the scene.");
			ImGui.TextWrapped("Mapping is done against a specific rig, and without a model there is nothing to map.");
			return;
		}

		if (!ReferenceEquals(skeleton, _skeleton) ||
			!string.Equals(modelPath, _modelPath, StringComparison.OrdinalIgnoreCase))
		{
			Load(skeleton, modelPath);
		}

		ImGui.Text($"Model: {name}");
		ImGui.Text($"Bones in rig: {skeleton.JointCount}");

		if (_viewport.IsAvatarAuto(modelPath))
		{
			ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
				"Mapping is automatic - the avatar file has not been saved yet.");
		}

		if (!_avatar.HasReferencePose)
		{
			ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
				"Reference pose not captured - retargeting with this avatar is impossible.");
		}
		else
		{
			var pose = HumanoidReferencePose.Evaluate(_avatar, skeleton);
			var color = pose.LooksLikeTPose
				? new Vector4(0.4f, 0.9f, 0.45f, 1f)
				: new Vector4(1f, 0.75f, 0.25f, 1f);

			ImGui.TextColored(color, $"Reference pose: {_avatar.ReferenceLocals.Count} bones, " +
				$"deviation from T up to {pose.Worst:0.#}°" +
				$"{(pose.Complete ? "" : " (chains are not fully mapped)")}");
		}

		DrawToolbar();
		ImGui.Separator();
		DrawSlots(skeleton);
		ImGui.Separator();
		DrawIssues();
	}

	private void Load(PreparedSkeleton skeleton, string modelPath)
	{
		_skeleton = skeleton;
		_modelPath = modelPath;

		// Same source as PrefabSceneViewport.AvatarFor, so the window shows the live mapping.
		_avatar = (HumanoidAvatarAsset.Load(modelPath) ?? HumanoidAutoMap.Build(skeleton)).Clone();
		_dirty = false;
		_status = string.Empty;

		Revalidate();
	}

	private void DrawToolbar()
	{
		if (ImGui.Button("Auto-map"))
		{
			_avatar = HumanoidAutoMap.Build(_skeleton!);
			_dirty = true;
			_status = "Mapping rebuilt automatically.";
			Revalidate();
		}

		ImGui.SameLine();

		if (ImGui.Button("Swap sides"))
		{
			SwapSides();
			_dirty = true;
			_status = "Left and right sides swapped.";
			Revalidate();
		}

		ImGui.SameLine();

		// Captured from the bind pose: the displayed pose is already the result of a clip.
		if (ImGui.Button("Capture T-pose"))
		{
			HumanoidReferencePose.CaptureFromBind(_avatar, _skeleton!);
			_dirty = true;

			var captured = HumanoidReferencePose.Evaluate(_avatar, _skeleton!);
			_status = $"Reference pose captured from bind: deviation up to {captured.Worst:0.#}° " +
				$"({(captured.LooksLikeTPose ? "looks like a T-pose" : "does NOT look like a T-pose")}).";
		}

		ImGui.SameLine();

		if (ImGui.Button("Clear"))
		{
			_avatar.Clear();
			_dirty = true;
			_status = "Mapping cleared.";
			Revalidate();
		}

		ImGui.SameLine();

		if (ImGui.Button("Reload"))
		{
			Load(_skeleton!, _modelPath);
			_status = "Mapping reloaded from disk.";
		}

		ImGui.SameLine();

		bool fatal = _issues.Exists(issue => issue.Fatal);

		if (fatal)
		{
			ImGui.BeginDisabled();
		}

		if (ImGui.Button("Save"))
		{
			HumanoidAvatarAsset.Save(_avatar, _modelPath);

			// Required: otherwise scene characters keep running on the replaced mapping.
			_viewport.InvalidateAvatar(_modelPath);

			_dirty = false;
			_status = $"Saved: {HumanoidAvatarAsset.PathFor(_modelPath)}";
		}

		if (fatal)
		{
			ImGui.EndDisabled();

			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.35f, 1f), "errors present - saving is blocked");
		}
		else if (_dirty)
		{
			ImGui.SameLine();
			ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "unsaved edits");
		}

		if (_status.Length > 0)
		{
			ImGui.TextDisabled(_status);
		}
	}

	private void DrawSlots(PreparedSkeleton skeleton)
	{
		if (!ImGui.BeginChild("HumanoidSlots", new Vector2(0f, ImGui.GetContentRegionAvail().Y - 140f * _scale),
			ImGuiChildFlags.Borders))
		{
			ImGui.EndChild();
			return;
		}

		foreach (var info in HumanoidBones.All)
		{
			string current = _avatar[info.Bone];
			bool missing = info.Required && current.Length == 0;

			ImGui.PushID((int)info.Bone);

			if (missing)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.35f, 1f));
			}

			ImGui.Text(info.Required ? $"{info.Title} *" : info.Title);

			if (missing)
			{
				ImGui.PopStyleColor();
			}

			ImGui.SameLine(190f * _scale);
			ImGui.SetNextItemWidth(-1f);

			if (ImGui.BeginCombo("##joint", current.Length > 0 ? current : "-"))
			{
				if (ImGui.Selectable("-", current.Length == 0))
				{
					_avatar[info.Bone] = string.Empty;
					_dirty = true;
					Revalidate();
				}

				for (int joint = 0; joint < skeleton.JointCount; joint++)
				{
					string jointName = skeleton.JointNames[joint];

					if (ImGui.Selectable(jointName, string.Equals(jointName, current, StringComparison.Ordinal)))
					{
						_avatar[info.Bone] = jointName;
						_dirty = true;
						Revalidate();
					}

					if (ImGui.IsItemHovered())
					{
						_viewport.HighlightJoint = jointName;
					}
				}

				ImGui.EndCombo();
			}
			else if (ImGui.IsItemHovered() && current.Length > 0)
			{
				_viewport.HighlightJoint = current;
			}

			ImGui.PopID();
		}

		ImGui.EndChild();
	}

	private void DrawIssues()
	{
		if (_issues.Count == 0)
		{
			ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.45f, 1f), "No issues.");
			return;
		}

		ImGui.Text($"Issues: {_issues.Count}");

		foreach (var issue in _issues)
		{
			var color = issue.Fatal ? new Vector4(1f, 0.4f, 0.35f, 1f) : new Vector4(1f, 0.75f, 0.25f, 1f);
			ImGui.TextColored(color, $"{HumanoidBones.Of(issue.Bone).Title}: {issue.Message}");
		}
	}

	private void Revalidate() =>
		_issues = _skeleton != null ? HumanoidValidation.Validate(_avatar, _skeleton) : new List<HumanoidIssue>();

	// Pairs are listed explicitly rather than derived from slot names, which are display-only.
	private void SwapSides()
	{
		(HumanoidBone Left, HumanoidBone Right)[] pairs =
		[
			(HumanoidBone.LeftShoulder, HumanoidBone.RightShoulder),
			(HumanoidBone.LeftUpperArm, HumanoidBone.RightUpperArm),
			(HumanoidBone.LeftLowerArm, HumanoidBone.RightLowerArm),
			(HumanoidBone.LeftHand, HumanoidBone.RightHand),
			(HumanoidBone.LeftUpperLeg, HumanoidBone.RightUpperLeg),
			(HumanoidBone.LeftLowerLeg, HumanoidBone.RightLowerLeg),
			(HumanoidBone.LeftFoot, HumanoidBone.RightFoot),
			(HumanoidBone.LeftToes, HumanoidBone.RightToes),
		];

		foreach (var (left, right) in pairs)
		{
			(_avatar[left], _avatar[right]) = (_avatar[right], _avatar[left]);
		}
	}
}
