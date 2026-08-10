using System;

namespace DecaEngine.Core;

public enum ResourceState
{
	Unknown = 0,
	RenderTarget,
	DepthWrite,
	DepthRead,
	ShaderResource,
	UnorderedAccess,
	VertexBuffer,
	IndexBuffer,
	IndirectArgument,
	CopySource,
	CopyDest,
	ResolveSource,
	ResolveDest,
}

[Flags]
public enum ClearDepthStencilFlags
{
	None = 0,
	Depth = 1 << 0,
	Stencil = 1 << 1,
}

[Flags]
public enum SetVertexBuffersFlags
{
	None = 0,
	/// <summary>Сбросить слоты, не попавшие в этот вызов, вместо сохранения прежних привязок.</summary>
	Reset = 1 << 0,
}
