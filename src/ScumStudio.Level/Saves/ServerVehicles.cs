using System.Globalization;
using Microsoft.Data.Sqlite;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Level.Saves;

/// <summary>A vehicle of the server's save (<c>SCUM.db</c>).</summary>
/// <param name="Id">The vehicle's id in the save (the entity id the admin commands use).</param>
/// <param name="Type">The vehicle's type without the prefix: <c>BPC_Rager1</c>.</param>
/// <param name="Alias">The name somebody gave it, or empty.</param>
/// <param name="X">Where it stands, X (cm).</param>
/// <param name="Y">Y (cm).</param>
/// <param name="Z">Z (cm).</param>
/// <param name="LastUsed">When the game last counted it used (UTC), or null.</param>
/// <param name="Works">The game counts it functional.</param>
/// <param name="Spawned">It was made by the game (not by a player's command).</param>
/// <param name="ContainerId">The id of its trunk (container entity), or 0.</param>
/// <param name="LockOwner">The player whose lock is on its trunk (the owner), or empty when it is not locked.</param>
/// <param name="LockOwnerSteamId">That player's Steam id, or empty.</param>
/// <param name="Lock">The lock(s) fitted (their assets), or empty.</param>
public sealed record SavedVehicle(long Id, string Type, string Alias, double X, double Y, double Z, DateTime? LastUsed, bool Works, bool Spawned,
    long ContainerId = 0, string LockOwner = "", string LockOwnerSteamId = "", string Lock = "");

/// <summary>A player of the server's save.</summary>
/// <param name="Name">The name.</param>
/// <param name="SteamId">The Steam id.</param>
public sealed record SavedPlayer(string Name, string SteamId);

/// <summary>
/// Reads the vehicles and the players of a dedicated server's live save without touching it: the database and its write-ahead log
/// are copied to a temporary folder and read there (the game keeps the newest changes in the log, and a copy needs no lock).
/// The owner of a vehicle is the player whose lock is on it: <c>item_containers</c> lists the locked containers with their owner (a
/// vehicle's trunk is one), <c>item_container_upgrades</c> the lock fitted. A vehicle nobody locked has no owner in the save.
/// </summary>
public static class ServerVehicles
{
    /// <summary>The server's save.</summary>
    public static string DatabasePath(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Path.Combine(ServerLogs.SavedFolder(server), "SaveFiles", "SCUM.db");
    }

    /// <summary>The vehicles and the players of the save; empty when there is no save yet.</summary>
    /// <exception cref="IOException">The save cannot be copied or read.</exception>
    public static (IReadOnlyList<SavedVehicle> Vehicles, IReadOnlyList<SavedPlayer> Players) Read(ServerInstall server)
    {
        var db = DatabasePath(server);
        if (!File.Exists(db))
        {
            return ([], []);
        }

        var temp = Path.Combine(Path.GetTempPath(), "scumstudio-vehicles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var copy = Path.Combine(temp, "SCUM.db");
            File.Copy(db, copy, overwrite: true);
            if (File.Exists(db + "-wal"))
            {
                File.Copy(db + "-wal", copy + "-wal", overwrite: true);
            }

            var builder = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            return (Vehicles(connection), Players(connection));
        }
        catch (SqliteException ex)
        {
            throw new IOException($"{db} cannot be read: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp folder left behind is harmless.
            }
        }
    }

    private static IReadOnlyList<SavedVehicle> Vehicles(SqliteConnection connection)
    {
        try
        {
            return Vehicles(connection, withLocks: true);
        }
        catch (SqliteException)
        {
            // A save of another version without the lock tables: the vehicles alone.
            return Vehicles(connection, withLocks: false);
        }
    }

    private static IReadOnlyList<SavedVehicle> Vehicles(SqliteConnection connection, bool withLocks)
    {
        using var command = connection.CreateCommand();
        var locks = withLocks
            ? """
                , COALESCE(v.item_container_entity_id, 0),
                  COALESCE((SELECT p.name FROM item_containers ic JOIN user_profile p ON p.id = ic.user_profile_id
                            WHERE ic.runtime_id IN (CAST(v.item_container_entity_id AS TEXT), CAST(e.id AS TEXT)) LIMIT 1), ''),
                  COALESCE((SELECT p.user_id FROM item_containers ic JOIN user_profile p ON p.id = ic.user_profile_id
                            WHERE ic.runtime_id IN (CAST(v.item_container_entity_id AS TEXT), CAST(e.id AS TEXT)) LIMIT 1), ''),
                  COALESCE((SELECT group_concat(u.asset, ', ') FROM item_container_upgrades u JOIN item_containers ic ON ic.id = u.item_container_id
                            WHERE ic.runtime_id IN (CAST(v.item_container_entity_id AS TEXT), CAST(e.id AS TEXT))), '')
                """
            : ", COALESCE(v.item_container_entity_id, 0), '', '', ''";
        command.CommandText = $"""
            SELECT e.id, s.vehicle_asset_id, s.vehicle_alias, e.location_x, e.location_y, e.location_z,
                   s.vehicle_last_access_time, s.is_vehicle_functional, s.is_vehicle_automatically_created
                   {locks}
            FROM vehicle_entity v
            JOIN entity e ON e.id = v.entity_id
            JOIN vehicle_spawner s ON s.vehicle_entity_id = e.id
            ORDER BY e.id
            """;
        var list = new List<SavedVehicle>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var asset = reader.GetString(1);
            var type = asset[(asset.IndexOf(':', StringComparison.Ordinal) + 1)..];
            var seconds = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
            list.Add(new SavedVehicle(reader.GetInt64(0), type, reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null,
                !reader.IsDBNull(7) && reader.GetInt64(7) != 0, !reader.IsDBNull(8) && reader.GetInt64(8) != 0,
                reader.GetInt64(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)));
        }

        return list;
    }

    private static IReadOnlyList<SavedPlayer> Players(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, user_id FROM user_profile WHERE user_id IS NOT NULL";
        var list = new List<SavedPlayer>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SavedPlayer(reader.IsDBNull(0) ? string.Empty : reader.GetString(0), Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return list;
    }
}
