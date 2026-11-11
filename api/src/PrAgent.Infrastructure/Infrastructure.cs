using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrAgent.Application;
using PrAgent.Application.Data;

namespace PrAgent.Infrastructure;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    public const string S3 = "S3";
    public const string Local = "Local";

    /// <summary>S3 (real AWS) or Local (a folder on disk: tests, and before the bucket exists).</summary>
    public string Mode { get; set; } = Local;
    public string? Bucket { get; set; }
    public string Prefix { get; set; } = "attachments/";
    public string LocalPath { get; set; } = ".data/attachments";
}

public sealed class EmailOptions
{
    public const string Section = "Email";
    public const string Ses = "Ses";
    public const string Log = "Log";

    /// <summary>Ses (real AWS) or Log (writes the email to the application log instead of sending).</summary>
    public string Mode { get; set; } = Log;

    /// <summary>Verified SES sender address.</summary>
    public string? From { get; set; }

    /// <summary>
    /// Sends every email to this one address instead of its real recipients, and names them in the body.
    /// Needed while SES is in its sandbox, which only delivers to verified addresses.
    /// </summary>
    public string? RedirectAllTo { get; set; }
}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(typeof(InfrastructureRegistration).Assembly.GetName().Name)));

        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section);
        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section);
        services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.Section);

        // AWS clients use the default credential chain (aws login, AWS_PROFILE, or a role) and are
        // only created when a mode that needs them is selected. AWS:Region overrides the profile's region.
        var region = configuration["AWS:Region"];
        services.AddSingleton<IAmazonS3>(_ => string.IsNullOrWhiteSpace(region)
            ? new AmazonS3Client()
            : new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region)));
        services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => string.IsNullOrWhiteSpace(region)
            ? new AmazonSimpleEmailServiceV2Client()
            : new AmazonSimpleEmailServiceV2Client(Amazon.RegionEndpoint.GetBySystemName(region)));

        var storage = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new();
        if (storage.Mode.Equals(StorageOptions.S3, StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IAttachmentStore, S3AttachmentStore>();
        else
            services.AddSingleton<IAttachmentStore, LocalAttachmentStore>();

        var email = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new();
        if (email.Mode.Equals(EmailOptions.Ses, StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmailSender, SesEmailSender>();
        else
            services.AddSingleton<IEmailSender, LogEmailSender>();

        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        return services;
    }
}

/// <summary>Lets `dotnet ef migrations add` build the model without starting the API or reaching a database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=pragent;Username=pragent;Password=pragent_dev",
                npgsql => npgsql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .Options);
}

public sealed class S3AttachmentStore(IAmazonS3 s3, IOptions<StorageOptions> options) : IAttachmentStore
{
    private readonly string _bucket = options.Value.Bucket
        ?? throw new InvalidOperationException("Storage:Bucket is required when Storage:Mode is S3.");

    private string Key(string key) => options.Value.Prefix + key;

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct) =>
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = Key(key),
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        }, ct);

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(_bucket, Key(key), ct);
        var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    public async Task DeleteAsync(string key, CancellationToken ct) => await s3.DeleteObjectAsync(_bucket, Key(key), ct);
}

public sealed class LocalAttachmentStore(IOptions<StorageOptions> options) : IAttachmentStore
{
    private readonly string _root = Path.GetFullPath(options.Value.LocalPath);

    private string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Attachment key escapes the storage root.");
        return path;
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(PathFor(key)));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

public sealed class SesEmailSender(IAmazonSimpleEmailServiceV2 ses, IOptions<EmailOptions> options, ILogger<SesEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var from = options.Value.From ?? throw new InvalidOperationException("Email:From is required when Email:Mode is Ses.");
        var to = message.To.ToList();
        var cc = message.Cc.ToList();
        var body = message.TextBody;

        if (!string.IsNullOrWhiteSpace(options.Value.RedirectAllTo))
        {
            body = $"[Redirected. Intended for To: {string.Join(", ", to)}; Cc: {(cc.Count == 0 ? "-" : string.Join(", ", cc))}]\n\n{body}";
            to = [options.Value.RedirectAllTo];
            cc = [];
        }

        var response = await ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = from,
            Destination = new Destination { ToAddresses = to, CcAddresses = cc },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = message.Subject, Charset = "UTF-8" },
                    Body = new Body { Text = new Content { Data = body, Charset = "UTF-8" } },
                },
            },
        }, ct);

        logger.LogInformation("Email sent via SES ({MessageId}): {Subject}", response.MessageId, message.Subject);
    }
}

/// <summary>Writes the email to the log instead of sending it.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation("EMAIL (not sent, Email:Mode is Log)\n  To: {To}\n  Cc: {Cc}\n  Subject: {Subject}\n{Body}",
            string.Join(", ", message.To), string.Join(", ", message.Cc), message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}
