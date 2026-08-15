using System;
using System.IO;
using System.IO.Compression;

namespace DecaEngine.Editor;

/// <summary>Разворачивает нативные библиотеки (шим апскейлеров DecaFfxShim.dll, FSR
/// amd_fidelityfx_upscaler_dx12.dll, DLSS nvngx_dlss.dll) из папки <c>NativeLibrary</c> в корне
/// репозитория в каталог экзешника при старте. Без этого каждая пересборка в чистый bin теряла
/// бы их, а нативные бэкенды молча откатывались бы на TAAU.
///
/// Копирование ленивое: только когда файла нет или источник новее (пересобранный шим доезжает
/// сам). Безопасно по времени: P/Invoke и NGX грузят DLL лениво, при первом использовании -
/// на момент вызова из Main ни одна ещё не загружена, и перезапись возможна.</summary>
public static class NativeLibraryDeployer
{
	public const string FolderName = "NativeLibrary";

	/// <summary>Версия D3D12SDKVersion редиста в agility-sdk.zip (минор пакета
	/// Microsoft.Direct3D.D3D12 1.619.x). Менять вместе с обновлением архива.</summary>
	private const uint AgilitySdkVersion = 619;

	[System.Runtime.InteropServices.DllImport("DecaFfxShim.dll",
		CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
		CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int DecaAgility_Init(uint sdkVersion, string sdkPath);

	/// <summary>Включает разложенный <see cref="DeployAgilitySdk"/> редист Agility SDK - строго ДО
	/// создания D3D12-устройства (см. EditorMain). Все неудачи безопасны: остаёмся на встроенном
	/// рантайме Windows.</summary>
	public static void TryEnableAgilitySdk()
	{
		if (Environment.GetEnvironmentVariable("DECA_AGILITY") == "0" ||
		    !File.Exists(Path.Combine(AppContext.BaseDirectory, "D3D12", "D3D12Core.dll")))
		{
			return;
		}

		try
		{
			DecaAgility_Init(AgilitySdkVersion, ".\\D3D12\\");
		}
		catch (DllNotFoundException)
		{
			// Нет шима - нет и нативных апскейлеров, ради которых Agility и нужен.
		}
	}

	public static void Deploy()
	{
		var baseDir = AppContext.BaseDirectory;

		// Папка ищется ВВЕРХ от каталога экзешника (bin/x64/Debug/net10.0 -> корень репозитория):
		// так работает и Debug, и Release, и любой будущий каталог публикации внутри репо.
		string? sourceDir = null;
		for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, FolderName);
			if (Directory.Exists(candidate))
			{
				sourceDir = candidate;
				break;
			}
		}

		if (sourceDir is null || string.Equals(Path.GetFullPath(sourceDir),
			    Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase))
		{
			// Машина без нативных либ (или запуск прямо из папки) - штатный случай: конвейер
			// умеет жить на встроенном TAAU, а окно Graphics честно покажет недоступность.
			return;
		}

		foreach (var source in Directory.EnumerateFiles(sourceDir, "*.dll"))
		{
			var target = Path.Combine(baseDir, Path.GetFileName(source));
			try
			{
				if (!File.Exists(target) ||
				    File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target))
				{
					File.Copy(source, target, overwrite: true);
					Console.WriteLine($"[native] {Path.GetFileName(source)} -> {baseDir}");
				}
			}
			catch (IOException e)
			{
				// Занятый файл (второй экземпляр редактора уже загрузил DLL) - не смертельно:
				// работает прежняя копия, свежая доедет следующим запуском.
				Console.WriteLine($"[native] {Path.GetFileName(source)}: {e.Message}");
			}
		}

		DeployAgilitySdk(sourceDir, baseDir);
	}

	/// <summary>Раскладывает DirectX Agility SDK из <c>NativeLibrary/agility-sdk.zip</c> (nupkg
	/// Microsoft.Direct3D.D3D12 под нейтральным именем) в <c>bin/D3D12/</c>. Из архива, а не
	/// готовыми файлами, нарочно: рантайм требует ИМЕННО имени D3D12Core.dll, и раскладывать его
	/// удобнее самим процессом. Включается затем через DecaAgility_Init (см. EditorMain).</summary>
	private static void DeployAgilitySdk(string sourceDir, string baseDir)
	{
		var package = Path.Combine(sourceDir, "agility-sdk.zip");
		if (!File.Exists(package))
		{
			return;
		}

		var targetDir = Path.Combine(baseDir, "D3D12");
		Directory.CreateDirectory(targetDir);

		try
		{
			using var zip = System.IO.Compression.ZipFile.OpenRead(package);
			foreach (var entry in zip.Entries)
			{
				// Debug-слои не раскладываем: нужен только сам рантайм.
				if (!entry.FullName.StartsWith("build/native/bin/x64/", StringComparison.OrdinalIgnoreCase) ||
				    !entry.Name.Equals("D3D12Core.dll", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var target = Path.Combine(targetDir, entry.Name);
				if (!File.Exists(target) || entry.LastWriteTime.UtcDateTime > File.GetLastWriteTimeUtc(target))
				{
					entry.ExtractToFile(target, overwrite: true);
					Console.WriteLine($"[native] Agility SDK: {entry.Name} -> {targetDir}");
				}
			}
		}
		catch (Exception e)
		{
			Console.WriteLine($"[native] Agility SDK: {e.Message}");
		}
	}
}
