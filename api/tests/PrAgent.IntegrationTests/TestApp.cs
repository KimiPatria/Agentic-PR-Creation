using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using PrAgent.Application;
using PrAgent.Application.Sap;
using PrAgent.Infrastructure;
using PrAgent.Sap.Mock;

namespace PrAgent.IntegrationTests;

/// <summary>
/// One PostgreSQL server for the whole test run; every test gets its own database and API host.
///
/// By default the fixture creates a throwaway cluster from the locally installed PostgreSQL
/// binaries, in a temp folder on a free port, and deletes it at the end. Nothing needs to be
/// running first. To use a server you already have instead, set PRAGENT_TEST_POSTGRES to a
/// connection string for a user that may create databases.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string ExistingServerVariable = "PRAGENT_TEST_POSTGRES";
    private string _adminConnectionString = "";
    private LocalPostgresCluster? _cluster;
    private int _counter;

    public async Task InitializeAsync()
    {
        var existing = Environment.GetEnvironmentVariable(ExistingServerVariable);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            _adminConnectionString = existing;
            return;
        }

        _cluster = await LocalPostgresCluster.StartAsync();
        _adminConnectionString = _cluster.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_cluster is not null) await _cluster.DisposeAsync();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString;
    }
}

/// <summary>A disposable PostgreSQL cluster run from the local installation's initdb and pg_ctl.</summary>
public sealed class LocalPostgresCluster : IAsyncDisposable
{
    private readonly string _bin;
    private readonly string _dataDir;

    private LocalPostgresCluster(string bin, string dataDir, int port)
    {
        _bin = bin;
        _dataDir = dataDir;
        // Trust authentication is safe here: the cluster listens on 127.0.0.1 only and lives for one test run.
        ConnectionString = $"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;Pooling=true";
    }

    public string ConnectionString { get; }

    public static async Task<LocalPostgresCluster> StartAsync()
    {
        var bin = FindBinaries();
        var dataDir = Path.Combine(Path.GetTempPath(), "pragent-test-pg", Guid.NewGuid().ToString("N"));
        var port = FreePort();
        var cluster = new LocalPostgresCluster(bin, dataDir, port);

        await Run(bin, "initdb", $"-D \"{dataDir}\" -U postgres --auth=trust -E UTF8 --no-locale");
        File.AppendAllText(Path.Combine(dataDir, "postgresql.conf"),
            $"\nlisten_addresses = '127.0.0.1'\nport = {port}\nfsync = off\nsynchronous_commit = off\nfull_page_writes = off\nmax_connections = 200\n");
        await Run(bin, "pg_ctl", $"start -D \"{dataDir}\" -l \"{Path.Combine(dataDir, "server.log")}\" -w -t 60");
        return cluster;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Run(_bin, "pg_ctl", $"stop -D \"{_dataDir}\" -m immediate -w");
        }
        finally
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(_dataDir); attempt++)
            {
                try { Directory.Delete(_dataDir, recursive: true); }
                catch (IOException) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) { await Task.Delay(500); }
            }
        }
    }

    /// <summary>
    /// Runs a PostgreSQL tool in its own window-less process. The server that pg_ctl starts inherits
    /// the caller's handles; with shell execution there are none to inherit, so nothing waits on it.
    /// </summary>
    private static async Task Run(string bin, string tool, string arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(bin, tool + (OperatingSystem.IsWindows() ? ".exe" : "")), arguments)
        {
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{tool} {arguments} exited with code {process.ExitCode}.");
    }

    /// <summary>PG_BIN, then pg_ctl on PATH, then the newest C:\Program Files\PostgreSQL\{version}\bin.</summary>
    private static string FindBinaries()
    {
        var exe = OperatingSystem.IsWindows() ? "pg_ctl.exe" : "pg_ctl";

        var fromVariable = Environment.GetEnvironmentVariable("PG_BIN");
        if (!string.IsNullOrWhiteSpace(fromVariable) && File.Exists(Path.Combine(fromVariable, exe))) return fromVariable;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, exe))) return dir;

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (Directory.Exists(installRoot))
        {
            var newest = Directory.GetDirectories(installRoot)
                .Where(d => File.Exists(Path.Combine(d, "bin", exe)))
                .OrderByDescending(d => int.TryParse(Path.GetFileName(d).Split('.')[0], out var major) ? major : 0)
                .FirstOrDefault();
            if (newest is not null) return Path.Combine(newest, "bin");
        }

        throw new InvalidOperationException(
            "The integration tests need PostgreSQL. Install it (https://www.postgresql.org/download/), set PG_BIN to its bin folder, " +
            $"or set {"PRAGENT_TEST_POSTGRES"} to the connection string of a server you already run.");
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "api";
}

public sealed class CapturingEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }
}

public enum SapFailure { None, BeforeSap, AfterSap }

/// <summary>
/// Wraps the mock SAP writer so a test can make chosen calls fail: before SAP did anything, or after
/// SAP created the PR but before the caller heard back (the case idempotency exists for).
/// </summary>
public sealed class ScriptedSapPrWriter(MockSapPrWriter inner) : ISapPrWriter
{
    private int _calls;

    public int CreateCalls => _calls;
    public Func<SapPrCreateRequest, int, SapFailure> Script { get; set; } = (_, _) => SapFailure.None;
    public bool IsSimulated => true;

