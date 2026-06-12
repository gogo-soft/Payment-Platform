# SRP — Single Responsibility Principle

## Real Scenario

In a payment platform, "changing a user's balance" sounds simple. But consider what actually happens when a collection order succeeds:

1. Deduct from partner balance
2. Credit merchant balance
3. Calculate and distribute multi-tier commissions (partner's upline)
4. Calculate and distribute merchant rebates (merchant's upline)
5. Update system ledger
6. Trigger post-commit reconciliation
7. Log structured metrics
8. Publish order-completed event

**The naive approach** puts all of this into one god-class `OrderService`. The result: a 2000-line method, impossible to test, and every bug fix risks breaking unrelated logic.

## The Pattern

Split responsibilities so each class has exactly **one reason to change**.

```csharp
// ❌ BEFORE: one class, too many responsibilities
public class GodOrderService
{
    public async Task CompleteOrder(string code, string utr)
    {
        // 200+ lines: balance, commission, rebate, ledger, reconcile, notify...
    }
}
```

```csharp
// ✅ AFTER: each class has one job

// THIS class ONLY handles fund movement (debit/credit/arrears)
public class BalanceService
{
    private readonly ILogger<BalanceService> _logger;

    public BalanceService(ILogger<BalanceService> logger)
        => _logger = logger;

    public async Task<bool> ChangeBalanceAsync(
        DbConnection conn, string table, long userId, decimal amount,
        string orderCode, ScenarioType scenario, FundType fundType)
    {
        // SELECT ... FOR UPDATE (atomic read)
        // Calculate: can we debit? do we generate arrears? is there debt to repay?
        // UPDATE balance
        // INSERT balance_record (audit trail)
        // Return success/failure
        // THAT'S IT. Nothing else.
    }
}

// THIS class ONLY handles commission distribution
public class CommissionService
{
    public async Task<CommissionResult> DistributeAsync(
        DbConnection conn, long partnerId, decimal amount, string channelCode)
    {
        // Walk the partner tree
        // Calculate each level's share
        // Return distribution plan (caller decides when to execute)
    }
}

// THIS class ONLY handles post-commit hooks
public class PostCommitHookManager
{
    private readonly ConcurrentDictionary<string, List<Func<Task>>> _hooks = new();

    public void Enqueue(string key, Func<Task> callback)
        => _hooks.AddOrUpdate(key, _ => [callback], (_, list) => { list.Add(callback); return list; });

    public async Task ExecuteAsync()
    {
        foreach (var (_, callbacks) in _hooks)
            foreach (var cb in callbacks)
                await cb();
        _hooks.Clear();
    }
}

// Orchestrator: coordinates the services (thin, no business logic)
public class OrderCompletionService
{
    private readonly BalanceService _balance;
    private readonly CommissionService _commission;
    private readonly PostCommitHookManager _postCommit;
    private readonly IEventPublisher _events;

    public async Task<bool> CompleteOrderAsync(string code, string utr)
    {
        // 1. Validate order exists
        // 2. Debit partner   → BalanceService
        // 3. Credit merchant  → BalanceService
        // 4. Distribute fees  → CommissionService
        // 5. Publish event    → IEventPublisher
        // 6. Enqueue reconcile → PostCommitHookManager
        return true;
    }
}
```

## Trade-offs

| Gain | Cost |
|------|------|
| Each service testable in isolation | More files, more DI registrations |
| Bug in commission calc can't corrupt balance | Need an orchestrator to coordinate |
| New devs understand one class at a time | Must document the orchestration flow |

## Key Takeaway

**"One reason to change"** is the test. If modifying commission logic forces you to touch `BalanceService`, SRP is violated. In the real system, `BalanceService` has changed 3 times in 12 months; `CommissionService` has changed 12+ times. They evolve at different rates — that's the proof SRP works.
