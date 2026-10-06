using Core.Globbing;

namespace Core.Tests;

/// <summary>Glob 解释器 ripgrep 语义对齐测试（≥10 条）。</summary>
public class GlobbingTests
{
    private static bool M(string pattern, string rel, bool isDir = false)
        => GlobMatcher.IsMatch(GlobMatcher.Parse(pattern), rel, isDir);

    [Theory]
    [InlineData("src/a.txt", true)]
    [InlineData("src/b.txt", true)]
    [InlineData("src/sub/a.txt", false)]   // * 不跨 /
    [InlineData("other/src/a.txt", false)] // 含 / 锚定到根
    public void Star_DoesNotCrossSlash(string rel, bool expected)
        => Assert.Equal(expected, M("src/*.txt", rel));

    [Theory]
    [InlineData("a.txt", true)]     // **/ 前缀可匹配根
    [InlineData("x/a.txt", true)]
    [InlineData("x/y/z/a.txt", true)] // ** 跨 /
    [InlineData("b.txt", true)]
    public void DoubleStar_CrossesSlash(string rel, bool expected)
        => Assert.Equal(expected, M("**/*.txt", rel));

    [Theory]
    [InlineData("a/b/x.txt", true)]
    [InlineData("a/x.txt", true)]      // 中间 **/ 可匹配零层
    [InlineData("b/x.txt", false)]
    public void DoubleStar_Middle(string rel, bool expected)
        => Assert.Equal(expected, M("a/**/*.txt", rel));

    [Theory]
    [InlineData("build.log", true)]
    [InlineData("sub/build.log", false)] // 以 / 开头相对搜索根
    public void LeadingSlash_AnchorsToRoot(string rel, bool expected)
        => Assert.Equal(expected, M("/build.log", rel));

    [Fact]
    public void PatternWithSlash_IsAnchored()
    {
        Assert.True(M("src/main.cs", "src/main.cs"));
        Assert.False(M("src/main.cs", "deep/src/main.cs"));
    }

    [Fact]
    public void TrailingSlash_MatchesDirectoryOnly()
    {
        Assert.True(M("build/", "build", isDir: true));
        Assert.False(M("build/", "build", isDir: false));
        Assert.False(M("build/", "build/x.txt", isDir: false));
    }

    [Fact]
    public void BareName_MatchesAtAnyDepth()
    {
        Assert.True(M("*.log", "a.log"));
        Assert.True(M("*.log", "deep/nested/a.log"));
        Assert.False(M("*.log", "a.txt"));
        Assert.False(M("*.log", "dir.log/x")); // * 不跨 /
    }

    [Fact]
    public void QuestionMark_MatchesSingleChar()
    {
        Assert.True(M("file?.txt", "file1.txt"));
        Assert.True(M("file?.txt", "fileA.txt"));
        Assert.False(M("file?.txt", "file10.txt"));
        Assert.False(M("file?.txt", "file/.txt"));
    }

    [Fact]
    public void CharClass_WithNegation()
    {
        Assert.True(M("[abc].txt", "a.txt"));
        Assert.False(M("[abc].txt", "d.txt"));
        Assert.False(M("[!a].txt", "a.txt"));
        Assert.True(M("[!a].txt", "b.txt"));
    }

    [Fact]
    public void ExclamationPrefix_EqualsExclude()
    {
        var rs = GlobRuleSet.Create(["*.cs", "!Generated.cs"], null, null, null);
        // ! 前缀在 include 列表中等价 exclude（一票否决）
        Assert.False(rs.IsFileAccepted("Generated.cs"));
        Assert.True(rs.IsFileAccepted("Normal.cs"));
    }

    [Fact]
    public void Exclude_IsOneVoteVeto()
    {
        var rs = GlobRuleSet.Create(["*.cs", "*.md"], ["*Tests.cs"], null, null);
        Assert.True(rs.IsFileAccepted("a.cs"));
        Assert.True(rs.IsFileAccepted("readme.md"));
        Assert.False(rs.IsFileAccepted("aTests.cs")); // exclude 一票否决
    }

    [Fact]
    public void Include_IsOrCombined()
    {
        var rs = GlobRuleSet.Create(["*.cs", "*.md"], null, null, null);
        Assert.True(rs.IsFileAccepted("a.cs"));
        Assert.True(rs.IsFileAccepted("b.md"));
        Assert.False(rs.IsFileAccepted("c.txt"));
    }

    [Fact]
    public void EmptyInclude_AcceptsAllExceptExcluded()
    {
        var rs = GlobRuleSet.Create(null, ["secret.txt"], null, null);
        Assert.False(rs.IsFileAccepted("secret.txt"));
        Assert.True(rs.IsFileAccepted("other.txt"));
    }

    [Fact]
    public void ExcludeDir_PrunesByNameAnywhere()
    {
        var rs = GlobRuleSet.Create(null, null, null, ["bin", "*.egg-info"]);
        Assert.True(rs.IsDirPruned("src/bin", "bin"));
        Assert.True(rs.IsDirPruned("pkg.egg-info", "pkg.egg-info"));
        Assert.False(rs.IsDirPruned("src/lib", "lib"));
    }

    [Fact]
    public void ExcludeFrom_LinesActAsExcludes()
    {
        var rs = GlobRuleSet.Create(null, null, ["temp.log", "!important.log"], null);
        Assert.False(rs.IsFileAccepted("temp.log"));
        // exclude-from 中 ! 前缀为 re-include（豁免）
        Assert.True(rs.IsFileAccepted("important.log"));
    }
}
