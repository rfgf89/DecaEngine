using System.Diagnostics;
using Microsoft.Build.Evaluation;

namespace DecaEngine.Core.Build;

public static class CsprojOutputResolver
{
    /// <summary>Returns existing build outputs (dll/exe and companions) for a csproj, optionally building first.</summary>
    // platform must be passed explicitly (e.g. x64): DiligentEngine refuses to build on AnyCPU.
    public static List<string> GetBuildOutputs(
        string csprojPath,
        string configuration = "Debug",
        string targetFramework = null,
        bool buildIfMissing = false,
        string platform = null,
        bool rebuild = false)
    {
        if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
            throw new FileNotFoundException("csproj not found", csprojPath);

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

        string tf = proj.GetPropertyValue("TargetFramework");
        if (string.IsNullOrEmpty(tf))
        {
            var tfs = proj.GetPropertyValue("TargetFrameworks");
            if (!string.IsNullOrEmpty(tfs))
                tf = tfs.Split(';')[0].Trim();
        }

        // Re-evaluate with the discovered TF so MSBuild properties expand correctly.
        if (!string.IsNullOrEmpty(tf) && !globals.ContainsKey("TargetFramework"))
        {
            globals["TargetFramework"] = tf;
            if (!string.IsNullOrEmpty(platform))
                globals["Platform"] = platform;
            pc = new ProjectCollection(globals);
            proj = pc.LoadProject(csprojPath);
        }

        string targetPath = proj.GetPropertyValue("TargetPath");
        string targetDir  = proj.GetPropertyValue("TargetDir");
        string fileName   = proj.GetPropertyValue("TargetFileName");
        string outputPath = proj.GetPropertyValue("OutputPath");
        string assemblyName = proj.GetPropertyValue("AssemblyName");
        string outputType = proj.GetPropertyValue("OutputType");

        string primaryPath = null;
        if (!string.IsNullOrEmpty(targetPath))
        {
            primaryPath = targetPath;
        }
        else
        {
            if (string.IsNullOrEmpty(assemblyName))
                assemblyName = Path.GetFileNameWithoutExtension(csprojPath);

            string ext = ".dll";
            if (!string.IsNullOrEmpty(outputType) &&
                (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                 outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
                ext = ".exe";

            if (!string.IsNullOrEmpty(fileName))
                assemblyName = fileName;

            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine("bin", configuration, tf ?? "");
            }

            var projectDir = Path.GetDirectoryName(csprojPath) ?? Directory.GetCurrentDirectory();
            var fullOutput = Path.IsPathRooted(outputPath) ? outputPath : Path.GetFullPath(Path.Combine(projectDir, outputPath));
            primaryPath = Path.Combine(fullOutput, assemblyName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? assemblyName : assemblyName + ext);
        }

        // rebuild builds even when outputs exist: a stale dll built against an older engine loads
        // cleanly (reflection lookup, no cctor) but crashes the editor on the first call into it.
        if (rebuild && !Build(csprojPath, configuration, tf, platform))
        {
            return new List<string>();
        }

        var results = new List<string>();
        var primaryDir = Path.GetDirectoryName(primaryPath) ?? "";

        var candidates = new List<string>
        {
            primaryPath,
            Path.ChangeExtension(primaryPath, ".pdb"),
            Path.ChangeExtension(primaryPath, ".deps.json"),
            Path.ChangeExtension(primaryPath, ".runtimeconfig.json")
        };

        try
        {
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    results.Add(Path.GetFullPath(c));

            if (Directory.Exists(primaryDir))
            {
                foreach (var f in Directory.GetFiles(primaryDir, "*.dll"))
                    if (!results.Contains(f, StringComparer.OrdinalIgnoreCase)) results.Add(f);
                foreach (var f in Directory.GetFiles(primaryDir, "*.exe"))
                    if (!results.Contains(f, StringComparer.OrdinalIgnoreCase)) results.Add(f);
            }
        }
        catch { }

        if (results.Count == 0 && buildIfMissing)
        {
            if (Build(csprojPath, configuration, tf, platform))
            {
                return GetBuildOutputs(csprojPath, configuration, tf, buildIfMissing: false, platform);
            }
        }

        return results;
    }

    // Output MUST be drained asynchronously: redirected-but-unread pipes fill up, dotnet blocks
    // on write and WaitForExit deadlocks forever. Forwarded to Console for EngineLog capture.
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