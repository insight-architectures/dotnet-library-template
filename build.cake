#tool "nuget:?package=GitVersion.CommandLine&version=4.0.0"

var target = Argument("Target", "Full");

var skipVerification = Argument<bool>("SkipVerification", false);

Setup<BuildState>(_ => 
{
    var state = new BuildState
    {
        Paths = new BuildPaths
        {
            SolutionFile = GetSolutionFile()
        }
    };

    CleanDirectory(state.Paths.OutputFolder);

    Information($"Solution file: {state.Paths.SolutionFile.ToString()}");
    Information($"SkipVerification: {skipVerification}");

    return state;
});

Task("Version")
    .Does<BuildState>(state =>
{
    var version = GitVersion();

    state.Version = new VersionInfo
    {
        PackageVersion = version.SemVer,
        AssemblyVersion = $"{version.SemVer}+{version.Sha.Substring(0, 8)}",
        BuildVersion = $"{version.FullSemVer}+{version.Sha.Substring(0, 8)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
    };

    Information($"Package version: {state.Version.PackageVersion}");
    Information($"Assembly version: {state.Version.AssemblyVersion}");
    Information($"Build version: {state.Version.BuildVersion}");

    if (BuildSystem.IsRunningOnAppVeyor)
    {
        AppVeyor.UpdateBuildVersion(state.Version.BuildVersion);
    }
});

Task("Restore")
    .Does<BuildState>(state =>
{
    var settings = new DotNetCoreRestoreSettings
    {

    };

    DotNetCoreRestore(state.Paths.SolutionFile.ToString(), settings);
});

Task("Verify")
    .WithCriteria(!skipVerification)
    .IsDependentOn("Restore")
    .Does<BuildState>(state => 
{
    var settings = new DotNetCoreBuildSettings
    {
        Configuration = "Debug",
        NoRestore = true,
        MSBuildSettings = new DotNetCoreMSBuildSettings()
                            .WithProperty("TreatWarningsAsErrors", "True")
    };

    DotNetCoreBuild(state.Paths.SolutionFile.ToString(), settings);
});

Task("Build")
    .IsDependentOn("Restore")
    .Does<BuildState>(state => 
{
    var settings = new DotNetCoreBuildSettings
    {
        Configuration = "Debug",
        NoRestore = true
    };

    DotNetCoreBuild(state.Paths.SolutionFile.ToString(), settings);
});

Task("RunTests")
    .IsDependentOn("Build")
    .Does<BuildState>(state => 
{
    var settings = new DotNetCoreTestSettings
    {
        NoBuild = true,
        NoRestore = true,
        Loggers = new[] { $"nunit;LogFilePath={state.Paths.TestOutputFolder.FullPath}/{{assembly}}/{{framework}}/TestResults.xml" },
        Filter = "TestCategory!=External"
    };

    DotNetCoreTest(state.Paths.SolutionFile.ToString(), settings);
});

Task("UploadTestsToAppVeyor")
    .IsDependentOn("RunTests")
    .WithCriteria(BuildSystem.IsRunningOnAppVeyor)
    .Does<BuildState>(state =>
{
    Information("Uploading test result files to AppVeyor");
    var testResultFiles = GetFiles($"{state.Paths.TestOutputFolder}/**/TestResults.xml");

    foreach (var file in testResultFiles)
    {
        Information($"\tUploading {file.GetFilename()}");
        try
        {
            AppVeyor.UploadTestResults(file, AppVeyorTestResultsType.NUnit);
        }
        catch 
        {
            Error($"Failed to upload {file.GetFilename()}");
        }
    }
});

Task("Test")
    .IsDependentOn("RunTests")
    .IsDependentOn("UploadTestsToAppVeyor");

Task("PackLibraries")
    .IsDependentOn("Version")
    .IsDependentOn("Restore")
    .Does<BuildState>(state =>
{
    var settings = new DotNetCorePackSettings
    {
        Configuration = "Release",
        NoRestore = true,
        OutputDirectory = state.Paths.OutputFolder,
        IncludeSymbols = true,
        MSBuildSettings = new DotNetCoreMSBuildSettings()
                            .SetInformationalVersion(state.Version.AssemblyVersion)
                            .SetVersion(state.Version.PackageVersion)
                            .WithProperty("ContinuousIntegrationBuild", "true"),
    };

    if (skipVerification)
    {
        settings.MSBuildSettings.WithProperty("TreatWarningsAsErrors", "False");
    }

    DotNetCorePack(state.Paths.SolutionFile.ToString(), settings);
});

Task("Pack")
    .IsDependentOn("PackLibraries");

Task("UploadPackagesToAppVeyor")
    .IsDependentOn("Pack")
    .WithCriteria(BuildSystem.IsRunningOnAppVeyor)
    .Does<BuildState>(state => 
{
    Information("Uploading packages");
    var files = GetFiles($"{state.Paths.OutputFolder}/*.nukpg");

    foreach (var file in files)
    {
        Information($"\tUploading {file.GetFilename()}");
        AppVeyor.UploadArtifact(file, new AppVeyorUploadArtifactsSettings {
            ArtifactType = AppVeyorUploadArtifactType.NuGetPackage,
            DeploymentName = "NuGet"
        });
    }
});

Task("Push")
    .IsDependentOn("UploadPackagesToAppVeyor");

Task("Full")
    .IsDependentOn("Version")
    .IsDependentOn("Restore")
    .IsDependentOn("Verify")
    .IsDependentOn("Build")
    .IsDependentOn("Test")
    .IsDependentOn("Pack")
    .IsDependentOn("Push");

RunTarget(target);

private FilePath GetSolutionFile()
{
    var solutionFilesInRoot = GetFiles("./*.sln");

    var solutionFile = solutionFilesInRoot.FirstOrDefault();

    if (solutionFile == null) throw new ArgumentNullException("No solution file found");

    return solutionFile;
}

public class BuildState
{
    public VersionInfo Version { get; set; }

    public BuildPaths Paths { get; set; }
}

public class BuildPaths
{
    public FilePath SolutionFile { get; set; }

    public DirectoryPath SolutionFolder => SolutionFile.GetDirectory();

    public DirectoryPath OutputFolder => SolutionFolder.Combine("outputs");

    public DirectoryPath TestOutputFolder => OutputFolder.Combine("tests");
}

public class VersionInfo
{
    public string PackageVersion { get; set; }

    public string AssemblyVersion { get; set; }

    public string BuildVersion { get; set; }
}