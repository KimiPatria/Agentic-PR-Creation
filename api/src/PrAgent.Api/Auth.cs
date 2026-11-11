using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PrAgent.Application;
using PrAgent.Infrastructure;

namespace PrAgent.Api;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const string Dev = "Dev";
    public const string Cognito = "Cognito";

    /// <summary>Cognito (real sign-in) or Dev (pick a demo persona; refused outside Development and Testing).</summary>
    public string Mode { get; set; } = Dev;
    public string? CognitoRegion { get; set; }
    public string? CognitoUserPoolId { get; set; }
    public string? CognitoClientId { get; set; }
}

public static class AuthRegistration
{
    public const string DevScheme = "Dev";
    public const string DevUserHeader = "X-Dev-User";
    public const string AdminPolicy = "Admin";

    public static void AddPrAgentAuth(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new();
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

        if (options.Mode.Equals(AuthOptions.Cognito, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.CognitoRegion) || string.IsNullOrWhiteSpace(options.CognitoUserPoolId) || string.IsNullOrWhiteSpace(options.CognitoClientId))
                throw new InvalidOperationException("Auth:Mode is Cognito but Auth:CognitoRegion, Auth:CognitoUserPoolId or Auth:CognitoClientId is missing.");

            // The web app sends the Cognito ID token: it carries the email, name and groups the app needs.
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
            {
                jwt.Authority = $"https://cognito-idp.{options.CognitoRegion}.amazonaws.com/{options.CognitoUserPoolId}";
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudience = options.CognitoClientId,
                    ValidateLifetime = true,
                    NameClaimType = "name",
                    RoleClaimType = "cognito:groups",
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.FindFirstValue("token_use") != "id")
                            context.Fail("Send the Cognito ID token, not the access token.");
                        return Task.CompletedTask;
                    },
                };
            });
        }
        else
        {
            var environment = builder.Environment;
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException("Auth:Mode Dev lets anyone sign in as anyone. It is only allowed in the Development and Testing environments.");

            builder.Services.AddAuthentication(DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, null);
        }

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, p => p.RequireRole(Roles.Admin));
    }
}

/// <summary>Development sign-in: the X-Dev-User header names one of the demo personas.</summary>
public sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var email = Request.Headers[AuthRegistration.DevUserHeader].ToString();
        if (string.IsNullOrWhiteSpace(email)) return Task.FromResult(AuthenticateResult.NoResult());

        var persona = DemoUsers.All.FirstOrDefault(u => u.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
        if (persona is null) return Task.FromResult(AuthenticateResult.Fail($"'{email}' is not a demo user."));

        var claims = new List<Claim> { new("sub", persona.Email), new("email", persona.Email), new("name", persona.Name) };
        claims.AddRange(persona.Roles.Select(r => new Claim("cognito:groups", r)));
        var identity = new ClaimsIdentity(claims, AuthRegistration.DevScheme, "name", "cognito:groups");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthRegistration.DevScheme)));
    }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? throw new InvalidOperationException("No HTTP request in scope.");

    public string Id => Principal.FindFirstValue("sub") ?? throw new InvalidOperationException("The token has no sub claim.");
    public string Email => (Principal.FindFirstValue("email") ?? Id).ToLowerInvariant();
    public string Name => Principal.FindFirstValue("name") ?? Principal.FindFirstValue("email") ?? Id;

    /// <summary>Everyone who can sign in can raise a request; Reviewer and Admin come from Cognito groups.</summary>
    public IReadOnlyCollection<string> Roles =>
        Principal.FindAll("cognito:groups").Select(c => c.Value).Append(Application.Roles.Requestor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
