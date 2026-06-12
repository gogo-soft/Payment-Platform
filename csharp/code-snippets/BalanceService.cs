// code-snippets/BalanceService.cs
// SRP + DIP + Post-Commit Hook: Fund management service
// Extracted from a production payment platform

using System.Data;
using System.Data.Common;

namespace PaymentPlatform.Patterns;

// === Domain Types ===
public record Balance(decimal Available, decimal Frozen, decimal Deposit)
{
    public decimal Total => Available + Frozen + Deposit;
    public bool HasDebt => Deposit < 0;
}

public enum ScenarioType
{
    Collection = 0,
    Payout = 1,
    Withdrawal = 2,
    Refund = 3,
}

public enum FundType
{
    CollectionRecharge = 1,
    CollectionDeduction = 18,
    CollectionCommission = 19,
    PayoutDeduction = 4,
    PreDeduction = 42,
    PreDeductionArrears = 40,
    SupplementArrears = 37,
    MerchantArrearsCancel = 17,
    MerchantDeductArrears = 16,
}

// === Service: ONE responsibility — fund movement ===
public interface IBalanceService
{
    Task<bool> ChangeBalanceAsync(
        DbConnection conn,
        DbTransaction transaction,
        string table,
        long userId,
        decimal amount,
        string orderCode,
        ScenarioType scenario,
        FundType fundType);
}

public class BalanceService : IBalanceService
{
    private readonly ILogger<BalanceService> _logger;
    private readonly IPostCommitHookManager _hooks;

    public BalanceService(ILogger<BalanceService> logger, IPostCommitHookManager hooks)
    {
        _logger = logger;
        _hooks = hooks;
    }

    public async Task<bool> ChangeBalanceAsync(
        DbConnection conn,
        DbTransaction transaction,
        string table,
        long userId,
        decimal amount,
        string orderCode,
        ScenarioType scenario,
        FundType fundType)
    {
        if (table is not ("partner" and "merchant"))
            throw new ArgumentException($"Invalid table: {table}");

        amount = decimal.Round(amount, 4);
        var userType = table == "partner" ? 0 : 1;

        // 1. Atomic read with row lock
        var balance = await ReadBalanceForUpdate(conn, transaction, table, userId);
        if (balance is null)
        {
            _logger.LogWarning("User {UserId} not found in {Table}", userId, table);
            return false;
        }

        var beforeTotal = balance.Value.Total;
        var debtRecordType = GetDebtRecordType(fundType);

        // 2. Branch: Debit with insufficient balance → generate arrears
        if (amount < 0 && balance.Value.Available + amount < 0)
        {
            if (debtRecordType is null)
            {
                _logger.LogWarning("Insufficient balance and arrears not allowed: fundType={FundType}", fundType);
                return false;
            }

            var debtChange = balance.Value.Available + amount; // Negative number

            await InsertBalanceRecord(conn, transaction, orderCode, userType, userId,
                beforeTotal, amount, beforeTotal + amount, scenario, fundType,
                balance.Value.Available, balance.Value.Frozen, balance.Value.Deposit,
                0, balance.Value.Frozen, balance.Value.Deposit + debtChange);

            await InsertBalanceRecord(conn, transaction, orderCode, userType, userId,
                balance.Value.Deposit, debtChange, balance.Value.Deposit + debtChange,
                scenario, debtRecordType.Value);

            await ExecuteUpdate(conn, transaction, table, userId,
                "balance = 0, balance_deposit = balance_deposit + @Debt",
                new { Debt = debtChange });

            _logger.LogInformation("Generated arrears: userId={UserId} amount={Amount}", userId, debtChange);
            return true;
        }

        // 3. Branch: Credit with outstanding debt → repay first
        if (amount > 0 && balance.Value.HasDebt)
        {
            var repayAmount = Math.Min(amount, Math.Abs(balance.Value.Deposit));
            var balanceAdd = amount - repayAmount;
            var depositAfter = balance.Value.Deposit + repayAmount;

            await InsertBalanceRecord(conn, transaction, orderCode, userType, userId,
                beforeTotal, amount, beforeTotal + amount, scenario, fundType,
                balance.Value.Available, balance.Value.Frozen, balance.Value.Deposit,
                balanceAdd, balance.Value.Frozen, depositAfter);

            await InsertBalanceRecord(conn, transaction, orderCode, userType, userId,
                balance.Value.Deposit, repayAmount, depositAfter,
                scenario, FundType.MerchantArrearsCancel);

            await ExecuteUpdate(conn, transaction, table, userId,
                "balance = balance + @BalanceAdd, balance_deposit = balance_deposit + @Repay",
                new { BalanceAdd = balanceAdd, Repay = repayAmount });

            // Enqueue post-commit reconciliation
            if (userType == 0 && balanceAdd > 0)
            {
                _hooks.Enqueue($"reconcile:{userId}", async () =>
                    await TriggerReconciliation(userId, orderCode));
            }

            _logger.LogInformation("Repaid debt: userId={UserId} repaid={Repay}", userId, repayAmount);
            return true;
        }

        // 4. Normal credit/debit
        var afterBalance = balance.Value.Available + amount;
        await ExecuteUpdate(conn, transaction, table, userId,
            "balance = balance + @Amount", new { Amount = amount });

        await InsertBalanceRecord(conn, transaction, orderCode, userType, userId,
            beforeTotal, amount, beforeTotal + amount, scenario, fundType,
            balance.Value.Available, balance.Value.Frozen, balance.Value.Deposit,
            afterBalance, balance.Value.Frozen, 0);

        if (userType == 0 && amount > 0)
        {
            _hooks.Enqueue($"reconcile:{userId}", async () =>
                await TriggerReconciliation(userId, orderCode));
        }

        _logger.LogInformation("Balance change: userId={UserId} amount={Amount} after={After}",
            userId, amount, afterBalance);
        return true;
    }

