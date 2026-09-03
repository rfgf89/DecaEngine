using System;
using System.Numerics;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Skeleton pose in three forms at once: local TRS, model matrices, skinning palette.</summary>
// Arrays are reused across frames: a per-frame allocation for 200 joints is garbage generated
// exactly where the frame must not stall.
public sealed class SkeletonPose
{
	public readonly PreparedSkeleton Skeleton;

	/// <summary>Local TRS relative to the parent; starts at the bind pose.</summary>
	public readonly Transform[] Locals;

	/// <summary>Joint matrices in model space, not world; valid after <see cref="ComputeModelMatrices"/>.</summary>
	public readonly Matrix4x4[] ModelMatrices;

	/// <summary>Skinning palette <c>InverseBind * Model</c>; valid after <see cref="ComputeSkinMatrices"/>.</summary>
	public readonly Matrix4x4[] SkinMatrices;

	public SkeletonPose(PreparedSkeleton skeleton)
	{
		Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));

		int count = skeleton.JointCount;
		Locals = new Transform[count];
		ModelMatrices = new Matrix4x4[count];
		SkinMatrices = new Matrix4x4[count];

		ResetToBind();
	}

	public void ResetToBind() => Skeleton.BindLocals.AsSpan().CopyTo(Locals);

	/// <summary>Local TRS to model matrices.</summary>
	// Single non-recursive pass: joints are topologically ordered, so parents are done first.
	public void ComputeModelMatrices()
	{
		var parents = Skeleton.Parents;

		for (int i = 0; i < Locals.Length; i++)
		{
			ref var local = ref Locals[i];
			var matrix = Matrix4x4.CreateScale(local.scale)
				* Matrix4x4.CreateFromQuaternion(local.rotation)
				* Matrix4x4.CreateTranslation(local.position);

			int parent = parents[i];
			ModelMatrices[i] = parent < 0 ? matrix : matrix * ModelMatrices[parent];
		}
	}

	/// <summary>Builds the skinning palette.</summary>
	// Factor order follows the engine's row-vector convention: mul(pos, matrix) in HLSL.
	public void ComputeSkinMatrices()
	{
		var inverseBind = Skeleton.InverseBind;

		for (int i = 0; i < SkinMatrices.Length; i++)
		{
			SkinMatrices[i] = inverseBind[i] * ModelMatrices[i];
		}
	}

	/// <summary>Runs both stages, for callers with no procedural layer in between.</summary>
	public void Finish()
	{
		ComputeModelMatrices();
		ComputeSkinMatrices();
	}
}

/// <summary>Last-frame key index per channel of one track, saving a binary search each frame.</summary>
// A hint, not state: a wrong cursor costs one binary search, never a wrong pose.
public struct ClipCursor
{
	public int Translation;
	public int Rotation;
	public int Scale;

	public void Reset() => Translation = Rotation = Scale = 0;
}

/// <summary>Stateless clip sampler: clip plus time to local TRS.</summary>
public static class ClipSampler
{
	/// <summary>Samples a clip into the pose's local TRS; cursors may be an empty span.</summary>
	// Channels are independent: an untouched channel falls back to the bind pose value.
	public static void Sample(PreparedAnimation clip, float time, SkeletonPose pose, Span<ClipCursor> cursors)
	{
		var skeleton = pose.Skeleton;
		int jointCount = Math.Min(skeleton.JointCount, clip.Tracks.Length);

		for (int i = 0; i < jointCount; i++)
		{
			var track = clip.Tracks[i];
			ref var local = ref pose.Locals[i];
			var bind = skeleton.BindLocals[i];

			if (track.IsEmpty)
			{
				local = bind;
				continue;
			}

			ref var cursor = ref i < cursors.Length ? ref cursors[i] : ref Unused;

			local.position = track.TranslationTimes.Length > 0
				? SampleVector(track.TranslationTimes, track.Translations, time, ref cursor.Translation)
				: bind.position;

			local.rotation = track.RotationTimes.Length > 0
				? SampleQuaternion(track.RotationTimes, track.Rotations, time, ref cursor.Rotation)
				: bind.rotation;

			local.scale = track.ScaleTimes.Length > 0
				? SampleVector(track.ScaleTimes, track.Scales, time, ref cursor.Scale)
				: bind.scale;
		}

		// Joints past the clip's tracks reset to bind, else a clip switch strands them last frame.
		for (int i = jointCount; i < skeleton.JointCount; i++)
		{
			pose.Locals[i] = skeleton.BindLocals[i];
		}
	}

