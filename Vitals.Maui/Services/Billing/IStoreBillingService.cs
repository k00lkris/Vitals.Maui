using Vitals.Maui.Services;
namespace Vitals.Maui.Services.Billing;

/// <summary>
/// Platform billing boundary.
///
/// Implementations talk only to the native store. They never grant Vitals
/// entitlement. Purchased/owned transactions must be sent to the backend for
/// server-authoritative verification before CompletePurchaseAsync is called.
/// </summary>
public interface IStoreBillingService
{
    string Provider { get; }

    Task<IReadOnlyList<StoreBillingProduct>> GetProductsAsync(
        BillingCatalog catalog,
        CancellationToken cancellationToken = default);

    Task<StorePurchaseResult> PurchaseAsync(
        StoreBillingProduct product,
        string accountBindingToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorePurchaseResult>> GetOwnedPurchasesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finish/acknowledge a transaction only after the Vitals API has
    /// successfully verified it and applied the household entitlement.
    /// </summary>
    Task CompletePurchaseAsync(
        StorePurchaseResult purchase,
        CancellationToken cancellationToken = default);
}
