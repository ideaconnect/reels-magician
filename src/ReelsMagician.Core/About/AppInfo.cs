using System.Reflection;
using System.Runtime.InteropServices;

namespace ReelsMagician.Core.About;

/// <summary>Who makes ReelsMagician, where it lives, and which version and system this is.</summary>
public static class AppInfo
{
    public const string Company = "IDCT";
    public const string LegalName = "IDCT Bartosz Pachołek";
    public const string CompanyDescription = "AI-first software studio from Szczecin, Poland";

    public const string CompanyLink = "https://idct.tech/";
    public const string RepositoryLink = "https://github.com/ideaconnect/reels-magician";
    public const string IssuesLink = RepositoryLink + "/issues";
    public const string ReleasesLink = RepositoryLink + "/releases";

    /// <summary>The version CI gives a build that is not made from a release tag, and a local build gets.</summary>
    public const string DevelopmentVersion = "0.0.0-dev";

    public const string DevelopmentBuild = "development build";

    /// <summary>
    /// The version as the About tab shows it: <c>0.2.0</c> for a release, without the
    /// <c>+commit</c> suffix the SDK appends, and "development build" for any other build.
    /// </summary>
    public static string DisplayVersion(string? informationalVersion)
    {
        var version = (informationalVersion ?? "").Split('+')[0].Trim();
        return version.Length == 0 || version == DevelopmentVersion ? DevelopmentBuild : version;
    }

    /// <summary>The version an assembly was built as, which CI sets from the tag with <c>-p:Version=</c>.</summary>
    public static string? InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>This system, such as "Microsoft Windows 10.0.26100 · x64 · .NET 10.0.14".</summary>
    public static string SystemDescription() =>
        DescribeSystem(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription);

    public static string DescribeSystem(string os, Architecture architecture, string framework) =>
        $"{os.Trim()} · {architecture.ToString().ToLowerInvariant()} · {framework.Trim()}";
}