	// Scratch cursor for joints the caller did not allocate one for; the ref ternary above needs it.
	[ThreadStatic]
	private static ClipCursor Unused;

	// Time outside the track clamps to the edge key; wrapping is the player's job, not the sampler's.
	private static Vector3 SampleVector(float[] times, Vector3[] values, float time, ref int cursor)
	{
		int index = FindKey(times, time, ref cursor);
		if (index < 0)
		{
			return values[0];
		}

		if (index >= times.Length - 1)
		{
			return values[^1];
		}

		float t = Fraction(times[index], times[index + 1], time);
		return Vector3.Lerp(values[index], values[index + 1], t);
	}

	private static Quaternion SampleQuaternion(float[] times, Quaternion[] values, float time, ref int cursor)
	{
		int index = FindKey(times, time, ref cursor);
		if (index < 0)
		{
			return values[0];
		}

		if (index >= times.Length - 1)
		{
			return values[^1];
		}

		var from = values[index];
		var to = values[index + 1];

		// Shortest arc: q and -q are the same rotation, but Slerp between them takes the long way.
		if (Quaternion.Dot(from, to) < 0f)
		{
			to = -to;
		}

		float t = Fraction(times[index], times[index + 1], time);
		return Quaternion.Normalize(Quaternion.Slerp(from, to, t));
	}

	private static float Fraction(float from, float to, float time)
	{
		float span = to - from;
		// Coincident key times are intentional step keys; dividing by zero would spread NaN.
		return span > 1e-6f ? Math.Clamp((time - from) / span, 0f, 1f) : 0f;
	}

	// Index of the key at or before time, -1 before the first key; cursor hint, else binary search.
	private static int FindKey(float[] times, float time, ref int cursor)
	{
		if (time < times[0])
		{
			cursor = 0;
			return -1;
		}

		int hint = Math.Clamp(cursor, 0, times.Length - 1);

		if (times[hint] <= time)
		{
			if (hint == times.Length - 1 || time < times[hint + 1])
			{
				cursor = hint;
				return hint;
			}

			// Next interval: the common case when playing forward.
			if (hint + 1 == times.Length - 1 || (hint + 2 < times.Length && time < times[hint + 2]))
			{
				cursor = hint + 1;
				return hint + 1;
			}
		}

		int low = 0;
		int high = times.Length - 1;
		while (low < high)
		{
			int mid = (low + high + 1) / 2;
			if (times[mid] <= time)
			{
				low = mid;
			}
			else
			{
				high = mid - 1;
			}
		}

		cursor = low;
		return low;
	}
}

/// <summary>Single-clip player: time, speed, looping and the per-track cursors.</summary>
public sealed class AnimationPlayer
{
	private ClipCursor[] _cursors = [];
	private PreparedAnimation _clip;

	public PreparedAnimation Clip
	{
		get => _clip;
		set
		{
			if (ReferenceEquals(_clip, value))
			{
				return;
			}

			_clip = value;
			Time = 0f;

			ResetCursors();
		}
	}

	public float Time;
	public float Speed = 1f;
	public bool Loop = true;

	/// <summary>Non-looping clip reached its end; the player holds the last frame.</summary>
	public bool Finished { get; private set; }

	public void Advance(float deltaSeconds)
	{
		if (_clip == null || _clip.Duration <= 0f)
		{
			return;
		}

		Time += deltaSeconds * Speed;

		if (Loop)
		{
			// Wrap via floor, not %: C# remainder is negative for negative speed.
			Time -= _clip.Duration * MathF.Floor(Time / _clip.Duration);
			Finished = false;
		}
		else if (Time >= _clip.Duration)
		{
			Time = _clip.Duration;
			Finished = true;
		}
		else if (Time < 0f)
		{
			Time = 0f;
			Finished = true;
		}
	}

	/// <summary>Samples the clip into the pose and finishes matrices; falls back to the bind pose.</summary>
	public void Apply(SkeletonPose pose)
	{
		if (_clip == null)
		{
			pose.ResetToBind();
			pose.Finish();
			return;
		}

		if (_cursors.Length < pose.Skeleton.JointCount)
		{
			_cursors = new ClipCursor[pose.Skeleton.JointCount];
		}

		ClipSampler.Sample(_clip, Time, pose, _cursors);
		pose.Finish();
	}

	/// <summary>Sets the time directly and resets the cursors.</summary>
	public void Seek(float time)
	{
		Time = time;
		Finished = false;
		ResetCursors();
	}

	private void ResetCursors() => Array.Clear(_cursors);
}