    public async Task<SapPrCreateResult> CreatePurchaseRequisitionAsync(SapPrCreateRequest request, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        var failure = Script(request, call);
        if (failure == SapFailure.BeforeSap) throw new SapException("Injected: SAP did not answer.", isTransient: true);

        var result = await inner.CreatePurchaseRequisitionAsync(request, ct);
        if (failure == SapFailure.AfterSap) throw new SapException("Injected: connection dropped after SAP created the PR.", isTransient: true);
        return result;
    }

    public Task LinkAttachmentAsync(string prNumber, SapAttachment attachment, CancellationToken ct) => inner.LinkAttachmentAsync(prNumber, attachment, ct);
}

public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string ServiceToken = "test-service-token";
    private readonly string _connectionString;
    private readonly string _attachmentPath = Path.Combine(Path.GetTempPath(), "pragent-tests", Guid.NewGuid().ToString("N"));

    private readonly string _sapMode;

    private TestApp(string connectionString, string sapMode) => (_connectionString, _sapMode) = (connectionString, sapMode);

    public static async Task<TestApp> StartAsync(PostgresFixture postgres, string sapMode = "Mock")
    {
        var app = new TestApp(await postgres.CreateDatabaseAsync(), sapMode);
        _ = app.Services; // build the host now, which migrates and seeds the database
        return app;
    }

    public CapturingEmailSender Email => Services.GetRequiredService<CapturingEmailSender>();
    public ScriptedSapPrWriter Sap => Services.GetRequiredService<ScriptedSapPrWriter>();
    public MockSapLedger Ledger => Services.GetRequiredService<MockSapLedger>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        builder.UseSetting("Auth:Mode", "Dev");
        builder.UseSetting("Sap:Mode", _sapMode);
        builder.UseSetting("Sap:Mock:StatePath", "");
        builder.UseSetting("Storage:Mode", "Local");
        builder.UseSetting("Storage:LocalPath", _attachmentPath);
        builder.UseSetting("Email:Mode", "Log");
        builder.UseSetting("Agent:ServiceToken", ServiceToken);
        builder.UseSetting("Seed:DemoData", "true");
        // Tests drive the outbox themselves so every step is observable.
        builder.UseSetting("Outbox:Enabled", "false");
        builder.UseSetting("Outbox:MaxAttempts", "3");
        builder.UseSetting("Outbox:RetryDelaysSeconds:0", "0");
        builder.UseSetting("Outbox:RetryDelaysSeconds:1", "0");
        builder.UseSetting("Outbox:RetryDelaysSeconds:2", "0");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<CapturingEmailSender>();
            services.Replace(ServiceDescriptor.Singleton<IEmailSender>(sp => sp.GetRequiredService<CapturingEmailSender>()));

            // Failure injection wraps the mock writer; with any other adapter the app runs exactly as configured.
            if (_sapMode == "Mock")
            {
                services.AddSingleton<MockSapPrWriter>();
                services.AddSingleton<ScriptedSapPrWriter>();
                services.Replace(ServiceDescriptor.Singleton<ISapPrWriter>(sp => sp.GetRequiredService<ScriptedSapPrWriter>()));
            }
        });
    }

    public HttpClient As(DemoUsers.DemoUser user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", user.Email);
        return client;
    }

    /// <summary>Runs one pass of the outbox: every message that is due is attempted once.</summary>
    public Task<int> DispatchOnceAsync() => Services.GetRequiredService<OutboxDispatcher>().DispatchPendingAsync(CancellationToken.None);

    /// <summary>Runs the outbox until nothing is due.</summary>
    public async Task DrainAsync()
    {
        for (var i = 0; i < 20; i++)
            if (await DispatchOnceAsync() == 0) return;
        throw new InvalidOperationException("Outbox did not drain.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_attachmentPath)) Directory.Delete(_attachmentPath, recursive: true);
    }
}

public static class Http
{
    public static readonly JsonSerializerOptions Json = PrAgent.Application.Json.Options;

    public static async Task<JsonNode> ReadAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Empty response body.");

    public static async Task<JsonNode> OkAsync(this Task<HttpResponseMessage> pending)
    {
        var response = await pending;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty response body.");
    }

    public static Task<HttpResponseMessage> PostAsync(this HttpClient client, string url, object? body = null) =>
        client.PostAsJsonAsync(url, body ?? new { }, Json);

    public static async Task<Guid> SectionIdAsync(this HttpClient client, string code)
    {
        var sections = (await client.GetAsync("/api/sections").OkAsync()).AsArray();
        return sections.Single(s => s!["code"]!.GetValue<string>() == code)!["id"]!.GetValue<Guid>();
    }
}

public static class Drafts
{
    public static string DeliveryDate => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd");

    public static object Request(Guid sectionId, params object[] lines) => new
    {
        title = "Laptops and setup for new joiners",
        justification = "Five engineers join in January.",
        plant = "1100",
        currency = "IDR",
        sectionId,
        lines,
    };

    public static object Material(int lineNo, string group = "IT01", string text = "Laptop 14in", decimal unitPrice = 15_000_000, string costCenter = "CC1101") => new
    {
        lineNo, budgetType = "Opex", itemType = "Material", costObjectType = "CostCenter", shortText = text, materialGroup = group,
        quantity = 2, uom = "EA", unitPrice, deliveryDate = DeliveryDate, costCenter,
    };

    public static object Service(int lineNo, string text = "Laptop imaging and setup") => new
    {
        lineNo, budgetType = "Opex", itemType = "Service", costObjectType = "CostCenter", shortText = text, materialGroup = "IT10",
        quantity = 1, uom = "AU", unitPrice = 5_000_000m, deliveryDate = DeliveryDate, costCenter = "CC1101",
    };
}
