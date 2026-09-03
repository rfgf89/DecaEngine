using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Editor;

/// <summary>Scene View selection outline, drawn as a separate pass at end of frame.</summary>
// Engine PSOs have no blending, so the composite reads a copy of the frame and rewrites
// the target whole. Geometry arrives in WORLD space, rebaked on CPU when the selection
// changes. Pass commands are frozen with the graph: changing the index COUNT or
// recreating buffers requires InvalidateGraph, updating contents in place does not.
public sealed class SelectionOutlineOverlay : IDisposable
{
	private readonly DiligentGraphicsApi _dilApi;
	private readonly IRenderTarget _colorTarget;
	private readonly IRenderTarget _maskTarget;
	private readonly IRenderTarget _sceneScratch;
	private readonly IMaterialObject _maskMaterial;
	private readonly IMaterialObject _compositeMaterial;

	private IBufferHandle? _vertexBuffer;
	private IBufferHandle? _indexBuffer;
	private int _vertexCapacity;
	private int _indexCapacity;
	private uint _indexCount;

	public SelectionOutlineOverlay(DiligentGraphicsApi dilApi, IGraphicsApi api, IBatchRenderer batchRenderer,
		IRenderTarget colorTarget)
	{
		_dilApi = dilApi;
		_colorTarget = colorTarget;

		var size = colorTarget.Size;
		var width = (uint)Math.Max(1f, size.X);
		var height = (uint)Math.Max(1f, size.Y);

		_maskTarget = api.CreateRenderTarget(new TextureInfo
		{
			name = "Prefab Selection Mask",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// Format must match ColorTarget exactly: CopyTexture does not convert.
		_sceneScratch = api.CreateRenderTarget(new TextureInfo
		{
			name = "Prefab Selection Scene Copy",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		var maskVs = api.CreateShader("Selection Mask VS", "EditorAssets/shader", "SelectionMaskVS.hlsl", ShaderObjectType.Vertex);
		var maskPs = api.CreateShader("Selection Mask PS", "EditorAssets/shader", "SelectionMaskPS.hlsl", ShaderObjectType.Pixel);

		_maskMaterial = api.CreateMaterial("Selection Mask Material");
		_maskMaterial.SetShader(maskVs, maskPs);
		_maskMaterial.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Selection Mask PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			// No culling and no depth: the silhouette must show through other objects.
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout =
			[
				new InputLayoutElementInfo
				{
					InputIndex = 0,
					BufferSlot = 0,
					NumComponents = 3,
					ValueType = InputElementValueType.Float32,
				},
			],
		}));
		batchRenderer.BindViewConstants(_maskMaterial);

		var compositeVs = api.CreateShader("Selection Outline VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositePs = api.CreateShader("Selection Outline PS", "EditorAssets/shader", "SelectionOutlinePS.hlsl", ShaderObjectType.Pixel);

		_compositeMaterial = api.CreateMaterial("Selection Outline Material");
		_compositeMaterial.SetShader(compositeVs, compositePs);
		_compositeMaterial.SetState(api.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Selection Outline PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		}));
		_compositeMaterial.SetTexture("_MaskTex", _maskTarget);
		_compositeMaterial.SetTexture("_SceneTex", _sceneScratch);
	}

	public bool HasGeometry => _indexCount > 0;

