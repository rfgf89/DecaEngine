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
		BindFlags bindFlags = (BindFlags)Info.type;
		/*
				BufferHandleType.Structured => BindFlags.None,
				BufferHandleType.Constant => BindFlags.UniformBuffer,
				BufferHandleType.Vertex => BindFlags.VertexBuffer,
				BufferHandleType.Index => BindFlags.IndexBuffer,
				BufferHandleType.IndirectArgs => BindFlags.IndirectDrawArgs
			};*/

		if (Info.type is not BufferHandleType.Constant)
		{
			if (Info.access == HandleAccess.Compute)
			{
				bindFlags |= BindFlags.UnorderedAccess;
			}

			if (Info.access is HandleAccess.Vertex or HandleAccess.Pixel)
			{
				bindFlags |= BindFlags.ShaderResource;
			}
		}

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