    // === Private helpers ===

    private static async Task<Balance?> ReadBalanceForUpdate(
        DbConnection conn, DbTransaction transaction, string table, long userId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT balance, balance_frozen, balance_deposit FROM {table} WHERE id = @Id FOR UPDATE";
        AddParameter(cmd, "@Id", userId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new Balance(
            reader.GetDecimal(0),
            reader.GetDecimal(1),
            reader.GetDecimal(2));
    }

    private static async Task InsertBalanceRecord(
        DbConnection conn, DbTransaction transaction,
        string code, int userType, long userId,
        decimal before, decimal amount, decimal after,
        ScenarioType scenario, FundType fundType,
        decimal balanceAvailableBefore = 0, decimal balanceFrozenBefore = 0,
        decimal balanceDepositBefore = 0, decimal balanceAvailableAfter = 0,
        decimal balanceFrozenAfter = 0, decimal balanceDepositAfter = 0)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO balance_record
            (code, user_type, user_id, change_before, amount, change_after,
             record_type, fund_type, balance_available_before, balance_frozen_before,
             balance_deposit_before, balance_available_after, balance_frozen_after,
             balance_deposit_after)
            VALUES
            (@Code, @UserType, @UserId, @Before, @Amount, @After,
             @RecordType, @FundType, @AvailBefore, @FrozenBefore,
             @DepositBefore, @AvailAfter, @FrozenAfter, @DepositAfter)";

        AddParameter(cmd, "@Code", code);
        AddParameter(cmd, "@UserType", userType);
        AddParameter(cmd, "@UserId", userId);
        AddParameter(cmd, "@Before", before);
        AddParameter(cmd, "@Amount", amount);
        AddParameter(cmd, "@After", after);
        AddParameter(cmd, "@RecordType", (int)scenario);
        AddParameter(cmd, "@FundType", (int)fundType);
        AddParameter(cmd, "@AvailBefore", balanceAvailableBefore);
        AddParameter(cmd, "@FrozenBefore", balanceFrozenBefore);
        AddParameter(cmd, "@DepositBefore", balanceDepositBefore);
        AddParameter(cmd, "@AvailAfter", balanceAvailableAfter);
        AddParameter(cmd, "@FrozenAfter", balanceFrozenAfter);
        AddParameter(cmd, "@DepositAfter", balanceDepositAfter);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteUpdate(
        DbConnection conn, DbTransaction transaction,
        string table, long userId, string setClause, object parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"UPDATE {table} SET {setClause} WHERE id = @Id";
        AddParameter(cmd, "@Id", userId);

        foreach (var prop in parameters.GetType().GetProperties())
            AddParameter(cmd, $"@{prop.Name}", prop.GetValue(parameters));

        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static FundType? GetDebtRecordType(FundType fundType) => fundType switch
    {
        FundType.PreDeduction => FundType.PreDeductionArrears,
        FundType.CollectionDeduction => FundType.SupplementArrears,
        FundType.PayoutDeduction => FundType.MerchantDeductArrears,
        _ => null
    };

    private Task TriggerReconciliation(long userId, string orderCode)
    {
        // Delegate to reconciliation service
        _logger.LogInformation("Post-commit reconcile queued: userId={UserId} order={Order}",
            userId, orderCode);
        return Task.CompletedTask;
    }
}

// === Post-Commit Hook Manager ===
public interface IPostCommitHookManager
{
    void Enqueue(string key, Func<Task> callback);
    Task ExecuteAllAsync();
    void Clear();
}

public class PostCommitHookManager : IPostCommitHookManager
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
        var hooks = Interlocked.Exchange(
            ref _hooks, new ConcurrentDictionary<string, Func<Task>>());

        foreach (var (key, callback) in hooks)
        {
            try { await callback(); }
            catch (Exception ex)
            {
                // Hook failure must not affect the committed transaction
                Console.Error.WriteLine($"Post-commit hook failed: {key} — {ex.Message}");
            }
        }
    }

    public void Clear() => _hooks.Clear();
}
