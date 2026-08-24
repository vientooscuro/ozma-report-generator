using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ReportGenerator
{
    public class Startup
    {
        public Startup(IWebHostEnvironment env, IConfiguration configuration)
        {
            Configuration = configuration;
            Environment = env;
        }

        public IConfiguration Configuration { get; }
        public IWebHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors();

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
                {
                    options.ExpireTimeSpan = TimeSpan.FromHours(24);
                    options.SlidingExpiration = true;
                })
                .AddOpenIdConnect(options =>
                    {
                        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.ClientId = Configuration["AuthSettings:ClientId"];
                        options.ClientSecret = Configuration["AuthSettings:ClientSecret"];
                        options.Authority = Configuration["AuthSettings:Authority"];
                        // Can be null, then `Authority` is used.
                        options.MetadataAddress = Configuration["AuthSettings:MetadataAddress"];
                        options.RequireHttpsMetadata = Configuration.GetValue("AuthSettings:RequireHttpsMetadata", true);
                        options.SaveTokens = true;
                        options.ResponseType = "code";
                        options.GetClaimsFromUserInfoEndpoint = true;
                        options.TokenValidationParameters =
                            new TokenValidationParameters
                            {
                                ValidateIssuer = false,
                                ValidateAudience = false,
                                ValidateIssuerSigningKey = true
                            };
                        options.Scope.Clear();
                        options.Scope.Add("openid");
                        options.Events = new OpenIdConnectEvents
                        {
                            OnTokenValidated = x =>
                            {
                                var identity = (ClaimsIdentity)x.Principal!.Identity!;
                                identity.AddClaims(new[]
                                {
                                    new Claim("access_token", x.TokenEndpointResponse!.AccessToken),
                                    new Claim("refresh_token", x.TokenEndpointResponse!.RefreshToken)
                                });
                                x.Properties!.IsPersistent = true;
                                //var accessToken = new JwtSecurityToken(x.TokenEndpointResponse.AccessToken);
                                //x.Properties.ExpiresUtc = accessToken.ValidTo;
                                return Task.CompletedTask;
                            }
                        };
                        if (Environment.IsDevelopment())
                        {
                            options.NonceCookie.SameSite = SameSiteMode.Unspecified;
                            options.CorrelationCookie.SameSite = SameSiteMode.Unspecified;
                        }
                    }
                );
            services.AddSingleton<Services.IOzmaPermissionsChecker, Services.OzmaPermissionsChecker>();
            services.AddMemoryCache();
            services.AddControllersWithViews();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            var pathBase = Configuration["HostSettings:PathBase"];
            if (pathBase != null)
            {
                app.UsePathBase(pathBase);
            }

            var options = new RewriteOptions()
                .AddRedirect("(?i)^admin/([^/]+)$", "admin/$1/");
            app.UseRewriter(options);

            if (Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                IdentityModelEventSource.ShowPII = true;
            }
            else
            {
                app.UseExceptionHandler("/Admin/Error");
            }
            app.UseStatusCodePages();

            var allowedOrigins = Configuration.GetRequiredSection("HostSettings:AllowedOrigins").Get<string[]>();
            app.UseCors(builder => builder
                .SetIsOriginAllowedToAllowWildcardSubdomains()
                .WithOrigins(allowedOrigins!)
                .AllowAnyHeader()
                );

            app.UseStaticFiles();
            app.UseRouting();

            var forwardingOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            forwardingOptions.KnownNetworks.Clear();
            forwardingOptions.KnownProxies.Clear();
            app.UseForwardedHeaders(forwardingOptions);

            app.UseAuthentication();
            app.UseAuthorization();

            //app.UseSession();

            app.UseEndpoints(endpoints =>
            {
                //endpoints.MapControllers();
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Admin}/{action=Index}/{id?}");
            });
        }
    }
}
