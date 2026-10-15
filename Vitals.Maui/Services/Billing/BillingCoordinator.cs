using System.Diagnostics;

namespace Vitals.Maui.Services.Billing;

/// <summary>
/// Coordinates native-store billing with the server-authoritative Vitals
/// entitlement contract.
///
/// Flow:
///   backend catalog -> native store -> opaque purchase reference
///   -> /api/billing/verify -> entitlement refresh -> finish/acknowledge
///
/// The native store result alone never unlocks Premium.
/// </summary>
public sealed class BillingCoordinator
{
    private readonly ApiService _api;
    private readonly EntitlementService _entitlements;
    private readonly IStoreBillingService _store;

    public BillingCoordinator(
        ApiService api,
        EntitlementService entitlements,
        IStoreBillingService store)
    {
        _api = api;
        _entitlements = entitlements;
        _store = store;
    }

    public string Provider => _store.Provider;

    public async Task<IReadOnlyList<StoreBillingProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await LoadCatalogAsync();
        cancellationToken.ThrowIfCancellationRequested();

        return await _store.GetProductsAsync(catalog, cancellationToken);
    }

    public async Task<BillingActionResult> PurchaseAsync(
        string plan,
        string billingPeriod,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entitlement = await _entitlements.RefreshAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (entitlement is null)
                return BillingActionResult.Failed(
                    "We couldn't load your Vitals plan. Please try again.");

            if (!entitlement.CanStartPurchase && !entitlement.CanManageBilling)
                return BillingActionResult.Failed(
                    "This account is not authorized to manage billing for the household.");

            var catalog = await LoadCatalogAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var configuredProduct = catalog.Products.FirstOrDefault(product =>
                product.Configured
                && string.Equals(product.Plan, plan, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    product.BillingPeriod,
                    billingPeriod,
                    StringComparison.OrdinalIgnoreCase));

            if (configuredProduct is null)
                return BillingActionResult.Failed(
                    "That subscription option is not configured for this store.");

            var products = await _store.GetProductsAsync(catalog, cancellationToken);
            var product = products.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ProductId,
                    configuredProduct.ProductId,
                    StringComparison.Ordinal)
                && string.Equals(
                    candidate.BasePlanId ?? string.Empty,
                    configuredProduct.BasePlanId ?? string.Empty,
                    StringComparison.Ordinal));

            if (product is null)
                return BillingActionResult.Failed(
                    "That subscription is not currently available from the store.");

            var purchase = await _store.PurchaseAsync(
                product,
                catalog.AccountBindingToken,
                cancellationToken);

            return await VerifyAndCompleteAsync(purchase, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"=== BILLING PURCHASE ERROR: {ex}");
            return BillingActionResult.Failed(
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "The purchase couldn't be completed. Please try again."
                    : ex.Message);
        }
    }

    /// <summary>
    /// Replays currently owned store subscriptions through the same backend
    /// verification path used for a new purchase. This is the basis for
    /// Restore Purchases and multi-device recovery.
    /// </summary>
    public async Task<BillingActionResult> RestorePurchasesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Loading the catalog first confirms billing is configured for the
            // current platform and keeps restore behavior aligned with purchase.
            _ = await LoadCatalogAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var purchases = await _store.GetOwnedPurchasesAsync(cancellationToken);
            if (purchases.Count == 0)
                return BillingActionResult.Failed(
                    "No active Vitals subscription was found for this store account.");

            BillingVerificationResult? latestVerification = null;
            Exception? lastVerificationError = null;

            foreach (var purchase in purchases.Where(p =>
                         p.Status == StorePurchaseStatus.Purchased
                         && p.HasVerificationReference))
            {
                try
                {
                    var verification = await VerifyPurchaseAsync(
                        purchase,
                        cancellationToken);

                    if (verification?.Verified != true)
                        continue;

                    await _entitlements.RefreshAsync();
                    await _store.CompletePurchaseAsync(purchase, cancellationToken);
                    latestVerification = verification;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A store account can contain purchases that belong to a
                    // different Vitals account/household. Keep looking; the
                    // server is the authority on whether each purchase may bind.
                    lastVerificationError = ex;
                    Debug.WriteLine($"=== BILLING RESTORE ITEM ERROR: {ex.Message}");
                }
            }

            if (latestVerification is not null)
                return BillingActionResult.Succeeded(latestVerification);

            return BillingActionResult.Failed(
                lastVerificationError?.Message
                ?? "No Vitals subscription could be restored for this account.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"=== BILLING RESTORE ERROR: {ex}");
            return BillingActionResult.Failed(
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "Purchases couldn't be restored. Please try again."
                    : ex.Message);
        }
    }

    private async Task<BillingActionResult> VerifyAndCompleteAsync(
        StorePurchaseResult purchase,
        CancellationToken cancellationToken)
    {
        switch (purchase.Status)
        {
            case StorePurchaseStatus.Pending:
                return BillingActionResult.Pending();

            case StorePurchaseStatus.Cancelled:
                return BillingActionResult.Cancelled();

            case StorePurchaseStatus.Failed:
                return BillingActionResult.Failed(
                    purchase.ErrorMessage
                    ?? "The store couldn't complete the purchase.");

            case StorePurchaseStatus.Purchased:
                break;

            default:
                return BillingActionResult.Failed(
                    "The store returned an unknown purchase state.");
        }

        if (!purchase.HasVerificationReference)
            return BillingActionResult.Failed(
                "The store purchase is missing its verification reference.");

        var verification = await VerifyPurchaseAsync(purchase, cancellationToken);
        if (verification?.Verified != true)
            return BillingActionResult.Failed(
                "The purchase could not be verified.");

        // Refresh before finishing the native transaction so the app observes
        // the server-applied entitlement while the purchase is still recoverable.
        await _entitlements.RefreshAsync();

        // Only now may the native transaction be finished/acknowledged.
        await _store.CompletePurchaseAsync(purchase, cancellationToken);

        return BillingActionResult.Succeeded(verification);
    }

    private Task<BillingVerificationResult?> VerifyPurchaseAsync(
        StorePurchaseResult purchase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return _api.VerifyBillingPurchaseAsync(
            _store.Provider,
            transactionId: purchase.TransactionId,
            purchaseToken: purchase.PurchaseToken);
    }

    private async Task<BillingCatalog> LoadCatalogAsync()
    {
        var catalog = await _api.GetBillingCatalogAsync(_store.Provider);
        if (catalog is null)
            throw new InvalidOperationException(
                "The Vitals billing catalog couldn't be loaded.");

        if (!catalog.Configured)
            throw new InvalidOperationException(
                $"{_store.Provider} billing products are not configured yet.");

        if (string.IsNullOrWhiteSpace(catalog.AccountBindingToken))
            throw new InvalidOperationException(
                "The Vitals billing account binding is unavailable.");

        return catalog;
    }
}
