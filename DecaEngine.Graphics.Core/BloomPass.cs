using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for bloom: the progressive down/up mip chain and its
/// materials, following Jimenez, SIGGRAPH 2014.</summary>
public sealed unsafe class BloomPassResources : IReleaseObject
{
	/// <summary>Chain length; the bottom level runs at 1/64 resolution.</summary>
	public const int MaxLevels = 5;

	private const uint MinLevelSize = 8;

	// Downsample chain: [0] is half screen, each following one half of that again.
	private readonly IRenderTarget[] _down = new IRenderTarget[MaxLevels];

	// Upsample chain; the top down-level starts the accumulation, so this has one level fewer.
	private readonly IRenderTarget[] _up = new IRenderTarget[MaxLevels - 1];

	private readonly IMaterialObject _prefilter;
	private readonly IMaterialObject[] _downMaterials = new IMaterialObject[MaxLevels - 1];
	private readonly IMaterialObject[] _upMaterials = new IMaterialObject[MaxLevels - 1];
	private readonly IMaterialObject _composite;

	// Filled via UpdateBuffer, not SetConstant: rebinding the SRB in flight fails Vulkan validation.
	private readonly IBufferHandle[] _constantBuffers;
	private readonly BloomConstantsData* _constants;

	private readonly int _materialCount;
	private int _levels;

	/// <summary>How many chain levels are alive at the current resolution.</summary>
	public int Levels => _levels;

	private const int PrefilterIndex = 0;
	private int DownIndex(int i) => 1 + i;
	private int UpIndex(int i) => 1 + (MaxLevels - 1) + i;
	private int CompositeIndex => 1 + 2 * (MaxLevels - 1);

