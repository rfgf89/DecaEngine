using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>GPU resources for auto-exposure: the log-luminance reduction chain plus the 1x1 adaptation ping-pong.</summary>
public sealed unsafe class EyeAdaptationPassResources : IReleaseObject
{
	// 64 -> 8 -> 1: two 8x8-tap steps cover it exactly, so the average has no subsampling.
	private const uint LuminanceSize = 64;

	private readonly IRenderTarget _lum64;
	private readonly IRenderTarget _lum8;
	private readonly IRenderTarget _lum1;

	// Both 1x1 targets are drawn every frame (A->B, B->A): the graph's command buffer is frozen,
	// so bindings cannot be swapped by frame parity.
	private readonly IRenderTarget _adaptA;
	private readonly IRenderTarget _adaptB;

	private readonly IMaterialObject _initMaterial;
	private readonly IMaterialObject _reduce64Material;
	private readonly IMaterialObject _reduce8Material;
	private readonly IMaterialObject _adaptAtoB;
	private readonly IMaterialObject _adaptBtoA;

	// One cbuffer per chain step, filled via cmd.UpdateBuffer from this unmanaged memory, not
	// SetConstant: re-binding the SRB while the previous frame is in flight trips Vulkan validation
	// ("bound VkDescriptorSet was destroyed or updated").
	private const int MaterialCount = 5;
	private const int InitIndex = 0, Reduce64Index = 1, Reduce8Index = 2, AdaptAtoBIndex = 3, AdaptBtoAIndex = 4;

	private readonly IBufferHandle[] _constantBuffers = new IBufferHandle[MaterialCount];
	private readonly EyeAdaptationConstantsData* _constants;

	/// <summary>1x1 RGBA32F holding the adapted frame luminance the tonemap reads.</summary>
	public IGpuTexture AdaptationTarget => _adaptA;

