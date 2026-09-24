namespace TicketTriage.Core.Domain;

/// <summary>
/// Deterministic priority lookup: <see cref="Urgency"/> x <see cref="Impact"/> -> <see cref="Priority"/>.
/// Priority is never predicted by the LLM; it is always derived from this table.
/// </summary>
public static class PriorityMatrix
{
    // Rows are indexed by Urgency, columns by Impact (enum declaration order).
    private static readonly Priority[,] Table =
    {
        //                Major             Significant       Moderate         Minor            NoImpact
        /* Critical */ { Priority.Highest, Priority.Highest, Priority.High,   Priority.Medium, Priority.Medium },
        /* High     */ { Priority.Highest, Priority.High,    Priority.High,   Priority.Medium, Priority.Low    },
        /* Medium   */ { Priority.High,    Priority.High,    Priority.Medium, Priority.Low,    Priority.Low    },
        /* Low      */ { Priority.Medium,  Priority.Medium,  Priority.Low,    Priority.Low,    Priority.Lowest },
        /* Lowest   */ { Priority.Medium,  Priority.Low,     Priority.Low,    Priority.Lowest, Priority.Lowest },
    };

    public static Priority Resolve(Urgency urgency, Impact impact)
    {
        if (!Enum.IsDefined(urgency))
        {
            throw new ArgumentOutOfRangeException(nameof(urgency), urgency, "Unknown urgency.");
        }

        if (!Enum.IsDefined(impact))
        {
            throw new ArgumentOutOfRangeException(nameof(impact), impact, "Unknown impact.");
        }

        return Table[(int)urgency, (int)impact];
    }
}
