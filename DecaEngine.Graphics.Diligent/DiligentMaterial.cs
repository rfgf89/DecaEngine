using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

public class DiligentMaterial : IMaterialObject
{
	public string Name { get; }

	private readonly DiligentGraphicsApi _api;
	private IPipelineState? _pipelineState;
	private IShaderResourceBinding? _srb;

	private readonly Dictionary<ShaderObjectType, IShader> _shaders = new();

	public bool OwnsShaders { get; set; } = true;
	private readonly Dictionary<string, DiligentBufferHandle> _constantBuffers = new();
	private readonly Dictionary<string, IDeviceObject> _pendingResources = new();

	// Array variables (Texture2D name[N]); bound with one SetArray, unlike _pendingResources.
	private readonly Dictionary<string, IDeviceObject[]> _pendingResourceArrays = new();
	private readonly Dictionary<string, ImmutableSamplerDesc> _immutableSamplers = new();

	private readonly ConcurrentDictionary<string, ShaderResourceVariableDesc> _variablesDesc = new();

	private readonly ConcurrentDictionary<string, List<IShaderResourceVariable>> _variables = new();

	private bool _isDirty = true;
	private GraphicsPipelineStateCreateInfo? _basePsoCreateInfo;

	// Source of _basePsoCreateInfo; state objects are shared, so object identity keys the PSO.
	private object? _baseStateOwner;

	// PSO is owned by the manager and shared: must not be disposed here.
	private bool _pipelineStateIsShared;

	private readonly object _psoRebuildLock = new object();

	// DECA_PSO_SHARE=0 falls back to one PSO per material, for diagnostics.
	private static readonly bool SharePsos = Environment.GetEnvironmentVariable("DECA_PSO_SHARE") != "0";

	public DiligentMaterial(string name, DiligentGraphicsApi api)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
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

	public void SetState(IStateObject stateObject)
	{
		ArgumentNullException.ThrowIfNull(stateObject);

		if (stateObject is not DiligentGraphicsStateObject graphicsState)
		{
			throw new ArgumentException($"Unsupported state object '{stateObject.GetType().Name}'. Expected {nameof(DiligentGraphicsStateObject)}.", nameof(stateObject));
		}

		SetBasePipelineState(graphicsState.CreateInfo, graphicsState);
	}

	private void SetBasePipelineState(GraphicsPipelineStateCreateInfo psoCreateInfo, object? owner = null)
	{
		_basePsoCreateInfo = psoCreateInfo;
		// With no state object the createInfo itself is the identity: same lifetime, same config.
		_baseStateOwner = owner ?? psoCreateInfo;
		_isDirty = true;
	}

	public void SetBuffer(string name, IBufferHandle bufferHandle, HandleAccess access = HandleAccess.Pixel)
	{
		if (bufferHandle is DiligentBufferHandle dilBuffer)
		{
			// Compute stage must be masked off: in a graphics PSO layout it silently breaks
			// binding on Vulkan, leaving the other variables of the set without descriptors.
			var shaderStages = (dilBuffer.GetShaderType() | DiligentGraphicsUtility.AccessToShaderType(access))
				& ~ShaderType.Compute;
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

	/// <summary>Binds a TLAS for inline ray tracing; pixel stage only (see SetBuffer). The descriptor
	/// points at the TLAS object, so rebuilding it does not invalidate the binding.</summary>
	public void SetAccelStructure(string name, ITopLevelAS tlas)
	{
		if (tlas == null)
		{
			return;
		}

		DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, ShaderType.Pixel, ref _isDirty);
		_pendingResources[name] = tlas;
		DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
	}

	/// <summary>Binds a raw structured buffer as a pixel-stage SRV; pixel-only for the same reason as
	/// <see cref="SetAccelStructure"/>.</summary>
	public void SetStructuredBufferSrv(string name, IBuffer buffer)
	{
		if (buffer == null)
		{
			return;
		}

		DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, ShaderType.Pixel, ref _isDirty);
		_pendingResources[name] = buffer.GetDefaultView(BufferViewType.ShaderResource);
		DiligentGraphicsUtility.UpdatePendingResources(_isDirty, _variables, name, _pendingResources);
	}

