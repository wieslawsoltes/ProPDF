namespace ProPDF.Core;

public enum PdfChangeKind { Opened, Edited, Undo, Redo, Saved }
public sealed class PdfSessionChangedEventArgs(PdfSnapshot snapshot, PdfChangeKind kind) : EventArgs
{
    public PdfSnapshot Snapshot { get; } = snapshot;
    public PdfChangeKind Kind { get; } = kind;
}

/// <summary>Serializes document transactions. Notifications occur after releasing the transaction lock.</summary>
public sealed class PdfSession
{
    private readonly IPdfDocumentLoader _loader;
    private readonly IPdfEditor? _editor;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly List<PdfSnapshot> _undo = [];
    private readonly List<PdfSnapshot> _redo = [];
    private readonly object _state = new();
    private PdfSnapshot? _current;
    private Guid? _savedId;
    private string? _filePath;

    public PdfSession(IPdfDocumentLoader loader, IPdfEditor? editor = null, int maximumHistoryEntries = 32, long maximumHistoryBytes = 256L * 1024 * 1024)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _editor = editor;
        if (maximumHistoryEntries < 0 || maximumHistoryBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumHistoryEntries));
        MaximumHistoryEntries = maximumHistoryEntries;
        MaximumHistoryBytes = maximumHistoryBytes;
    }

    public int MaximumHistoryEntries { get; }
    public long MaximumHistoryBytes { get; }
    public PdfSnapshot? Current { get { lock (_state) return _current; } }
    public bool CanUndo { get { lock (_state) return _undo.Count != 0; } }
    public bool CanRedo { get { lock (_state) return _redo.Count != 0; } }
    public bool IsDirty { get { lock (_state) return _current is not null && _current.Id != _savedId; } }
    public string? FilePath { get { lock (_state) return _filePath; } }
    public IReadOnlySet<PdfCapability> Capabilities => _editor?.Capabilities ?? EmptyCapabilities;
    private static readonly IReadOnlySet<PdfCapability> EmptyCapabilities = new HashSet<PdfCapability>();
    public event EventHandler<PdfSessionChangedEventArgs>? Changed;

    public async Task OpenAsync(Stream source, PdfOpenOptions? options = null, string? filePath = null, CancellationToken cancellationToken = default)
    {
        PdfSnapshot snapshot;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = await _loader.OpenAsync(source, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_state)
            {
                _current = snapshot;
                _savedId = snapshot.Id;
                _filePath = filePath;
                _undo.Clear();
                _redo.Clear();
            }
        }
        finally { _commands.Release(); }
        Changed?.Invoke(this, new PdfSessionChangedEventArgs(snapshot, PdfChangeKind.Opened));
    }

    public Task ApplyAsync(IPdfEditOperation operation, Guid? expectedRevision = null, CancellationToken cancellationToken = default) =>
        ApplyAsync([operation], expectedRevision, cancellationToken);

    public async Task ApplyAsync(IEnumerable<IPdfEditOperation> operations, Guid? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var batch = operations.ToArray();
        if (batch.Length == 0) return;
        var editor = _editor ?? throw new NotSupportedException("This session has no editing backend.");
        if (batch.Any(op => op is null || !editor.Capabilities.Contains(op.Capability)))
            throw new NotSupportedException("The selected backend does not support every operation in the transaction.");
        PdfSnapshot after;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Current ?? throw new InvalidOperationException("No document is open.");
            if (expectedRevision.HasValue && expectedRevision != before.Id) throw new PdfRevisionConflictException();
            after = await editor.ApplyAsync(before, Array.AsReadOnly(batch), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_state)
            {
                _undo.Add(before);
                _redo.Clear();
                _current = after;
                TrimHistory();
            }
        }
        finally { _commands.Release(); }
        Changed?.Invoke(this, new PdfSessionChangedEventArgs(after, PdfChangeKind.Edited));
    }

    public Task<bool> UndoAsync(CancellationToken cancellationToken = default) => MoveHistoryAsync(undo: true, cancellationToken);
    public Task<bool> RedoAsync(CancellationToken cancellationToken = default) => MoveHistoryAsync(undo: false, cancellationToken);

    private async Task<bool> MoveHistoryAsync(bool undo, CancellationToken cancellationToken)
    {
        PdfSnapshot? target = null;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_state)
            {
                var from = undo ? _undo : _redo;
                var to = undo ? _redo : _undo;
                if (from.Count != 0 && _current is not null)
                {
                    target = from[^1];
                    from.RemoveAt(from.Count - 1);
                    to.Add(_current);
                    _current = target;
                    TrimHistory();
                }
            }
        }
        finally { _commands.Release(); }
        if (target is null) return false;
        Changed?.Invoke(this, new PdfSessionChangedEventArgs(target, undo ? PdfChangeKind.Undo : PdfChangeKind.Redo));
        return true;
    }

    public Task SaveAsAsync(string path, CancellationToken cancellationToken = default) =>
        SaveAndPublishAsync(path, null, cancellationToken);

    /// <summary>Save privately, then publish through a host transfer before marking this revision saved.
    /// Browser downloads and StorageFile providers use this without pretending a temporary file is the destination.</summary>
    public async Task SaveAndPublishAsync(string path, Func<string, CancellationToken, Task>? publish,
        CancellationToken cancellationToken = default)
    {
        PdfSnapshot snapshot;
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = Current ?? throw new InvalidOperationException("No document is open.");
            await PdfStreams.SaveAtomicAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
            if (publish is not null) await publish(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
            lock (_state)
            {
                _savedId = snapshot.Id;
                _filePath = Path.GetFullPath(path);
            }
        }
        finally { _commands.Release(); }
        Changed?.Invoke(this, new PdfSessionChangedEventArgs(snapshot, PdfChangeKind.Saved));
    }

    private void TrimHistory()
    {
        while (_undo.Count + _redo.Count > MaximumHistoryEntries ||
               _undo.Sum(s => s.ByteLength) + _redo.Sum(s => s.ByteLength) > MaximumHistoryBytes)
        {
            if (_undo.Count > 0) _undo.RemoveAt(0);
            else if (_redo.Count > 0) _redo.RemoveAt(0);
            else break;
        }
    }
}
