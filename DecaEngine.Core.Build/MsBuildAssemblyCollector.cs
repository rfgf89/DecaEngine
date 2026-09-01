using System.Diagnostics;
using Microsoft.Build.Evaluation;

namespace DecaEngine.Core.Build;

public static class CsprojOutputResolver
{
    // Возвращает список существующих файлов сборки (dll/exe и сопутствующие) для указанного csproj.
    // Если buildIfMissing == true и файлов нет — попробует выполнить "dotnet build".
    /// <param name="platform">
    /// Платформа сборки (<c>x64</c> и т.п.); null - как решит проект.
    ///
    /// Задавать её приходится по существу: движок тянет DiligentEngine, который на AnyCPU просто
    /// отказывается собираться («does not work correctly on 'AnyCPU' platform»). Без платформы
    /// сборка падала, выходных файлов не находилось, и открытие проекта заканчивалось сообщением
    /// «не удалось собрать проект» - при том что руками тот же проект собирается.
    /// </param>
    public static List<string> GetBuildOutputs(
        string csprojPath,
        string configuration = "Debug",
        string targetFramework = null,
        bool buildIfMissing = false,
        string platform = null)
    {
        if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
            throw new FileNotFoundException("csproj not found", csprojPath);

        // Глобальные свойства для оценки
        var globals = new Dictionary<string, string>
        {
            ["Configuration"] = configuration
        };
        if (!string.IsNullOrEmpty(targetFramework))
            globals["TargetFramework"] = targetFramework;
        if (!string.IsNullOrEmpty(platform))
            globals["Platform"] = platform;

        var pc = new ProjectCollection(globals);
        var proj = pc.LoadProject(csprojPath);

        // Если TargetFramework не передан, попробуем взять из проекта
        string tf = proj.GetPropertyValue("TargetFramework");
        if (string.IsNullOrEmpty(tf))
        {
            var tfs = proj.GetPropertyValue("TargetFrameworks");
            if (!string.IsNullOrEmpty(tfs))
                tf = tfs.Split(';')[0].Trim(); // можно выбирать нужный
        }

        // Если определили TF после загрузки, переоценим проект с этим TF (чтобы свойства корректно раскрылись)
        if (!string.IsNullOrEmpty(tf) && !globals.ContainsKey("TargetFramework"))
        {
            globals["TargetFramework"] = tf;
            if (!string.IsNullOrEmpty(platform))
                globals["Platform"] = platform;
            pc = new ProjectCollection(globals);
            proj = pc.LoadProject(csprojPath);
        }

        // Попробуем получить готовые свойства MSBuild
        string targetPath = proj.GetPropertyValue("TargetPath");       // полный путь к целевому файлу, если есть
        string targetDir  = proj.GetPropertyValue("TargetDir");
        string fileName   = proj.GetPropertyValue("TargetFileName");
        string outputPath = proj.GetPropertyValue("OutputPath");
        string assemblyName = proj.GetPropertyValue("AssemblyName");
        string outputType = proj.GetPropertyValue("OutputType"); // Exe/WinExe/Library

        // Если TargetPath есть — используем его прямо
        string primaryPath = null;
        if (!string.IsNullOrEmpty(targetPath))
        {
            primaryPath = targetPath;
        }
        else
        {
            // Сформируем путь вручную
            if (string.IsNullOrEmpty(assemblyName))
                assemblyName = Path.GetFileNameWithoutExtension(csprojPath);

            string ext = ".dll";
            if (!string.IsNullOrEmpty(outputType) &&
                (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                 outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
                ext = ".exe";

            // если есть TargetFileName — используем
            if (!string.IsNullOrEmpty(fileName))
                assemblyName = fileName;

            if (string.IsNullOrEmpty(outputPath))
            {
                // запасной стандартный путь: bin\<Configuration>\<TF>\
                outputPath = Path.Combine("bin", configuration, tf ?? "");
            }

            var projectDir = Path.GetDirectoryName(csprojPath) ?? Directory.GetCurrentDirectory();
            var fullOutput = Path.IsPathRooted(outputPath) ? outputPath : Path.GetFullPath(Path.Combine(projectDir, outputPath));
            primaryPath = Path.Combine(fullOutput, assemblyName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? assemblyName : assemblyName + ext);
        }

        var results = new List<string>();
        var primaryDir = Path.GetDirectoryName(primaryPath) ?? "";

        // Соберём возможные связанные файлы
        var candidates = new List<string>
        {
            primaryPath,
            Path.ChangeExtension(primaryPath, ".pdb"),
            Path.ChangeExtension(primaryPath, ".deps.json"),
            Path.ChangeExtension(primaryPath, ".runtimeconfig.json")
        };

        // Также добавим другие файлы в той же папке (на случай дополнительных dll)
        try
        {
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    results.Add(Path.GetFullPath(c));

            if (Directory.Exists(primaryDir))
            {
                // добавим все dll и exe в каталоге сборки
                foreach (var f in Directory.GetFiles(primaryDir, "*.dll"))
                    if (!results.Contains(f, StringComparer.OrdinalIgnoreCase)) results.Add(f);
                foreach (var f in Directory.GetFiles(primaryDir, "*.exe"))
                    if (!results.Contains(f, StringComparer.OrdinalIgnoreCase)) results.Add(f);
            }
        }
        catch { /* игнорируем проблемы чтения папки */ }

        // Если ничего не найдено и разрешено — попробуем собрать проект
        if (results.Count == 0 && buildIfMissing)
        {
            if (Build(csprojPath, configuration, tf, platform))
            {
                // после сборки попробуем снова (рекурсивно, но уже без попытки билдить снова)
                return GetBuildOutputs(csprojPath, configuration, tf, buildIfMissing: false, platform);
            }
            // если сборка не удалась — возвращаем пустой список
        }

        return results;
    }

    /// <summary>
    /// Собирает проект и возвращает, удалось ли.
    ///
    /// ВЫВОД ЧИТАЕТСЯ АСИНХРОННО, и это не удобство, а условие работоспособности. Прежний вариант
    /// перенаправлял stdout и stderr, но НЕ читал их и сразу звал WaitForExit: как только сборка
    /// заполняла буфер трубы (несколько килобайт — а сборка проекта, тянущего за собой два десятка
    /// проектов движка, выдаёт сотни строк), дочерний dotnet вставал на записи, а редактор — на
    /// ожидании его завершения. Взаимная блокировка навсегда: снаружи это выглядит как «открытие
    /// проекта зависло».
    ///
    /// Вывод пересылается в Console — движок его перехватывает (см. EngineLog.Install), и
    /// ход сборки виден в окне Console вместо тишины на несколько минут.
    /// </summary>
    private static bool Build(string csprojPath, string configuration, string targetFramework, string platform)
    {
        var arguments = $"build \"{csprojPath}\" -c {configuration}" +
            (targetFramework != null ? $" -f {targetFramework}" : "") +
            (!string.IsNullOrEmpty(platform) ? $" -p:Platform={platform}" : "") +
            " --nologo -v:m";

        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return false;
        }

        process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit();

        return process.ExitCode == 0;
    }
}