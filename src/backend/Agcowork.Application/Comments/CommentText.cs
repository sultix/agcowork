using System.Globalization;
using System.Text;
using Agcowork.Application.Common;
using System.Text.Json;

namespace Agcowork.Application.Comments;

/// <summary>
/// Readable text of comments for agents: the MCP text blocks and the Markdown form of a record with its comments
/// (<c>GET …/records/{id}?format=markdown&amp;includeComments=true</c>, <c>record_get</c>). One rendering for all adapters.
/// Names are user and agent input: JSON-quoted, so quotes and line breaks cannot break the one-line headers.
/// </summary>
public static class CommentText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The record's Markdown followed by a separator and a <c># Comments</c> section.</summary>
    public static string AppendTo(string recordMarkdown, CommentThreadsDto threads)
    {
        ArgumentNullException.ThrowIfNull(recordMarkdown);
        return $"{recordMarkdown.TrimEnd()}\n\n---\n\n# Comments\n\n{Threads(threads)}";
    }

    /// <summary>The threads of a record: roots with their replies indented below; each comment's Markdown indented under its header.</summary>
    public static string Threads(CommentThreadsDto threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        var text = new StringBuilder();
        text.Append(Invariant, $"{threads.Threads.Count} thread(s), oldest first");
        text.Append(threads.OmittedThreads > 0 ? $"; {threads.OmittedThreads} older thread(s) omitted.\n" : ".\n");
        foreach (var root in threads.Threads)
        {
            text.Append('\n').Append(Comment(root, indent: string.Empty));
            if (root.OmittedReplies is > 0 and var omitted)
            {
                text.Append(Invariant, $"  ({omitted} older repl{(omitted == 1 ? "y" : "ies")} omitted)\n");
            }

            foreach (var reply in root.Replies ?? [])
            {
                text.Append(Comment(reply, indent: "  "));
            }
        }

        return text.ToString();
    }

    /// <summary>One comment: a header line (id, author, time, state) and its Markdown indented below.</summary>
    public static string Comment(CommentDto comment, string indent)
    {
        ArgumentNullException.ThrowIfNull(comment);
        var text = new StringBuilder();
        text.Append(Invariant, $"{indent}- comment {comment.Id} by {QuotedString.Of(comment.AuthorName)} ({Wire(comment.AuthorKind)}, actor {comment.AuthorId}) at {comment.CreatedAt:O}");
        if (comment.EditedAt is not null)
        {
            text.Append(", edited");
        }

        if (comment.ResolvedAt is not null)
        {
            text.Append(Invariant, $", RESOLVED by actor {comment.ResolvedBy}");
        }

        if (comment.DeletedAt is not null)
        {
            text.Append(", deleted");
        }

        text.Append('\n');
        if (comment.Markdown is { Length: > 0 } markdown)
        {
            foreach (var line in markdown.TrimEnd().ReplaceLineEndings("\n").Split('\n'))
            {
                text.Append(indent).Append("  ").Append(line).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Wire name of an enum value (snake_case), as in the JSON.</summary>
    public static string Wire<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
