using System;
using System.Collections.Generic;
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
	/// <summary>Диагностика: сколько графических PSO создано и во что это обошлось. Статика -
	/// сознательно: счётчик глобальный на процесс, его смысл именно в суммарной цене за запуск.</summary>
	public static long DiagCreateMs;
	public static int DiagCreateCount;

	/// <summary>Имена PSO, уже прошедшие через <see cref="_psoCache"/> В ЭТОМ ПРОЦЕССЕ - см.
	/// <see cref="CreateGraphicsPipelineState"/>. Не персистентно (в отличие от файла кэша) и живёт
	/// ровно для того, чтобы поймать ПОВТОРНОЕ создание PSO под уже виденным именем.</summary>
	private readonly HashSet<string> _seenPsoNames = new();
	private readonly object _seenPsoNamesLock = new();

	/// <summary>Создаёт графический PSO. Имя (см. DiligentMaterial.RebuildPipelineIfNeeded - оно
	/// целиком описывает конфигурацию: шейдеры, сэмплеры, лейаут переменных) обычным путём идёт
	/// через дисковый кэш, НО только первый раз за это же имя в рамках процесса - см. комментарий
	/// у <see cref="CreateComputePipelineState"/> про D3D12 pipeline library: тот же класс дефекта
	/// (загрузка по имени возвращает невалидный объект без ошибки и без null) бьёт и по графике,
	/// просто реже - раньше графические материалы под одним именем не пересоздавались В ПРЕДЕЛАХ
	/// ОДНОГО запуска. Теперь пересоздаются: смена техники AO (SsaoPassResources.Release + новый
	/// экземпляр под тем же литеральным именем "SSAO Composite Material"/"SSAO Material") или смена
	/// провайдера/качества нативного апскейлера освобождают материал и заводят новый под тем же
	/// именем, а библиотека PSO это имя уже зарегистрировала. Симптом идентичен компьютному -
	/// AV в IDeviceContext.SetPipelineState на СВЕЖЕМ, только что созданном материале (воспроизведено
	/// DECA_LOOP_TOGGLE=1: переключение AO Ssao/Gtao валит процесс на первой же перерисовке после
	/// тоггла; DECA_PSO_CACHE=0 - чисто). Первое создание каждого имени за процесс кэш всё ещё
	/// использует (даёт выигрыш холодного старта из файла), только ПОВТОРЫ идут мимо него.</summary>
	public IPipelineState CreateGraphicsPipelineState(GraphicsPipelineStateCreateInfo createInfo)
	{
		var name = createInfo.PSODesc.Name;
		bool reused;
		lock (_seenPsoNamesLock)
		{
			reused = !_seenPsoNames.Add(name);
		}

		createInfo.PSOCache = reused ? null : _psoCache;

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var pso = _device.CreateGraphicsPipelineState(createInfo);
		DiagCreateMs += sw.ElapsedMilliseconds;
		DiagCreateCount++;
		return pso;
	}

	/// <summary>
	/// Создает Compute Pipeline State БЕЗ дискового кэша - сознательно. D3D12 pipeline library
	/// для компьют-PSO, сохранённого другим запуском, возвращает НЕвалидный объект без единой
	/// ошибки и без null - бинд такого PSO падает AV-ом в реплее замороженных команд
	/// (воспроизводится двумя запусками full-loop подряд: первый пишет кэш, второй падает на
	/// "Light Cluster CS"; с DECA_PSO_CACHE=0 оба чистые). Графический путь от того же спасает
	/// null-фолбэк в DiligentMaterial - компьюту Diligent null не возвращает, ловить нечего.
	/// Выигрыша кэш тут и не давал: компьют-PSO без растровых состояний создаётся за миллисекунды
	/// из уже скомпилированного байткода.
	/// </summary>
	public IPipelineState CreateComputePipelineState(ComputePipelineStateCreateInfo createInfo)
	{
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
