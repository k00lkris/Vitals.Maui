#if ANDROID
using Android.BillingClient.Api;
using Microsoft.Maui.ApplicationModel;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Vitals.Maui.Services.Billing;

/// <summary>
/// Google Play Billing implementation for Vitals subscriptions.
///
/// This service is intentionally store-only: it retrieves localized product
/// details, launches Play purchase UI, returns the opaque purchase token, and
/// acknowledges only after BillingCoordinator has completed server-side
/// verification and entitlement application.
/// </summary>
public sealed class GooglePlayBillingService : IStoreBillingService, IDisposable
{
    private sealed record NativeSubscriptionProduct(
        ProductDetails ProductDetails,
        ProductDetails.SubscriptionOfferDetails OfferDetails);

    private readonly BillingClient _billingClient;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _purchaseGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Purchase> _knownPurchases = new();
    private readonly Dictionary<(string ProductId, string BasePlanId), NativeSubscriptionProduct>
        _nativeProducts = new();

    private readonly object _purchaseSync = new();
    private TaskCompletionSource<StorePurchaseResult>? _pendingPurchase;
    private string? _pendingProductId;
    private bool _disposed;

    public GooglePlayBillingService()
    {
        var context = Platform.AppContext
            ?? throw new InvalidOperationException(
                "Android application context is unavailable.");

        var builder = BillingClient.NewBuilder(context);

        // The binding supplies an Action overload so we do not need a separate
        // Java listener wrapper object.
        builder.SetListener((result, purchases) =>
            OnPurchasesUpdated(result, purchases));

        var pendingParams = PendingPurchasesParams.NewBuilder()
            .EnableOneTimeProducts()
            .Build();

        builder.EnablePendingPurchases(pendingParams);
        builder.EnableAutoServiceReconnection();

        _billingClient = builder.Build();
    }

    public string Provider => "google";

