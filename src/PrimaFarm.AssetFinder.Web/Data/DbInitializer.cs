using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Security;

namespace PrimaFarm.AssetFinder.Web.Data;

public class DevSeedUser
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = Roles.Staff;
    public string Password { get; set; } = "";
}

/// <summary>
/// Development convenience: creates the database and applies the numbered T-SQL scripts in
/// /Database (001_, 002_, 003_ ...). Production deployments run the same scripts through the
/// change-management process (sqlcmd / DBA) and set Database:AutoDeploy=false.
/// Scripts numbered 900+ are operational jobs and are never applied here.
/// </summary>
public static partial class DbInitializer
{
    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    public static async Task ApplyScriptsAsync(string connectionString, string scriptFolder, ILogger logger)
    {
        var dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        var masterCsb = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using (var master = new SqlConnection(masterCsb.ConnectionString))
        {
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            // Database names cannot be parameters; QUOTENAME guards the identifier.
            cmd.CommandText = "IF DB_ID(@db) IS NULL BEGIN DECLARE @sql NVARCHAR(400) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC (@sql); END";
            cmd.Parameters.AddWithValue("@db", dbName);
            await cmd.ExecuteNonQueryAsync();
        }

        var scripts = Directory.GetFiles(scriptFolder, "*.sql")
            .Where(f => int.TryParse(Path.GetFileName(f).Split('_')[0], out var n) && n < 900)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var script in scripts)
        {
            var text = await File.ReadAllTextAsync(script);
            foreach (var batch in GoSeparator().Split(text).Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var cmd = conn.CreateCommand();
#pragma warning disable CA2100 // Batches come from version-controlled scripts shipped with the app, never from user input.
                cmd.CommandText = batch;
#pragma warning restore CA2100
                await cmd.ExecuteNonQueryAsync();
            }
            logger.LogInformation("Applied database script {Script}", Path.GetFileName(script));
        }
    }

    /// <summary>Creates the DevSeed:Users accounts that do not exist yet. Never called outside Development.</summary>
    public static async Task SeedDevUsersAsync(AppDbContext db, IEnumerable<DevSeedUser> users, ILogger logger)
    {
        foreach (var u in users.Where(u => !string.IsNullOrWhiteSpace(u.Username) && !string.IsNullOrWhiteSpace(u.Password)))
        {
            if (await db.AppUsers.AnyAsync(x => x.Username == u.Username)) continue;
            db.AppUsers.Add(new AppUser
            {
                Username = u.Username,
                DisplayName = u.DisplayName,
                Role = u.Role,
                PasswordHash = PasswordHasher.Hash(u.Password)
            });
            logger.LogInformation("Seeded development user {Username} ({Role})", u.Username, u.Role);
        }
        await db.SaveChangesAsync();
    }
}
