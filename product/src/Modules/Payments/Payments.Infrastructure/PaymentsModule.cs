using EventTicketing.Payments.Application;
using EventTicketing.Payments.Application.Checkout;
using EventTicketing.Payments.Application.Refunds;
using EventTicketing.Payments.Application.Webhooks;
using EventTicketing.Payments.Contracts;
using EventTicketing.Payments.Infrastructure.Persistence;
using EventTicketing.Payments.Infrastructure.Providers;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Http;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EventTicketing.Payments.Infrastructure;

public sealed class PaymentsModule : IModule
{
    public string Name => "Payments";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<PaymentsDbContext>(host, PaymentsDbContext.SchemaName);

        var application = typeof(CreateCheckout).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        builder.Services.AddSingleton(builder.Configuration.GetSection("Payments").Get<PaymentsPolicy>() ?? new PaymentsPolicy());
        builder.Services.AddScoped<IPaymentAttemptRepository, PaymentAttemptRepository>();
        builder.Services.AddScoped<IWebhookRepository, WebhookRepository>();
        builder.Services.AddScoped<IRefundRepository, RefundRepository>();
        builder.Services.AddScoped<PaymentOutcomes>();
        builder.Services.AddScoped<RefundOutcomes>();
        builder.Services.AddScoped<IPaymentsApi, PaymentsApi>();

        builder.Services.Configure<LocalPayOptions>(builder.Configuration.GetSection("Payments:LocalPay"));
        builder.Services.AddHttpClient<LocalPayProvider>((services, client) =>
            client.BaseAddress = services.GetRequiredService<IOptions<LocalPayOptions>>().Value.BaseUrl);
        builder.Services.AddScoped<IPaymentProvider>(services => services.GetRequiredService<LocalPayProvider>());
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host != HostKind.WebhookIngress)
        {
            return;
        }

        // Providers call this without a token; the signature is checked by the command.
        endpoints.MapPost("/webhooks/{provider}", async (string provider, HttpRequest request, ICommandHandler<ReceiveWebhook, Unit> handler, CancellationToken cancellationToken) =>
            {
                using var reader = new StreamReader(request.Body);
                var payload = await reader.ReadToEndAsync(cancellationToken);
                var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                // A duplicate is acknowledged like the original, so the provider stops retrying it.
                var result = await handler.HandleAsync(new ReceiveWebhook(provider, payload, headers), cancellationToken);
                return result.IsSuccess || result.Error == WebhookErrors.Duplicate ? Results.Ok() : result.Error.ToProblem();
            })
            .AllowAnonymous()
            .WithTags("Webhooks")
            .WithName("ReceivePaymentWebhook")
            .WithSummary("Payment provider callbacks: verify the signature, store once, reply 200")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}

/// <summary>The in-process implementation of Payments' module API; a gRPC client if the module is ever extracted (ADR-0001).</summary>
internal sealed class PaymentsApi(
    ICommandHandler<CreateCheckout, string> createCheckout,
    ICommandHandler<CancelCheckout, CheckoutCancellation> cancelCheckout,
    ICommandHandler<CheckPaymentStatus, ProviderPaymentStatus> checkStatus,
    ICommandHandler<RequestRefund, RefundRequestOutcome> requestRefund)
    : IPaymentsApi
{
    public async Task<CreateCheckoutResult> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await createCheckout.HandleAsync(
            new CreateCheckout(request.OrderId, request.AmountMinorUnits, request.Currency, request.ReturnUrl),
            cancellationToken);

        return result.IsSuccess
            ? new CreateCheckoutResult(CreateCheckoutOutcome.Created, result.Value)
            : new CreateCheckoutResult(CreateCheckoutOutcome.ProviderUnavailable, null);
    }

    public async Task<CancelCheckoutResult> CancelCheckoutAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var result = await cancelCheckout.HandleAsync(new CancelCheckout(orderId), cancellationToken);
        return new CancelCheckoutResult(result switch
        {
            { IsSuccess: true, Value: CheckoutCancellation.AlreadyCompleted } => CancelCheckoutOutcome.AlreadyCompleted,
            { IsSuccess: true } => CancelCheckoutOutcome.Cancelled,
            _ when result.Error == CheckoutErrors.ProviderUnavailable => CancelCheckoutOutcome.ProviderUnavailable,
            _ => throw new InvalidOperationException($"Could not cancel the payment of order {orderId}: {result.Error.Code}."),
        });
    }

    public async Task<PaymentStatusResult> CheckPaymentStatusAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var result = await checkStatus.HandleAsync(new CheckPaymentStatus(orderId), cancellationToken);
        return new PaymentStatusResult(result switch
        {
            { IsSuccess: true, Value: ProviderPaymentStatus.Open } => PaymentStatusOutcome.Pending,
            { IsSuccess: true, Value: ProviderPaymentStatus.Succeeded } => PaymentStatusOutcome.Succeeded,
            { IsSuccess: true, Value: ProviderPaymentStatus.Failed } => PaymentStatusOutcome.Failed,
            { IsSuccess: true } => PaymentStatusOutcome.Cancelled,
            _ when result.Error == CheckoutErrors.ProviderUnavailable => PaymentStatusOutcome.ProviderUnavailable,
            _ => PaymentStatusOutcome.Cancelled, // no session was ever created: nothing can be paid
        });
    }

    public async Task<RefundRequestResult> RequestRefundAsync(Guid orderId, string reason, CancellationToken cancellationToken)
    {
        var result = await requestRefund.HandleAsync(new RequestRefund(orderId, reason), cancellationToken);
        return new RefundRequestResult(result switch
        {
            { IsSuccess: true } => result.Value,
            _ when result.Error == CheckoutErrors.ProviderUnavailable => RefundRequestOutcome.ProviderUnavailable,
            _ => throw new InvalidOperationException($"Could not refund order {orderId}: {result.Error.Code}."),
        });
    }
}
