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

	private readonly DiligentGraphicsApi _api;
	private IPipelineState? _pipelineState;
	private IShaderResourceBinding? _srb;

	private readonly Dictionary<ShaderObjectType, IShader> _shaders = new();

	/// <summary>См. <see cref="IMaterialObject.OwnsShaders"/>. По умолчанию true - историческое
	/// поведение, на которое опираются все пассы движка.</summary>
	public bool OwnsShaders { get; set; } = true;
	private readonly Dictionary<string, DiligentBufferHandle> _constantBuffers = new();
	private readonly Dictionary<string, IDeviceObject> _pendingResources = new();
	private readonly Dictionary<string, ImmutableSamplerDesc> _immutableSamplers = new();

	private readonly ConcurrentDictionary<string, ShaderResourceVariableDesc> _variablesDesc = new();

	private readonly ConcurrentDictionary<string, List<IShaderResourceVariable>> _variables = new();

	private bool _isDirty = true;
	private GraphicsPipelineStateCreateInfo? _basePsoCreateInfo;
	private readonly object _psoRebuildLock = new object();

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

		SetBasePipelineState(graphicsState.CreateInfo);
	}

	private void SetBasePipelineState(GraphicsPipelineStateCreateInfo psoCreateInfo)
	{
		_basePsoCreateInfo = psoCreateInfo;
		_isDirty = true;
	}

	public void SetBuffer(string name, IBufferHandle bufferHandle, HandleAccess access = HandleAccess.Pixel)
	{
		if (bufferHandle is DiligentBufferHandle dilBuffer)
		{
			// БЕЗ Compute-стадии: у графического материала её не бывает, а буфер, созданный с
			// HandleAccess.Compute в access (UAV для компьют-пассов, см. кластерные буферы
			// DiligentBatchRenderer), тащил её через GetShaderType в дескриптор переменной.
			// Переменная с compute-стадией в лейауте ГРАФИЧЕСКОГО PSO ломает привязку на Vulkan -
			// остальные переменные сета молча оставались без дескрипторов (GPURenderInstances
			// "has never been updated", весь батч-дроу рисовал пустоту).
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
		if (sampler is not DiligentSamplerObject dilSampler)
		{
			return;
		}

		// DECA_MAT_DIAG=1 - трассировка привязки сэмплеров (диагностика мёртвых ручек bias/aniso).
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

		// Сравнение ВМЕСТЕ с Desc: прежний ранний выход по одним лишь стадиям молча глотал НОВЫЙ
		// дескриптор (смену анизотропии/bias на живом материале) - PSO оставался со старым.
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

			// Конфигурация immutable-сэмплеров - ЧАСТЬ ИМЕНИ, потому что имя - ключ дискового
			// PSO-кэша (D3D12 pipeline library, см. DiligentPsoManager). Сэмплеры живут в
			// рут-сигнатуре внутри кэшированного блоба, и совпадение имени возвращало блоб со
			// СТАРЫМИ сэмплерами первого запуска: ручки анизотропии и mip bias были мертвы, что ни
			// делай (замерено: кадры с ANISO=0/1 и MIPBIAS=+4 бит-в-бит, с DECA_PSO_CACHE=0 bias
			// сразу ожил). Сигнатура строится детерминированно (HashCode рандомизирован на процесс
			// и в ключ кэша не годится).
			var samplerSignature = "";
			foreach (var s in _immutableSamplers.Values)
			{
				samplerSignature += $"|{s.SamplerOrTextureName}:{(int)s.Desc.MinFilter}" +
					$":{(int)s.Desc.AddressU}:{s.Desc.MipLODBias:F2}:{s.Desc.MaxAnisotropy}:{s.Desc.MaxLOD:G3}";
			}

			// Сигнатура ЛЕЙАУТА ПЕРЕМЕННЫХ - тоже часть ключа кэша: набор переменных у живого
			// материала растёт (ресайз довешивает _SceneColor всем материалам модели), и блоб под
			// старым именем нёс рут-сигнатуру БЕЗ неё - библиотека отвергала создание, Diligent
			// возвращал null, а бинд null-PSO ронял процесс AV-ом на первом же ресайзе превью.
			// FNV, а не HashCode: тот рандомизирован на процесс и в дисковый ключ не годится.
			uint layoutHash = 2166136261;
			foreach (var name in _variablesDesc.Keys.OrderBy(n => n, StringComparer.Ordinal))
			{
				foreach (var c in name)
				{
					layoutHash = (layoutHash ^ c) * 16777619;
				}
			}

			psoDesc.Name = $"{Name} Material PSO{samplerSignature}|L{layoutHash:X8}";

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
			_pipelineState = _api.PsoManager.CreateGraphicsPipelineState(psoCreateInfo);

			// Протухший/несовместимый блоб дискового кэша - не повод ронять процесс: пересоздаём
			// БЕЗ кэша. null дальше по коду означал бинд null-PSO и AV без единого сообщения.
			if (_pipelineState is null)
			{
				Console.WriteLine($"[material] PSO '{psoDesc.Name}': кэш отверг создание - пересоздаю без кэша");
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

			_isDirty = false;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void SetPipelineState(IDeviceContext ctx)
	{
		// DECA_MAT_DIAG=2 - трассировка биндов PSO (охота на AV в реплее после ресайза).
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

	/// <summary>Идемпотентен: повторный вызов ничего не делает. Материал законно оказывается в
	/// нескольких коллекциях сразу (дефолтный материал модели раздаётся всем её null-материалам),
	/// и без обнуления полей второй Release дважды диспозил бы нативные SRB/PSO - то есть обращался
	/// к освобождённой памяти.</summary>
	public void Release()
	{
		_srb?.Dispose();
		_srb = null;
		_pipelineState?.Dispose();
		_pipelineState = null;
		// Только СВОИ шейдеры: у шареных владелец другой, и лишний Release здесь - это декремент
		// чужого счётчика ссылок и падение на следующем владельце (см. IMaterialObject.OwnsShaders).
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