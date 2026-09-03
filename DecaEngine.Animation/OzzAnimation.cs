using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;

namespace DecaEngine.Animation;

/// <summary>Interop with the native ozz-animation shim (native/DecaOzzShim).</summary>
// The boundary is drawn at coarse operations only: ozz poses four bones per SIMD register, and a
// per-joint managed/native crossing would eat the whole win. The joint reordering ozz does
// internally does not leak out - everything crosses in PreparedSkeleton order.
public static unsafe class Ozz
{
	private const string Library = "DecaOzzShim";

	[StructLayout(LayoutKind.Sequential)]
	internal struct JointDesc
	{
		public IntPtr Name;
		public int Parent;
		public Vector3 Translation;
		public Quaternion Rotation;
		public Vector3 Scale;
	}

	// Separate floats, not Vector3/Vector4: JIT-chosen alignment of the intrinsic vector types can
	// diverge from the packed C struct.
	[StructLayout(LayoutKind.Sequential)]
	internal struct Key
	{
		public float Time;
		public float X, Y, Z, W;
	}

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr DecaOzz_BuildSkeleton(JointDesc* joints, int jointCount);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void DecaOzz_ReleaseSkeleton(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_SkeletonJointCount(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr DecaOzz_BuildAnimation(IntPtr skeleton, byte* name, float duration,
		int trackCount, Key** translationKeys, int* translationCounts, Key** rotationKeys, int* rotationCounts,
		Key** scaleKeys, int* scaleCounts);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void DecaOzz_ReleaseAnimation(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern float DecaOzz_AnimationDuration(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern IntPtr DecaOzz_CreatePose(IntPtr skeleton);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern void DecaOzz_ReleasePose(IntPtr handle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_SamplePose(IntPtr pose, IntPtr animation, float ratio);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_BlendPoses(IntPtr destination, IntPtr* layers, float* weights, int layerCount);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_BlendPosesMasked(IntPtr destination, IntPtr* layers, float* weights,
		float** jointWeights, int layerCount);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_BlendPosesLayered(IntPtr destination, IntPtr* layers, float* weights,
		float** jointWeights, int* additiveFlags, int layerCount);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_LocalToModel(IntPtr pose);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_ReadModelMatrices(IntPtr pose, float* output, int jointCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_ReadLocalTransforms(IntPtr pose, Transform* output, int jointCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_WriteLocalTransforms(IntPtr pose, Transform* input, int jointCount);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_TwoBoneIk(IntPtr pose, int startJoint, int midJoint, int endJoint,
		float* target, float* poleVector, float* midAxis, float weight, float soften, float twistAngle);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	internal static extern int DecaOzz_AimIk(IntPtr pose, int joint, float* target, float* forward, float* up,
		float* poleVector, float weight);

	private static bool? _available;

	/// <summary>Whether the native ozz shim loaded; absence falls back to the C# ClipSampler.</summary>
	public static bool IsAvailable
	{
		get
		{
			if (_available.HasValue)
			{
				return _available.Value;
			}

			try
			{
				// A probe call, not NativeLibrary.TryLoad: this also proves the CRT resolves and the
				// expected export exists.
				DecaOzz_SkeletonJointCount(IntPtr.Zero);
				_available = true;
			}
			catch (DllNotFoundException)
			{
				_available = false;
			}
			catch (EntryPointNotFoundException)
			{
				_available = false;
			}

			return _available.Value;
		}
	}
}

/// <summary>Runtime ozz skeleton, one per model; poses are created per instance from it.</summary>
public sealed unsafe class OzzSkeleton : IDisposable
{
	internal IntPtr Handle { get; private set; }

	public PreparedSkeleton Source { get; }

	public int JointCount => Source.JointCount;

	private OzzSkeleton(IntPtr handle, PreparedSkeleton source)
	{
		Handle = handle;
		Source = source;
	}

	/// <summary>Builds an ozz skeleton; null when ozz is unavailable or the hierarchy is invalid.</summary>
	public static OzzSkeleton Build(PreparedSkeleton skeleton)
	{
		if (skeleton == null || skeleton.JointCount == 0 || !Ozz.IsAvailable)
		{
			return null;
		}

		var names = new IntPtr[skeleton.JointCount];
		try
		{
			var descriptors = new Ozz.JointDesc[skeleton.JointCount];
			for (int i = 0; i < descriptors.Length; i++)
			{
				names[i] = Marshal.StringToHGlobalAnsi(skeleton.JointNames[i] ?? string.Empty);
				var bind = skeleton.BindLocals[i];

				descriptors[i] = new Ozz.JointDesc
				{
					Name = names[i],
					Parent = skeleton.Parents[i],
					Translation = bind.position,
					Rotation = bind.rotation,
					Scale = bind.scale,
				};
			}

			IntPtr handle;
			fixed (Ozz.JointDesc* ptr = descriptors)
			{
				handle = Ozz.DecaOzz_BuildSkeleton(ptr, descriptors.Length);
			}

			return handle != IntPtr.Zero ? new OzzSkeleton(handle, skeleton) : null;
		}
		finally
		{
			// ozz copies the names into its own skeleton, so they are only needed during the build.
			foreach (var name in names)
			{
				if (name != IntPtr.Zero)
				{
					Marshal.FreeHGlobal(name);
				}
			}
		}
	}

	public void Dispose()
	{
		if (Handle != IntPtr.Zero)
		{
			Ozz.DecaOzz_ReleaseSkeleton(Handle);
			Handle = IntPtr.Zero;
		}
	}
}

/// <summary>Runtime ozz clip built from a PreparedAnimation, which it repacks and no longer needs.</summary>
public sealed unsafe class OzzClip : IDisposable
{
	internal IntPtr Handle { get; private set; }

	public string Name { get; }
	public float Duration { get; }

	private OzzClip(IntPtr handle, string name, float duration)
	{
		Handle = handle;
		Name = name;
		Duration = duration;
	}

	public static OzzClip Build(OzzSkeleton skeleton, PreparedAnimation clip)
	{
		if (skeleton == null || clip == null || clip.Duration <= 0f || !Ozz.IsAvailable)
		{
			return null;
		}

		int trackCount = clip.Tracks.Length;

		var translations = new Ozz.Key[trackCount][];
		var rotations = new Ozz.Key[trackCount][];
		var scales = new Ozz.Key[trackCount][];

		var translationCounts = new int[trackCount];
		var rotationCounts = new int[trackCount];
		var scaleCounts = new int[trackCount];

		for (int i = 0; i < trackCount; i++)
		{
			var track = clip.Tracks[i];

			translations[i] = PackVector(track.TranslationTimes, track.Translations);
			rotations[i] = PackQuaternion(track.RotationTimes, track.Rotations);
			scales[i] = PackVector(track.ScaleTimes, track.Scales);

			translationCounts[i] = translations[i].Length;
			rotationCounts[i] = rotations[i].Length;
			scaleCounts[i] = scales[i].Length;
		}

		// Native side wants an array of pointers, so every key array must be pinned first. GCHandle
		// rather than fixed: the track count is only known at runtime.
		var handles = new List<GCHandle>(trackCount * 3);
		try
		{
			var translationPtrs = new IntPtr[trackCount];
			var rotationPtrs = new IntPtr[trackCount];
			var scalePtrs = new IntPtr[trackCount];

			for (int i = 0; i < trackCount; i++)
			{
				translationPtrs[i] = Pin(handles, translations[i]);
				rotationPtrs[i] = Pin(handles, rotations[i]);
				scalePtrs[i] = Pin(handles, scales[i]);
			}

			var nameBytes = System.Text.Encoding.UTF8.GetBytes((clip.Name ?? string.Empty) + "\0");

			IntPtr handle;
			fixed (byte* namePtr = nameBytes)
			fixed (IntPtr* tPtr = translationPtrs)
			fixed (IntPtr* rPtr = rotationPtrs)
			fixed (IntPtr* sPtr = scalePtrs)
			fixed (int* tCount = translationCounts)
			fixed (int* rCount = rotationCounts)
			fixed (int* sCount = scaleCounts)
			{
				handle = Ozz.DecaOzz_BuildAnimation(skeleton.Handle, namePtr, clip.Duration, trackCount,
					(Ozz.Key**)tPtr, tCount, (Ozz.Key**)rPtr, rCount, (Ozz.Key**)sPtr, sCount);
			}

			return handle != IntPtr.Zero ? new OzzClip(handle, clip.Name, clip.Duration) : null;
		}
		finally
		{
			foreach (var pinned in handles)
			{
				pinned.Free();
			}
		}
	}

	private static IntPtr Pin(List<GCHandle> handles, Ozz.Key[] keys)
	{
		if (keys.Length == 0)
		{
			// Empty track: the zero count keeps native from dereferencing this.
			return IntPtr.Zero;
		}

		var handle = GCHandle.Alloc(keys, GCHandleType.Pinned);
		handles.Add(handle);
		return handle.AddrOfPinnedObject();
	}

	private static Ozz.Key[] PackVector(float[] times, Vector3[] values)
	{
		int count = Math.Min(times.Length, values.Length);
		var keys = new Ozz.Key[count];

		for (int i = 0; i < count; i++)
		{
			keys[i] = new Ozz.Key { Time = times[i], X = values[i].X, Y = values[i].Y, Z = values[i].Z };
		}

		return keys;
	}

	private static Ozz.Key[] PackQuaternion(float[] times, Quaternion[] values)
	{
		int count = Math.Min(times.Length, values.Length);
		var keys = new Ozz.Key[count];

		for (int i = 0; i < count; i++)
		{
			keys[i] = new Ozz.Key
			{
				Time = times[i],
				X = values[i].X, Y = values[i].Y, Z = values[i].Z, W = values[i].W,
			};
		}

		return keys;
	}

	public void Dispose()
	{
		if (Handle != IntPtr.Zero)
		{
			Ozz.DecaOzz_ReleaseAnimation(Handle);
			Handle = IntPtr.Zero;
		}
	}
}

/// <summary>One instance's pose on the ozz side.</summary>
// Holds the sampling context with key cursors, so it must be kept alive across frames rather than
// created per call - the cursors are where ozz's sequential-playback speed comes from.
public sealed unsafe class OzzPose : IDisposable
{
	private IntPtr _handle;

	public OzzSkeleton Skeleton { get; }

	private OzzPose(IntPtr handle, OzzSkeleton skeleton)
	{
		_handle = handle;
		Skeleton = skeleton;
	}

	public static OzzPose Create(OzzSkeleton skeleton)
	{
		if (skeleton == null)
		{
			return null;
		}

		var handle = Ozz.DecaOzz_CreatePose(skeleton.Handle);
		return handle != IntPtr.Zero ? new OzzPose(handle, skeleton) : null;
	}

	/// <summary>Samples a clip; time is in seconds, normalized to ozz's ratio here.</summary>
	public bool Sample(OzzClip clip, float timeSeconds)
	{
		if (clip == null || clip.Duration <= 0f)
		{
			return false;
		}

		float ratio = Math.Clamp(timeSeconds / clip.Duration, 0f, 1f);
		return Ozz.DecaOzz_SamplePose(_handle, clip.Handle, ratio) != 0;
	}

	/// <summary>Blends layer poses into this one.</summary>
	// Weights are NOT normalized: ozz fills the remainder to 1 with the rest pose.
	public bool Blend(ReadOnlySpan<OzzPose> layers, ReadOnlySpan<float> weights)
	{
		if (layers.Length == 0 || layers.Length != weights.Length)
		{
			return false;
		}

		var handles = new IntPtr[layers.Length];
		for (int i = 0; i < layers.Length; i++)
		{
			handles[i] = layers[i]._handle;
		}

		fixed (IntPtr* handlePtr = handles)
		fixed (float* weightPtr = weights)
		{
			return Ozz.DecaOzz_BlendPoses(_handle, handlePtr, weightPtr, layers.Length) != 0;
		}
	}

	/// <summary>Blends layer poses with per-joint weights (partial blend).</summary>
	// Per layer: null (weight 1 everywhere) or an array of JointCount in PreparedSkeleton order.
	// The destination may alias a layer: ozz writes each joint after reading all layers of it.
	public bool Blend(ReadOnlySpan<OzzPose> layers, ReadOnlySpan<float> weights,
		ReadOnlySpan<float[]?> jointWeights)
	{
		if (layers.Length == 0 || layers.Length != weights.Length || layers.Length != jointWeights.Length)
		{
			return false;
		}

		var handles = new IntPtr[layers.Length];
		for (int i = 0; i < layers.Length; i++)
		{
			handles[i] = layers[i]._handle;

			// A short mask overruns in native code: the shim reads JointCount entries regardless.
			if (jointWeights[i] != null && jointWeights[i]!.Length < Skeleton.JointCount)
			{
				return false;
			}
		}

		var pins = new System.Runtime.InteropServices.GCHandle[layers.Length];
		var maskPointers = new IntPtr[layers.Length];

		try
		{
			for (int i = 0; i < layers.Length; i++)
			{
				if (jointWeights[i] != null)
				{
					pins[i] = System.Runtime.InteropServices.GCHandle.Alloc(jointWeights[i],
						System.Runtime.InteropServices.GCHandleType.Pinned);
					maskPointers[i] = pins[i].AddrOfPinnedObject();
				}
			}

			fixed (IntPtr* handlePtr = handles)
			fixed (IntPtr* maskPtr = maskPointers)
			fixed (float* weightPtr = weights)
			{
				return Ozz.DecaOzz_BlendPosesMasked(_handle, handlePtr, weightPtr, (float**)maskPtr,
					layers.Length) != 0;
			}
		}
		finally
		{
			for (int i = 0; i < pins.Length; i++)
			{
				if (pins[i].IsAllocated)
				{
					pins[i].Free();
				}
			}
		}
	}

	/// <summary>Blend with additive layers.</summary>
	// A layer flagged additive must hold a DELTA pose; it is added on top of the normal layers and
	// takes no part in weight averaging.
	public bool BlendLayered(ReadOnlySpan<OzzPose> layers, ReadOnlySpan<float> weights,
		ReadOnlySpan<float[]?> jointWeights, ReadOnlySpan<bool> additive)
	{
		if (layers.Length == 0 || layers.Length != weights.Length ||
			layers.Length != jointWeights.Length || layers.Length != additive.Length)
		{
			return false;
		}

		var handles = new IntPtr[layers.Length];
		var flags = new int[layers.Length];

		for (int i = 0; i < layers.Length; i++)
		{
			handles[i] = layers[i]._handle;
			flags[i] = additive[i] ? 1 : 0;

			if (jointWeights[i] != null && jointWeights[i]!.Length < Skeleton.JointCount)
			{
				return false;
			}
		}

		var pins = new System.Runtime.InteropServices.GCHandle[layers.Length];
		var maskPointers = new IntPtr[layers.Length];

		try
		{
			for (int i = 0; i < layers.Length; i++)
			{
				if (jointWeights[i] != null)
				{
					pins[i] = System.Runtime.InteropServices.GCHandle.Alloc(jointWeights[i],
						System.Runtime.InteropServices.GCHandleType.Pinned);
					maskPointers[i] = pins[i].AddrOfPinnedObject();
				}
			}

			fixed (IntPtr* handlePtr = handles)
			fixed (IntPtr* maskPtr = maskPointers)
			fixed (float* weightPtr = weights)
			fixed (int* flagPtr = flags)
			{
				return Ozz.DecaOzz_BlendPosesLayered(_handle, handlePtr, weightPtr, (float**)maskPtr,
					flagPtr, layers.Length) != 0;
			}
		}
		finally
		{
			for (int i = 0; i < pins.Length; i++)
			{
				if (pins[i].IsAllocated)
				{
					pins[i].Free();
				}
			}
		}
	}

	public bool LocalToModel() => Ozz.DecaOzz_LocalToModel(_handle) != 0;

	/// <summary>Reads model matrices in PreparedSkeleton joint order.</summary>
	public bool ReadModelMatrices(Matrix4x4[] destination)
	{
		if (destination == null || destination.Length < Skeleton.JointCount)
		{
			return false;
		}

		fixed (Matrix4x4* ptr = destination)
		{
			return Ozz.DecaOzz_ReadModelMatrices(_handle, (float*)ptr, destination.Length) != 0;
		}
	}

	/// <summary>Reads local TRS in PreparedSkeleton joint order; input of the procedural layer.</summary>
	public bool ReadLocalTransforms(Transform[] destination)
	{
		if (destination == null || destination.Length < Skeleton.JointCount)
		{
			return false;
		}

		fixed (Transform* ptr = destination)
		{
			return Ozz.DecaOzz_ReadLocalTransforms(_handle, ptr, destination.Length) != 0;
		}
	}

	/// <summary>Writes edited local TRS back into the pose.</summary>
	// Model matrices are stale afterwards: the caller must call LocalToModel again.
	public bool WriteLocalTransforms(Transform[] source)
	{
		if (source == null || source.Length < Skeleton.JointCount)
		{
			return false;
		}

		fixed (Transform* ptr = source)
		{
			return Ozz.DecaOzz_WriteLocalTransforms(_handle, ptr, source.Length) != 0;
		}
	}

	/// <summary>Two-bone IK (leg, arm).</summary>
	// Needs up-to-date model matrices: Sample -> LocalToModel -> TwoBoneIk -> LocalToModel.
	public bool TwoBoneIk(int startJoint, int midJoint, int endJoint, Vector3 target, Vector3 poleVector,
		Vector3 midAxis, float weight = 1f, float soften = 1f, float twistAngle = 0f)
	{
		return Ozz.DecaOzz_TwoBoneIk(_handle, startJoint, midJoint, endJoint,
			(float*)&target, (float*)&poleVector, (float*)&midAxis, weight, soften, twistAngle) != 0;
	}

	/// <summary>Aim IK: turns a single bone toward a target (head, torso, weapon barrel).</summary>
	public bool AimIk(int joint, Vector3 target, Vector3 forward, Vector3 up, Vector3 poleVector,
		float weight = 1f)
	{
		return Ozz.DecaOzz_AimIk(_handle, joint, (float*)&target, (float*)&forward, (float*)&up,
			(float*)&poleVector, weight) != 0;
	}

	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			Ozz.DecaOzz_ReleasePose(_handle);
			_handle = IntPtr.Zero;
		}
	}
}
