using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using Diligent;
using UnsafeCollections.Collections.Unsafe;
// Прямая работа с нативным контекстом - состояния здесь Diligent-овские, не из ICommandBuffer.
using ResourceState = Diligent.ResourceState;

namespace DecaEngine.Graphics.Diligent;

public static unsafe class DiligentGraphicsUtility
{
	// Временная диагностика (DECA_UPLOAD_DEBUG=1): лог крупных UpdateBuffer-аплоадов - на D3D12
	// каждый такой вызов берёт staging из динамического хипа контекста, и повторяющийся большой
	// аплоад каждый кадр раздувает хип (симптом: "Created dynamic memory page" каждый кадр).
	private static readonly bool UploadDebugEnabled = Environment.GetEnvironmentVariable("DECA_UPLOAD_DEBUG") == "1";
	private const uint UploadDebugThreshold = 1u * 1024 * 1024;

	public static void LogLargeUpload(IBuffer buffer, uint size, string origin)
	{
		if (!UploadDebugEnabled || size < UploadDebugThreshold || buffer == null) return;
		Console.WriteLine($"[upload-debug] {origin}: '{buffer.GetDesc().Name}' {size / (1024.0 * 1024.0):F1} MB");
	}

	public static ShaderType AccessToShaderType(HandleAccess access)
	{
		ShaderType shaderType = ShaderType.Unknown;
		if ((access & HandleAccess.Compute) != 0) shaderType |= ShaderType.Compute;
		if ((access & HandleAccess.Pixel) != 0) shaderType |= ShaderType.Pixel;
		if ((access & HandleAccess.Vertex) != 0) shaderType |= ShaderType.Vertex;
		return shaderType;
	}

	public static void UpdateVariableDesc(ConcurrentDictionary<string, ShaderResourceVariableDesc> variablesDesc, string name, ShaderType shaderStages, ref bool isDirty)
	{
		if (!variablesDesc.TryGetValue(name, out var existingDesc) || (existingDesc.ShaderStages & shaderStages) != shaderStages)
		{
			variablesDesc[name] = new ShaderResourceVariableDesc()
			{
				Name = name,
				ShaderStages = (existingDesc.ShaderStages | shaderStages),
				Type = ShaderResourceVariableType.Mutable,
				Flags = ShaderVariableFlags.None,
			};
			isDirty = true;
		}
	}

	public static void UpdatePendingResources(bool isDirty, ConcurrentDictionary<string, List<IShaderResourceVariable>> variables, string name, Dictionary<string, IDeviceObject> pendingResources)
	{
		if (!isDirty && variables.TryGetValue(name, out var vars))
		{
			foreach (var variable in vars)
				variable.Set(pendingResources[name], SetShaderResourceFlags.AllowOverwrite);
		}
	}

