using System;
using System.IO;
using Diligent;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Глобальный менеджер Pipeline State Object (PSO) кэша.
/// Рекомендуется создавать один экземпляр на приложение (например, внутри DiligentGraphicsPipeline).
///
/// Файл кэша обязан быть ПЕР-БЭКЕНДНЫМ (см. DiligentGraphicsApi.Initialize): D3D12 pipeline
/// library и Vulkan pipeline cache - несовместимые бинарные форматы, а редактор (D3D12) и
/// CLI-пробы (Vulkan) запускаются из одной рабочей директории. Загрузка чужих байт в лучшем
/// случае молча отбрасывается драйвером, в худшем - ломает создание PSO.
///
/// Переменные окружения:
///   DECA_PSO_CACHE=0     - полностью отключить кэш (PSO создаются без него);
///   DECA_PSO_CACHE=clear - удалить файл кэша при старте (одноразовая чистка каждым запуском).
/// </summary>
public class DiligentPsoManager : IDisposable
{
	private readonly IRenderDevice _device;
	private readonly string _cacheFilePath;
	private readonly bool _disabled;
	private IPipelineStateCache? _psoCache;

	public DiligentPsoManager(IRenderDevice device, string cacheFilePath)
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_cacheFilePath = cacheFilePath;

		var mode = Environment.GetEnvironmentVariable("DECA_PSO_CACHE");
		_disabled = mode == "0";
		if (_disabled)
		{
			return;
		}

		if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
		{
			TryDeleteCacheFile();
		}

		InitializeCache();
	}

	private unsafe void InitializeCache()
	{
		byte[] cacheData;
		try
		{
			cacheData = File.Exists(_cacheFilePath) ? File.ReadAllBytes(_cacheFilePath) : Array.Empty<byte>();
		}
		catch (IOException)
		{
			cacheData = Array.Empty<byte>();
		}

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

				// Битый/чужой/устаревший (после апдейта драйвера) файл не должен убивать запуск и
				// не должен переживать неудачную загрузку - удаляем и стартуем с пустым кэшем.
				try
				{
					_psoCache = _device.CreatePipelineStateCache(createInfo);
				}
				catch
				{
					_psoCache = null;
				}

				if (_psoCache is null)
				{
					TryDeleteCacheFile();
				}
			}
		}

		if (_psoCache is null)
		{
			// Файла нет (или загрузка не удалась) - создаем кэш только для сохранения.
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
			catch (IOException)
			{
				// Некритично: следующий запуск просто скомпилирует PSO заново.
			}
			finally
			{
				dataBlob.Dispose();
			}
		}
		else
		{
			// Сериализация не удалась ("Failed to serialize D3D12 pipeline library") - файл на
			// диске, если есть, уже не соответствует накопленному состоянию и при следующем
			// запуске загрузится протухшим. Лучше без кэша, чем с невалидным.
			TryDeleteCacheFile();
		}
	}

	private void TryDeleteCacheFile()
	{
		try
		{
			if (File.Exists(_cacheFilePath))
			{
				File.Delete(_cacheFilePath);
			}
		}
		catch (IOException)
		{
		}
	}

	public void Dispose()
	{
		SaveCache();
		_psoCache?.Dispose();
	}
}
