using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The stderr tail a fault message quotes, as a first-N + last-N window rather than last-N only.
/// </summary>
/// <remarks>
///     <para>
///         The defect this replaces (W-48): the buffer kept the last 20 lines, but a .NET stack-overflow
///         dump puts the diagnostic header - "Stack overflow.", "Repeated N times:" - <i>first</i>. On a
///         deep or multi-threaded dump the header scrolled off and the user got 20 stack frames with no
///         statement of what happened. The fix keeps the first N lines (the header and the top of the
///         dump) and the last N lines (the bottom), with a count of what the middle dropped.
///     </para>
///     <para>
///         Pure and testable in isolation because the window logic is independent of where the lines
///         come from: the worker's <c>ErrorDataReceived</c> handler just calls <c>Add</c>, and the fault
///         composers just read the snapshot.
///     </para>
/// </remarks>
public sealed class StderrBufferTests
{
    [Fact]
    public void Empty_RendersNothing()
    {
        Assert.Equal("", new StderrBuffer(headLines: 20, tailLines: 20).ToString());
    }

    [Fact]
    public void FewerThanTheHeadKeeps_RrendersAllInOrderWithNoSeparator()
    {
        var buffer = new StderrBuffer(headLines: 10, tailLines: 10);

        buffer.Add("a");
        buffer.Add("b");
        buffer.Add("c");

        Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}c", buffer.ToString());
    }

    /// <summary>
    ///     The case the fix exists for. A stack-overflow dump: a header first, many stack frames in the
    ///     middle, and a footer last. Last-N-only lost the header; first-N + last-N keeps both and says
    ///     how many frames the middle dropped.
    /// </summary>
    [Fact]
    public void StackOverflowDump_KeepsHeaderAndFooter_DropsAndCountsTheMiddle()
    {
        var buffer = new StderrBuffer(headLines: 3, tailLines: 3);

        buffer.Add("Stack overflow.");           // header - must survive
        buffer.Add("frame 1");
        buffer.Add("frame 2");
        buffer.Add("frame 3");
        buffer.Add("frame 4");
        buffer.Add("frame 5");
        buffer.Add("frame 6");
        buffer.Add("frame 7");
        buffer.Add("frame 8");
        buffer.Add("frame 9");
        buffer.Add("Repeated 2 times.");         // footer - must survive

        var rendered = buffer.ToString();
        var lines = rendered.Split(Environment.NewLine);

        // The header is the first line; the footer is the last.
        Assert.Equal("Stack overflow.", lines[0]);
        Assert.Equal("Repeated 2 times.", lines[^1]);

        // The middle was dropped and counted: 11 lines total, 3 head + 3 tail kept, 5 omitted.
        Assert.Contains("5", string.Join(' ', lines));  // the omitted count appears
        Assert.DoesNotContain("frame 5", rendered);      // a middle line is gone

        // The boundary frames are kept: the last head frame ("frame 2") and the first tail frame
        // ("frame 8") sit on either side of the omitted middle.
        Assert.Contains("frame 2", rendered);
        Assert.Contains("frame 8", rendered);
    }

    /// <summary>
    ///     Exactly head + tail lines: nothing is dropped, so there is no separator, and the order is
    ///     preserved across the head/tail boundary.
    /// </summary>
    [Fact]
    public void ExactlyHeadPlusTail_RendersAllInOrderWithNoSeparator()
    {
        var buffer = new StderrBuffer(headLines: 3, tailLines: 3);

        for (var i = 1; i <= 6; i++)
            buffer.Add($"line {i}");

        Assert.Equal(
            $"line 1{Environment.NewLine}line 2{Environment.NewLine}line 3"
                + $"{Environment.NewLine}line 4{Environment.NewLine}line 5{Environment.NewLine}line 6",
            buffer.ToString());
    }

    /// <summary>
    ///     Between head and head+tail lines, the head and tail windows overlap. The rendered output is
    ///     the full ordered sequence with no duplicate lines and no separator.
    /// </summary>
    [Fact]
    public void WithinTheOverlap_RendersAllInOrderWithNoDuplicates()
    {
        var buffer = new StderrBuffer(headLines: 4, tailLines: 4);

        for (var i = 1; i <= 6; i++)
            buffer.Add($"line {i}");

        var rendered = buffer.ToString();
        var lines = rendered.Split(Environment.NewLine);

        Assert.Equal(6, lines.Length);
        Assert.Equal($"line {1}", lines[0]);
        Assert.Equal($"line {6}", lines[^1]);
        Assert.Equal(6, lines.Distinct().Count());  // no duplicate from the overlap
    }

    [Fact]
    public void Add_IsOrderableAcrossInstances_HeadFillsBeforeTailRolls()
    {
        var buffer = new StderrBuffer(headLines: 2, tailLines: 2);

        buffer.Add("h1");
        buffer.Add("h2");
        buffer.Add("m1");  // head is full; this rolls the tail
        buffer.Add("m2");
        buffer.Add("t1");  // now the middle is dropped

        var rendered = buffer.ToString();
        var lines = rendered.Split(Environment.NewLine);

        Assert.Equal("h1", lines[0]);
        Assert.Equal("h2", lines[1]);
        Assert.Equal("t1", lines[^1]);
        Assert.Contains("1", string.Join(' ', lines));  // one middle line omitted
    }
}