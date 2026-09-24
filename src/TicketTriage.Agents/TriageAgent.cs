namespace TicketTriage.Agents;

/// <summary>Definition of the triage agent. Resolve it as <c>[FromKeyedServices(TriageAgent.Name)] AIAgent</c>.</summary>
public static class TriageAgent
{
    public const string Name = "TriageAgent";

    // TODO: implement - real system prompt (catalog, routing hints, output schema) once classification is built.
    public const string Instructions =
        """
        You are a service desk triage assistant for an insurance company's IT support.
        You help analysts classify incoming tickets and draft resolution comments.
        Answer concisely.
        """;
}
