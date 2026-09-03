using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Animation;

namespace DecaEngine.Probes;

/// <summary>Numeric quality report for animation clips (DECA_PROBE_ANIMREPORT=1).</summary>
public static class AnimationReportProbe
{
	private const int Samples = 60;

	public static void Run(ModelLoader model)
	{
		var skeleton = model.Skeleton;

		if (skeleton == null || model.Animations.Count == 0)
		{
			Console.WriteLine("[probe] animreport: no skeleton or clips - nothing to report");
			return;
		}

		var avatar = HumanoidAvatarAsset.Load(ModelPathHint) ?? HumanoidAutoMap.Build(skeleton);
		var slots = avatar.Resolve(skeleton);

		foreach (var clip in model.Animations)
		{
			Report(skeleton, avatar, slots, clip);
		}
	}

	/// <summary>Model path, when known, so a saved avatar wins over the auto-mapped one.</summary>
	public static string ModelPathHint = string.Empty;

	private static void Report(PreparedSkeleton skeleton, HumanoidAvatar avatar, int[] slots,
		PreparedAnimation clip)
	{
		var pose = new SkeletonPose(skeleton);
		var player = new AnimationPlayer { Clip = clip, Loop = true, Speed = 1f };

		float duration = MathF.Max(clip.Duration, 1e-4f);

		var positions = new Vector3[Samples][];
		for (int i = 0; i < Samples; i++)
		{
			player.Time = duration * i / Samples;
			player.Apply(pose);

			var frame = new Vector3[skeleton.JointCount];
			for (int j = 0; j < skeleton.JointCount; j++)
			{
				frame[j] = pose.ModelMatrices[j].Translation;
			}

			positions[i] = frame;
		}

		Console.WriteLine($"[probe] animreport: clip '{clip.Name}', {duration:0.###} s, " +
			$"samples {Samples}, bones {skeleton.JointCount}");

		ReportLoop(positions, skeleton, clip.Name);
		ReportRoot(positions, slots, clip.Name);

		// Prefer the toe: the ankle travels a foot length during roll and fakes sliding.
		ReportFoot(positions, slots, HumanoidBone.LeftToes, HumanoidBone.LeftFoot, "foot L", clip.Name);
		ReportFoot(positions, slots, HumanoidBone.RightToes, HumanoidBone.RightFoot, "foot R", clip.Name);
		ReportFoot(positions, slots, HumanoidBone.LeftHand, HumanoidBone.LeftHand, "hand L", clip.Name);
		ReportFoot(positions, slots, HumanoidBone.RightHand, HumanoidBone.RightHand, "hand R", clip.Name);

		ReportSymmetry(positions, slots, clip.Name);
	}

	// Loop closure, measured as a fraction of pose extent: model scale is arbitrary.
	private static void ReportLoop(Vector3[][] positions, PreparedSkeleton skeleton, string clipName)
	{
		float extent = Extent(positions);
		float worst = 0f;
		int worstJoint = 0;

		for (int j = 0; j < positions[0].Length; j++)
		{
			float delta = Vector3.Distance(positions[0][j], positions[^1][j]);

			// Samples run at i/Samples, so the last one is one step before the clip end.
			if (delta > worst)
			{
				worst = delta;
				worstJoint = j;
			}
		}

		float step = StepMotion(positions);
		float relative = extent > 1e-6f ? worst / extent : 0f;

		// 3% of pose extent: seamless reference clips measure 0.5..2%.
		Console.WriteLine($"[probe] animreport [{clipName}]: loop closure - worst gap " +
			$"{worst:0.###} ({relative * 100f:0.#}% of extent) at '{skeleton.JointNames[worstJoint]}', " +
			$"motion per step {step:0.###} {(relative <= 0.03f ? "OK" : "LOOP NOT CLOSED")}");
	}

