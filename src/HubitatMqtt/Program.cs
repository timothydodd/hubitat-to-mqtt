using HubitatMqtt.Common;
using HubitatMqtt.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Http.Resilience;
using MQTTnet;
using Polly;

namespace HubitatToMqtt
{
    public class Program
    {

        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddMemoryCache();
            builder.Services.AddRequestDecompression();
            builder.Services.AddResponseCompression(options =>
            {
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
                {
            "application/json"
        });
            });
            // Validate configuration early
            ValidateConfiguration(builder.Configuration);
            
            var hubSettings = builder.Configuration.GetSection("Hubitat").Get<HubitatApiSettings>();
            if (hubSettings == null)
            {
                throw new InvalidOperationException("Hubitat configuration section is missing or invalid");
            }
            builder.Services.AddSingleton(b => hubSettings);
            // Register HubitatClient and its dependencies


            builder.Services.AddHttpClient<HubitatClient>((serviceProvider, client) =>
            {
                // Configure HttpClient with optimized settings
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.ConnectionClose = false; // Enable connection reuse
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
            {
                // Optimize connection pooling
                MaxConnectionsPerServer = 10,
                UseCookies = false, // Not needed for API calls
                UseDefaultCredentials = false
            })
            .AddStandardResilienceHandler(options =>
            {
                // Configure retry policy
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.Delay = TimeSpan.FromSeconds(1);
                
                // Configure circuit breaker
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.FailureRatio = 0.5; // 50% failure rate
                options.CircuitBreaker.MinimumThroughput = 5;
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
                
                // Configure timeout
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });
            builder.Services.AddSingleton<DeviceCache>();
            builder.Services.AddSingleton<MqttSyncService>();
            builder.Services.AddSingleton<SyncCoordinator>();
            builder.Services.AddSingleton<MqttDeviceReader>();
            // Register MQTT client using factory pattern to avoid blocking startup
            builder.Services.AddSingleton<IMqttClient>(serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                
                // Create client without connecting - connection happens in background services
                return MqttBuilder.CreateClientWithoutConnection(logger, configuration);
            });

            builder.Services.AddLogging(logging =>
            {
                logging.AddSimpleConsole(c =>
                {
                    c.SingleLine = true;
                    c.IncludeScopes = false;
                    c.TimestampFormat = "HH:mm:ss ";
                });

                logging.AddDebug();
            });

            builder.Services.AddSingleton<MqttPublishService>();
            builder.Services.AddHostedService<Worker>();
            builder.Services.AddHostedService<MqttCommandHandler>();


            builder.Services.AddHealthChecks().AddMqtt();

            var app = builder.Build();
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseResponseCaching();
            app.UseResponseCompression();
            app.MapControllers();
            app.UseRouting();

            app.UseHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthCheck.WriteResponse });


            app.Run();
        }

        private static void ValidateConfiguration(IConfiguration configuration)
        {
            var errors = new List<string>();

            // Validate Hubitat settings
            var hubitatSection = configuration.GetSection("Hubitat");
            if (!hubitatSection.Exists())
            {
                errors.Add("Hubitat configuration section is missing");
            }
            else
            {
                var baseUrl = hubitatSection["BaseUrl"];
                var accessToken = hubitatSection["AccessToken"];
                var deviceId = hubitatSection["DeviceId"];

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    errors.Add("Hubitat:BaseUrl is required");
                }
                else if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    errors.Add("Hubitat:BaseUrl must be a valid HTTP or HTTPS URL");
                }

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    errors.Add("Hubitat:AccessToken is required");
                }

                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    errors.Add("Hubitat:DeviceId is required");
                }
            }

            // Validate MQTT settings
            var mqttSection = configuration.GetSection("MQTT");
            if (!mqttSection.Exists())
            {
                errors.Add("MQTT configuration section is missing");
            }
            else
            {
                var server = mqttSection["Server"];
                var portString = mqttSection["Port"];
                var baseTopic = mqttSection["BaseTopic"];

                if (string.IsNullOrWhiteSpace(server))
                {
                    errors.Add("MQTT:Server is required");
                }

                if (string.IsNullOrWhiteSpace(portString) || !int.TryParse(portString, out var port) || port <= 0 || port > 65535)
                {
                    errors.Add("MQTT:Port must be a valid port number (1-65535)");
                }

                if (string.IsNullOrWhiteSpace(baseTopic))
                {
                    errors.Add("MQTT:BaseTopic is required");
                }
                else if (baseTopic.Contains("#") || baseTopic.Contains("+"))
                {
                    errors.Add("MQTT:BaseTopic cannot contain MQTT wildcards (# or +)");
                }
            }

            // Validate sync interval
            var syncInterval = configuration.GetValue<int>("SyncPollIntervalHours", 4);
            if (syncInterval < 0)
            {
                errors.Add("SyncPollIntervalHours must be >= 0 (0 disables periodic sync)");
            }

            // Validate retry settings
            var maxRetries = configuration.GetValue<int>("MQTT:MaxRetryAttempts", 3);
            if (maxRetries < 0 || maxRetries > 10)
            {
                errors.Add("MQTT:MaxRetryAttempts must be between 0 and 10");
            }

            var retryDelay = configuration.GetValue<int>("MQTT:RetryDelayMs", 1000);
            if (retryDelay < 100 || retryDelay > 30000)
            {
                errors.Add("MQTT:RetryDelayMs must be between 100 and 30000 milliseconds");
            }

            if (errors.Count > 0)
            {
                var errorMessage = "Configuration validation failed:\n" + string.Join("\n", errors.Select(e => "- " + e));
                throw new InvalidOperationException(errorMessage);
            }
        }
    }
}
