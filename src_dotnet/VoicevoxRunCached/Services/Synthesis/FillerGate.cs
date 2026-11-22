namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// フィラーの読み込みを <see cref="SpeechLock"/> の間だけに限る。<see cref="CloseAsync"/> の後は読み込まずに null を返す。
/// </summary>
/// <remarks>
/// <para>フィラーはキャッシュのフォルダ (<c>Filler.Directory</c>) から読むので、<see cref="SpeechLock"/> の外で読むと、
/// ほかのプロセスの <c>--clear</c> や <c>--init</c> の掃除と同時になり、削除が共有違反で失敗する (<c>--clear</c> なら終了コード 4)。</para>
/// <para>フィラーは次の文を待つ間にしか使わず、producer が終わればすべての文が channel にあるので、ロックを手放した後は要らない。
/// <see cref="CloseAsync"/> は読み込み中のものが終わるのを待ってから戻る (戻った後はフォルダに触れない)。</para>
/// </remarks>
public sealed class FillerGate(Func<CancellationToken, Task<byte[]?>> loadFiller) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _closed;

    /// <summary>
    /// フィラーを読む。<see cref="CloseAsync"/> の後は読まずに null。
    /// </summary>
    public async Task<byte[]?> GetAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return _closed ? null : await loadFiller(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// 以後は読まないようにする。読み込み中のものがあれば終わるまで待つ。
    /// </summary>
    /// <remarks>
    /// キャンセルを取らない。待たずに戻ると、ロックを手放した後も読み込みが続くため (読み込みは呼び出し元のキャンセルで止まる)。
    /// </remarks>
    public async Task CloseAsync()
    {
        await _semaphore.WaitAsync(CancellationToken.None);
        _closed = true;
        _semaphore.Release();
    }

    public void Dispose() => _semaphore.Dispose();
}
