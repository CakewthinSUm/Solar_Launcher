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
    public void ChildReplacesParentArtifactWithDifferentVersion()
    {
        // 原版 1.21.4 要 asm 9.6，Fabric 要 9.10.1。
        // 两个都留在 classpath 上会让 Fabric 的 Knot 报
        // "duplicate ASM classes found on classpath" 并拒绝启动。
        var child = new VersionDetail
        {
            Id = "fabric-loader-0.20.1-1.21.4",
            Libraries = [new Library { Name = "org.ow2.asm:asm:9.10.1" }],
        };

        var parent = new VersionDetail
        {
            Id = "1.21.4",
            Libraries =
            [
                new Library { Name = "org.ow2.asm:asm:9.6" },
                new Library { Name = "com.mojang:brigadier:1.3.10" },
            ],
        };

        var merged = VersionResolver.Merge([child, parent]);

        Assert.Equal(2, merged.Libraries.Count);
        Assert.Contains(merged.Libraries, l => l.Name == "org.ow2.asm:asm:9.10.1");
        Assert.DoesNotContain(merged.Libraries, l => l.Name == "org.ow2.asm:asm:9.6");
        Assert.Contains(merged.Libraries, l => l.Name == "com.mojang:brigadier:1.3.10");
    }

    [Fact]
    public void DifferentClassifiersAreDistinctLibraries()
    {
        // lwjgl 与 lwjgl:natives-windows 是两个库，不能互相覆盖
        var child = new VersionDetail
        {
            Id = "child",
            Libraries = [new Library { Name = "org.lwjgl:lwjgl:3.3.3:natives-windows" }],
        };

        var parent = new VersionDetail
        {
            Id = "parent",
            Libraries = [new Library { Name = "org.lwjgl:lwjgl:3.3.3" }],
        };

        var merged = VersionResolver.Merge([child, parent]);
        Assert.Equal(2, merged.Libraries.Count);
    }

    [Fact]
    public void DeeperChildWinsOverIntermediate()
    {
        // Forge 这类多层继承：最具体的那一层说了算
        var leaf = new VersionDetail
        {
            Id = "leaf",
            Libraries = [new Library { Name = "org.ow2.asm:asm:9.8" }],
        };

        var middle = new VersionDetail
        {
            Id = "middle",
            Libraries = [new Library { Name = "org.ow2.asm:asm:9.7" }],
        };

        var root = new VersionDetail
        {
            Id = "root",
            Libraries = [new Library { Name = "org.ow2.asm:asm:9.6" }],
        };

        var merged = VersionResolver.Merge([leaf, middle, root]);

        var asm = Assert.Single(merged.Libraries);
        Assert.Equal("org.ow2.asm:asm:9.8", asm.Name);
    }

    [Theory]
    [InlineData("org.ow2.asm:asm:9.6", "org.ow2.asm:asm")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org.lwjgl:lwjgl:natives-windows")]
    [InlineData("com.mojang:brigadier", "com.mojang:brigadier")]
    public void ArtifactKeyIgnoresVersion(string mavenName, string expected)
    {
        Assert.Equal(expected, VersionResolver.GetArtifactKey(mavenName));
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
