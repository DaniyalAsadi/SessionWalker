using Microsoft.Build.Locator;

namespace SessionWalker.Infrastructure;

using Microsoft.Build.Locator;

public static class MsBuildBootstrapper
{
    public static VisualStudioInstance Register()
    {
        if (!MSBuildLocator.CanRegister)
        {
            throw new InvalidOperationException(
                "MSBuild assemblies have already been loaded before registration.");
        }

        var instances = MSBuildLocator.QueryVisualStudioInstances()
            .OrderByDescending(x => x.Version)
            .ToList();

        if (instances.Count == 0)
        {
            throw new InvalidOperationException(
                "Visual Studio or Visual Studio Build Tools was not found.");
        }

        var selected = instances[0];

        MSBuildLocator.RegisterInstance(selected);

        Console.Error.WriteLine(
            $"MSBuild registered: {selected.Name} {selected.Version} - {selected.MSBuildPath}");

        return selected;
    }
}