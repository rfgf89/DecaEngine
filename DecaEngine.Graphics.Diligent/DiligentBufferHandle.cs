using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine;

public class DiligentBufferHandle : IBufferHandle
{
	public uint SizeInBytes { get; private set; }

	public BufferInfo Info { get; private set; }

	public IBuffer Buffer { get { return _buffer; } }

	private readonly IRenderDevice _device;
	private IBuffer _buffer;

	public DiligentBufferHandle(IRenderDevice device)
	{
		_device = device;
	}

	/// <summary>
	/// Wraps an already-created native buffer (e.g. one owned and allocated by the render graph)
	/// without creating a new one, so render graph resources can flow through the same
	/// <see cref="ICommandBuffer"/>/<see cref="IMaterialObject"/> code paths as any other
	/// <see cref="IBufferHandle"/>. See <see cref="DiligentRenderGraphContext.GetBuffer"/>.
	/// </summary>
	public DiligentBufferHandle(IBuffer buffer, BufferInfo info)
	{
		_buffer = buffer;
		Info = info;
		SizeInBytes = info.sizeInBytes;
	}

	public void Alloc(BufferInfo info)
	{
		this.Info = info;
		SizeInBytes = info.sizeInBytes;

		if (info.type == BufferHandleType.Constant)
		{
			SizeInBytes = (info.sizeInBytes + 15u) & ~15u;
		}

		CreateBuffer();
	}

	public ShaderType GetShaderType()
	{
		ShaderType shaderType = ShaderType.Unknown;

		if ((Info.access & HandleAccess.Compute) != 0)
		{
			shaderType |= ShaderType.Compute;
		}

		if ((Info.access & HandleAccess.Pixel) != 0)
		{
			shaderType |= ShaderType.Pixel;
		}

		if ((Info.access & HandleAccess.Vertex) != 0)
		{
			shaderType |= ShaderType.Vertex;
		}

		return shaderType;
	}

	public BufferViewType GetViewFlags(HandleAccess access)
	{
		BufferViewType bindFlags = BufferViewType.Undefined;

		if (Info.type is not BufferHandleType.Constant)
		{
			bindFlags = access switch
			{
				HandleAccess.Compute => BufferViewType.UnorderedAccess,
				HandleAccess.Vertex or HandleAccess.Pixel => BufferViewType.ShaderResource,
				_ => bindFlags
			};
		}

		return bindFlags;
	}

	private void CreateBuffer()
	{
		BindFlags bindFlags = DiligentResourceFormats.ToBufferBindFlags(Info.type, Info.access);

		var usage = Info.dynamic ? Usage.Dynamic : Usage.Default;
		var cpuAccessFlags = Info.dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None;

		var desc = new BufferDesc
		{
			Name = Info.name,
			Size = Info.sizeInBytes,
			ElementByteStride = Info.stride,
			BindFlags = bindFlags,
			Usage = usage,
			CPUAccessFlags = cpuAccessFlags,
			Mode = Info.type is BufferHandleType.Constant ? Diligent.BufferMode.Undefined : Diligent.BufferMode.Structured,
		};

		_buffer = _device.CreateBuffer(desc);
	}

	private void ReleaseResources()
	{
		_buffer?.Dispose();
		_buffer = null;
	}

	public void Release()
	{
		ReleaseResources();
	}
}