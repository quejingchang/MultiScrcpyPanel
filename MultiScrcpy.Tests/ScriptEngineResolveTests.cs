using System;
using System.IO;
using System.Threading.Tasks;
using MultiScrcpy.Core.Scripting;
using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>覆盖脚本路径解析（修复「选了脚本仍提示请选择脚本」回归）。</summary>
public sealed class ScriptEngineResolveTests
{
    [Fact]
    public void ResolveScriptNameToPath_KeepsSubdir()
    {
        string got = ScriptEngine.ResolveScriptNameToPath("mhxy/01_师门任务.scr");
        // 子目录相对路径应拼到脚本目录，而不是当成无分隔符的名字被忽略
        Assert.EndsWith(Path.Combine("scripts", "mhxy", "01_师门任务.scr"), got);
        // 不应出现 scripts/scripts 双重拼接
        Assert.DoesNotContain(
            "scripts" + Path.DirectorySeparatorChar + "scripts" + Path.DirectorySeparatorChar, got);
    }

    [Fact]
    public void ResolveScriptNameToPath_AbsolutePassthrough()
    {
        string abs = Path.Combine(Path.GetTempPath(), "some.scr");
        Assert.Equal(abs, ScriptEngine.ResolveScriptNameToPath(abs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("浏览…")]
    public void ResolveScriptLocation_NullOrBrowseReturnsNull(string? raw)
    {
        Assert.Null(ScriptEngine.ResolveScriptLocation(raw));
    }

    [Fact]
    public async Task ResolveScriptLocation_AbsoluteExistingFileReturnsPath()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "mhxy_loc_test.scr");
        await File.WriteAllTextAsync(tmp, "WAIT 1");
        try
        {
            Assert.Equal(tmp, ScriptEngine.ResolveScriptLocation(tmp));
            // 不存在的文件返回 null（不再误用相对文本导致 File.Exists 失败）
            Assert.Null(ScriptEngine.ResolveScriptLocation(Path.Combine(Path.GetTempPath(), "nope_不存在.scr")));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
