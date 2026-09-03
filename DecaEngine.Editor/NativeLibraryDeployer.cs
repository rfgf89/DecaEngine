using System;
using System.IO;
using System.IO.Compression;

namespace DecaEngine.Editor;

/// <summary>Copies native upscaler libraries from <c>NativeLibrary</c> into the output directory at startup.</summary>
// Must run before any of those DLLs is loaded; P/Invoke and NGX load lazily, so Main is early enough.
public static class NativeLibraryDeployer
{
	public const string FolderName = "NativeLibrary";

	// D3D12SDKVersion of the redist in agility-sdk.zip; bump together with the archive.
	private const uint AgilitySdkVersion = 619;

	[System.Runtime.InteropServices.DllImport("DecaFfxShim.dll",
		CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
		CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int DecaAgility_Init(uint sdkVersion, string sdkPath);

	/// <summary>Enables the deployed Agility SDK redist; must be called before the D3D12 device is created.</summary>
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
			// No shim means no native upscalers, which is the only reason Agility is needed.
		}
	}

	public static void Deploy()
	{
		var baseDir = AppContext.BaseDirectory;

		// Searched upwards from the output directory so any bin layout inside the repo works.
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
			// Normal case: without the native libs the pipeline stays on the built-in TAAU.
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
				// Locked file (another editor instance holds the DLL): the old copy keeps working.
				Console.WriteLine($"[native] {Path.GetFileName(source)}: {e.Message}");
			}
		}

		DeployAgilitySdk(sourceDir, baseDir);
	}

	// Extracts D3D12Core.dll from the Microsoft.Direct3D.D3D12 nupkg; the runtime demands that name.
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
				// Debug layers are skipped: only the runtime itself is needed.
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
