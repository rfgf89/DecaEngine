using System;

namespace DecaEngine.Graphics;

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
	ConstantBuffer,
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
	/// <summary>Reset slots not covered by this call instead of keeping prior bindings.</summary>
	Reset = 1 << 0,
}