	/// <summary>Binds a texture array to `Texture2D name[N]`. Count must equal N and every slot needs
	/// a live view - Vulkan rejects empty descriptors, so pad with any valid texture.</summary>
	public void SetTextureSrvArray(string name, IReadOnlyList<IGpuTexture> textures)
	{
		if (textures == null || textures.Count == 0)
		{
			return;
		}

		var views = new IDeviceObject[textures.Count];
		for (int i = 0; i < textures.Count; i++)
		{
			ITexture native = textures[i] switch
			{
				DiligentGpuTexture t => t.Texture,
				DiligentRenderTarget rt => rt.Texture,
				_ => null,
			};

			if (native == null)
			{
				return;
			}

			views[i] = native.GetDefaultView(TextureViewType.ShaderResource);
		}

		DiligentGraphicsUtility.UpdateVariableDesc(_variablesDesc, name, ShaderType.Pixel, ref _isDirty);
		_pendingResourceArrays[name] = views;
		if (!_isDirty && _variables.TryGetValue(name, out var vars))
		{
			foreach (var variable in vars)
			{
				variable.SetArray(views, 0, SetShaderResourceFlags.AllowOverwrite);
			}
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
		if (sampler is not DiligentSamplerObject dilSampler)
		{
			return;
		}

		// DECA_MAT_DIAG=1 traces sampler binding.
		if (Environment.GetEnvironmentVariable("DECA_MAT_DIAG") == "1")
		{
			var attached = name.EndsWith("_sampler") &&
				_pendingResources.TryGetValue(name.Substring(0, name.Length - 8), out var r) && r is ITextureView;
			Console.WriteLine($"[matdiag] SetSampler {name}: bias={dilSampler.Desc.MipLODBias:F1} " +
				$"filter={dilSampler.Desc.MinFilter} viewAttached={attached}");
		}

		var shaderStages = DiligentGraphicsUtility.AccessToShaderType(access);
		if (shaderStages == ShaderType.Unknown)
		{
			shaderStages = ShaderType.Pixel;
		}

		if (_immutableSamplers.Remove(name))
		{
			_isDirty = true;
		}

		_pendingResources[name] = dilSampler.Sampler;

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

	public void SetImmutableSampler(string name, ISamplerObject sampler, HandleAccess access = HandleAccess.Pixel)
	{
		if (sampler is not DiligentSamplerObject dilSampler)
		{
			return;
		}

		var shaderStages = DiligentGraphicsUtility.AccessToShaderType(access);
		if (shaderStages == ShaderType.Unknown) shaderStages = ShaderType.Pixel;

		// Desc must be part of the check: comparing stages alone swallows aniso/bias changes.
		if (_immutableSamplers.TryGetValue(name, out var existing) &&
		    existing.ShaderStages == shaderStages && existing.Desc.Equals(dilSampler.Desc))
		{
			return;
		}

		_immutableSamplers[name] = new ImmutableSamplerDesc
		{
			ShaderStages = shaderStages,
			SamplerOrTextureName = name,
			Desc = dilSampler.Desc,
		};

		_isDirty = true;
	}

	public unsafe void SetConstant<T>(string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged
	{
		SetConstant(_api.ImmediateContext, name, ref data, access);
	}

	public unsafe void SetConstant<T>(int ctx, string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged
	{
		SetConstant(_api.DeferredContexts[ctx], name, ref data, access);
	}

	public unsafe void SetConstant<T>(IDeviceContext ctx, string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged
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

			// Samplers live in the root signature inside the cached PSO blob, so they must be part
			// of the PSO name. Sorted and built without HashCode: the key must be deterministic.
			var samplerSignature = "";
			foreach (var s in _immutableSamplers.Values.OrderBy(s => s.SamplerOrTextureName, StringComparer.Ordinal))
			{
				samplerSignature += $"|{s.SamplerOrTextureName}:{(int)s.Desc.MinFilter}" +
					$":{(int)s.Desc.AddressU}:{s.Desc.MipLODBias:F2}:{s.Desc.MaxAnisotropy}:{s.Desc.MaxLOD:G3}";
			}

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

			// The PSO name keys the disk cache (D3D12 pipeline library), so it must describe the
			// configuration and nothing else: state object (S), shaders (P), immutable samplers and
			// the variable layout (V). Anything omitted makes a material run someone else's PSO.
			var shaderSignature = "";
			foreach (var shader in _shaders.OrderBy(s => (int)s.Key))
			{
				shaderSignature += $"|{(int)shader.Key}:{_api.PsoManager.ObjectId(shader.Value)}";
			}

			var variableSignature = "";
			foreach (var v in allVariables.OrderBy(v => v.Name, StringComparer.Ordinal))
			{
				variableSignature += $"|{v.Name}:{(int)v.ShaderStages}:{(int)v.Type}";
			}

			var psoKey = $"S{_api.PsoManager.ObjectId(_baseStateOwner!)}|P{shaderSignature}" +
				$"{samplerSignature}|V{variableSignature}";

			psoDesc.Name = SharePsos ? "Material PSO|" + psoKey : $"{Name} Material PSO|{psoKey}";
			psoCreateInfo.PSODesc = psoDesc;

			// Static variables live in the PSO, not the SRB: sharing one would let materials
			// overwrite each other's resources.
			bool shareable = SharePsos && !allVariables.Any(v => v.Type == ShaderResourceVariableType.Static);

			ReleaseOwnedPipelineState();
			_pipelineStateIsShared = shareable;
			_pipelineState = shareable
				? _api.PsoManager.GetOrCreateSharedGraphicsPipelineState(psoKey, psoCreateInfo)
				: _api.PsoManager.CreateGraphicsPipelineState(psoCreateInfo);

			// A stale disk-cache blob must not be fatal: retry without the cache, since a null PSO
			// would be bound and crash later.
			if (_pipelineState is null)
			{
				Console.WriteLine($"[material] PSO '{psoDesc.Name}': cache rejected creation - recreating without the cache");
				_pipelineStateIsShared = false;
				_pipelineState = _api.Device.CreateGraphicsPipelineState(psoCreateInfo);
			}

			if (_pipelineState is null)
			{
				throw new InvalidOperationException($"Failed to create PSO '{psoDesc.Name}'.");
			}

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

			foreach (var pendingArray in _pendingResourceArrays)
			{
				if (_variables.TryGetValue(pendingArray.Key, out var variables))
				{
					foreach (var variable in variables)
						variable.SetArray(pendingArray.Value, 0, SetShaderResourceFlags.AllowOverwrite);
				}
			}

			_isDirty = false;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void SetPipelineState(IDeviceContext ctx)
	{
		// DECA_MAT_DIAG=2 traces PSO binds.
		if (Environment.GetEnvironmentVariable("DECA_MAT_DIAG") == "2")
		{
			Console.WriteLine($"[matdiag] SetPipelineState '{Name}' dirty={_isDirty}");
			Console.Out.Flush();
		}

		RebuildPipelineIfNeeded();
		ctx.SetPipelineState(_pipelineState);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void CommitShaderResources(IDeviceContext ctx, ResourceStateTransitionMode transition = ResourceStateTransitionMode.Transition)
	{
		ctx.CommitShaderResources(_srb, transition);
	}

	// Nulls the fields so a repeated Release is a no-op; a shared PSO is never disposed here.
	private void ReleaseOwnedPipelineState()
	{
		if (!_pipelineStateIsShared)
		{
			_pipelineState?.Dispose();
		}

		_pipelineState = null;
		_pipelineStateIsShared = false;
	}

	public void Release()
	{
		_srb?.Dispose();
		_srb = null;
		ReleaseOwnedPipelineState();
		// Owned shaders only: releasing a shared one decrements someone else's refcount.
		if (OwnsShaders)
		{
			foreach (var shader in _shaders.Values) shader.Release();
		}
		foreach (var buffer in _constantBuffers.Values) buffer.Release();
		_constantBuffers.Clear();
		_variables.Clear();
		_pendingResources.Clear();
		_variablesDesc.Clear();
	}
}