	/// <summary>Uploads world-space selection geometry; true means call InvalidateGraph.</summary>
	public bool UpdateGeometry(List<Vector3> positions, List<uint> indices)
	{
		bool commandsDirty = false;

		if (_vertexBuffer == null || _vertexCapacity < positions.Count ||
			_indexBuffer == null || _indexCapacity < indices.Count)
		{
			_dilApi.ImmediateContext.Flush();
			_dilApi.ImmediateContext.WaitForIdle();

			_vertexBuffer?.Release();
			_indexBuffer?.Release();

			_vertexCapacity = Math.Max(positions.Count, 256);
			_indexCapacity = Math.Max(indices.Count, 768);

			_vertexBuffer = _dilApi.CreateBuffer<Vector3>(_vertexCapacity, new BufferInfo
			{
				name = "Selection Outline VB",
				type = BufferHandleType.Vertex,
			});
			_indexBuffer = _dilApi.CreateBuffer<uint>(_indexCapacity, new BufferInfo
			{
				name = "Selection Outline IB",
				type = BufferHandleType.Index,
			});

			commandsDirty = true;
		}

		if (_indexCount != (uint)indices.Count)
		{
			_indexCount = (uint)indices.Count;
			commandsDirty = true;
		}

		if (positions.Count > 0)
		{
			// Guard the upload: an overrun here means inconsistent state, not a small buffer,
			// and writing past a buffer is an access violation inside UpdateBuffer.
			var vertexBuffer = ((DiligentBufferHandle)_vertexBuffer!).Buffer;
			var indexBuffer = ((DiligentBufferHandle)_indexBuffer!).Buffer;

			if (vertexBuffer == null || indexBuffer == null ||
				positions.Count > _vertexCapacity || indices.Count > _indexCapacity)
			{
				Console.WriteLine($"[selection] outline NOT uploaded: vertices {positions.Count}/{_vertexCapacity}, " +
					$"indices {indices.Count}/{_indexCapacity}, " +
					$"buffers {(vertexBuffer == null ? "VB=null " : "")}{(indexBuffer == null ? "IB=null" : "")}");

				_indexCount = 0;
				return true;
			}

			_dilApi.ImmediateContext.UpdateBuffer<Vector3>(vertexBuffer, 0,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions),
				global::Diligent.ResourceStateTransitionMode.Transition);
			_dilApi.ImmediateContext.UpdateBuffer<uint>(indexBuffer, 0,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices),
				global::Diligent.ResourceStateTransitionMode.Transition);
		}

		return commandsDirty;
	}

	/// <summary>Pass body: silhouette mask, frame copy, outline composite into ColorTarget.</summary>
	public void Draw(ICommandBuffer cmd)
	{
		if (_indexCount == 0 || _vertexBuffer == null || _indexBuffer == null)
		{
			return;
		}

		var size = _maskTarget.Size;
		var width = (uint)Math.Max(1f, size.X);
		var height = (uint)Math.Max(1f, size.Y);

		cmd.SetRenderTarget(_maskTarget, null);
		cmd.ClearRenderTarget(_maskTarget, Vector4.Zero);
		cmd.SetViewport(width, height);
		cmd.SetPipelineState(_maskMaterial);
		cmd.CommitShaderResources(_maskMaterial);
		cmd.SetVertexBuffers(0, [_vertexBuffer], [0ul], SetVertexBuffersFlags.Reset);
		cmd.SetIndexBuffer(_indexBuffer);
		cmd.DrawIndexed(0, _indexCount, 0, 0, 1, IndexType.UInt32);

		cmd.CopyTexture(_colorTarget, _sceneScratch);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(width, height);
		cmd.SetPipelineState(_compositeMaterial);
		cmd.CommitShaderResources(_compositeMaterial);
		cmd.Draw(3);
	}

	/// <summary>Resizes the overlay targets; call after the viewport's GPU barrier, since Resize
	/// recreates the native textures and the composite SRB must be rebound.</summary>
	public void Resize(Vector2 newSize)
	{
		_maskTarget.Resize(newSize);
		_sceneScratch.Resize(newSize);
		_compositeMaterial.SetTexture("_MaskTex", _maskTarget);
		_compositeMaterial.SetTexture("_SceneTex", _sceneScratch);
	}

	public void Dispose()
	{
		_maskMaterial.Release();
		_compositeMaterial.Release();
		_maskTarget.Release();
		_sceneScratch.Release();
		_vertexBuffer?.Release();
		_indexBuffer?.Release();
	}
}
