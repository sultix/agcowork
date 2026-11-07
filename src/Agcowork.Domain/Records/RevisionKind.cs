namespace Agcowork.Domain.Records;

/// <summary>What produced a <see cref="Revision"/>. Wire names are snake_case. Values are stable: add, never renumber.</summary>
public enum RevisionKind
{
    /// <summary>The record was created (revision 1).</summary>
    Created = 1,

    /// <summary>Title, properties or body were changed.</summary>
    Updated = 2,

    /// <summary>Markdown was appended to the body.</summary>
    Appended = 3,

    /// <summary>The record was archived.</summary>
    Archived = 4,

    /// <summary>The record was restored from the archive.</summary>
    Restored = 5,
}