	// Vertical hip travel: zero means the character glides on rails.
	private static void ReportRoot(Vector3[][] positions, int[] slots, string clipName)
	{
		int hips = slots[(int)HumanoidBone.Hips];
		if (hips < 0)
		{
			return;
		}

		float min = float.MaxValue;
		float max = float.MinValue;

		foreach (var frame in positions)
		{
			min = MathF.Min(min, frame[hips].Y);
			max = MathF.Max(max, frame[hips].Y);
		}

		Console.WriteLine($"[probe] animreport [{clipName}]: hips - vertical travel {max - min:0.###} " +
			$"(y {min:0.##}..{max:0.##})");
	}

	// Foot sliding: horizontal travel while the limb is near its lowest height.
	private static void ReportFoot(Vector3[][] positions, int[] slots, HumanoidBone preferred,
		HumanoidBone fallback, string title, string clipName)
	{
		int joint = slots[(int)preferred];
		if (joint < 0)
		{
			joint = slots[(int)fallback];
		}

		if (joint < 0)
		{
			return;
		}

		float min = float.MaxValue;
		float max = float.MinValue;

		foreach (var frame in positions)
		{
			min = MathF.Min(min, frame[joint].Y);
			max = MathF.Max(max, frame[joint].Y);
		}

		// Contact threshold is the lowest fifth of limb travel: a ratio, since units vary.
		float lift = max - min;
		float threshold = min + MathF.Max(lift * 0.2f, 1e-5f);

		float slide = 0f;
		int contacts = 0;

		for (int i = 1; i < positions.Length; i++)
		{
			if (positions[i][joint].Y > threshold || positions[i - 1][joint].Y > threshold)
			{
				continue;
			}

			var a = positions[i - 1][joint];
			var b = positions[i][joint];

			slide += MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
			contacts++;
		}

		float verdictBase = MathF.Max(lift, 1e-6f);

		// No verdict: slide is in MODEL space, so root motion legitimately inflates it.
		Console.WriteLine($"[probe] animreport [{clipName}]: {title} - lift {lift:0.###}, " +
			$"contact {contacts}/{positions.Length} frames, slide {slide:0.###} " +
			$"({slide / verdictBase * 100f:0.#}% of lift){(contacts == 0 ? " NO CONTACT" : "")}");
	}

	// Gait phase: the shift at which left foot height best matches right foot height.
	private static void ReportSymmetry(Vector3[][] positions, int[] slots, string clipName)
	{
		int left = slots[(int)HumanoidBone.LeftFoot];
		int right = slots[(int)HumanoidBone.RightFoot];

		if (left < 0 || right < 0)
		{
			return;
		}

		float best = float.MaxValue;
		int bestShift = 0;

		for (int shift = 0; shift < positions.Length; shift++)
		{
			float error = 0f;

			for (int i = 0; i < positions.Length; i++)
			{
				float a = positions[i][left].Y;
				float b = positions[(i + shift) % positions.Length][right].Y;
				error += MathF.Abs(a - b);
			}

			if (error < best)
			{
				best = error;
				bestShift = shift;
			}
		}

		float phase = bestShift / (float)positions.Length;

		// No verdict: antiphase is a biped trait; quadruped gaits give other shifts.
		Console.WriteLine($"[probe] animreport [{clipName}]: leg phase - shift {phase:0.##} of the period " +
			$"({(MathF.Abs(phase - 0.5f) < 0.15f ? "antiphase - walk" : "in sync - jump/gallop/pace")})");
	}

	private static float Extent(Vector3[][] positions)
	{
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);

		foreach (var frame in positions)
		{
			foreach (var p in frame)
			{
				min = Vector3.Min(min, p);
				max = Vector3.Max(max, p);
			}
		}

		return (max - min).Length();
	}

	// Mean pose motion per sample step - the scale the loop gap is judged against.
	private static float StepMotion(Vector3[][] positions)
	{
		float sum = 0f;
		int count = 0;

		for (int i = 1; i < positions.Length; i++)
		{
			for (int j = 0; j < positions[i].Length; j++)
			{
				sum += Vector3.Distance(positions[i - 1][j], positions[i][j]);
				count++;
			}
		}

		return count > 0 ? sum / count : 0f;
	}
}
