namespace Sheddueller.Postgres.Internal;

using System.Diagnostics.CodeAnalysis;

using Npgsql;

/// <summary>
/// Creates the provider's commands. Every command the provider runs is created here, so this is the one place that
/// sets command text that isn't a compile-time constant.
/// </summary>
/// <remarks>
/// The provider's SQL is composed from constant fragments and the schema-qualified identifiers of
/// <see cref="PostgresNames" />, which quotes them; every value a caller supplies travels as a parameter. Keep it
/// that way: don't build SQL from values, and don't set <see cref="NpgsqlCommand.CommandText" /> anywhere else, so
/// CA2100 still flags dynamic SQL written outside this class.
/// </remarks>
internal static class PostgresCommands
{
    /// <summary>
    /// Creates a command on the connection, enlisted in the transaction when there is one.
    /// </summary>
    public static NpgsqlCommand Create(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        SetCommandText(command, commandText);
        return command;
    }

    /// <summary>
    /// Creates a command whose text depends on its parameters: <paramref name="build" /> adds the parameters and
    /// returns the text, as a search does when each filter adds a condition and its parameter.
    /// </summary>
    public static NpgsqlCommand Create(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Func<NpgsqlCommand, string> build)
    {
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            SetCommandText(command, build(command));
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    [SuppressMessage(
      "Security",
      "CA2100:Review SQL queries for security vulnerabilities",
      Justification = "The provider's SQL is built from constant fragments and quoted identifiers only; values are always parameters. See the class remarks.")]
    private static void SetCommandText(NpgsqlCommand command, string commandText)
      => command.CommandText = commandText;
}
