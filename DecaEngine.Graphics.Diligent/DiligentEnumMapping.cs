using System;
using CoreClearDepthStencilFlags = DecaEngine.Core.ClearDepthStencilFlags;
using CoreResourceState = DecaEngine.Core.ResourceState;
using CoreSetVertexBuffersFlags = DecaEngine.Core.SetVertexBuffersFlags;
using NativeClearDepthStencilFlags = Diligent.ClearDepthStencilFlags;
using NativeResourceState = Diligent.ResourceState;
using NativeSetVertexBuffersFlags = Diligent.SetVertexBuffersFlags;

namespace DecaEngine.Graphics.Diligent;

/// <summary>Перевод backend-независимых <see cref="CoreResourceState"/>/<see cref="CoreClearDepthStencilFlags"/>
/// (см. ICommandBuffer) в нативные типы Diligent - только в этой точке, чтобы Diligent-типы не
/// протекали в сигнатуру ICommandBuffer.</summary>
internal static class DiligentEnumMapping
{
	/// <summary>Выставляется в DiligentGraphicsApi.Initialize. Нужен маппингу DepthRead: смысл
	/// этого состояния в движке - "депт-текстура читается шейдером как SRV", но нативное
	/// представление зависит от бэкенда (см. ниже).</summary>
	internal static DecaEngine.Core.GraphicsBackend Backend;

	// DepthRead:
	//  - Vulkan: одиночный DEPTH_READ - SRV депта биндится с лейаутом
	//    DEPTH_STENCIL_READ_ONLY_OPTIMAL (VUID-VkDescriptorImageInfo-imageLayout-00344), а
	//    комбинированные стейты текстур Diligent на Vulkan не переводит в лейаут (VERIFY
	//    "single bit state" в ResourceStateToVkImageLayout).
	//  - D3D12: голый D3D12_RESOURCE_STATE_DEPTH_READ НЕ разрешает чтение SRV - пиксельному
	//    шейдеру нужен ещё PIXEL_SHADER_RESOURCE, иначе чтение депта в SSAO/SSGI/тенях - UB
	//    (page fault -> TDR -> device removed). Комбинация read-only состояний валидна.
	public static NativeResourceState ToNative(this CoreResourceState state) => state switch
	{
		CoreResourceState.Unknown => NativeResourceState.Unknown,
		CoreResourceState.RenderTarget => NativeResourceState.RenderTarget,
		CoreResourceState.DepthWrite => NativeResourceState.DepthWrite,
		CoreResourceState.DepthRead => Backend == DecaEngine.Core.GraphicsBackend.Vulkan
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
