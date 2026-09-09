using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Erbai.Core.Storage;

/// <summary>
/// SQLite 串行执行器：所有数据库访问经单一专用线程串行化
/// （docs/03 §3.2「专用写线程串行化所有写」的 C# 等价实现，读写都走同一
/// 通道，与旧版 asyncio.Lock 串行化语义一致）。
/// 异常兜底回滚活动事务：部分执行的语句（如 UPDATE 成功、INSERT 审计失败）
/// 不得悬挂到下一次 commit 意外提交。
/// </summary>
internal sealed class SqliteExecutor
{
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;
    private readonly SqliteConnection _connection;

    public SqliteExecutor(SqliteConnection connection)
    {
        _connection = connection;
        _worker = Task.Run(RunLoopAsync);
    }

    /// <summary>投递一个操作到专用线程执行；调用方可取消等待，但已投递的操作会执行完。</summary>
    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = _channel.Writer.TryWrite(new WorkItem(connection =>
        {
            try
            {
                tcs.SetResult(action(connection));
            }
            catch (Exception ex)
            {
                RollbackQuietly(connection);
                tcs.SetException(ex);
            }
        }));
        if (!accepted)
        {
            // 通道已完成（StopAsync 之后）：TryWrite 失败且无后续回包，TCS 将
            // 永不完成 → 无 ct 的调用方挂死。显式失败而不是悬挂（阶段 1 遗留
            // 补课：SqliteExecutor 关闭竞态）。
            tcs.SetException(new InvalidOperationException("storage engine is closed"));
        }

        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>完成在途操作后退出专用线程并关闭连接（对齐旧版 close 等 in-flight 语义）。</summary>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        await _worker;
        _connection.Close();
    }

    private async Task RunLoopAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            item.Invoke(_connection);
        }
    }

    private static void RollbackQuietly(SqliteConnection connection)
    {
        // 写失败自动回滚防半截事务；无活动事务时 ROLLBACK 报错，忽略。
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private sealed record WorkItem(Action<SqliteConnection> Invoke);
}
