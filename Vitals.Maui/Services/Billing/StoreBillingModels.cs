namespace Vitals.Maui.Services.Billing;

public enum StorePurchaseStatus
{
    Purchased,
    Pending,
    Cancelled,
    Failed
}

/// <summary>
/// Store-authoritative presentation for one configured Vitals subscription.
/// Plan/period come from the backend catalog; price/currency come from Apple
/// or Google and are display-only.
/// </summary>
public sealed class StoreBillingProduct
{
    public string Provider { get; init; } = string.Empty;
    public string Plan { get; init; } = string.Empty;
    public string BillingPeriod { get; init; } = string.Empty;
    public string ProductId { get; init; } = string.Empty;
    public string? BasePlanId { get; init; }

    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string LocalizedPrice { get; init; } = string.Empty;
    public decimal? Price { get; init; }
    public string? CurrencyCode { get; init; }
}

/// <summary>
/// Opaque native-store result. The mobile client never derives entitlement
/// from this object; a purchased result must still be verified by the Vitals
/// API before the native transaction is completed/acknowledged.
/// </summary>
public sealed class StorePurchaseResult
{
    public StorePurchaseStatus Status { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ProductId { get; init; } = string.Empty;
    public string? BasePlanId { get; init; }

    // Apple -> StoreKit transactionId
    public string? TransactionId { get; init; }

    // Google -> Play Billing purchaseToken
    public string? PurchaseToken { get; init; }

    public string? ErrorMessage { get; init; }

    public bool HasVerificationReference =>
        !string.IsNullOrWhiteSpace(TransactionId)
        || !string.IsNullOrWhiteSpace(PurchaseToken);
}

public enum BillingActionStatus
{
    Succeeded,
    Pending,
    Cancelled,
    Failed
}

public sealed class BillingActionResult
{
    public BillingActionStatus Status { get; init; }
    public BillingVerificationResult? Verification { get; init; }
    public string? ErrorMessage { get; init; }

    public static BillingActionResult Succeeded(BillingVerificationResult verification) =>
        new()
        {
            Status = BillingActionStatus.Succeeded,
            Verification = verification
        };

    public static BillingActionResult Pending() =>
        new() { Status = BillingActionStatus.Pending };

    public static BillingActionResult Cancelled() =>
        new() { Status = BillingActionStatus.Cancelled };

    public static BillingActionResult Failed(string message) =>
        new()
        {
            Status = BillingActionStatus.Failed,
            ErrorMessage = message
        };
}