	public static void UploadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, UnsafeArray* array, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null || array == null)
		{
			return;
		}

		// Для буферов (Structured, UAV, SRV), которые не являются чистыми UniformBuffer,
		// UpdateBuffer работает надежнее и не вызывает проблем с Direct3D12 Map/Unmap ограничениями.
		if ((buffer.GetDesc().BindFlags & BindFlags.UniformBuffer) == 0 || buffer.GetDesc().Usage != Usage.Dynamic)
		{
			uint size = (uint)(UnsafeArray.GetLength(array) * Unsafe.SizeOf<T>());
			if (size > 0)
			{
				LogLargeUpload(buffer, size, "UploadBufferExt(UnsafeArray)");
				ctx.UpdateBuffer(buffer, 0, size, new IntPtr(UnsafeArray.GetPtr<T>(array, 0)),
					ResourceStateTransitionMode.Transition);
			}

			return;
		}

		var mapPtr = ctx.MapBuffer(buffer, MapType.Write, flags);
		Unsafe.CopyBlock(mapPtr.ToPointer(), UnsafeArray.GetPtr<T>(array, 0),
			(uint)(UnsafeArray.GetLength(array) * Unsafe.SizeOf<T>()));
		ctx.UnmapBuffer(buffer, MapType.Write);
	}

	public static void UploadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, UnsafeList* list, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null || list == null)
		{
			return;
		}

		// Для буферов (Structured, UAV, SRV), которые не являются чистыми UniformBuffer,
		// UpdateBuffer работает надежнее и не вызывает проблем с Direct3D12 Map/Unmap ограничениями.
		if ((buffer.GetDesc().BindFlags & BindFlags.UniformBuffer) == 0 || buffer.GetDesc().Usage != Usage.Dynamic)
		{
			uint size = (uint)(UnsafeList.GetCount(list) * Unsafe.SizeOf<T>());
			if (size > 0)
			{
				LogLargeUpload(buffer, size, "UploadBufferExt(UnsafeList)");
				ctx.UpdateBuffer(buffer, 0, size, new IntPtr(UnsafeList.GetPtr<T>(list, 0)), ResourceStateTransitionMode.Transition);
			}
			return;
		}

		var mapPtr = ctx.MapBuffer(buffer, MapType.Write, flags);
		Unsafe.CopyBlock(mapPtr.ToPointer(), UnsafeList.GetPtr<T>(list, 0), (uint)(UnsafeList.GetCount(list) * Unsafe.SizeOf<T>()));
		ctx.UnmapBuffer(buffer, MapType.Write);
	}

	public static void UploadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, T value, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null)
		{
			return;
		}

		if ((buffer.GetDesc().BindFlags & BindFlags.UniformBuffer) == 0 || buffer.GetDesc().Usage != Usage.Dynamic)
		{
			ctx.UpdateBuffer(buffer, 0, (uint)Unsafe.SizeOf<T>(), new IntPtr(&value), ResourceStateTransitionMode.Transition);
			return;
		}

		IntPtr mapPtr = ctx.MapBuffer(buffer, MapType.Write, flags);
		((T*)mapPtr.ToPointer())[0] = value;
		ctx.UnmapBuffer(buffer, MapType.Write);
	}

	public static void UploadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, T[] value, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null || value.Length == 0)
		{
			return;
		}

		if ((buffer.GetDesc().BindFlags & BindFlags.UniformBuffer) == 0 || buffer.GetDesc().Usage != Usage.Dynamic)
		{
			LogLargeUpload(buffer, (uint)(value.Length * Unsafe.SizeOf<T>()), "UploadBufferExt(T[])");
			fixed (T* ptr = value)
			{
				ctx.UpdateBuffer(buffer, 0, (uint)(value.Length * Unsafe.SizeOf<T>()), new IntPtr(ptr), ResourceStateTransitionMode.Transition);
			}
			return;
		}

		fixed (T* ptr = value)
		{
			IntPtr mapPtr = ctx.MapBuffer(buffer, MapType.Write, flags);
			Unsafe.CopyBlock(mapPtr.ToPointer(), ptr, (uint)(value.Length * Unsafe.SizeOf<T>()));
			ctx.UnmapBuffer(buffer, MapType.Write);
		}
	}
	
	/// <summary>
	/// Reads data from a GPU buffer into an UnsafeArray.
	/// The buffer must have been created with Usage.Staging and CPUAccessFlags.Read.
	/// </summary>
	public static void ReadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, UnsafeArray* array, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null || array == null)
		{
			return;
		}

		var mapPtr = ctx.MapBuffer(buffer, MapType.Read, flags);
		if (mapPtr != IntPtr.Zero)
		{
			try
			{
				Unsafe.CopyBlock(UnsafeArray.GetPtr<T>(array, 0), mapPtr.ToPointer(),
					(uint)(UnsafeArray.GetLength(array) * Unsafe.SizeOf<T>()));
			}
			finally
			{
				ctx.UnmapBuffer(buffer, MapType.Read);
			}
		}
	}

	/// <summary>
	/// Reads data from a GPU buffer into an UnsafeList.
	/// The buffer must have been created with Usage.Staging and CPUAccessFlags.Read.
	/// The list must have enough capacity to hold the data.
	/// </summary>
	public static void ReadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, UnsafeList* list, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null || list == null)
		{
			return;
		}

		var mapPtr = ctx.MapBuffer(buffer, MapType.Read, flags);
		if (mapPtr != IntPtr.Zero)
		{
			try
			{
				Unsafe.CopyBlock(UnsafeList.GetPtr<T>(list, 0), mapPtr.ToPointer(),
					(uint)(UnsafeList.GetCount(list) * Unsafe.SizeOf<T>()));
			}
			finally
			{
				ctx.UnmapBuffer(buffer, MapType.Read);
			}
		}
	}

	/// <summary>
	/// Reads data from a GPU buffer into a managed array.
	/// The buffer must have been created with Usage.Staging and CPUAccessFlags.Read.
	/// </summary>
	public static void ReadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, T[] value, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		if (buffer == null || value.Length == 0)
		{
			return;
		}

		var mapPtr = ctx.MapBuffer(buffer, MapType.Read, flags);
		if (mapPtr != IntPtr.Zero)
		{
			try
			{
				fixed (T* ptr = value)
				{
					Unsafe.CopyBlock(ptr, mapPtr.ToPointer(), (uint)(value.Length * Unsafe.SizeOf<T>()));
				}
			}
			finally
			{
				ctx.UnmapBuffer(buffer, MapType.Read);
			}
		}
	}

	/// <summary>
	/// Reads a single value from a GPU buffer.
	/// The buffer must have been created with Usage.Staging and CPUAccessFlags.Read.
	/// </summary>
	public static void ReadBufferExt<T>(this IDeviceContext ctx, IBuffer buffer, out T value, MapFlags flags = MapFlags.DoNotWait) where T : unmanaged
	{
		value = default;
		if (buffer == null)
		{
			return;
		}

		var mapPtr = ctx.MapBuffer(buffer, MapType.Read, flags);
		if (mapPtr != IntPtr.Zero)
		{
			try
			{
				value = Unsafe.Read<T>(mapPtr.ToPointer());
			}
			finally
			{
				ctx.UnmapBuffer(buffer, MapType.Read);
			}
		}
	}
	
	public static void ReadBufferExt<T>(this IDeviceContext ctx, IRenderDevice device, IBuffer sourceBuffer, void* destination, uint sizeInBytes) where T : unmanaged
	{
		if (sourceBuffer == null || destination == null || sizeInBytes == 0)
			return;

		var stagingDesc = new BufferDesc
		{
			Name = "Staging Buffer for Readback",
			Usage = Usage.Staging,
			CPUAccessFlags = CpuAccessFlags.Read,
			Size = sizeInBytes
		};
		using var stagingBuffer = device.CreateBuffer(stagingDesc);

		var originalState = sourceBuffer.GetState();

		ctx.TransitionResourceStates(new[]
		{
			new StateTransitionDesc
			{
				Resource = sourceBuffer,
				OldState = originalState,
				NewState = global::Diligent.ResourceState.CopySource,
				Flags = StateTransitionFlags.UpdateState
			}
		});

		ctx.CopyBuffer(sourceBuffer, 0, ResourceStateTransitionMode.Transition, stagingBuffer, 0, sizeInBytes,
			ResourceStateTransitionMode.Transition);

		ctx.TransitionResourceStates(new[]
		{
			new StateTransitionDesc
			{
				Resource = sourceBuffer,
				OldState = global::Diligent.ResourceState.CopySource,
				NewState = originalState,
				Flags = StateTransitionFlags.UpdateState
			}
		});
		
		ctx.Flush();
		ctx.WaitForIdle();

		var mapPtr = ctx.MapBuffer(stagingBuffer, MapType.Read, MapFlags.DoNotWait);
		if (mapPtr != IntPtr.Zero)
		{
			try
			{
				Unsafe.CopyBlock(destination, mapPtr.ToPointer(), sizeInBytes);
			}
			finally
			{
				ctx.UnmapBuffer(stagingBuffer, MapType.Read);
			}
		}
	}
}