    public async Task<IReadOnlyList<StoreBillingProduct>> GetProductsAsync(
        BillingCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!string.Equals(catalog.Provider, Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The billing catalog does not belong to Google Play.");

        await EnsureConnectedAsync(cancellationToken);

        var configured = catalog.Products
            .Where(product =>
                product.Configured
                && !string.IsNullOrWhiteSpace(product.ProductId)
                && !string.IsNullOrWhiteSpace(product.BasePlanId))
            .ToList();

        if (configured.Count == 0)
            return Array.Empty<StoreBillingProduct>();

        var queryProducts = configured
            .Select(product => product.ProductId!)
            .Distinct(StringComparer.Ordinal)
            .Select(productId =>
                QueryProductDetailsParams.Product.NewBuilder()
                    .SetProductId(productId)
                    .SetProductType(BillingClient.ProductType.Subs)
                    .Build())
            .ToList();

        var queryParams = QueryProductDetailsParams.NewBuilder()
            .SetProductList(queryProducts)
            .Build();

        var queryResult = await _billingClient
            .QueryProductDetailsAsync(queryParams)
            .WaitAsync(cancellationToken);

        EnsureOk(queryResult.Result, "Google Play couldn't load subscription products");

        var detailsByProduct = queryResult.ProductDetailsList
            .GroupBy(details => details.ProductId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var results = new List<StoreBillingProduct>();
        _nativeProducts.Clear();

        foreach (var configuredProduct in configured)
        {
            if (!detailsByProduct.TryGetValue(
                    configuredProduct.ProductId!,
                    out var productDetails))
            {
                continue;
            }

            var offer = productDetails
                .GetSubscriptionOfferDetails()?
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.BasePlanId,
                        configuredProduct.BasePlanId,
                        StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(candidate.OfferId));

            // We deliberately buy the base plan rather than silently selecting
            // an introductory/promotional offer. Marketing offers can be added
            // later as an explicit commercial feature.
            if (offer is null)
                continue;

            var pricingPhase = offer.PricingPhases.PricingPhaseList.LastOrDefault();
            if (pricingPhase is null)
                continue;

            var key = (
                configuredProduct.ProductId!,
                configuredProduct.BasePlanId!);

            _nativeProducts[key] = new NativeSubscriptionProduct(
                productDetails,
                offer);

            results.Add(new StoreBillingProduct
            {
                Provider = Provider,
                Plan = configuredProduct.Plan,
                BillingPeriod = configuredProduct.BillingPeriod,
                ProductId = configuredProduct.ProductId!,
                BasePlanId = configuredProduct.BasePlanId,
                DisplayName = productDetails.Name,
                Description = productDetails.Description,
                LocalizedPrice = pricingPhase.FormattedPrice,
                Price = pricingPhase.PriceAmountMicros / 1_000_000m,
                CurrencyCode = pricingPhase.PriceCurrencyCode
            });
        }

        return results;
    }

    public async Task<StorePurchaseResult> PurchaseAsync(
        StoreBillingProduct product,
        string accountBindingToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!string.Equals(product.Provider, Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This product is not a Google Play product.");

        if (string.IsNullOrWhiteSpace(product.BasePlanId))
            throw new InvalidOperationException(
                "The Google Play base plan is missing.");

        if (string.IsNullOrWhiteSpace(accountBindingToken))
            throw new InvalidOperationException(
                "The Vitals billing account binding is unavailable.");

        await _purchaseGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var key = (product.ProductId, product.BasePlanId);
            if (!_nativeProducts.TryGetValue(key, out var nativeProduct))
                throw new InvalidOperationException(
                    "Google Play product details are stale. Reload the subscription options and try again.");

            var activity = Platform.CurrentActivity;
            if (activity is null)
                throw new InvalidOperationException(
                    "The Android purchase screen is not currently available.");

            var productParams = BillingFlowParams.ProductDetailsParams.NewBuilder()
                .SetProductDetails(nativeProduct.ProductDetails)
                .SetOfferToken(nativeProduct.OfferDetails.OfferToken)
                .Build();

            var flowParams = BillingFlowParams.NewBuilder()
                .SetProductDetailsParamsList(
                    new List<BillingFlowParams.ProductDetailsParams>
                    {
                        productParams
                    })
                .SetObfuscatedAccountId(accountBindingToken)
                .Build();

            var completion = new TaskCompletionSource<StorePurchaseResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_purchaseSync)
            {
                _pendingProductId = product.ProductId;
                _pendingPurchase = completion;
            }

            BillingResult launchResult;
            try
            {
                launchResult = await MainThread.InvokeOnMainThreadAsync(
                    () => _billingClient.LaunchBillingFlow(activity, flowParams));
            }
            catch
            {
                ClearPendingPurchase(completion);
                throw;
            }

            if (launchResult.ResponseCode == BillingResponseCode.ItemAlreadyOwned)
            {
                ClearPendingPurchase(completion);

                var owned = await GetOwnedPurchasesAsync(cancellationToken);
                var existing = owned.FirstOrDefault(purchase =>
                    purchase.Status == StorePurchaseStatus.Purchased
                    && string.Equals(
                        purchase.ProductId,
                        product.ProductId,
                        StringComparison.Ordinal));

                return existing
                    ?? new StorePurchaseResult
                    {
                        Status = StorePurchaseStatus.Failed,
                        Provider = Provider,
                        ProductId = product.ProductId,
                        BasePlanId = product.BasePlanId,
                        ErrorMessage =
                            "Google Play reports that this subscription is already owned. Use Restore Purchases."
                    };
            }

            if (launchResult.ResponseCode != BillingResponseCode.Ok)
            {
                ClearPendingPurchase(completion);
                return FromBillingFailure(
                    launchResult,
                    product.ProductId,
                    product.BasePlanId);
            }

            try
            {
                return await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                ClearPendingPurchase(completion);
            }
        }
        finally
        {
            _purchaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<StorePurchaseResult>> GetOwnedPurchasesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken);

        var queryParams = QueryPurchasesParams.NewBuilder()
            .SetProductType(BillingClient.ProductType.Subs)
            .Build();

        var queryResult = await _billingClient
            .QueryPurchasesAsync(queryParams)
            .WaitAsync(cancellationToken);

        EnsureOk(queryResult.Result, "Google Play couldn't restore subscriptions");

        var results = new List<StorePurchaseResult>();

        foreach (var purchase in queryResult.Purchases)
        {
            if (string.IsNullOrWhiteSpace(purchase.PurchaseToken))
                continue;

            _knownPurchases[purchase.PurchaseToken] = purchase;

            var productId = purchase.Products.FirstOrDefault() ?? string.Empty;
            results.Add(ToStorePurchaseResult(purchase, productId, basePlanId: null));
        }

        return results;
    }

    public async Task CompletePurchaseAsync(
        StorePurchaseResult purchase,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!string.Equals(purchase.Provider, Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This purchase did not originate from Google Play.");

        if (string.IsNullOrWhiteSpace(purchase.PurchaseToken))
            throw new InvalidOperationException(
                "Google Play purchase token is missing.");

        await EnsureConnectedAsync(cancellationToken);

        var nativePurchase = await FindPurchaseAsync(
            purchase.PurchaseToken,
            cancellationToken);

        if (nativePurchase is null)
            throw new InvalidOperationException(
                "Google Play no longer reports this subscription as owned.");

        if (nativePurchase.PurchaseState != PurchaseState.Purchased)
            throw new InvalidOperationException(
                "Google Play has not completed this purchase yet.");

        if (nativePurchase.IsAcknowledged)
            return;

        var acknowledgeParams = AcknowledgePurchaseParams.NewBuilder()
            .SetPurchaseToken(nativePurchase.PurchaseToken)
            .Build();

        var result = await _billingClient
            .AcknowledgePurchaseAsync(acknowledgeParams)
            .WaitAsync(cancellationToken);

        EnsureOk(result, "Google Play couldn't acknowledge the subscription");
    }

    private async Task EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        if (_billingClient.IsReady)
            return;

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_billingClient.IsReady)
                return;

            var result = await _billingClient
                .StartConnectionAsync()
                .WaitAsync(cancellationToken);

