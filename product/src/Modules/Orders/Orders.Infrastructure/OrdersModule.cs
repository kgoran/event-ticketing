using EventTicketing.Orders.Application;
using EventTicketing.Orders.Application.Checkout;
using EventTicketing.Orders.Infrastructure.Persistence;
using EventTicketing.Shared.Application;
using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Application.Context;
using EventTicketing.Shared.Infrastructure.Http;
using EventTicketing.Shared.Infrastructure.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Orders.Infrastructure;

public sealed record StartCheckoutRequest(Guid HoldId);

public sealed record OrderSeatResponse(Guid SeatId, long PriceMinorUnits);

/// <summary>What the return page shows while it polls: the redirect from the provider proves nothing, the status does.</summary>
/// <param name="PaymentDeadline">Until when a Pending order can still be paid.</param>
/// <param name="RefundReason">Set while refunding: SeatsLost or PaidAfterClose.</param>
public sealed record OrderStatusResponse(
    Guid OrderId,
    string Status,
    long TotalMinorUnits,
    string Currency,
    Guid EventId,
    DateTimeOffset PaymentDeadline,
    string? RefundReason,
    IReadOnlyList<OrderSeatResponse> Seats);

public sealed class OrdersModule : IModule
{
    public string Name => "Orders";

    public void Register(IHostApplicationBuilder builder, HostKind host)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModuleDatabase<OrdersDbContext>(host, OrdersDbContext.SchemaName);

        var application = typeof(StartCheckout).Assembly;
        builder.Services.AddCommandHandlers(application);
        builder.Services.AddMessageHandlers(application);

        builder.Services.AddSingleton(builder.Configuration.GetSection("Checkout").Get<CheckoutPolicy>() ?? new CheckoutPolicy());
        builder.Services.AddScoped<IOrderRepository, OrderRepository>();
        builder.Services.AddScoped<IPriceList, PriceList>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
        if (host != HostKind.PublicApi)
        {
            return;
        }

        var orders = endpoints.MapGroup("/orders").WithTags("Orders");

        orders.MapPost("/", async (StartCheckoutRequest request, ICommandHandler<StartCheckout, CheckoutStarted> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new StartCheckout(request.HoldId), cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/orders/{result.Value.OrderId}", result.Value)
                    : result.Error.ToProblem();
            })
            .WithName("StartCheckout")
            .WithSummary("Turn a hold into an order and get the URL to pay on")
            .Produces<CheckoutStarted>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The provider's redirect proves nothing; the return page polls this until the webhook has confirmed the order.
        // Someone else's order is "not found": order IDs reveal nothing about other fans.
        orders.MapGet("/{orderId:guid}", async (Guid orderId, OrdersDbContext db, IExecutionContext caller, HttpResponse response, CancellationToken cancellationToken) =>
            {
                var order = await db.Orders.AsNoTracking()
                    .Include(o => o.Lines)
                    .SingleOrDefaultAsync(o => o.Id == orderId && o.CustomerId == caller.UserId, cancellationToken);

                if (order is null)
                {
                    return Results.NotFound();
                }

                response.Headers.CacheControl = "no-store"; // the return page polls this for changes
                return Results.Ok(new OrderStatusResponse(
                    order.Id,
                    order.Status.ToString(),
                    order.TotalMinorUnits,
                    order.Currency,
                    order.EventId,
                    order.PaymentDeadline,
                    order.RefundReason?.ToString(),
                    [.. order.Lines.OrderBy(line => line.SeatId).Select(line => new OrderSeatResponse(line.SeatId, line.PriceMinorUnits))]));
            })
            .WithName("GetOrder")
            .WithSummary("Your order: what the return page polls until the payment's outcome is known")
            .Produces<OrderStatusResponse>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
