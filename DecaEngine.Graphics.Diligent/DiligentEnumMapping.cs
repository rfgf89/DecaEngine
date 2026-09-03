using System;
using CoreClearDepthStencilFlags = DecaEngine.Graphics.ClearDepthStencilFlags;
using CoreResourceState = DecaEngine.Graphics.ResourceState;
using CoreSetVertexBuffersFlags = DecaEngine.Graphics.SetVertexBuffersFlags;
using NativeClearDepthStencilFlags = Diligent.ClearDepthStencilFlags;
using NativeResourceState = Diligent.ResourceState;
using NativeSetVertexBuffersFlags = Diligent.SetVertexBuffersFlags;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Sole translation point from engine enums to native Diligent types.</summary>
internal static class DiligentEnumMapping
{
	/// <summary>Set in DiligentGraphicsApi.Initialize; the native DepthRead mapping is backend-specific.</summary>
	internal static DecaEngine.Graphics.GraphicsBackend Backend;

	// DepthRead quirk: Vulkan needs a single-bit DEPTH_READ (Diligent can't map combined
	// states to a layout), while D3D12 DEPTH_READ alone forbids SRV reads — it needs
	// ShaderResource OR'd in, else depth reads in SSAO/SSGI/shadows are UB (TDR).
	public static NativeResourceState ToNative(this CoreResourceState state) => state switch
	{
		CoreResourceState.Unknown => NativeResourceState.Unknown,
		CoreResourceState.RenderTarget => NativeResourceState.RenderTarget,
		CoreResourceState.DepthWrite => NativeResourceState.DepthWrite,
		CoreResourceState.DepthRead => Backend == DecaEngine.Graphics.GraphicsBackend.Vulkan
			? NativeResourceState.DepthRead
			: NativeResourceState.DepthRead | NativeResourceState.ShaderResource,
		CoreResourceState.ShaderResource => NativeResourceState.ShaderResource,
		CoreResourceState.UnorderedAccess => NativeResourceState.UnorderedAccess,
		CoreResourceState.VertexBuffer => NativeResourceState.VertexBuffer,
		CoreResourceState.IndexBuffer => NativeResourceState.IndexBuffer,
		CoreResourceState.IndirectArgument => NativeResourceState.IndirectArgument,
		CoreResourceState.CopySource => NativeResourceState.CopySource,
		CoreResourceState.CopyDest => NativeResourceState.CopyDest,
		CoreResourceState.ResolveSource => NativeResourceState.ResolveSource,
		CoreResourceState.ResolveDest => NativeResourceState.ResolveDest,
		CoreResourceState.ConstantBuffer => NativeResourceState.ConstantBuffer,
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
	};

	public static NativeClearDepthStencilFlags ToNative(this CoreClearDepthStencilFlags flags)
	{
		var result = NativeClearDepthStencilFlags.None;
		if ((flags & CoreClearDepthStencilFlags.Depth) != 0) result |= NativeClearDepthStencilFlags.Depth;
		if ((flags & CoreClearDepthStencilFlags.Stencil) != 0) result |= NativeClearDepthStencilFlags.Stencil;
		return result;
	}

	public static NativeSetVertexBuffersFlags ToNative(this CoreSetVertexBuffersFlags flags)
	{
		var result = NativeSetVertexBuffersFlags.None;
		if ((flags & CoreSetVertexBuffersFlags.Reset) != 0) result |= NativeSetVertexBuffersFlags.Reset;
		return result;
	}
}
