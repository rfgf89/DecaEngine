using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;
using UnsafeCollections.Collections.Native;

namespace DecaEngine.Core;

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

	public void SetBackBufferTarget(Vector4 color);


	public IMeshObject CreateMesh(string name);
	public IMaterialObject CreateMaterial(string name);
	public IComputeMaterial CreateComputeMaterial(string name); // Added for Compute
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type);
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint);

	/// <summary>Вариант шейдера с ключевыми словами (shader keywords): каждый ключ уходит в
	/// компиляцию макросом со значением 1, шейдер вырезает выключенные эффекты через #if - в
	/// отличие от рантайм-веток по cbuffer-флагам, выключенный эффект не стоит ни регистров, ни
	/// привязок. Кэширование вариантов - на совести вызывающего (см. ModelLoader).</summary>
	public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type, string entryPoint, IReadOnlyList<string> keywords);

	/// <summary>
	/// Создает abstract Graphics Pipeline State (<see cref="PipelineStateType.Graphics"/>) из
	/// backend-независимого описания <see cref="GraphicsStateInfo"/>. Конкретный backend
	/// (например DiligentEngine) сам решает, во что превратить это описание внутри себя.
	/// </summary>
	public IStateObject CreateGraphicsState(GraphicsStateInfo info);

	/// <summary>
	/// Создает abstract Compute Pipeline State (<see cref="PipelineStateType.Compute"/>) из
	/// backend-независимого описания <see cref="ComputeStateInfo"/>.
	/// </summary>
	public IStateObject CreateComputeState(ComputeStateInfo info);

	public IGpuTexture CreateTexture(CpuTextureData data);

	/// <summary>Immutable 2D-текстура с ЯВНОЙ мип-цепочкой: mipPixels[0] - базовый уровень
	/// width x height, каждый следующий вдвое меньше (min 1). Нужна там, где мипы несут смысловую
	/// нагрузку, а не просто уменьшение - например, префильтрованный по roughness энвайронмент
	/// превью (см. PreviewEnvironmentMap): SampleLevel(mip = f(roughness)).
	/// floatFormat = true - RGBA16Float (байты - little-endian half4 на пиксель, stride w*8) для
	/// HDR-содержимого; false - RGBA8 (stride w*4).</summary>
	public IGpuTexture CreateTexture2DWithMips(string name, IReadOnlyList<byte[]> mipPixels, int width, int height, bool floatFormat = false);

	public IRenderTarget CreateRenderTarget(TextureInfo info);

	/// <summary>Creates a new, backend-specific <see cref="ICommandBuffer"/> ready for recording.</summary>
	public ICommandBuffer CreateCommandBuffer();

	public IRenderGraph CreateRenderGraph();

	/// <summary>Raw texture view of the current swap chain back buffer's color target.</summary>
	public ITextureView GetBackBufferColorView();

	/// <summary>Raw texture view of the current swap chain back buffer's depth target.</summary>
	public ITextureView GetBackBufferDepthView();

	public ISamplerObject CreateSampler(string name,
		TextureFilter filter,
		TextureAddress address,
		CompFunction comparisonFunction,
		Vector4 border);
}

// Legacy, transitioning to IRenderTarget
public interface IRenderHandle : IReleaseObject
{
	public Vector2 Size { get; }
	public void Alloc(TextureInfo info);
	public void Resize(Vector2 size);
}

// New: Represents a texture in VRAM that can be rendered to
public interface IRenderTarget : IGpuTexture
{
	public Vector2 Size { get; }
	public void Resize(Vector2 size);
}