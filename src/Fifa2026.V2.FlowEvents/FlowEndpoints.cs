using Fifa2026.V2.FlowEvents.Data;
using Fifa2026.V2.FlowEvents.Hubs;

namespace Fifa2026.V2.FlowEvents;

/// <summary>
/// AC-3/AC-5/AC-6 — endpoints HTTP do FlowEvents service:
///   GET  /api/flow/recent           → últimas N compras (lista do front, AC-5)
///   GET  /api/flow/{correlationId}  → timeline de eventos (fallback polling 2s, AC-6)
///   POST /api/flow/{correlationId}/replay → relê a timeline e a empurra via SignalR
///                                            (anima a bolinha em tempo real, AC-6)
///   GET  /api/flow/diploma-summary  → Story 4.6 (Diploma vivo): correlation-IDs ESCOPADOS
///                                     ao aluno (customDimensions.UserId) + região do
///                                     ambiente (backend-resolved) — infalsificável (AC-2/3/6)
///
/// O serviço fica ATRÁS do gateway YARP (rota nova flow-events) — o gateway valida o
/// Bearer Entra (ADE-004/ADE-005). Este serviço não revalida o JWT.
/// </summary>
public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/flow");

        // AC-5 — lista das últimas compras (default 50, máx 200).
        group.MapGet("/recent", async (
            IFlowEventRepository repository,
            CancellationToken cancellationToken,
            int? top) =>
        {
            var limit = Math.Clamp(top ?? 50, 1, 200);
            var purchases = await repository.GetRecentPurchasesAsync(limit, cancellationToken);
            return Results.Ok(purchases);
        });

        // AC-6 — timeline completa (usada como fallback de polling se o WebSocket falhar).
        group.MapGet("/{correlationId}", async (
            string correlationId,
            IFlowEventRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidCorrelationId(correlationId))
            {
                return Results.BadRequest(new { error = "correlationId inválido (esperado GUID)." });
            }

            var timeline = await repository.GetTimelineAsync(correlationId, cancellationToken);
            return Results.Ok(timeline);
        });

        // AC-6 — relê a timeline e empurra cada evento via SignalR ao grupo correlation-<id>,
        // disparando a animação da bolinha nos clientes assinantes em tempo real.
        group.MapPost("/{correlationId}/replay", async (
            string correlationId,
            IFlowEventRepository repository,
            IFlowEventPublisher publisher,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidCorrelationId(correlationId))
            {
                return Results.BadRequest(new { error = "correlationId inválido (esperado GUID)." });
            }

            var timeline = await repository.GetTimelineAsync(correlationId, cancellationToken);
            foreach (var flowEvent in timeline)
            {
                await publisher.PublishAsync(flowEvent, cancellationToken);
            }

            return Results.Ok(new { correlationId, pushed = timeline.Count });
        });

        // Story 4.6 (Diploma vivo) AC-2/AC-3/AC-6 — resumo da telemetria de UM aluno:
        //   region          → resolvida no backend (env do ambiente) — nunca fabricada no cliente
        //   correlationIds  → filtrados por customDimensions.UserId (reúso da MESMA fonte App
        //                     Insights do F6 — AC-2); a RESPOSTA não contém PII de terceiros
        //                     (só GUIDs opacos + region + count)
        //   count           → correlationIds.Length (a "narrativa é o número", spec UX §1.4)
        // deployTime NÃO nasce aqui: é injetado no BUILD do frontend (VITE_BUILD_TIME) — outro
        // metadado de origem confiável (o runner do CI), não o navegador (AC-3).
        //
        // POSTURA (honesta, NÃO fail-closed): como o resto do FlowEvents (ingress externo, sem
        // revalidar JWT, fora do X-Gateway-Key — ADE-009 Inv 1), este endpoint é ANÔNIMO no FQDN
        // externo e o `userId` é um filtro, NÃO um controle de acesso — qualquer chamador pode
        // passar qualquer userId sequencial e enumerar seus GUIDs opacos (mesma classe do
        // /api/flow/recent já exposto sem escopo desde a 2.6; zero PII na resposta). Débito
        // registrado em docs/security/final-security-debt.md (família do MEDIUM-1).
        group.MapGet("/diploma-summary", async (
            string userId,
            IFlowEventRepository repository,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidUserId(userId))
            {
                return Results.BadRequest(new { error = "userId inválido (esperado inteiro positivo do usuário v1)." });
            }

            var purchases = await repository.GetPurchasesByUserAsync(userId, top: 50, cancellationToken);
            var correlationIds = purchases.Select(p => p.CorrelationId).ToArray();

            // Região do ambiente (App Setting explícito do deploy, ou a convenção REGION_NAME do
            // App Service). Ausente → null: o front degrada graciosamente (nunca inventa um valor).
            var region = configuration["DeployRegion"] ?? configuration["REGION_NAME"];

            return Results.Ok(new
            {
                region,
                correlationIds,
                count = correlationIds.Length
            });
        });
    }

    /// <summary>O userId v1 é sempre um inteiro positivo (1..12 dígitos). Guard anti-abuso.</summary>
    private static bool IsValidUserId(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 12
           && value.All(char.IsAsciiDigit);

    /// <summary>O correlationId é sempre um GUID (gerado pelo gateway YARP — nó zero).</summary>
    private static bool IsValidCorrelationId(string value) => Guid.TryParse(value, out _);
}
