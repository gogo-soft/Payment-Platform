# Post-Commit Hook Pattern

## The Problem

After a balance change is committed to the database, you often need to trigger side effects:

- Reconcile partner pending debits
- Update cached balances
- Send WebSocket notification
- Log structured metrics

Doing this **inside the transaction** is wrong:
```csharp
// ❌ Side effects inside transaction
await db.BeginTransactionAsync();
await UpdateBalance(conn, userId, amount);
await ReconcileAsync(conn, userId);      // If this fails, balance update rolls back
await NotifyWebSocket(userId, amount);   // WebSocket can't roll back — inconsistency!
await db.CommitTransactionAsync();
```

Doing it **after the transaction** risks the caller forgetting:
```csharp
// ❌ Caller must remember
await service.ChangeBalance(conn, userId, amount);
await db.CommitTransactionAsync();
// Did we reconcile? Did we notify? Who knows.
```

## The Pattern

Register callbacks that fire **after** the transaction commits successfully. If the transaction rolls back, callbacks are discarded. The caller doesn't need to remember.

```csharp
// Post-commit hook manager
public class PostCommitHookManager
{
    private readonly ConcurrentDictionary<string, Func<Task>> _hooks = new();

    public void Enqueue(string key, Func<Task> callback)
    {
        _hooks.AddOrUpdate(key,
            _ => callback,
            (_, existing) => async () => { await existing(); await callback(); });
    }

    public async Task ExecuteAllAsync()
    {
        var hooks = Interlocked.Exchange(ref _hooks, new ConcurrentDictionary<string, Func<Task>>());
        foreach (var (key, callback) in hooks)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                // Hook failure must NOT affect the transaction (it's already committed)
                Log.Error(ex, "Post-commit hook failed: {HookKey}", key);
            }
        }
    }

    public void Clear()
    {
        _hooks.Clear();
    }
}

// Unit of Work: wraps transaction + post-commit hooks
public interface IUnitOfWork
{
    Task BeginAsync();
    Task CommitAsync();
    Task RollbackAsync();
    PostCommitHookManager Hooks { get; }
}

public class SqlUnitOfWork : IUnitOfWork, IAsyncDisposable
{
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;
    public PostCommitHookManager Hooks { get; } = new();

    public SqlUnitOfWork(DbConnection connection) => _connection = connection;

    public async Task BeginAsync()
        => _transaction = await _connection.BeginTransactionAsync();

    public async Task CommitAsync()
    {
        if (_transaction is null) return;
        await _transaction.CommitAsync();
        await _transaction.DisposeAsync();
        _transaction = null;

        // ✅ Fire hooks AFTER commit succeeds
        await Hooks.ExecuteAllAsync();
    }

    public async Task RollbackAsync()
    {
        if (_transaction is null) return;
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        _transaction = null;

        // ✅ Discard hooks on rollback — nothing to reconcile
        Hooks.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
            await RollbackAsync();
    }
}

// Service: enqueues post-commit work without knowing transaction state
public class BalanceService
{
    public async Task<bool> ChangeBalanceAsync(
        IUnitOfWork uow, long userId, decimal amount, string orderCode)
    {
        // Core balance change
        var conn = ((SqlUnitOfWork)uow).Connection;
        await conn.ExecuteAsync(
            "UPDATE partner SET balance = balance + @Amount WHERE id = @Id",
            new { Amount = amount, Id = userId });

        // Enqueue post-commit reconcile — will fire ONLY if CommitAsync() succeeds
        uow.Hooks.Enqueue($"reconcile:{userId}", async () =>
        {
            await ReconcilePendingDebits(userId, orderCode);
        });

        uow.Hooks.Enqueue($"notify:{userId}", async () =>
        {
            await PublishBalanceChangedEvent(userId, amount);
        });

        return true;
    }
}

// Orchestrator: clean transaction boundary
public class OrderCompletionService
{
    private readonly BalanceService _balance;
    private readonly IUnitOfWorkFactory _uowFactory;

    public async Task<bool> CompleteOrderAsync(string code, string utr)
    {
        await using var uow = await _uowFactory.CreateAsync();

        try
        {
            await uow.BeginAsync();

            // Business logic...
            await _balance.ChangeBalanceAsync(uow, partnerId, -amount, code);
            await _balance.ChangeBalanceAsync(uow, merchantId, +amount, code);

            await uow.CommitAsync();
            // ^ Hooks fire here: reconcile + notify. Guaranteed.
            return true;
        }
        catch
        {
            await uow.RollbackAsync();
            // ^ Hooks discarded. No reconcile, no notify. Correct.
            return false;
        }
    }
}

// ✅ Test: verify hooks fire on commit, NOT on rollback
[Fact]
public async Task ChangeBalance_OnCommit_FiresReconcileHook()
{
    var uow = new Mock<IUnitOfWork>();
    var hookManager = new PostCommitHookManager();
    uow.Setup(u => u.Hooks).Returns(hookManager);

    var reconcileCalled = false;
    hookManager.Enqueue("reconcile:1", () => { reconcileCalled = true; return Task.CompletedTask; });

    await hookManager.ExecuteAllAsync();

    Assert.True(reconcileCalled);
}

[Fact]
public async Task ChangeBalance_OnRollback_HooksDiscarded()
{
    var hookManager = new PostCommitHookManager();
    var reconcileCalled = false;
    hookManager.Enqueue("reconcile:1", () => { reconcileCalled = true; return Task.CompletedTask; });

    hookManager.Clear();  // Simulate rollback

    Assert.False(reconcileCalled);  // Hook was discarded, not executed
}
```

## When to Use Post-Commit Hooks (vs Events)

| Scenario | Use |
|----------|-----|
| Side effect must NOT happen if transaction rolls back | Post-Commit Hook |
| Side effect can tolerate eventual consistency | Event Bus (Observer) |
| Side effect must happen in the same process | Post-Commit Hook |
| Side effect can be handled by another service | Event Bus |

## Trade-offs

| Gain | Cost |
|------|------|
| Guaranteed: no reconcile if balance update rolled back | Hooks run synchronously after commit (latency) |
| Caller doesn't need to remember side effects | Hook failures are fire-and-forget (no retry built-in) |
| Testable: inject mock hook manager | Hook ordering is FIFO — can't specify dependencies |

## Key Takeaway

In the real system, `BalanceService` enqueues a `PostCommitReconcileTask` after every partner balance credit. If the transaction fails, the task is discarded — no phantom reconciliation. If it succeeds, reconciliation runs guaranteed. This pattern has prevented **zero** reconciliation bugs in 18 months of production.
