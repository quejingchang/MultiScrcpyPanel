using System;
using System.IO;
using MultiScrcpy.Core.Adb;
using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// <see cref="AdbClient.LocalizeIfRemote"/> 的纯逻辑测试（不依赖网络/可移动盘，可在 CI 无头运行）。
/// <para>核心契约：空路径、不存在的路径、本地盘路径都必须原样返回且不抛异常；
/// 只有「网络/可移动盘上的真实 adb」才会触发本地缓存（该分支需真机/真网络盘，这里不测）。</para>
/// </summary>
[Trait("Category", "ADB")]
public sealed class AdbClientLocalizeTests
{
    [Fact]
    public void 空路径原样返回且不抛异常()
    {
        Assert.Equal(string.Empty, AdbClient.LocalizeIfRemote(string.Empty));
    }

    [Fact]
    public void null路径原样返回且不抛异常()
    {
        Assert.Null(AdbClient.LocalizeIfRemote(null!));
    }

    [Fact]
    public void 不存在的路径原样返回且不抛异常()
    {
        string bad = Path.Combine(Path.GetTempPath(), "multiscrcpy-nosuch-" + Guid.NewGuid().ToString("N"), "adb.exe");
        Assert.Equal(bad, AdbClient.LocalizeIfRemote(bad));
    }

    [Fact]
    public void 本地盘上的真实文件原样返回且不抛异常()
    {
        // 在本地临时盘放一个占位文件（非网络盘 → 不应被复制/改写路径）
        string dir = Path.Combine(Path.GetTempPath(), "multiscrcpy-localize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string fake = Path.Combine(dir, "adb.exe");
        File.WriteAllText(fake, "placeholder");

        try
        {
            Assert.Equal(fake, AdbClient.LocalizeIfRemote(fake));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
