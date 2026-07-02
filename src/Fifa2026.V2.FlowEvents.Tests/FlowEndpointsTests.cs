using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fifa2026.V2.FlowEvents.Data;
using Fifa2026.V2.FlowEvents.Hubs;
using Fifa2026.V2.FlowEvents.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fifa2026.V2.FlowEvents.Tests;

/// <summary>
/// AC-3/AC-5/AC-6 — testes ponta-a-ponta dos endpoints via WebApplicationFactory,
/// com App Insights MOCADO (FakeFlowEventRepository) e SignalR MOCADO
/// (RecordingFlowEventPublisher) — Story 2.6 Testing approach. Sem workspace App
/// Insights nem SignalR Service reais.
/// </summary>
public sealed class FlowEndpointsTests : IClassFixture<FlowEndpointsTests.FlowAppFactory>
{
    private readonly FlowAppFactory _factory;

    public FlowEndpointsTests(FlowAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_endpoint_responds_healthy()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("flow-events", body.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Timeline_returns_five_nodes_with_gateway_yarp_as_node_zero()
    {
        var client = _factory.CreateClient();
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        var timeline = await client.GetFromJsonAsync<List<FlowEvent>>($"/api/flow/{id}");

        Assert.NotNull(timeline);
        // ADE-008 Inv 5 — 5 nós (o nó do n8n saiu). SQL_INSERTED é o último (índice 4).
        Assert.Equal(5, timeline!.Count);
        // AC-13 — o primeiro nó é o Gateway YARP (nó zero), NUNCA APIM.
        Assert.Equal(FlowEventType.GATEWAY_YARP_RECEIVED, timeline[0].EventType);
        Assert.Equal(0, timeline[0].NodeIndex);
        Assert.Equal(FlowEventType.SQL_INSERTED, timeline[4].EventType);
    }

    [Fact]
    public async Task Timeline_rejects_non_guid_correlation_id()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/flow/not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recent_returns_purchases_list()
    {
        var client = _factory.CreateClient();
        var purchases = await client.GetFromJsonAsync<List<RecentPurchase>>("/api/flow/recent");
        Assert.NotNull(purchases);
        Assert.NotEmpty(purchases!);
    }

    [Fact]
    public async Task DiplomaSummary_returns_scoped_correlationIds_and_count()
    {
        // Story 4.6 — o resumo do Diploma retorna SÓ as compras do userId pedido, com count.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/flow/diploma-summary?userId=42");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // O fake escopa por userId=42 → 2 compras (ver FakeFlowEventRepository).
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(2, body.GetProperty("correlationIds").GetArrayLength());
    }

    [Fact]
    public async Task DiplomaSummary_rejects_non_numeric_userId()
    {
        // AC-6/AC-10 — userId não numérico é rejeitado (guard anti-abuso), nunca vaza dado.
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/flow/diploma-summary?userId=not-a-number");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Replay_pushes_each_event_to_signalr_group()
    {
        var client = _factory.CreateClient();
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _factory.Publisher.Reset();

        var response = await client.PostAsync($"/api/flow/{id}/replay", content: null);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, body.GetProperty("pushed").GetInt32());

        // Cada um dos 5 eventos foi empurrado via SignalR, em ordem de nó (ADE-008 Inv 5).
        Assert.Equal(5, _factory.Publisher.Published.Count);
        Assert.Equal(FlowEventType.GATEWAY_YARP_RECEIVED, _factory.Publisher.Published[0].EventType);
    }

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    public sealed class FlowAppFactory : WebApplicationFactory<Program>
    {
        public RecordingFlowEventPublisher Publisher { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFlowEventRepository>();
                services.AddSingleton<IFlowEventRepository, FakeFlowEventRepository>();

                services.RemoveAll<IFlowEventPublisher>();
                services.AddSingleton<IFlowEventPublisher>(Publisher);
            });
        }
    }

    private sealed class FakeFlowEventRepository : IFlowEventRepository
    {
        public Task<IReadOnlyList<FlowEvent>> GetTimelineAsync(string correlationId, CancellationToken cancellationToken = default)
        {
            // Os 5 hops REAIS em ordem (Gateway YARP → SQL) — ADE-008 Inv 5 (sem n8n).
            var timeline = Enum.GetValues<FlowEventType>()
                .OrderBy(t => (int)t)
                .Select(t => new FlowEvent
                {
                    CorrelationId = correlationId,
                    EventType = t,
                    Timestamp = DateTimeOffset.UtcNow,
                    Status = "ok",
                    Message = t.ToString()
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<FlowEvent>>(timeline);
        }

        public Task<IReadOnlyList<RecentPurchase>> GetRecentPurchasesAsync(int top, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<RecentPurchase> list = new[]
            {
                new RecentPurchase { CorrelationId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow, Status = "ok" }
            };
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<RecentPurchase>> GetPurchasesByUserAsync(string userId, int top, CancellationToken cancellationToken = default)
        {
            // Story 4.6 — devolve 2 compras "do aluno" (escopadas) para provar count/escopo no teste.
            IReadOnlyList<RecentPurchase> list = new[]
            {
                new RecentPurchase { CorrelationId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow, Status = "ok" },
                new RecentPurchase { CorrelationId = Guid.NewGuid().ToString(), Timestamp = DateTimeOffset.UtcNow, Status = "ok" }
            };
            return Task.FromResult(list);
        }
    }

    public sealed class RecordingFlowEventPublisher : IFlowEventPublisher
    {
        public List<FlowEvent> Published { get; } = new();

        public void Reset() => Published.Clear();

        public Task PublishAsync(FlowEvent flowEvent, CancellationToken cancellationToken = default)
        {
            Published.Add(flowEvent);
            return Task.CompletedTask;
        }
    }
}
