namespace Badger.SqlMigrate.Core.Project;

/// <summary>
/// A script under repeatable/ (views, procedures, functions, permissions). It is re-applied whenever its
/// hash changes. Name is the forward-slash path relative to repeatable/, e.g. "views/vCustomer.sql".
/// </summary>
public sealed record RepeatableScript(string Name, string Script, string Hash, IReadOnlyList<string> Requires);
