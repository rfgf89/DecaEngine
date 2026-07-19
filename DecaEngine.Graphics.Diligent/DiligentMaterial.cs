using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentMaterial : IMaterialObject
{
	public string Name { get; }

	private readonly DiligentGraphicsPipeline _pipeline;
	private IPipelineState? _pipelineState;
	private IShaderResourceBinding? _srb;

	private readonly Dictionary<ShaderObjectType, IShader> _shaders = new();
	private readonly Dictionary<string, DiligentBufferHandle> _constantBuffers = new();
	private readonly Dictionary<string, IDeviceObject> _pendingResources = new();
	private readonly Dictionary<string, ImmutableSamplerDesc> _immutableSamplers = new();

	private readonly ConcurrentDictionary<string, ShaderResourceVariableDesc> _variablesDesc = new();

	private readonly ConcurrentDictionary<string, List<IShaderResourceVariable>> _variables = new();

	private bool _isDirty = true;
	private GraphicsPipelineStateCreateInfo? _basePsoCreateInfo;
	private readonly object _psoRebuildLock = new object();

	public DiligentMaterial(string name, DiligentGraphicsPipeline pipeline)
	{
		_pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
		Name = name;
	}

	public void SetShader(IShaderObject shader)
	{
		if (shader is not DiligentShader dilShader)
		{
			throw new ArgumentException("Shader must be a DiligentShader", nameof(shader));
		}

		if (_shaders.TryGetValue(shader.Type, out var value))
		{
			value?.Release();
		}

		dilShader.Compile();
		_shaders[shader.Type] = dilShader.NativeShader;
		_isDirty = true;
	}

	public void SetShader(params IShaderObject[] shaders)
	{
		foreach (var shader in shaders)
		{
			SetShader(shader);
		}
	}

	public void SetBasePipelineState(GraphicsPipelineStateCreateInfo psoCreateInfo)
	{
		_basePsoCreateInfo = psoCreateInfo;
		_isDirty = true;
	}

	public void SetBuffer(string name, IBufferHandle bufferHandle, HandleAccess access = HandleAccess.Pixel)
	{
		if (bufferHandle is DiligentBufferHandle dilBuffer)
		{
			var shaderStages = dilBuffer.GetShaderType() | DiligentGraphicsUtility.AccessToShaderType(access);
			DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, shaderStages, ref _isDirty);

			if (dilBuffer.Info.type == BufferHandleType.Constant)
			{
				_pendingResources[name] = dilBuffer.Buffer;
			}
			else
			{
				_pendingResources[name] = dilBuffer.Buffer.GetDefaultView(dilBuffer.GetViewFlags(access));
			}

			DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
		}
	}

	public void SetTexture(string name, IGpuTexture texture, HandleAccess access = HandleAccess.Pixel)
	{
		ITexture dilTexture = null;
		
		if (texture is DiligentGpuTexture t1) dilTexture = t1.Texture;
		else if (texture is DiligentRenderTarget rt) dilTexture = rt.Texture;

		if (dilTexture != null)
		{
			var shaderStages = DiligentGraphicsUtility.AccessToShaderType(access);
			DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, shaderStages, ref _isDirty);

			var viewFlags = access == HandleAccess.Compute ? TextureViewType.UnorderedAccess : TextureViewType.ShaderResource;
			var view = dilTexture.GetDefaultView(viewFlags);
			
			// Если для этой текстуры уже был задан динамический семплер, привязываем его к view
			if (_pendingResources.TryGetValue(name + "_sampler", out var res) && res is ISampler sampler)
			{
				view.SetSampler(sampler);
			}

			_pendingResources[name] = view;
			DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
		}
	}

	public void SetSampler(string name, ISamplerObject sampler, HandleAccess access = HandleAccess.Pixel)
	{
		SetSampler(name, sampler, access, true);
	}

	public void SetSampler(string name, ISamplerObject sampler, HandleAccess access, bool immutable)
	{
		if (sampler is DiligentSamplerObject dilSampler)
		{
			var shaderStages = DiligentGraphicsUtility.AccessToShaderType(access);
			if (shaderStages == ShaderType.Unknown) shaderStages = ShaderType.Pixel;

			if (immutable)
			{
				if (_immutableSamplers.TryGetValue(name, out var existing))
				{
					if (existing.ShaderStages == shaderStages) return;
				}

				_immutableSamplers[name] = new ImmutableSamplerDesc
				{
					ShaderStages = shaderStages,
					SamplerOrTextureName = name,
					Desc = dilSampler.Desc,
				};
				_isDirty = true;
			}
			else
			{
				if (_immutableSamplers.Remove(name)) _isDirty = true;

				_pendingResources[name] = dilSampler.Sampler;

				// Привязываем семплер к текстуре, если она уже существует в ресурсах
				if (name.EndsWith("_sampler"))
				{
					string textureName = name.Substring(0, name.Length - 8);
					if (_pendingResources.TryGetValue(textureName, out var res) && res is ITextureView view)
					{
						view.SetSampler(dilSampler.Sampler);
					}
				}

				DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, shaderStages, ref _isDirty);
				DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
			}
		}
	}

	public unsafe void SetConstant<T>(string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged
	{
		SetConstant(_pipeline.ImmediateContext, name, ref data, access);
	}

	public unsafe void SetConstant<T>(int ctx, string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged
	{
		SetConstant(_pipeline.DeferredContexts[ctx], name, ref data, access);
	}

	public unsafe void SetConstant<T>(IDeviceContext ctx, string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged
	{
		int size = Marshal.SizeOf<T>();
		int alignedSize = (size + 15) & ~15;

		if (!_constantBuffers.TryGetValue(name, out var buffer) || (int)buffer.SizeInBytes != alignedSize)
		{
			buffer?.Release();

			buffer = new DiligentBufferHandle(_pipeline.Device);
			buffer.Alloc(new BufferInfo
			{
				name = name,
				dynamic = false,
				type = BufferHandleType.Constant,
				access = access,
				sizeInBytes = (uint)alignedSize
			});

			_constantBuffers[name] = buffer;
		}

		ctx.UpdateBuffer(buffer.Buffer, 0, (uint)alignedSize, new IntPtr(Unsafe.AsPointer(ref data)), ResourceStateTransitionMode.Transition);

		var shaderStages = buffer.GetShaderType() | DiligentGraphicsUtility.AccessToShaderType(access);
		DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, shaderStages, ref _isDirty);

		_pendingResources[name] = buffer.Buffer;

		DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
	}

	private void RebuildPipelineIfNeeded()
	{
		lock (_psoRebuildLock)
		{
			if (!_isDirty) return;
			if (_basePsoCreateInfo == null) return;

			var psoCreateInfo = new GraphicsPipelineStateCreateInfo();
			psoCreateInfo.PSODesc = _basePsoCreateInfo.PSODesc;
			psoCreateInfo.GraphicsPipeline = _basePsoCreateInfo.GraphicsPipeline;
			
			var psoDesc = psoCreateInfo.PSODesc;
			psoDesc.Name = $"{Name} Material PSO";

			foreach (var shader in _shaders)
			{
				switch (shader.Key)
				{
					case ShaderObjectType.Vertex: psoCreateInfo.Vs = shader.Value; break;
					case ShaderObjectType.Pixel: psoCreateInfo.Ps = shader.Value; break;
					case ShaderObjectType.Geometry: psoCreateInfo.Gs = shader.Value; break;
					case ShaderObjectType.Domain: psoCreateInfo.Ds = shader.Value; break;
					case ShaderObjectType.Hull: psoCreateInfo.Hs = shader.Value; break;
				}
			}

			var allVariables = new List<ShaderResourceVariableDesc>();
			if (_basePsoCreateInfo.PSODesc.ResourceLayout.Variables != null)
			{
				allVariables.AddRange(_basePsoCreateInfo.PSODesc.ResourceLayout.Variables);
			}

			foreach (var vDesc in _variablesDesc.Values)
			{
				var existingIdx = allVariables.FindIndex(v => v.Name == vDesc.Name);
				if (existingIdx < 0) allVariables.Add(vDesc);
				else
				{
					var existing = allVariables[existingIdx];
					existing.ShaderStages |= vDesc.ShaderStages;
					existing.Type = ShaderResourceVariableType.Mutable;
					allVariables[existingIdx] = existing;
				}
			}

			var layout = psoDesc.ResourceLayout;
			layout.DefaultVariableType = ShaderResourceVariableType.Mutable;
			layout.Variables = allVariables.ToArray();
			layout.ImmutableSamplers = _immutableSamplers.Values.ToArray();
			psoDesc.ResourceLayout = layout;
			psoCreateInfo.PSODesc = psoDesc;

			_pipelineState?.Dispose();
			_pipelineState = _pipeline.PsoManager.CreateGraphicsPipelineState(psoCreateInfo);
			_variables.Clear();

			var stagesToTry = new[] { ShaderType.Vertex, ShaderType.Pixel, ShaderType.Compute, ShaderType.Geometry, ShaderType.Domain, ShaderType.Hull };

			foreach (var varDesc in allVariables)
			{
				if (varDesc.Type == ShaderResourceVariableType.Static)
				{
					var list = new List<IShaderResourceVariable>();
					foreach (var stage in stagesToTry)
					{
						if ((varDesc.ShaderStages & stage) != 0)
						{
							var v = _pipelineState.GetStaticVariableByName(stage, varDesc.Name);
							if (v != null) list.Add(v);
						}
					}
					if (list.Count > 0) _variables[varDesc.Name] = list;
				}
			}

			_srb?.Dispose();
			_srb = _pipelineState.CreateShaderResourceBinding(true);

			foreach (var varDesc in allVariables)
			{
				if (varDesc.Type != ShaderResourceVariableType.Static)
				{
					var list = new List<IShaderResourceVariable>();
					foreach (var stage in stagesToTry)
					{
						if ((varDesc.ShaderStages & stage) != 0)
						{
							var v = _srb.GetVariableByName(stage, varDesc.Name);
							if (v != null) list.Add(v);
						}
					}
					if (list.Count > 0) _variables[varDesc.Name] = list;
				}
			}

			foreach (var pendingResource in _pendingResources)
			{
				if (_variables.TryGetValue(pendingResource.Key, out var variables))
				{
					foreach (var variable in variables)
						variable.Set(pendingResource.Value, SetShaderResourceFlags.AllowOverwrite);
				}
			}

			_isDirty = false;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void SetPipelineState(IDeviceContext ctx)
	{
		RebuildPipelineIfNeeded();
		ctx.SetPipelineState(_pipelineState);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void CommitShaderResources(IDeviceContext ctx, ResourceStateTransitionMode transition = ResourceStateTransitionMode.Transition)
	{
		ctx.CommitShaderResources(_srb, transition);
	}

	public void Release()
	{
		_srb?.Dispose();
		_pipelineState?.Dispose();
		foreach (var shader in _shaders.Values) shader.Release();
		foreach (var buffer in _constantBuffers.Values) buffer.Release();
		_constantBuffers.Clear();
		_variables.Clear();
		_pendingResources.Clear();
		_variablesDesc.Clear();
	}
}