            EnsureOk(result, "Google Play billing is unavailable");
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private void OnPurchasesUpdated(
        BillingResult billingResult,
        IList<Purchase>? purchases)
    {
        TaskCompletionSource<StorePurchaseResult>? completion;
        string? expectedProductId;

        lock (_purchaseSync)
        {
            completion = _pendingPurchase;
            expectedProductId = _pendingProductId;
        }

        // Store callbacks can arrive outside an active purchase flow (for
        // example, when a previously pending transaction completes). Those
        // purchases are intentionally recovered by GetOwnedPurchasesAsync /
        // Restore Purchases so the server still performs verification.
        if (completion is null)
            return;

        if (billingResult.ResponseCode == BillingResponseCode.UserCancelled)
        {
            completion.TrySetResult(new StorePurchaseResult
            {
                Status = StorePurchaseStatus.Cancelled,
                Provider = Provider,
                ProductId = expectedProductId ?? string.Empty
            });
            return;
        }

        if (billingResult.ResponseCode != BillingResponseCode.Ok)
        {
            completion.TrySetResult(
                FromBillingFailure(
                    billingResult,
                    expectedProductId ?? string.Empty,
                    basePlanId: null));
            return;
        }

        var purchase = purchases?
            .FirstOrDefault(candidate =>
                string.IsNullOrWhiteSpace(expectedProductId)
                || candidate.Products.Contains(expectedProductId));

        if (purchase is null)
        {
            completion.TrySetResult(new StorePurchaseResult
            {
                Status = StorePurchaseStatus.Failed,
                Provider = Provider,
                ProductId = expectedProductId ?? string.Empty,
                ErrorMessage =
                    "Google Play completed the billing flow without returning a subscription."
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(purchase.PurchaseToken))
            _knownPurchases[purchase.PurchaseToken] = purchase;

        completion.TrySetResult(
            ToStorePurchaseResult(
                purchase,
                expectedProductId
                    ?? purchase.Products.FirstOrDefault()
                    ?? string.Empty,
                basePlanId: null));
    }

    private async Task<Purchase?> FindPurchaseAsync(
        string purchaseToken,
        CancellationToken cancellationToken)
    {
        if (_knownPurchases.TryGetValue(purchaseToken, out var known))
            return known;

        var queryParams = QueryPurchasesParams.NewBuilder()
            .SetProductType(BillingClient.ProductType.Subs)
            .Build();

        var queryResult = await _billingClient
            .QueryPurchasesAsync(queryParams)
            .WaitAsync(cancellationToken);

        EnsureOk(queryResult.Result, "Google Play couldn't reload the subscription");

        foreach (var purchase in queryResult.Purchases)
        {
            if (string.IsNullOrWhiteSpace(purchase.PurchaseToken))
                continue;

            _knownPurchases[purchase.PurchaseToken] = purchase;
        }

        return _knownPurchases.TryGetValue(purchaseToken, out var found)
            ? found
            : null;
    }

    private static StorePurchaseResult ToStorePurchaseResult(
        Purchase purchase,
        string productId,
        string? basePlanId)
    {
        var status = purchase.PurchaseState switch
        {
            PurchaseState.Purchased => StorePurchaseStatus.Purchased,
            PurchaseState.Pending => StorePurchaseStatus.Pending,
            _ => StorePurchaseStatus.Failed
        };

        return new StorePurchaseResult
        {
            Status = status,
            Provider = "google",
            ProductId = productId,
            BasePlanId = basePlanId,
            PurchaseToken = purchase.PurchaseToken,
            ErrorMessage = status == StorePurchaseStatus.Failed
                ? "Google Play returned an unspecified purchase state."
                : null
        };
    }

    private static StorePurchaseResult FromBillingFailure(
        BillingResult result,
        string productId,
        string? basePlanId)
    {
        var cancelled = result.ResponseCode == BillingResponseCode.UserCancelled;

        return new StorePurchaseResult
        {
            Status = cancelled
                ? StorePurchaseStatus.Cancelled
                : StorePurchaseStatus.Failed,
            Provider = "google",
            ProductId = productId,
            BasePlanId = basePlanId,
            ErrorMessage = cancelled
                ? null
                : FormatBillingError(result)
        };
    }

    private static void EnsureOk(
        BillingResult result,
        string operation)
    {
        if (result.ResponseCode == BillingResponseCode.Ok)
            return;

        throw new InvalidOperationException(
            $"{operation}: {FormatBillingError(result)}");
    }

    private static string FormatBillingError(BillingResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.DebugMessage)
            ? result.ResponseCode.ToString()
            : result.DebugMessage;

        return $"{message} ({result.ResponseCode})";
    }

    private void ClearPendingPurchase(
        TaskCompletionSource<StorePurchaseResult> completion)
    {
        lock (_purchaseSync)
        {
            if (!ReferenceEquals(_pendingPurchase, completion))
                return;

            _pendingPurchase = null;
            _pendingProductId = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _billingClient.EndConnection();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"=== GOOGLE BILLING DISPOSE ERROR: {ex.Message}");
        }

        _connectionGate.Dispose();
        _purchaseGate.Dispose();
    }
}
#endif
