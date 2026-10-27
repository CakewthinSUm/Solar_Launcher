using Solar.Core.Minecraft;
using Solar.Core.Models;
using Xunit;

namespace Solar.Core.Tests;

public class VersionMergeTests
{
    [Fact]
    public void ChildOverridesInheritedScalarFields()
    {
        var child = new VersionDetail
        {
            Id = "1.20.1-forge-47.2.0",
            InheritsFrom = "1.20.1",
            MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
        };

        var parent = new VersionDetail
        {
            Id = "1.20.1",
            MainClass = "net.minecraft.client.main.Main",
            Assets = "5",
            JavaVersion = new JavaVersionInfo { Component = "java-runtime-delta", MajorVersion = 17 },
        };

        var merged = VersionResolver.Merge([child, parent]);

        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", merged.MainClass);
        Assert.Equal("5", merged.Assets);
        Assert.Equal(17, merged.JavaVersion!.MajorVersion);
        Assert.Equal("java-runtime-delta", merged.JavaVersion.Component);
        Assert.Equal("1.20.1-forge-47.2.0", merged.Id);
    }

    [Fact]
    public void LibrariesAreConcatenatedParentFirst()
    {
        var child = new VersionDetail
        {
            Id = "child",
            Libraries = [new Library { Name = "com.example:child:1.0" }],
        };

        var parent = new VersionDetail
        {
            Id = "parent",
            Libraries = [new Library { Name = "com.mojang:brigadier:1.0.18" }],
        };

        var merged = VersionResolver.Merge([child, parent]);

        Assert.Equal(2, merged.Libraries.Count);
        Assert.Equal("com.mojang:brigadier:1.0.18", merged.Libraries[0].Name);
        Assert.Equal("com.example:child:1.0", merged.Libraries[1].Name);
    }

    [Fact]
    public void ArgumentsFromBothLevelsAreKept()
    {
        var child = new VersionDetail
        {
            Id = "child",
            Arguments = new Arguments { Game = [new ArgumentEntry { Value = "--fml.child" }] },
        };

        var parent = new VersionDetail
        {
            Id = "parent",
            Arguments = new Arguments { Game = [new ArgumentEntry { Value = "--username" }] },
        };

        var merged = VersionResolver.Merge([child, parent]);

        Assert.Equal(2, merged.Arguments!.Game.Count);
        Assert.Equal("--username", merged.Arguments.Game[0].Value);
        Assert.Equal("--fml.child", merged.Arguments.Game[1].Value);
    }
}
