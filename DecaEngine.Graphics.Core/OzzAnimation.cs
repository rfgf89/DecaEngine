using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

/// <summary>
/// Обёртка нативного ozz-animation (см. native/DecaOzzShim). Граница проведена по КРУПНЫМ
/// операциям - «просемплируй клип», «сблендь позы», «переведи в модельные матрицы»: выигрыш ozz в
/// том, что поза считается пачками по четыре кости в SIMD-регистре, и поштучный переход границы
/// managed/native съел бы его целиком.
///
/// Переупорядочивание костей, которое ozz делает при сборке скелета, наружу НЕ протекает: шим
/// принимает и отдаёт всё в порядке <see cref="PreparedSkeleton"/>. Поэтому ozz-путь и C#-путь
/// (<see cref="ClipSampler"/>) взаимозаменяемы и дают позу в одном и том же виде.
/// </summary>
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

	/// <summary>Ключ дорожки. Поля - ОТДЕЛЬНЫЕ float-ы, а не Vector3/Vector4: у векторных типов
	/// System.Numerics выравнивание задаётся джитом (они интринсики), и структура рискует разъехаться
	/// с плотно упакованной C-шной. Для полей-скаляров такого риска нет.</summary>
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

	/// <summary>
	/// Загрузился ли нативный ozz. Проверяется ОДИН РАЗ и мягко: шим - опциональная зависимость,
	/// собираемая отдельным CMake-шагом, и его отсутствие обязано означать «работаем C#-семплером»
	/// (<see cref="ClipSampler"/>), а не падение редактора при открытии первой же модели с ригом.
	/// </summary>
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
				// Пробный вызов, а не NativeLibrary.TryLoad: убедиться нужно не только в том, что DLL
				// нашлась, но и в том, что она резолвится (у неё статически влинкован ozz, но CRT -
				// динамический) и экспортирует ожидаемое имя.
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

/// <summary>Рантайм-скелет ozz, собранный из <see cref="PreparedSkeleton"/>. Один на модель -
/// позы (<see cref="OzzPose"/>) на нём заводятся по одной на инстанс.</summary>
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

	/// <summary>Собирает ozz-скелет. null - ozz недоступен или скелет не прошёл валидацию ozz
	/// (например, иерархия оказалась не топологичной); вызывающий падает на C#-семплер.</summary>
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
			// Имена нужны ozz только на время сборки - он копирует их в свой скелет.
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

/// <summary>Рантайм-клип ozz, собранный из <see cref="PreparedAnimation"/>. ozz перепаковывает
/// ключи в свой сжатый формат, поэтому исходный PreparedAnimation после сборки нужен только для
/// имени и длительности.</summary>
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

		// Каждый массив ключей пиннится отдельно: нативной стороне нужен МАССИВ УКАЗАТЕЛЕЙ на них, а
		// собрать его можно только когда все адреса зафиксированы. GCHandle, а не fixed - число
		// дорожек известно лишь в рантайме, и вложенных fixed на него не написать.
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
			// Пустая дорожка: нативная сторона не разыменует указатель, потому что счётчик нулевой,
			// но пиннить массив нулевой длины бессмысленно - GC вернул бы адрес, по которому нечего
			// читать, и это только запутывало бы отладку.
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

/// <summary>
/// Поза одного инстанса на стороне ozz. Держит внутри контекст семплирования с курсорами ключей -
/// ради него позу и нельзя создавать на вызов: именно в курсорах вся скорость ozz на
/// последовательном воспроизведении.
/// </summary>
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

	/// <summary>Семплирует клип. Время - в СЕКУНДАХ; нормализацию в ratio, которого ждёт ozz, делаем
	/// здесь, чтобы соглашение ozz не протекало в вызывающий код.</summary>
	public bool Sample(OzzClip clip, float timeSeconds)
	{
		if (clip == null || clip.Duration <= 0f)
		{
			return false;
		}

		float ratio = Math.Clamp(timeSeconds / clip.Duration, 0f, 1f);
		return Ozz.DecaOzz_SamplePose(_handle, clip.Handle, ratio) != 0;
	}

	/// <summary>Смешивает позы-слои в эту. Веса НЕ нормализуются: ozz сам добирает разницу до
	/// единицы rest-позой, и «нормализация» здесь ломала бы аддитивные сценарии.</summary>
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

	/// <summary>
	/// Смешивает позы-слои с ПОСУСТАВНЫМИ весами (частичный бленд ozz: верх тела играет свой клип,
	/// ноги - базовый). На слой - либо null (вес всюду единица), либо массив по числу костей
	/// скелета В ИСХОДНОМ порядке. Приёмник может совпадать с одним из слоёв: бленд ozz пишет
	/// выход посуставно после чтения всех слоёв того же сустава.
	/// </summary>
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

			// Короткая маска - выход за границу managed-массива УЖЕ В НАТИВНОМ коде: шим читает её
			// по числу костей скелета, и проверять длину обязан управляемый берег.
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

	/// <summary>
	/// Бленд с аддитивными слоями: слой с флагом true обязан содержать ДЕЛЬТУ (см.
	/// <see cref="AdditiveClip"/>) и суммируется ПОВЕРХ результата обычных слоёв, не участвуя в
	/// усреднении весов. Маски по суставам работают и на аддитивных слоях.
	/// </summary>
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

	/// <summary>Выгружает модельные матрицы В ПОРЯДКЕ <see cref="PreparedSkeleton"/> - переупорядочивание
	/// ozz остаётся его внутренним делом (см. шим).</summary>
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

	/// <summary>
	/// Локальные TRS в порядке <see cref="PreparedSkeleton"/> - вход процедурного слоя (spring
	/// bones, ручная правка костей, рэгдолл). Распаковка из SoA делается нативно: SoA - внутренняя
	/// раскладка ozz, и знать о ней C#-стороне незачем.
	/// </summary>
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

	/// <summary>Правленые локальные TRS обратно в позу. После записи модельные матрицы устарели -
	/// вызывающий обязан заново позвать <see cref="LocalToModel"/>.</summary>
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

	/// <summary>Two-bone IK (нога, рука). Требует АКТУАЛЬНЫХ модельных матриц, поэтому порядок
	/// вызова - Sample -> LocalToModel -> TwoBoneIk -> LocalToModel.</summary>
	public bool TwoBoneIk(int startJoint, int midJoint, int endJoint, Vector3 target, Vector3 poleVector,
		Vector3 midAxis, float weight = 1f, float soften = 1f, float twistAngle = 0f)
	{
		return Ozz.DecaOzz_TwoBoneIk(_handle, startJoint, midJoint, endJoint,
			(float*)&target, (float*)&poleVector, (float*)&midAxis, weight, soften, twistAngle) != 0;
	}

	/// <summary>Aim IK: доворот одной кости на цель (голова, торс, ствол оружия).</summary>
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
