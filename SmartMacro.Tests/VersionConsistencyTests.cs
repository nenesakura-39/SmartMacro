using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SmartMacro.Tests;

/// <summary>
/// The first public release is 1.0.0. Internal milestones (the old "v1.1 Intent" … "v1.8 Studio X" labels) must never
/// show up as a version users can see, and the version in SmartMacro.csproj must match the release documents
/// (tools\publish.cmd checks the same thing at publish time; this catches it on every test run).
/// </summary>
public sealed class VersionConsistencyTests
{
    private static readonly Regex InternalMilestoneLabel = new(@"\bv1\.\d\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartMacro.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Couldn't find the repository root (SmartMacro.slnx).");
    }

    private static string ProjectVersion()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "SmartMacro.csproj"));
        var version = csproj.Descendants("Version").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        Assert.False(string.IsNullOrEmpty(version), "SmartMacro.csproj has no <Version>.");
        return version!;
    }

    [Fact]
    public void Project_version_is_the_first_public_release()
    {
        Assert.Equal("1.0.0", ProjectVersion());
    }

    [Theory]
    [InlineData("EULA.txt")]
    [InlineData("README.txt")]
    public void Release_documents_name_the_project_version(string file)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "release-docs", file));
        Assert.Contains(ProjectVersion(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.8.0", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SmartMacro.Core", "Workflow", "MacroBlueprints.cs")]
    [InlineData("SmartMacro.Core", "Help", "ManualCatalog.cs")]
    public void User_visible_catalogs_have_no_internal_milestone_labels(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        var offenders = File.ReadAllLines(path)
            .Select((line, index) => (Line: line.Trim(), Number: index + 1))
            .Where(l => !l.Line.StartsWith("//", StringComparison.Ordinal)) // code comments aren't shown to users
            .Where(l => InternalMilestoneLabel.IsMatch(l.Line))
            .Select(l => $"line {l.Number}: {l.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "Internal milestone labels in user-visible text:\n" + string.Join("\n", offenders));
    }
}
