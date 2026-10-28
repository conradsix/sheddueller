namespace Sheddueller.Postgres.Internal;

/// <summary>
/// One schema change after the baseline, applied once to a database whose schema version is below <see cref="Version" />.
/// </summary>
/// <param name="Version">The schema version this step brings the database to.</param>
/// <param name="Sql">The step's DDL for the configured schema.</param>
internal sealed record PostgresMigrationStep(int Version, Func<PostgresNames, string> Sql);

/// <summary>
/// The schema changes made after the baseline, in version order.
/// </summary>
/// <remarks>
/// Each step runs exactly once per database, inside the migration transaction, so it should change only what its
/// version needs. These tables can hold millions of rows, and the migration holds its locks until it commits:
/// <list type="bullet">
/// <item>add CHECK and foreign key constraints to large tables with <c>not valid</c>, then <c>validate constraint</c>,
/// which doesn't block reads or writes;</item>
/// <item>avoid changes that rewrite <c>jobs</c> or <c>job_events</c>: volatile column defaults, stored generated
/// columns and column type changes.</item>
/// </list>
/// Adding a step means appending it here and raising <see cref="PostgresNames.ExpectedSchemaVersion" /> to its version.
/// </remarks>
internal static class PostgresMigrationSteps
{
    /// <summary>
    /// The schema version the baseline script creates, or upgrades a preview schema to.
    /// </summary>
    public const int BaselineVersion = 15;

    public static IReadOnlyList<PostgresMigrationStep> All { get; } = [];
}