	// adaptationTarget may be null (LDR); _AdaptTex still gets a placeholder - an empty descriptor
	// fails Vulkan validation (VUID-08114).
	public BloomPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture sceneCopyTarget, TextureObjectFormat colorFormat,
		IGpuTexture? adaptationTarget)
	{
		_materialCount = CompositeIndex + 1;
		_constantBuffers = new IBufferHandle[_materialCount];

		for (int i = 0; i < MaxLevels; i++)
		{
			var (w, h) = LevelSize(width, height, i);
			_down[i] = graphicsApi.CreateRenderTarget(new TextureInfo
			{
				name = $"{colorTargetName} Bloom Down {i}",
				width = w,
				height = h,
				format = colorFormat,
			});

			if (i < MaxLevels - 1)
			{
				_up[i] = graphicsApi.CreateRenderTarget(new TextureInfo
				{
					name = $"{colorTargetName} Bloom Up {i}",
					width = w,
					height = h,
					format = colorFormat,
				});
			}
		}

		// No depth, no MSAA: the whole chain runs at 1x over the already resolved frame.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Bloom PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Linear + Clamp are required: the chain relies on bilinear taps between texels, and Wrap
		// would drag a bright screen edge onto the opposite one.
		var sampler = graphicsApi.CreateSampler(
			name: "Bloom Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		_constants = (BloomConstantsData*)NativeMemory.AllocZeroed(
			(nuint)_materialCount, (nuint)sizeof(BloomConstantsData));

		var placeholder = adaptationTarget ?? sceneCopyTarget;

		_prefilter = CreateMaterial(graphicsApi, batchRenderer, state, sampler, "Bloom Prefilter",
			"BloomPrefilterPS.hlsl", PrefilterIndex, sceneCopyTarget, null, placeholder);

		for (int i = 0; i < MaxLevels - 1; i++)
		{
			_downMaterials[i] = CreateMaterial(graphicsApi, batchRenderer, state, sampler,
				$"Bloom Down {i}", "BloomDownPS.hlsl", DownIndex(i), _down[i], null, placeholder);

			// Upsample i reads the accumulated level below plus its own down-level; for the bottom
			// link nothing is accumulated yet, so the down-level itself stands in.
			var lower = i == MaxLevels - 2 ? _down[MaxLevels - 1] : (IGpuTexture)_up[i + 1];
			_upMaterials[i] = CreateMaterial(graphicsApi, batchRenderer, state, sampler,
				$"Bloom Up {i}", "BloomUpPS.hlsl", UpIndex(i), _down[i], lower, placeholder);
		}

		_composite = CreateMaterial(graphicsApi, batchRenderer, state, sampler, "Bloom Composite",
			"BloomCompositePS.hlsl", CompositeIndex, sceneCopyTarget, _up[0], placeholder);

		// The pass draws from frame one, so seed the cbuffers before any settings push arrives.
		SetParams(DefaultThreshold, DefaultKnee, DefaultRadius, DefaultIntensity);
		SetExposure(adaptationTarget is not null, 0.18f);
		Resize(width, height);
	}

	// Threshold is in display units, where 1.0 is display white; kept just under it because a
	// physically correct 1.0 measures as no visible effect on ordinary interiors.
	public const float DefaultThreshold = 0.6f;
	public const float DefaultKnee = 0.5f;
	public const float DefaultRadius = 1.0f;
	public const float DefaultIntensity = 0.8f;

	// Mirrors cbuffer BloomConstants in BloomCommon.hlsl.
	private struct BloomConstantsData
	{
		// xy - target size, zw - 1/xy.
		public Vector4 Target;

		// xy - source size, zw - 1/xy. In the composite, x instead holds the live level count.
		public Vector4 Source;

		// x - threshold, y - knee, z - tent radius, w - intensity.
		public Vector4 Params;

		// x - threshold is exposure-relative, y - key value.
		public Vector4 Exposure;
	}

	private static (uint W, uint H) LevelSize(uint width, uint height, int level)
	{
		uint div = (uint)(2 << level);
		return (Math.Max(width / div, 1u), Math.Max(height / div, 1u));
	}

	private IMaterialObject CreateMaterial(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		IStateObject state, ISamplerObject sampler, string name, string pixelShaderFile, int index,
		IGpuTexture source, IGpuTexture? lower, IGpuTexture adaptation)
	{
		// Own VS instance per material: a shared shader would be released twice on rebuild.
		var vs = graphicsApi.CreateShader(name + " VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader(name + " PS", "EditorAssets/shader", pixelShaderFile,
			ShaderObjectType.Pixel);

		var material = graphicsApi.CreateMaterial(name + " Material");
		material.SetShader(vs, ps);
		material.SetState(state);
		batchRenderer.BindViewConstants(material);

		material.SetTexture("_SourceTex", source);
		material.SetImmutableSampler("_SourceTex", sampler);

		// Bind _LowerTex only where the shader declares it: otherwise the compiler drops the unused
		// resource while the immutable sampler stays in the layout, and Diligent warns per PSO.
		if (lower is not null)
		{
			material.SetTexture("_LowerTex", lower);
			material.SetImmutableSampler("_LowerTex", sampler);
		}
		material.SetTexture("_AdaptTex", adaptation);

		// dynamic = false: Diligent updates dynamic buffers via Map, but we need UpdateBuffer.
		var buffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "BloomConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(BloomConstantsData),
		});

		_constantBuffers[index] = buffer;
		material.SetBuffer("BloomConstants", buffer, HandleAccess.Pixel);
		return material;
	}

	/// <summary>Threshold in display units, soft-knee width, upsample tent radius and intensity.</summary>
	public void SetParams(float threshold, float knee, float radius, float intensity)
	{
		var p = new Vector4(MathF.Max(threshold, 0f), MathF.Max(knee, 1e-4f),
			MathF.Max(radius, 0f), MathF.Max(intensity, 0f));

		for (int i = 0; i < _materialCount; i++)
		{
			_constants[i].Params = p;
		}
	}

	/// <summary>Ties the threshold to auto-exposure; key must match the eye-adaptation and tonemap
	/// passes.</summary>
	public void SetExposure(bool exposureRelative, float key)
	{
		var e = new Vector4(exposureRelative ? 1f : 0f, MathF.Max(key, 1e-4f), 0f, 0f);
		for (int i = 0; i < _materialCount; i++)
		{
			_constants[i].Exposure = e;
		}
	}

	/// <summary>Switches the mode only, preserving the key value.</summary>
	public void SetExposureRelative(bool exposureRelative)
	{
		for (int i = 0; i < _materialCount; i++)
		{
			_constants[i].Exposure.X = exposureRelative ? 1f : 0f;
		}
	}

	/// <summary>Recomputes level sizes for a new frame resolution and resizes their targets.</summary>
	public void Resize(uint width, uint height)
	{
		// Drop levels that would shrink to a few texels, where the 13-tap kernel reads one texel.
		_levels = 0;
		for (int i = 0; i < MaxLevels; i++)
		{
			var (w, h) = LevelSize(width, height, i);
			if (i > 0 && (w < MinLevelSize || h < MinLevelSize))
			{
				break;
			}

			_levels = i + 1;
		}

		for (int i = 0; i < MaxLevels; i++)
		{
			var (w, h) = LevelSize(width, height, i);
			var size = new Vector2(w, h);
			_down[i].Resize(size);
			if (i < MaxLevels - 1)
			{
				_up[i].Resize(size);
			}
		}

		var half = LevelSize(width, height, 0);
		SetSizes(PrefilterIndex, half.W, half.H, width, height);

		for (int i = 0; i < MaxLevels - 1; i++)
		{
			var src = LevelSize(width, height, i);
			var dst = LevelSize(width, height, i + 1);

			SetSizes(DownIndex(i), dst.W, dst.H, src.W, src.H);
			SetSizes(UpIndex(i), src.W, src.H, dst.W, dst.H);
		}

		SetSizes(CompositeIndex, width, height, width, height);

		// Intensity is divided by the live level count, so it means the same at any chain length.
		_constants[CompositeIndex].Source.X = Math.Max(_levels, 1);
	}

	private void SetSizes(int index, uint targetW, uint targetH, uint sourceW, uint sourceH)
	{
		_constants[index].Target = new Vector4(targetW, targetH, 1f / targetW, 1f / targetH);
		_constants[index].Source = new Vector4(sourceW, sourceH, 1f / sourceW, 1f / sourceH);
	}

	/// <summary>Must be called after a resize: it recreates the native textures behind these.</summary>
	public void RebindTargets(IGpuTexture sceneCopyTarget, uint width, uint height)
	{
		Resize(width, height);

		_prefilter.SetTexture("_SourceTex", sceneCopyTarget);
		_composite.SetTexture("_SourceTex", sceneCopyTarget);
		_composite.SetTexture("_LowerTex", _up[0]);

		for (int i = 0; i < MaxLevels - 1; i++)
		{
			_downMaterials[i].SetTexture("_SourceTex", _down[i]);
			_upMaterials[i].SetTexture("_SourceTex", _down[i]);
			_upMaterials[i].SetTexture("_LowerTex",
				i == MaxLevels - 2 ? _down[MaxLevels - 1] : (IGpuTexture)_up[i + 1]);
		}
	}

	// Caller must have prepared the scene copy and unbound the render targets first.
	internal void WriteCommands(ICommandBuffer cmd, IGpuTexture colorTarget, Ref<Vector2> viewPortRef)
	{
		Draw(cmd, PrefilterIndex, _prefilter, _down[0]);

		for (int i = 0; i < _levels - 1; i++)
		{
			Draw(cmd, DownIndex(i), _downMaterials[i], _down[i + 1]);
		}

		// Upwards from the second-to-last live level; the bottom one starts the accumulation.
		for (int i = _levels - 2; i >= 0; i--)
		{
			Draw(cmd, UpIndex(i), _upMaterials[i], _up[i]);
		}

		// The composite writes the frame itself, at full resolution rather than a level size.
		cmd.UpdateBuffer(_constantBuffers[CompositeIndex], 0, _constants + CompositeIndex);
		cmd.SetRenderTarget(colorTarget, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(_composite);
		cmd.CommitShaderResources(_composite);
		cmd.Draw(3);
	}

	private void Draw(ICommandBuffer cmd, int index, IMaterialObject material, IRenderTarget target)
	{
		// Re-reads CPU memory on every replay, so a resize lands without rebuilding the graph.
		cmd.UpdateBuffer(_constantBuffers[index], 0, _constants + index);

		var desc = target.Size;
		cmd.SetRenderTarget(target, null);
		cmd.SetViewport((uint)desc.X, (uint)desc.Y);
		cmd.SetPipelineState(material);
		cmd.CommitShaderResources(material);
		cmd.Draw(3);

		// The next level reads this target as an SRV: transition before it gets bound.
		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(target, ResourceState.ShaderResource);
	}

	public void Release()
	{
		_prefilter.Release();
		_composite.Release();
		foreach (var m in _downMaterials)
		{
			m?.Release();
		}

		foreach (var m in _upMaterials)
		{
			m?.Release();
		}

		foreach (var t in _down)
		{
			t?.Release();
		}

		foreach (var t in _up)
		{
			t?.Release();
		}

		foreach (var b in _constantBuffers)
		{
			b?.Release();
		}

		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that adds bloom (optical glare) to the finished linear frame.
/// </summary>
// Must run after fog and after the luminance measurement (the threshold is exposure-relative, so
// measuring afterwards would feed back), and before tonemap - light only adds in linear space.
public sealed class BloomPass : RenderGraphPass<BloomPass.PassData>
{
	public override string Name => "Bloom Pass";

	private readonly BloomPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public BloomPass(BloomPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
		Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_sceneCopy = sceneCopy;
		_viewPortRef = viewPortRef;
	}

	// The bloom pyramid is internal to the pass and deliberately not declared to the graph.
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// The copy is taken here so it includes the haze fog just wrote into the frame.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		_resources.WriteCommands(cmd, _colorTarget, _viewPortRef);
	}
}
