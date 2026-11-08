using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CanvasDesktop;

public sealed record TeamTelemetryConfig(
    string? MutexName = null,
    string? TestCommandPattern = null,
    string? CvlTemplate = null)
{
    public const string DefaultNamedMutexName = @"Global\CanvasWindowComposer.Tests";
    public const string DefaultCvlTemplate = ".agents/teams/team_architecture_cvl.json";
    public const string DefaultTestCommandPattern =
        @"Run-Tests|dotnet test|pytest|python -m unittest|npm test|yarn test|pnpm test|cargo test|go test|mvn test|gradlew? test";

    private static readonly Regex TelemetryBlockRegex = new(
        @"(?ms)^telemetry:\s*\r?\n(?<block>.*?(?=^[a-zA-Z0-9_\-]+:|\z))",
        RegexOptions.Compiled);

    public static TeamTelemetryConfig Load(string? teamsYamlPath = null)
    {
        string? resolvedPath = ResolveTeamsYamlPath(teamsYamlPath);
        if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
        {
            return new TeamTelemetryConfig();
        }

        try
        {
            string content = File.ReadAllText(resolvedPath);
            return ParseYaml(content);
        }
        catch
        {
            return new TeamTelemetryConfig();
        }
    }

    public static TeamTelemetryConfig ParseYaml(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
            return new TeamTelemetryConfig();

        string? mutex = null;
        string? testPattern = null;
        string? cvl = null;

        var match = TelemetryBlockRegex.Match(yamlContent);
        if (match.Success)
        {
            string block = match.Groups["block"].Value;
            var lines = block.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;

                int colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;

                string key = line.Substring(0, colonIdx).Trim();
                string val = line.Substring(colonIdx + 1).Trim();

                // Strip inline comment if outside quotes
                if (!val.StartsWith("\"") && !val.StartsWith("'"))
                {
                    int commentIdx = val.IndexOf('#');
                    if (commentIdx >= 0) val = val.Substring(0, commentIdx).Trim();
                }

                // Strip surrounding single or double quotes
                bool wasDoubleQuoted = val.StartsWith("\"") && val.EndsWith("\"");
                if (wasDoubleQuoted || (val.StartsWith("'") && val.EndsWith("'")))
                {
                    val = val.Length >= 2 ? val.Substring(1, val.Length - 2) : "";
                    if (wasDoubleQuoted)
                    {
                        val = val.Replace(@"\\", @"\");
                    }
                }

                switch (key.ToLowerInvariant())
                {
                    case "mutex_name":
                        mutex = val;
                        break;
                    case "test_command_pattern":
                        testPattern = val;
                        break;
                    case "cvl_template":
                        cvl = val;
                        break;
                }
            }
        }

        return new TeamTelemetryConfig(mutex, testPattern, cvl);
    }

    public static string? ResolveTeamsYamlPath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (File.Exists(explicitPath))
                return Path.GetFullPath(explicitPath);

            if (Directory.Exists(explicitPath))
            {
                var directYaml = Path.Combine(explicitPath, "teams.yaml");
                if (File.Exists(directYaml))
                    return Path.GetFullPath(directYaml);

                var nestedYaml = Path.Combine(explicitPath, ".agents", "teams", "teams.yaml");
                if (File.Exists(nestedYaml))
                    return Path.GetFullPath(nestedYaml);
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "teams.yaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".agents", "teams", "teams.yaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".agents", "teams", "teams.yaml")
        };

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c))
                return Path.GetFullPath(c);
        }

        return null;
    }
}
