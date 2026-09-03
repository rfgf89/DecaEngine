using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using Diligent;
using UnsafeCollections.Collections.Native;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

public enum TextureAddress
{
	Wrap,
	Mirror,
	Clamp,
	Border,
	MirrorOnce,
}

public enum TextureFilter : byte
{
	Unknown,
	Point,
	Linear,
	Anisotropic,
	ComparisonPoint,
	ComparisonLinear,
	ComparisonAnisotropic,
	MinimumPoint,
	MinimumLinear,
	MinimumAnisotropic,
	MaximumPoint,
	MaximumLinear,
	MaximumAnisotropic,
	NumFilters,
}

public enum CompFunction : byte
{
	Unknown,
	Never,
	Less,
	Equal,
	LessEqual,
	Greater,
	NotEqual,
	GreaterEqual,
	Always,
	NumFunctions,
}

/// <summary>Ray tracing capability; ordered by increasing power, so "&gt;= Inline" is meaningful.</summary>
public enum RayTracingSupport
{
	None,

	/// <summary>Full pipeline only (DXR 1.0): ray-gen/hit/miss shaders and a binding table.</summary>
	Pipeline,

	/// <summary>Inline tracing (RayQuery, DXR 1.1): rays from compute or pixel shaders.</summary>
	Inline,
}

public interface IGraphicsPipeline
{
	public void Initialize();
	public void Execute();

	public void SignalGraph(DirectionalLightCascadeData renderScene, RenderCamerasData renderViews);

	/// <summary>Debug-only per-frame render graph statistics. Always null in Release builds.</summary>
	public RenderGraphDebugSnapshot DebugSnapshot { get; }

	/// <summary>Debug-only recent frame history. Always null in Release builds.</summary>
	public RenderGraphDebugHistory DebugHistory { get; }
}

public interface IGraphicsApi : IReleaseObject
{
	public void Initialize(GraphicsBackend backend);

	public event Action<GraphicsPipelineSetupInfo> OnCreateSetupInfo;
	public event Action OnSwapChainInfo;

	public IWindowHandle WindowHandle { set; get; }

	public void Present();

	/// <summary>Present sync interval: 1 = vsync, 0 = uncapped; read on every present.</summary>
	public int PresentInterval { get; set; }

	/// <summary>Flushes the PSO cache to disk; must be idempotent and never throw.</summary>
	public void SavePipelineCache();

	public void SetBackBufferTarget(Vector4 color);


	public IMeshObject CreateMesh(string name);
	public IMaterialObject CreateMaterial(string name);
	public IComputeMaterial CreateComputeMaterial(string name);
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type);
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint);

	/// <summary>Keyword variant: each keyword compiles as a macro set to 1; caller caches variants.</summary>
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint, IReadOnlyList<string> keywords);

	/// <summary>Shader from the backend-wide cache; shared instance, Release is a no-op, so only
	/// callers with <see cref="IMaterialObject.OwnsShaders"/> = false may use it.</summary>
	public IShaderObject CreateSharedShader(string name, string factoryPath, string filePath,
		ShaderObjectType type, string entryPoint = "Main", IReadOnlyList<string> keywords = null);

	/// <summary>Creates a graphics PSO from the backend-independent <see cref="GraphicsStateInfo"/>.</summary>
	public IStateObject CreateGraphicsState(GraphicsStateInfo info);

	/// <summary>Creates a compute PSO from the backend-independent <see cref="ComputeStateInfo"/>.</summary>
	public IStateObject CreateComputeState(ComputeStateInfo info);

	/// <summary>Hardware ray tracing capability of the device.</summary>
	public RayTracingSupport RayTracing { get; }

	/// <summary>Dynamic indexing of shader resource arrays (NonUniformResourceIndex).</summary>
	public bool SupportsShaderResourceArrays => false;

	public IGpuTexture CreateTexture(CpuTextureData data);

	/// <summary>Immutable Texture2DArray from equal-sized RGBA8 layers; null when unsupported.</summary>
	public IGpuTexture? CreateTextureArray(string name, int width, int height,
		IReadOnlyList<byte[]> layers) => null;

	/// <summary>Immutable 2D texture with an explicit mip chain; mipPixels[0] is width x height,
	/// each next halved. floatFormat = RGBA16Float (stride w*8), else RGBA8 (stride w*4).</summary>
	public IGpuTexture CreateTexture2DWithMips(string name, IReadOnlyList<byte[]> mipPixels, int width, int height, bool floatFormat = false);

	/// <summary>Mutable mip-less 2D texture for repeated re-uploads; unorderedAccess adds a UAV.</summary>
	public IGpuTexture CreateTexture2DMutable(string name, int width, int height,
		bool floatFormat = false, bool unorderedAccess = false);

	/// <summary>Re-uploads the whole texture; must be called on the immediate-context thread.</summary>
	public void UpdateTexture2D(IGpuTexture texture, byte[] pixels);

	public IRenderTarget CreateRenderTarget(TextureInfo info);

	/// <summary>Standalone GPU buffer updatable in-frame via UpdateBuffer, even from a frozen buffer.</summary>
	public IBufferHandle CreateBuffer(BufferInfo info);

	/// <summary>Creates a new, backend-specific <see cref="ICommandBuffer"/> ready for recording.</summary>
	public ICommandBuffer CreateCommandBuffer();

	public IRenderGraph CreateRenderGraph();

	/// <summary>Flush and wait; required before releasing resources in-flight frames may reference.</summary>
	public void WaitForGpuIdle();

	/// <summary>Raw texture view of the current swap chain back buffer's color target.</summary>
	public ITextureView GetBackBufferColorView();

	/// <summary>Raw texture view of the current swap chain back buffer's depth target.</summary>
	public ITextureView GetBackBufferDepthView();

	/// <summary>mipLodBias must be log2(render scale) for temporal upscaling to have detail to work with.</summary>
	public ISamplerObject CreateSampler(string name,
		TextureFilter filter,
		TextureAddress address,
		CompFunction comparisonFunction,
		Vector4 border,
		float mipLodBias = 0f);
}

// Legacy, transitioning to IRenderTarget.
public interface IRenderHandle : IReleaseObject
{
	public Vector2 Size { get; }
	public void Alloc(TextureInfo info);
	public void Resize(Vector2 size);
}

/// <summary>A texture in VRAM that can be rendered to.</summary>
public interface IRenderTarget : IGpuTexture
{
	public Vector2 Size { get; }
	public void Resize(Vector2 size);
}