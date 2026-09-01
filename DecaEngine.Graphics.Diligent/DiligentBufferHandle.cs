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

		// BUFFER_MODE_STRUCTURED - только когда буферу реально нужен шейдерный вид (SRV или UAV),
		// то есть когда в access есть Compute/Pixel/Vertex. Структурированный режим описывает именно
		// РАСКЛАДКУ ДЛЯ ШЕЙДЕРА, и для чисто вершинного или индексного буфера он бессмыслен: D3D12
		// такое сочетание прямо отвергает ("Failed to create D3D12 buffer" при
		// Mode=STRUCTURED + BindFlags=BIND_VERTEX_BUFFER), а CreateBuffer возвращает обёртку с
		// пустым нативным объектом - падение случается позже и далеко от причины.
		bool needsShaderView = Info.type is not BufferHandleType.Constant && Info.access != 0;

		var desc = new BufferDesc
		{
			Name = Info.name,
			Size = Info.sizeInBytes,
			// Шаг элемента имеет смысл только у структурированного буфера.
			ElementByteStride = needsShaderView ? Info.stride : 0,
			BindFlags = bindFlags,
			Usage = usage,
			CPUAccessFlags = cpuAccessFlags,
			Mode = needsShaderView ? Diligent.BufferMode.Structured : Diligent.BufferMode.Undefined,
		};

		_buffer = _device.CreateBuffer(desc);
	}

	private void ReleaseResources()
	{
		_buffer?.Dispose();
		_buffer = null;
	}

	/// <summary>
	/// DECA_BUFDIAG=&lt;подстрока имени&gt; - печатать каждое освобождение буфера, чьё имя содержит
	/// подстроку, вместе со стеком вызова. Нужен, когда падение происходит на ОБРАЩЕНИИ к
	/// освобождённому буферу: в этот момент виновник уже давно отработал, и в стеке падения его нет.
	/// Пустая строка или "1" - печатать все.
	/// </summary>
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