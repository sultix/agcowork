using System.Diagnostics.CodeAnalysis;

// Use cases are named after their operation on the domain's Collection (CreateCollection, GetCollection, …);
// they are not .NET collection types.
[assembly: SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Domain term 'Collection' (architecture §2), not a .NET collection type.",
    Scope = "namespaceanddescendants",
    Target = "~N:Agcowork.Application.Collections")]
