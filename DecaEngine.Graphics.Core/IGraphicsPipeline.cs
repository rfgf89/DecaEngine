using System.Numerics;
using DecaEngine.Graphics.Core;

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

public interface IGraphicsPipeline : IReleaseObject
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

	public IGpuTexture CreateTexture(CpuTextureData data);
	public IRenderTarget CreateRenderTarget(RenderTargetInfo info);

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
	public void Alloc(RenderTargetInfo info);
	public void Resize(Vector2 size);
}

// New: Represents a texture in VRAM that can be rendered to
public interface IRenderTarget : IGpuTexture
{
	public Vector2 Size { get; }
	public void Resize(Vector2 size);
}

public struct RenderTargetInfo
{
	public string name;
	public uint width;
	public uint height;
	public uint arraySize;

	public enum Format
	{
		R8G8B8A8_UNORM,
		R16G16B16A16_FLOAT,
		D32_FLOAT, // Added for ShadowMap support
		D24_UNorm_S8_UInt,
		D32_Float_S8X24_UInt
	}

	public Format textureFormat;
}