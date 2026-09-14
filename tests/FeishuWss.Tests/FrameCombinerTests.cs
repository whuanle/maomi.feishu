using FeishuWss.Client;
using Xunit;

namespace FeishuWss.Tests;

/// <summary>
/// FrameCombiner 测试：覆盖飞书长连接对超大事件的拆包合包。
/// </summary>
public class FrameCombinerTests
{
    [Fact]
    public void Combine_SingleFrame_ReturnsNull_NeedsCallerBypass()
    {
        // sum=1 不进合包路径，这里仅冒烟
        var c = new FrameCombiner(TimeSpan.FromSeconds(2));
        Assert.Null(c.Combine("msg", 1, 0, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Combine_MultiFrame_AssemblesInOrder()
    {
        var c = new FrameCombiner(TimeSpan.FromSeconds(2));

        Assert.Null(c.Combine("msg1", 3, 0, new byte[] { 1, 2 }));
        Assert.Null(c.Combine("msg1", 3, 2, new byte[] { 5, 6 }));

        var merged = c.Combine("msg1", 3, 1, new byte[] { 3, 4 });
        Assert.NotNull(merged);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, merged!);
    }

    [Fact]
    public void Combine_DifferentMessages_IndependentBuffers()
    {
        var c = new FrameCombiner(TimeSpan.FromSeconds(2));

        Assert.Null(c.Combine("msgA", 2, 0, new byte[] { 0xAA }));
        var merged = c.Combine("msgA", 2, 1, new byte[] { 0xBB });
        Assert.Equal(new byte[] { 0xAA, 0xBB }, merged);

        // msgA 合并后清空缓存，msgB 互不影响
        Assert.Null(c.Combine("msgB", 2, 0, new byte[] { 0xCC }));
        Assert.Equal(new byte[] { 0xCC, 0xDD }, c.Combine("msgB", 2, 1, new byte[] { 0xDD }));
    }

    [Fact]
    public void Combine_InvalidSeq_Null()
    {
        var c = new FrameCombiner(TimeSpan.FromSeconds(2));
        Assert.Null(c.Combine("msg", 2, 5, new byte[] { 1 }));
        Assert.Null(c.Combine("msg", 2, -1, new byte[] { 1 }));
    }

    [Fact]
    public void Combine_DuplicateSeq_Overwrites()
    {
        var c = new FrameCombiner(TimeSpan.FromSeconds(2));
        Assert.Null(c.Combine("msg", 2, 0, new byte[] { 1 }));
        Assert.Null(c.Combine("msg", 2, 0, new byte[] { 2 })); // 覆盖 seq=0
        Assert.Equal(new byte[] { 2, 9 }, c.Combine("msg", 2, 1, new byte[] { 9 }));
    }
}
