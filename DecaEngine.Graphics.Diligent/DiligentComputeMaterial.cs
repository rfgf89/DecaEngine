using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentComputeMaterial : IComputeMaterial
{
	public string Name { get; }

	private readonly DiligentGraphicsApi _api;
	private IPipelineState? _pipelineState;
	private IShaderResourceBinding? _srb;

	private IShader? _computeShader;
	private readonly Dictionary<string, DiligentBufferHandle> _constantBuffers = new();
	private readonly Dictionary<string, IDeviceObject> _pendingResources = new();
	private readonly Dictionary<string, ImmutableSamplerDesc> _immutableSamplers = new();

	private readonly ConcurrentDictionary<string, ShaderResourceVariableDesc> _variablesDesc = new();
	private readonly ConcurrentDictionary<string, List<IShaderResourceVariable>> _variables = new();

	private bool _isDirty = true;
	private ComputePipelineStateCreateInfo? _basePsoCreateInfo;
	private readonly object _psoRebuildLock = new object();

	public DiligentComputeMaterial(string name, DiligentGraphicsApi api)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		Name = name;
	}

	public void SetShader(IShaderObject computeShader)
	{
		if (computeShader is not DiligentShader dilShader)
		{
			throw new ArgumentException("Shader must be a DiligentShader", nameof(computeShader));
		}

		if (computeShader.Type != ShaderObjectType.Compute)
		{
			throw new ArgumentException("Shader must be a Compute shader", nameof(computeShader));
		}

		_computeShader?.Release();

		dilShader.Compile();
		_computeShader = dilShader.NativeShader;
		_isDirty = true;
	}

	public void SetState(IStateObject stateObject)
	{
		ArgumentNullException.ThrowIfNull(stateObject);

		if (stateObject is not DiligentComputeStateObject computeState)
		{
			throw new ArgumentException(
				$"Unsupported state object '{stateObject.GetType().Name}'. Expected {nameof(DiligentComputeStateObject)}.",
				nameof(stateObject));
		}

		_basePsoCreateInfo = computeState.CreateInfo;
		_isDirty = true;
	}

	public void SetBuffer(string name, IBufferHandle bufferHandle)
	{
		if (bufferHandle is DiligentBufferHandle dilBuffer)
		{
			DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, ShaderType.Compute, ref _isDirty);

			if (dilBuffer.Info.type == BufferHandleType.Constant)
			{
				_pendingResources[name] = dilBuffer.Buffer;
			}
			else
			{
				_pendingResources[name] = dilBuffer.Buffer.GetDefaultView(dilBuffer.GetViewFlags(HandleAccess.Compute));
			}

			DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
		}
	}

	public void SetTexture(string name, IGpuTexture texture, bool isUnorderedAccess = false)
	{
		ITexture dilTexture = null;
		
		if (texture is DiligentGpuTexture t1) dilTexture = t1.Texture;
		else if (texture is DiligentRenderTarget rt) dilTexture = rt.Texture;

		if (dilTexture != null)
		{
			DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, ShaderType.Compute, ref _isDirty);

			var viewFlags = isUnorderedAccess ? TextureViewType.UnorderedAccess : TextureViewType.ShaderResource;
			_pendingResources[name] = dilTexture.GetDefaultView(viewFlags);

			DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
		}
	}

	public void SetSampler(string name, ISamplerObject sampler)
	{
		if (sampler is DiligentSamplerObject dilSampler)
		{
			if (_immutableSamplers.TryGetValue(name, out var existing))
			{
				return;
			}

			_immutableSamplers[name] = new ImmutableSamplerDesc
			{
				ShaderStages = ShaderType.Compute,
				SamplerOrTextureName = name,
				Desc = dilSampler.Desc
			};
			_isDirty = true;
		}
	}

	public unsafe void SetConstant<T>(string name, ref T data) where T : unmanaged
	{
		SetConstant(_api.ImmediateContext, name, ref data);
	}

	public unsafe void SetConstant<T>(int ctx, string name, ref T data) where T : unmanaged
	{
		SetConstant(_api.DeferredContexts[ctx], name, ref data);
	}

	public unsafe void SetConstant<T>(IDeviceContext ctx, string name, ref T data) where T : unmanaged
	{
		int size = Marshal.SizeOf<T>();
		int alignedSize = (size + 15) & ~15;

		if (!_constantBuffers.TryGetValue(name, out var buffer) || (int)buffer.SizeInBytes != alignedSize)
		{
			buffer?.Release();

			buffer = new DiligentBufferHandle(_api.Device);
			buffer.Alloc(new BufferInfo
			{
				name = name,
				dynamic = false,
				type = BufferHandleType.Constant,
				access = HandleAccess.Compute,
				sizeInBytes = (uint)alignedSize
			});

			_constantBuffers[name] = buffer;
		}

		ctx.UpdateBuffer(buffer.Buffer, 0, (uint)alignedSize, new IntPtr(Unsafe.AsPointer(ref data)), ResourceStateTransitionMode.Transition);

		DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, ShaderType.Compute, ref _isDirty);

		_pendingResources[name] = buffer.Buffer;

		DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
	}

	private void RebuildPipelineIfNeeded()
	{
		lock (_psoRebuildLock)
		{
			if (!_isDirty)
			{
				return;
			}

			if (_computeShader == null)
			{
				throw new InvalidOperationException("Compute shader must be set before dispatching");
			}

			var psoCreateInfo = _basePsoCreateInfo ?? new ComputePipelineStateCreateInfo();
			psoCreateInfo.PSODesc.Name = $"{Name} Compute PSO";
			psoCreateInfo.PSODesc.PipelineType = PipelineType.Compute;
			psoCreateInfo.Cs = _computeShader;

			var allVariables = new List<ShaderResourceVariableDesc>();
			
			foreach (var vDesc in _variablesDesc.Values)
			{
				allVariables.Add(vDesc);
			}

			var layout = psoCreateInfo.PSODesc.ResourceLayout;
			layout.DefaultVariableType = ShaderResourceVariableType.Mutable;
			layout.Variables = allVariables.ToArray();
			layout.ImmutableSamplers = _immutableSamplers.Values.ToArray();
			psoCreateInfo.PSODesc.ResourceLayout = layout;

			_pipelineState?.Dispose();
			_pipelineState = _api.PsoManager.CreateComputePipelineState(psoCreateInfo);

			// Протухший блоб дискового PSO-кэша (изменившийся байткод шейдера под старым именем) - не
			// повод ронять процесс: пересоздаём БЕЗ кэша. null дальше по коду - это бинд null-PSO и AV
			// без единого сообщения (ровно как в графическом RebuildPipelineIfNeeded).
			if (_pipelineState is null)
			{
				Console.WriteLine($"[compute-material] PSO '{psoCreateInfo.PSODesc.Name}': кэш отверг создание - пересоздаю без кэша");
				_pipelineState = _api.Device.CreateComputePipelineState(psoCreateInfo);
			}

			if (_pipelineState is null)
			{
				throw new InvalidOperationException($"Failed to create compute PSO '{psoCreateInfo.PSODesc.Name}'.");
			}

			_variables.Clear();

			_srb?.Dispose();
			_srb = _pipelineState.CreateShaderResourceBinding(true);

			foreach (var varDesc in allVariables)
			{
				var list = new List<IShaderResourceVariable>();
				var v = _srb.GetVariableByName(ShaderType.Compute, varDesc.Name);
				if (v != null) list.Add(v);

				if (list.Count > 0)
				{
					_variables[varDesc.Name] = list;
				}
			}

			foreach (var pendingResource in _pendingResources)
			{
				if (_variables.TryGetValue(pendingResource.Key, out var variables))
				{
					foreach (var variable in variables)
					{
						variable.Set(pendingResource.Value, SetShaderResourceFlags.AllowOverwrite);
					}
				}
			}

			_isDirty = false;
		}
	}

	public void SetPipelineState(IDeviceContext context)
	{
		// DECA_MAT_DIAG=2 - трассировка биндов PSO, как у графического материала (охота на AV
		// в реплее замороженных команд: последний напечатанный - виновник).
		if (Environment.GetEnvironmentVariable("DECA_MAT_DIAG") == "2")
		{
			Console.WriteLine($"[matdiag] compute SetPipelineState '{Name}' dirty={_isDirty}");
			Console.Out.Flush();
		}

		RebuildPipelineIfNeeded();
		context.SetPipelineState(_pipelineState);
	}

	public void CommitShaderResources(IDeviceContext context)
	{
		RebuildPipelineIfNeeded();
		context.CommitShaderResources(_srb, ResourceStateTransitionMode.Transition);
	}

	public void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ)
	{
		RebuildPipelineIfNeeded();

		var ctx = _api.ImmediateContext;
		ctx.SetPipelineState(_pipelineState);
		ctx.CommitShaderResources(_srb, ResourceStateTransitionMode.Transition);

		var dispatchAttrs = new DispatchComputeAttribs
		{
			ThreadGroupCountX = threadGroupCountX,
			ThreadGroupCountY = threadGroupCountY,
			ThreadGroupCountZ = threadGroupCountZ
		};

		ctx.DispatchCompute(dispatchAttrs);
	}

	public void Release()
	{
		_srb?.Dispose();
		_pipelineState?.Dispose();
		_computeShader?.Release();

		foreach (var buffer in _constantBuffers.Values)
		{
			buffer.Release();
		}
		_constantBuffers.Clear();
		_variables.Clear();
		_pendingResources.Clear();
		_variablesDesc.Clear();
	}
}