	public EyeAdaptationPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		IGpuTexture hdrColorTarget)
	{
		// Log luminance goes negative and well past 1, so RGBA8 will not do.
		_lum64 = CreateLuminanceTarget(graphicsApi, colorTargetName + " Luminance 64", LuminanceSize);
		_lum8 = CreateLuminanceTarget(graphicsApi, colorTargetName + " Luminance 8", 8);
		_lum1 = CreateLuminanceTarget(graphicsApi, colorTargetName + " Luminance 1", 1);

		// Full precision: the value persists across frames and half's drift shows as exposure jitter.
		_adaptA = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Adaptation A",
			width = 1,
			height = 1,
			format = TextureObjectFormat.R32G32B32A32Float,
		});
		_adaptB = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Adaptation B",
			width = 1,
			height = 1,
			format = TextureObjectFormat.R32G32B32A32Float,
		});

		var lumState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Eye Adaptation Luminance PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var adaptState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Eye Adaptation PSO",
			RenderTargetFormats = [TextureObjectFormat.R32G32B32A32Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var sampler = graphicsApi.CreateSampler(
			name: "Eye Adaptation Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		// Each material gets its own VS instance: a shared shader would be released twice.
		_initMaterial = CreateMaterial(graphicsApi, batchRenderer, "Luminance Init", "LuminanceInitPS.hlsl", lumState);
		_initMaterial.SetTexture("_SceneTex", hdrColorTarget);
		_initMaterial.SetImmutableSampler("_SceneTex", sampler);

		_reduce64Material = CreateMaterial(graphicsApi, batchRenderer, "Luminance Reduce 64", "LuminanceReducePS.hlsl", lumState);
		_reduce64Material.SetTexture("_LumTex", _lum64);
		_reduce64Material.SetImmutableSampler("_LumTex", sampler);

		_reduce8Material = CreateMaterial(graphicsApi, batchRenderer, "Luminance Reduce 8", "LuminanceReducePS.hlsl", lumState);
		_reduce8Material.SetTexture("_LumTex", _lum8);
		_reduce8Material.SetImmutableSampler("_LumTex", sampler);

		_adaptAtoB = CreateMaterial(graphicsApi, batchRenderer, "Eye Adaptation A->B", "EyeAdaptationPS.hlsl", adaptState);
		_adaptAtoB.SetTexture("_LumTex", _lum1);
		_adaptAtoB.SetTexture("_PrevTex", _adaptA);

		_adaptBtoA = CreateMaterial(graphicsApi, batchRenderer, "Eye Adaptation B->A", "EyeAdaptationPS.hlsl", adaptState);
		_adaptBtoA.SetTexture("_LumTex", _lum1);
		_adaptBtoA.SetTexture("_PrevTex", _adaptB);

		_constants = (EyeAdaptationConstantsData*)NativeMemory.AllocZeroed(
			(nuint)MaterialCount, (nuint)sizeof(EyeAdaptationConstantsData));

		BindConstants(graphicsApi, InitIndex, _initMaterial);
		BindConstants(graphicsApi, Reduce64Index, _reduce64Material);
		BindConstants(graphicsApi, Reduce8Index, _reduce8Material);
		BindConstants(graphicsApi, AdaptAtoBIndex, _adaptAtoB);
		BindConstants(graphicsApi, AdaptBtoAIndex, _adaptBtoA);

		InitConstantSizes();

		// Defaults before the first knob push: the pass draws from frame one. The ping-pong is
		// deliberately not cleared (a clear outside the graph breaks Vulkan layouts); the adaptation
		// shader clamps the garbage away.
		SetParams(0.18f, 0.03f, 8f, 0f);
		SetDeltaTime(0f);
	}

	private void BindConstants(IGraphicsApi graphicsApi, int index, IMaterialObject material)
	{
		// dynamic = false: Diligent updates dynamic buffers via Map, we need UpdateBuffer (USAGE_DEFAULT).
		var buffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "EyeAdaptation",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(EyeAdaptationConstantsData),
		});

		_constantBuffers[index] = buffer;
		material.SetBuffer("EyeAdaptation", buffer, HandleAccess.Pixel);
	}

	private static IRenderTarget CreateLuminanceTarget(IGraphicsApi graphicsApi, string name, uint size)
	{
		return graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = name,
			width = size,
			height = size,
			format = TextureObjectFormat.R16G16B16A16Float,
		});
	}

	private static IMaterialObject CreateMaterial(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string name,
		string pixelShaderFile, IStateObject state)
	{
		var vs = graphicsApi.CreateShader(name + " VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader(name + " PS", "EditorAssets/shader", pixelShaderFile, ShaderObjectType.Pixel);

		var material = graphicsApi.CreateMaterial(name + " Material");
		material.SetShader(vs, ps);
		material.SetState(state);
		batchRenderer.BindViewConstants(material);
		return material;
	}

	// Mirrors the "EyeAdaptation" cbuffer in EyeAdaptationCommon.hlsl.
	private struct EyeAdaptationConstantsData
	{
		// xy = target size in pixels, zw = 1/xy.
		public Vector4 Target;

		// xy = source size in pixels, zw = 1/xy.
		public Vector4 Source;

		// x = key value, y = min luminance, z = max luminance, w = exposure compensation (EV).
		public Vector4 Params;

		// x = deltaTime, y = adaptation speed up, z = speed down, w = reserved.
		public Vector4 Params2;
	}

	private float _key = 0.18f;
	private float _minLuminance = 0.03f;
	private float _maxLuminance = 8f;
	private float _exposureCompensation;
	private float _speedUp = 3f;
	private float _speedDown = 1f;
	private float _deltaTime;

	/// <summary>Target average frame luminance, its measured bounds, and exposure compensation in stops.</summary>
	public void SetParams(float key, float minLuminance, float maxLuminance, float exposureCompensation)
	{
		_key = key;
		_minLuminance = minLuminance;
		_maxLuminance = MathF.Max(maxLuminance, minLuminance);
		_exposureCompensation = exposureCompensation;
		UpdateConstants();
	}

	/// <summary>Temporal adaptation speeds in 1/sec, separate for brightening and darkening.</summary>
	public void SetSpeed(float speedUp, float speedDown)
	{
		_speedUp = speedUp;
		_speedDown = speedDown;
		UpdateConstants();
	}

	/// <summary>Frame time step; must be pushed every frame before <see cref="EyeAdaptationPass"/> runs.</summary>
	public void SetDeltaTime(float deltaTime)
	{
		_deltaTime = MathF.Max(deltaTime, 0f);
		UpdateConstants();
	}

	// Per-step target/source sizes never change, so they are written once at creation.
	private void InitConstantSizes()
	{
		SetSizes(InitIndex, LuminanceSize, LuminanceSize);
		SetSizes(Reduce64Index, 8, LuminanceSize);
		SetSizes(Reduce8Index, 1, 8);
		SetSizes(AdaptAtoBIndex, 1, 1);
		SetSizes(AdaptBtoAIndex, 1, 1);
	}

	private void SetSizes(int index, uint targetSize, uint sourceSize)
	{
		_constants[index].Target = new Vector4(targetSize, targetSize, 1f / targetSize, 1f / targetSize);
		_constants[index].Source = new Vector4(sourceSize, sourceSize, 1f / sourceSize, 1f / sourceSize);
	}

	// Touches only the CPU copy, so it is safe to call any time, including every frame.
	private void UpdateConstants()
	{
		var lumParams = new Vector4(_key, _minLuminance, _maxLuminance, _exposureCompensation);

		// Half dt per ping-pong step, so total adaptation speed is independent of the step count.
		var lumParams2 = new Vector4(_deltaTime * 0.5f, _speedUp, _speedDown, 0f);

		for (int i = 0; i < MaterialCount; i++)
		{
			_constants[i].Params = lumParams;
			_constants[i].Params2 = lumParams2;
		}
	}

	/// <summary>Must be called after the HDR target resizes: Resize recreates the native texture.</summary>
	public void RebindTargets(IGpuTexture hdrColorTarget)
	{
		_initMaterial.SetTexture("_SceneTex", hdrColorTarget);
	}

	// Caller must unbind render targets and transition the HDR frame to ShaderResource first.
	internal void WriteCommands(ICommandBuffer cmd)
	{
		// On frame one neither ping-pong target has been a render target, so on Vulkan they sit in
		// UNDEFINED and validation fires on the first draw (VUID-vkCmdDraw-None-09600).
		cmd.TransitionResource(_adaptA, ResourceState.ShaderResource);
		cmd.TransitionResource(_adaptB, ResourceState.ShaderResource);

		Draw(cmd, InitIndex, _initMaterial, _lum64, LuminanceSize);
		Draw(cmd, Reduce64Index, _reduce64Material, _lum8, 8);
		Draw(cmd, Reduce8Index, _reduce8Material, _lum1, 1);

		// A->B then B->A, so A ends up holding the current adaptation the tonemap reads.
		Draw(cmd, AdaptAtoBIndex, _adaptAtoB, _adaptB, 1);
		Draw(cmd, AdaptBtoAIndex, _adaptBtoA, _adaptA, 1);
	}

	private void Draw(ICommandBuffer cmd, int index, IMaterialObject material, IRenderTarget target, uint size)
	{
		// UpdateBuffer re-reads CPU memory on every replay of the frozen buffer, so dt lands without
		// rebuilding the graph.
		cmd.UpdateBuffer(_constantBuffers[index], 0, _constants + index);

		cmd.SetRenderTarget(target, null);
		cmd.SetViewport(size, size);
		cmd.SetPipelineState(material);
		cmd.CommitShaderResources(material);
		cmd.Draw(3);

		// The next step samples this target as an SRV: transition before it is bound in a draw.
		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(target, ResourceState.ShaderResource);
	}

	public void Release()
	{
		_lum64.Release();
		_lum8.Release();
		_lum1.Release();
		_adaptA.Release();
		_adaptB.Release();
		_initMaterial.Release();
		_reduce64Material.Release();
		_reduce8Material.Release();
		_adaptAtoB.Release();
		_adaptBtoA.Release();

		foreach (var buffer in _constantBuffers)
		{
			buffer?.Release();
		}

		NativeMemory.Free(_constants);
	}
}

// Must run after ForwardPass and SsgiPass (needs the finished linear frame) and before the tonemap.
/// <summary>Measures the HDR frame's average log-luminance into the adapted exposure the tonemap applies.</summary>
public sealed class EyeAdaptationPass : RenderGraphPass<EyeAdaptationPass.PassData>
{
	public override string Name => "Eye Adaptation Pass";

	private readonly EyeAdaptationPassResources _resources;
	private readonly IGpuTexture _hdrColorTarget;

	public struct PassData
	{
	}

	public EyeAdaptationPass(EyeAdaptationPassResources resources, IGpuTexture hdrColorTarget)
	{
		_resources = resources;
		_hdrColorTarget = hdrColorTarget;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_hdrColorTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_hdrColorTarget, ResourceState.ShaderResource);

		_resources.WriteCommands(cmd);
	}
}
