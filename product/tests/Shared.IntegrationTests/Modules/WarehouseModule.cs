using EventTicketing.Shared.Application.Messaging;
using EventTicketing.Shared.Infrastructure.Modules;
using EventTicketing.Shared.Infrastructure.Persistence;
using EventTicketing.Shared.IntegrationTests.Sales;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace EventTicketing.Shared.IntegrationTests.Warehouse;

// A tiny test module with two consumers of Sales' OrderPlaced, each of which can be told to fail.

internal sealed class WarehouseModule : IModule
{
    public string Name => "Warehouse";

    public void Register(IHostApplicationBuilder builder, HostKind host) =>
        builder.AddModuleDatabase<WarehouseDbContext>(host, "warehouse", typeof(WarehouseModule).Namespace);

    public void MapEndpoints(IEndpointRouteBuilder endpoints, HostKind host)
    {
    }
}

internal sealed class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : ModuleDbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<Label> Labels => Set<Label>();

    protected override string Schema => "warehouse";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Shipment>().ToTable("shipments");
        modelBuilder.Entity<Label>().ToTable("labels");
    }
}

internal sealed class Shipment
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }
}

internal sealed class Label
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }
}

internal sealed class ShipOnOrderPlaced(WarehouseDbContext db, TestFaults faults) : IIntegrationEventHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken)
    {
        faults.ThrowIfShippingShouldFail();
        db.Shipments.Add(new Shipment { Id = Guid.CreateVersion7(), OrderId = integrationEvent.OrderId });
        return Task.CompletedTask;
    }
}

internal sealed class PrintLabelOnOrderPlaced(WarehouseDbContext db, TestFaults faults) : IIntegrationEventHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken)
    {
        faults.ThrowIfLabelPrintingShouldFail();
        db.Labels.Add(new Label { Id = Guid.CreateVersion7(), OrderId = integrationEvent.OrderId });
        return Task.CompletedTask;
    }
}

/// <summary>Makes a consumer throw a given number of times.</summary>
internal sealed class TestFaults
{
    private int _shippingFailures;
    private int _labelFailures;

    public void FailShipping(int times) => _shippingFailures = times;

    public void FailLabelPrinting(int times) => _labelFailures = times;

    public void ThrowIfShippingShouldFail()
    {
        if (Interlocked.Decrement(ref _shippingFailures) >= 0)
        {
            throw new InvalidOperationException("Simulated shipping failure.");
        }
    }

    public void ThrowIfLabelPrintingShouldFail()
    {
        if (Interlocked.Decrement(ref _labelFailures) >= 0)
        {
            throw new InvalidOperationException("Simulated label printer failure.");
        }
    }
}
