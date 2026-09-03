using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

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

	/// <summary>Wraps a native buffer owned elsewhere (e.g. by the render graph) without creating one.</summary>
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

		// D3D12 rejects Mode=STRUCTURED combined with BIND_VERTEX_BUFFER, so ask for it only
		// when the buffer actually needs an SRV or UAV.
		bool needsShaderView = Info.type is not BufferHandleType.Constant && Info.access != 0;

		var desc = new BufferDesc
		{
			Name = Info.name,
			Size = Info.sizeInBytes,
			// Element stride is meaningful only for a structured buffer.
			ElementByteStride = needsShaderView ? Info.stride : 0,
			BindFlags = bindFlags,
			Usage = usage,
			CPUAccessFlags = cpuAccessFlags,
			Mode = needsShaderView ? global::Diligent.BufferMode.Structured : global::Diligent.BufferMode.Undefined,
		};

		_buffer = _device.CreateBuffer(desc);
	}

	private void ReleaseResources()
	{
		_buffer?.Dispose();
		_buffer = null;
	}

	// DECA_BUFDIAG=<name substring> logs matching buffer releases with a stack trace; "1" or "" logs all.
	private static readonly string? DiagFilter = Environment.GetEnvironmentVariable("DECA_BUFDIAG");

	public void Release()
	{
		if (DiagFilter != null && _buffer != null &&
			(DiagFilter is "1" or "" || (Info.name?.Contains(DiagFilter, StringComparison.OrdinalIgnoreCase) ?? false)))
		{
			Console.WriteLine($"[bufdiag] Release '{Info.name}' ({SizeInBytes} B)\n{Environment.StackTrace}");
		}

		ReleaseResources();
	}
}