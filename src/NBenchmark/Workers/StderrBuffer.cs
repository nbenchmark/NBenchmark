namespace NBenchmark.Workers;

/// <summary>
///     A first-N + last-N window over the worker's stderr, for the line a fault message quotes.
/// </summary>
/// <remarks>
///     <para>
///         The worker's stderr is the only evidence left when it dies hard, and a .NET crash dump is
///         shaped against a tail-only window: the diagnostic header - "Stack overflow.",
///         "Repeated N times:" - comes <i>first</i>, followed by the stack frames. A last-N-only
///         buffer (what this replaces) kept the frames and let the header scroll off, so a deep or
///         multi-threaded dump produced twenty frames and no statement of what happened. Keeping the
///         first N lines as well as the last N keeps both the header and the bottom of the dump, and
///     </para>
///     <para>
///         Not thread-safe by design: the worker's <c>ErrorDataReceived</c> handler and the fault
///         composers run on different threads, so <c>WorkerHost</c> locks the buffer around every
///         <see cref="Add" /> and <see cref="ToString" /> call. Keeping the locking out of the buffer
///         makes the window logic unit-testable in isolation.
///     </para>
/// </remarks>
internal sealed class StderrBuffer
{
    private readonly int _headLines;
    private readonly int _tailLines;

    // The first N lines, in arrival order. Fills to _headLines and then stops: once the head is full
    // every later line belongs to the tail window.
    private readonly Queue<string> _head = new();

    // The last N lines, in arrival order, rolling. A Queue rather than a ring buffer because N is
    // small (a crash dump, not a log) and the dequeue-on-overflow cost is negligible against a process
    // that is dying.
    private readonly Queue<string> _tail = new();

    // The total lines ever added, even those no longer held, so the omitted count is exact.
    private long _total;

    public StderrBuffer(int headLines, int tailLines)
    {
        if (headLines < 0)
            throw new ArgumentOutOfRangeException(nameof(headLines), headLines, "must be non-negative");
        if (tailLines < 0)
            throw new ArgumentOutOfRangeException(nameof(tailLines), tailLines, "must be non-negative");

        _headLines = headLines;
        _tailLines = tailLines;
    }

    public void Add(string line)
    {
        _total++;

        if (_head.Count < _headLines)
            _head.Enqueue(line);

        _tail.Enqueue(line);

        while (_tail.Count > _tailLines)
            _tail.Dequeue();
    }

    /// <summary>
    ///     The rendered window: the first <c>headLines</c> lines, then a count of any dropped middle,
    ///     then the last <c>tailLines</c> lines. When everything fits the windows are merged without a
    ///     separator and without duplicating the overlap.
    /// </summary>
    public override string ToString()
    {
        if (_total == 0)
            return string.Empty;

        // Every line is still in the head window: the tail is a duplicate subset, so the head alone is
        // the full ordered output.
        if (_total <= _headLines)
            return string.Join(Environment.NewLine, _head);

        var capacity = _headLines + _tailLines;

        if (_total <= capacity)
        {
            // The head and tail windows overlap. The tail's first (capacity - total) lines are the same
            // lines as the head's last, so skip them and the concatenation is the full ordered sequence
            // with no duplicate and no separator.
            var overlap = (int)(capacity - _total);

            return string.Join(Environment.NewLine, _head.Concat(_tail.Skip(overlap)));
        }

        // A middle was dropped. Name how many lines were omitted so the reader knows the dump was
        // truncated, not this short.
        var omitted = _total - capacity;

        return string.Join(
            Environment.NewLine,
            _head
                .Append($"[... {omitted} line{(omitted == 1 ? string.Empty : "s")} omitted ...]")
                .Concat(_tail));
    }
}