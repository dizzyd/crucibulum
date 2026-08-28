// Crucibulum - melt metal in a crucible on the forge, for Vintage Story
// Copyright (C) 2026 Dave (Dizzy) Smith
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Lesser General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. See COPYING.LESSER, or <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Frosting;
using Cake.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public class BuildContext : FrostingContext
{
    public const string ProjectName = "crucibulum";
    public string BuildConfiguration { get; }
    public string Version { get; }
    public string Name { get; }
    public bool SkipJsonValidation { get; }

    public BuildContext(ICakeContext context)
        : base(context)
    {
        BuildConfiguration = context.Argument("configuration", "Release");
        SkipJsonValidation = context.Argument("skipJsonValidation", false);
        var modInfo = context.DeserializeJsonFromFile<ModInfo>($"../{ProjectName}/modinfo.json");
        Version = modInfo.Version;
        Name = modInfo.ModID;
    }
}

[TaskName("ValidateJson")]
public sealed class ValidateJsonTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        if (context.SkipJsonValidation)
        {
            return;
        }

        var jsonFiles = context.GetFiles($"../{BuildContext.ProjectName}/assets/**/*.json");
        foreach (var file in jsonFiles)
        {
            try
            {
                var json = File.ReadAllText(file.FullPath);
                JToken.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new Exception(
                    $"Validation failed for JSON file: {file.FullPath}{Environment.NewLine}{ex.Message}", ex);
            }
        }
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.DotNetClean($"../{BuildContext.ProjectName}/{BuildContext.ProjectName}.csproj",
            new DotNetCleanSettings
            {
                Configuration = context.BuildConfiguration
            });


        context.DotNetPublish($"../{BuildContext.ProjectName}/{BuildContext.ProjectName}.csproj",
            new DotNetPublishSettings
            {
                Configuration = context.BuildConfiguration
            });
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.EnsureDirectoryExists("../Releases");
        context.CleanDirectory("../Releases");
        context.EnsureDirectoryExists($"../Releases/{context.Name}");
        context.CopyFiles($"../{BuildContext.ProjectName}/bin/{context.BuildConfiguration}/Mods/mod/publish/*",
            $"../Releases/{context.Name}");
        if (context.DirectoryExists($"../{BuildContext.ProjectName}/assets"))
        {
            context.CopyDirectory($"../{BuildContext.ProjectName}/assets", $"../Releases/{context.Name}/assets");
        }

        context.CopyFile($"../{BuildContext.ProjectName}/modinfo.json", $"../Releases/{context.Name}/modinfo.json");
        if (context.FileExists($"../{BuildContext.ProjectName}/modicon.png"))
        {
            context.CopyFile($"../{BuildContext.ProjectName}/modicon.png", $"../Releases/{context.Name}/modicon.png");
        }

        // The zip is what gets redistributed, and the Lesser licence asks that a copy travel with
        // the binary rather than only living in the repo.
        foreach (string legal in new[] { "COPYING", "COPYING.LESSER", "NOTICE" })
        {
            if (context.FileExists($"../{legal}"))
            {
                context.CopyFile($"../{legal}", $"../Releases/{context.Name}/{legal}");
            }
        }

        context.Zip($"../Releases/{context.Name}", $"../Releases/{context.Name}_{context.Version}.zip");
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public class DefaultTask : FrostingTask
{
}
