using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Editor;

/// <summary>
/// GPU side of <see cref="DebugDraw"/>: two draws, depth-tested and on-top.
///
/// Drawn inline at the end of ForwardPass because only there is the scene depth buffer bound.
/// That target is HDR and pre-tonemap, hence the <see cref="Intensity"/> multiplier.
///
/// The vertex count is baked into a frozen graph command, so every draw covers the full buffer
/// capacity and surplus vertices are killed by zero alpha in DebugLineVS.hlsl.
/// </summary>
public sealed class DebugLineOverlay : IDisposable
{
	// Depth-tested and on-top buckets differ only by the PSO depth state.
	private sealed class Bucket
	{
		public IMaterialObject Material = null!;
		public IBufferHandle? Buffer;

		// Capacity in vertices, which is also the vertex count of the draw.
		public int Capacity;

		// Vertices filled last frame: exactly the tail that must be cleared this frame.
		public int LiveLastFrame;

		public DebugLineVertex[] Scratch = [];
	}

	private struct DebugLineParams
	{
		public Vector4 Params;
	}

	private const int MinCapacity = 4096;

	private readonly DiligentGraphicsApi _dilApi;
	private readonly Bucket _depthTested = new();
	private readonly Bucket _onTop = new();

	private float _appliedIntensity = -1f;

	/// <summary>Line brightness multiplier that lifts debug lines out of the scene tonemap.</summary>
	public float Intensity { get; set; } = 4f;

	public int DepthTestedCapacity => _depthTested.Capacity;
	public int OnTopCapacity => _onTop.Capacity;

	public DebugLineOverlay(DiligentGraphicsApi dilApi, IGraphicsApi api, IBatchRenderer batchRenderer,
		TextureObjectFormat colorFormat)
	{
		_dilApi = dilApi;

		var vs = api.CreateShader("Debug Line VS", "EditorAssets/shader", "DebugLineVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = api.CreateShader("Debug Line PS", "EditorAssets/shader", "DebugLinePS.hlsl",
			ShaderObjectType.Pixel);

		_depthTested.Material = CreateMaterial(api, batchRenderer, vs, ps, colorFormat,
			"Debug Line Depth", depthTest: true);
		_onTop.Material = CreateMaterial(api, batchRenderer, vs, ps, colorFormat,
			"Debug Line OnTop", depthTest: false);

		ApplyIntensity();
	}

	private static IMaterialObject CreateMaterial(IGraphicsApi api, IBatchRenderer batchRenderer,
		IShaderObject vs, IShaderObject ps, TextureObjectFormat colorFormat, string name, bool depthTest)
	{
		var material = api.CreateMaterial($"{name} Material");
		material.SetShader(vs, ps);
		material.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = $"{name} PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.D32Float,
			PrimitiveTopology = PrimitiveTopologyType.LineList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo
			{
				DepthEnable = depthTest,

				// Reverse-Z like the rest of the scene; depth writes stay off so crossing
				// wireframes never occlude each other.
				DepthFunc = ComparisonFunctionType.GreaterEqual,
				DepthWriteEnable = false,
			},
			InputLayout =
			[
				new InputLayoutElementInfo
				{
					InputIndex = 0,
					BufferSlot = 0,
					NumComponents = 3,
					ValueType = InputElementValueType.Float32,
				},
				new InputLayoutElementInfo
				{
					InputIndex = 1,
					BufferSlot = 0,
					NumComponents = 4,
					ValueType = InputElementValueType.Float32,
				},
			],
		}));

		batchRenderer.BindViewConstants(material);
		return material;
	}

	/// <summary>Uploads the frame's debug geometry; true means the caller must InvalidateGraph.
	/// Must be called before the graph runs: the upload uses the immediate context.</summary>
	public bool Upload(DebugDraw draw)
	{
		if (_appliedIntensity != Intensity)
		{
			ApplyIntensity();
		}

		bool commandsDirty = UploadBucket(_depthTested, draw.DepthTestedVertices());
		commandsDirty |= UploadBucket(_onTop, draw.OnTopVertices());

		return commandsDirty;
	}

	private bool UploadBucket(Bucket bucket, ReadOnlySpan<DebugLineVertex> vertices)
	{
		bool commandsDirty = false;

		if (bucket.Buffer == null || bucket.Capacity < vertices.Length)
		{
			// Wait for the GPU: an in-flight frame may still be reading the old buffer.
			_dilApi.ImmediateContext.Flush();
			_dilApi.ImmediateContext.WaitForIdle();

			bucket.Buffer?.Release();

			int capacity = Math.Max(MinCapacity, bucket.Capacity == 0 ? MinCapacity : bucket.Capacity);
			while (capacity < vertices.Length)
			{
				capacity *= 2;
			}

			bucket.Capacity = capacity;
			bucket.Scratch = new DebugLineVertex[capacity];
			bucket.Buffer = _dilApi.CreateBuffer<DebugLineVertex>(capacity, new BufferInfo
			{
				name = "Debug Line VB",
				type = BufferHandleType.Vertex,
			});

			// A fresh buffer holds garbage and the draw covers all of it, so clear everything once.
			bucket.LiveLastFrame = capacity;
			commandsDirty = true;
		}

		var scratch = bucket.Scratch;
		vertices.CopyTo(scratch);

		// Zero alpha kills last frame's tail; beyond it the buffer is already zeroed.
		int tail = Math.Min(bucket.LiveLastFrame, bucket.Capacity);
		for (int i = vertices.Length; i < tail; i++)
		{
			scratch[i] = default;
		}

		int upload = Math.Max(vertices.Length, tail);
		bucket.LiveLastFrame = vertices.Length;

		if (upload > 0)
		{
			var buffer = ((DiligentBufferHandle)bucket.Buffer!).Buffer;
			if (buffer != null)
			{
				_dilApi.ImmediateContext.UpdateBuffer<DebugLineVertex>(buffer, 0,
					scratch.AsSpan(0, upload), global::Diligent.ResourceStateTransitionMode.Transition);
			}
		}

		return commandsDirty;
	}

	/// <summary>Draws into the render target ForwardPass already has bound.</summary>
	public void Draw(ICommandBuffer cmd)
	{
		DrawBucket(cmd, _depthTested);
		DrawBucket(cmd, _onTop);
	}

	private static void DrawBucket(ICommandBuffer cmd, Bucket bucket)
	{
		if (bucket.Buffer == null || bucket.Capacity == 0)
		{
			return;
		}

		cmd.SetPipelineState(bucket.Material);
		cmd.CommitShaderResources(bucket.Material);
		cmd.SetVertexBuffers(0, [bucket.Buffer], [0ul], SetVertexBuffersFlags.Reset);
		cmd.Draw((uint)bucket.Capacity);
	}

	private void ApplyIntensity()
	{
		_appliedIntensity = Intensity;

		var constants = new DebugLineParams { Params = new Vector4(Intensity, 0f, 0f, 0f) };
		_depthTested.Material.SetConstant("DebugLineParams", ref constants, HandleAccess.Vertex);
		_onTop.Material.SetConstant("DebugLineParams", ref constants, HandleAccess.Vertex);
	}

	public void Dispose()
	{
		_depthTested.Material.Release();
		_onTop.Material.Release();
		_depthTested.Buffer?.Release();
		_onTop.Buffer?.Release();
	}
}
