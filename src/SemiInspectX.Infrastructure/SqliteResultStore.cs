using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SemiInspectX.Core;

namespace SemiInspectX.Infrastructure;

public sealed class SqliteResultStore : IResultStore, IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly FaultSettings _faults;
    public string DatabasePath => Path.Combine(_root, "inspection.db");
    public SqliteResultStore(string root, FaultSettings faults)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _faults = faults;
        _connection = new($"Data Source={DatabasePath}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS Runs(Id TEXT PRIMARY KEY,Lot TEXT NOT NULL,Wafer TEXT NOT NULL,Started TEXT NOT NULL,Ended TEXT,Status TEXT NOT NULL,Recipe TEXT NOT NULL,Planned INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Results(RunId TEXT NOT NULL,X INTEGER NOT NULL,Y INTEGER NOT NULL,DieIndex INTEGER NOT NULL,Verdict TEXT NOT NULL,Payload TEXT NOT NULL,PRIMARY KEY(RunId,X,Y),FOREIGN KEY(RunId) REFERENCES Runs(Id));
            CREATE TABLE IF NOT EXISTS Events(RunId TEXT NOT NULL,Timestamp TEXT NOT NULL,Code TEXT NOT NULL,Message TEXT NOT NULL);
            PRAGMA foreign_keys=ON;
            """;
        command.ExecuteNonQuery();
    }
    private async Task Execute(string sql, params (string, object?)[] values)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in values)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
    public async Task BeginAsync(RunInfo r)
    {
        await _gate.WaitAsync();
        try
        {
            await Execute("INSERT INTO Runs VALUES($id,$lot,$wafer,$started,NULL,'Running',$recipe,$planned)",
            ("$id", r.Id), ("$lot", r.LotId), ("$wafer", r.WaferId), ("$started", r.StartedAt.ToString("O")), ("$recipe", JsonSerializer.Serialize(r.Recipe, Recipe.Json)), ("$planned", r.PlannedDies));
        }
        finally { _gate.Release(); }
    }
    public async Task<DieResult> SaveAsync(DieResult result, ImageFrame frame, long ticks)
    {
        await _gate.WaitAsync();
        try
        {
            if (_faults.Matches(FaultKind.DatabaseWrite, result.Die.Index))
                throw new IOException($"DATABASE_WRITE_FAILED die={result.Die.Index}");
            if (_faults.WriteDelayMs > 0)
                await Task.Delay(_faults.WriteDelayMs);
            string dir = Path.Combine(_root, "images", result.RunId);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"die-{result.Die.Index}.pgm");
            await Pgm.WriteAsync(file, frame);
            result = result with
            {
                ImagePath = Path.GetRelativePath(_root, file)
            };
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Results VALUES($run,$x,$y,$index,$verdict,$payload)";
            command.Parameters.AddWithValue("$run", result.RunId);
            command.Parameters.AddWithValue("$x", result.Die.X);
            command.Parameters.AddWithValue("$y", result.Die.Y);
            command.Parameters.AddWithValue("$index", result.Die.Index);
            command.Parameters.AddWithValue("$verdict", result.Inspection.Verdict.ToString());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result, Recipe.Json));
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            // Includes image writing and the result commit; update the persisted latency after that commit.
            result = result with
            {
                EndToEndMs = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds
            };
            await Execute("UPDATE Results SET Payload=$payload WHERE RunId=$run AND X=$x AND Y=$y",
                ("$payload", JsonSerializer.Serialize(result, Recipe.Json)), ("$run", result.RunId), ("$x", result.Die.X), ("$y", result.Die.Y));
            return result;
        }
        finally { _gate.Release(); }
    }
    public async Task EndAsync(string runId, RunStatus status, IReadOnlyList<EquipmentEvent> events)
    {
        await _gate.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var e in events)
            {
                using var c = _connection.CreateCommand();
                c.Transaction = transaction;
                c.CommandText = "INSERT INTO Events VALUES($run,$time,$code,$message)";
                c.Parameters.AddWithValue("$run", runId);
                c.Parameters.AddWithValue("$time", e.Timestamp.ToString("O"));
                c.Parameters.AddWithValue("$code", e.Code);
                c.Parameters.AddWithValue("$message", e.Message);
                await c.ExecuteNonQueryAsync();
            }
            using var end = _connection.CreateCommand();
            end.Transaction = transaction;
            end.CommandText = "UPDATE Runs SET Status=$status,Ended=$ended WHERE Id=$id";
            end.Parameters.AddWithValue("$status", status.ToString());
            end.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.ToString("O"));
            end.Parameters.AddWithValue("$id", runId);
            await end.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<RunSummary>> QueryRunsAsync(string? lot = null, string? wafer = null, string? run = null)
    {
        await _gate.WaitAsync();
        try
        {
            using var c = _connection.CreateCommand();
            c.CommandText = """
                SELECT r.Id,r.Lot,r.Wafer,r.Status,r.Planned,COUNT(d.DieIndex),COALESCE(SUM(CASE WHEN d.Verdict='Pass' THEN 1 ELSE 0 END),0),r.Recipe
                FROM Runs r LEFT JOIN Results d ON r.Id=d.RunId
                WHERE ($lot IS NULL OR r.Lot=$lot) AND ($wafer IS NULL OR r.Wafer=$wafer) AND ($run IS NULL OR r.Id=$run)
                GROUP BY r.Id ORDER BY r.Started DESC
                """;
            c.Parameters.AddWithValue("$lot", (object?)lot ?? DBNull.Value);
            c.Parameters.AddWithValue("$wafer", (object?)wafer ?? DBNull.Value);
            c.Parameters.AddWithValue("$run", (object?)run ?? DBNull.Value);
            using var reader = await c.ExecuteReaderAsync();
            var list = new List<RunSummary>();
            while (await reader.ReadAsync())
                list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7)));
            return list;
        }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<DieResult>> QueryResultsAsync(string runId, Verdict? verdict = null)
    {
        await _gate.WaitAsync();
        try
        {
            using var c = _connection.CreateCommand();
            c.CommandText = "SELECT Payload FROM Results WHERE RunId=$run AND ($verdict IS NULL OR Verdict=$verdict) ORDER BY DieIndex";
            c.Parameters.AddWithValue("$run", runId);
            c.Parameters.AddWithValue("$verdict", (object?)verdict?.ToString() ?? DBNull.Value);
            using var reader = await c.ExecuteReaderAsync();
            var list = new List<DieResult>();
            while (await reader.ReadAsync())
                list.Add(JsonSerializer.Deserialize<DieResult>(reader.GetString(0), Recipe.Json)!);
            return list;
        }
        finally { _gate.Release(); }
    }
    public static async Task ExportCsvAsync(string path, IReadOnlyList<DieResult> results)
    {
        static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var text = new StringBuilder("RunId,DieIndex,X,Y,Verdict,WidthPixels,WidthMicrons,AlgorithmMs,EndToEndMs,Image\n");
        foreach (var r in results)
            text.AppendLine(string.Join(",", Quote(r.RunId), r.Die.Index, r.Die.X, r.Die.Y, r.Inspection.Verdict,
            r.Inspection.WidthPixels.ToString("F4", CultureInfo.InvariantCulture), r.Inspection.WidthMicrons.ToString("F4", CultureInfo.InvariantCulture),
            r.Inspection.AlgorithmMs.ToString("F4", CultureInfo.InvariantCulture), r.EndToEndMs.ToString("F4", CultureInfo.InvariantCulture), Quote(r.ImagePath)));
        await File.WriteAllTextAsync(path, text.ToString());
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _connection.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
