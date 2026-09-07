using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

internal static class ArchiveSegmentStateSqlSchema
{
    public const string UnsealedState = "Unsealed";
    public const string SealedState = "Sealed";

    public static void CreateTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ArchiveSegmentState (
                SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                State TEXT NOT NULL CHECK (State IN ('Unsealed', 'Sealed')),
                SealedAtUtc TEXT NULL,
                CHECK (
                    (State = 'Unsealed' AND SealedAtUtc IS NULL)
                    OR
                    (State = 'Sealed' AND SealedAtUtc IS NOT NULL)
                )
            );

            INSERT INTO ArchiveSegmentState (SingletonId, State, SealedAtUtc)
            VALUES (1, 'Unsealed', NULL);
            """;
        command.ExecuteNonQuery();
    }

    public static ArchiveSegmentStateSnapshot ReadAndValidate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateColumns(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT State, SealedAtUtc
            FROM ArchiveSegmentState
            WHERE SingletonId = 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("ArchiveSegmentState is missing its singleton row.");
        }

        string state = reader.GetString(0);
        DateTimeOffset? sealedAtUtc = null;
        if (string.Equals(state, SealedState, StringComparison.Ordinal))
        {
            if (reader.IsDBNull(1) ||
                !DateTimeOffset.TryParse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed) ||
                parsed.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("Sealed archive must contain a UTC SealedAtUtc value.");
            }
            sealedAtUtc = parsed;
        }
        else if (!string.Equals(state, UnsealedState, StringComparison.Ordinal) || !reader.IsDBNull(1))
        {
            throw new InvalidDataException("ArchiveSegmentState contains an invalid state value.");
        }

        if (reader.Read())
        {
            throw new InvalidDataException("ArchiveSegmentState must contain exactly one row.");
        }

        return new ArchiveSegmentStateSnapshot(
            IsSealed: string.Equals(state, SealedState, StringComparison.Ordinal),
            sealedAtUtc);
    }

    public static void Seal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset sealedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (sealedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("SealedAtUtc must be UTC.", nameof(sealedAtUtc));
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ArchiveSegmentState
            SET State = 'Sealed',
                SealedAtUtc = $sealedAtUtc
            WHERE SingletonId = 1
              AND State = 'Unsealed'
              AND SealedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$sealedAtUtc",
            sealedAtUtc.ToString("O", CultureInfo.InvariantCulture));

        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "Archive segment could not transition from Unsealed to Sealed.");
        }
    }

    private static void ValidateColumns(SqliteConnection connection)
    {
        ExpectedColumn[] expected =
        [
            new("SingletonId", "INTEGER", NotNull: true, PrimaryKeyOrder: 1),
            new("State", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("SealedAtUtc", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
        ];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ArchiveSegmentState');";
        using SqliteDataReader reader = command.ExecuteReader();

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException("ArchiveSegmentState contains unexpected columns.");
            }

            ExpectedColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException("ArchiveSegmentState column contract is invalid.");
            }
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException("ArchiveSegmentState is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}

internal sealed record ArchiveSegmentStateSnapshot(
    bool IsSealed,
    DateTimeOffset? SealedAtUtc);
