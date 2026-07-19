using System;
using System.IO;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Глобальный менеджер Pipeline State Object (PSO) кэша.
/// Рекомендуется создавать один экземпляр на приложение (например, внутри DiligentGraphicsPipeline).
/// </summary>
public class DiligentPsoManager : IDisposable
{
	private readonly IRenderDevice _device;
	private readonly string _cacheFilePath;
	private IPipelineStateCache? _psoCache;

	public DiligentPsoManager(IRenderDevice device, string cacheFilePath)
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_cacheFilePath = cacheFilePath;

		InitializeCache();
	}

	private unsafe void InitializeCache()
	{
		byte[] cacheData = File.Exists(_cacheFilePath) ? File.ReadAllBytes(_cacheFilePath) : Array.Empty<byte>();

		if (cacheData.Length > 0)
		{
			fixed (byte* ptr = cacheData)
			{
				var createInfo = new PipelineStateCacheCreateInfo
				{
					Desc = new PipelineStateCacheDesc
					{
						Name = "Global PSO Cache",
						Mode = PsoCacheMode.LoadStore,
						Flags = PsoCacheFlags.None
					},
					CacheData = new IntPtr(ptr),
					CacheDataSize = (uint)cacheData.Length
				};
				_psoCache = _device.CreatePipelineStateCache(createInfo);
			}
		}
		else
		{
			// Файла нет, создаем кэш только для сохранения
			var createInfo = new PipelineStateCacheCreateInfo
			{
				Desc = new PipelineStateCacheDesc
				{
					Name = "Global PSO Cache",
					Mode = PsoCacheMode.Store,
					Flags = PsoCacheFlags.None
				},
				CacheData = IntPtr.Zero,
				CacheDataSize = 0
			};
			_psoCache = _device.CreatePipelineStateCache(createInfo);
		}
	}

	/// <summary>
	/// Создает Graphics Pipeline State с использованием глобального кэша.
	/// </summary>
	public IPipelineState CreateGraphicsPipelineState(GraphicsPipelineStateCreateInfo createInfo)
	{
		createInfo.PSOCache = _psoCache;
		return _device.CreateGraphicsPipelineState(createInfo);
	}

	/// <summary>
	/// Создает Compute Pipeline State с использованием глобального кэша.
	/// </summary>
	public IPipelineState CreateComputePipelineState(ComputePipelineStateCreateInfo createInfo)
	{
		createInfo.PSOCache = _psoCache;
		return _device.CreateComputePipelineState(createInfo);
	}

	/// <summary>
	/// Сохраняет накопленный кэш на диск. Нужно вызывать при выходе из игры или при завершении загрузки уровня.
	/// </summary>
	public unsafe void SaveCache()
	{
		if (_psoCache == null)
		{
			return;
		}

		var dataBlob = _psoCache.GetData();
		if (dataBlob != null)
		{
			try
			{
				var dataSpan = new ReadOnlySpan<byte>(dataBlob.GetDataPtr().ToPointer(), (int)dataBlob.GetSize());
				File.WriteAllBytes(_cacheFilePath, dataSpan.ToArray());
			}
			finally
			{
				dataBlob.Dispose();
			}
		}
	}

	public void Dispose()
	{
		SaveCache();
		_psoCache?.Dispose();
	}
}