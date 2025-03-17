using HubitatMqtt.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;
using MQTTnet;

namespace HubitatToMqtt
{
    public class Program
    {

        public static void Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            builder.Services.AddControllers();
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<IMemoryCache, MemoryCache>();
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
            var hubSettings = builder.Configuration.GetSection("Hubitat").Get<HubitatApiSettings>();
            builder.Services.AddSingleton(b => hubSettings);
            // Register HubitatClient and its dependencies


            builder.Services.AddHttpClient<HubitatClient>();
            builder.Services.AddSingleton<DeviceCache>();
            // Register MQTT client as a singleton so it can be shared
            builder.Services.AddSingleton<IMqttClient>(serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var configuration = serviceProvider.GetRequiredService<IConfiguration>();

                var mqttFactory = new MqttClientFactory();
                var mqttClient = mqttFactory.CreateMqttClient();

                // Configure MQTT client connection
                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithClientId($"HubitatWorker_{Guid.NewGuid()}")
                    .WithTcpServer(
                        configuration["MQTT:Server"],
                        configuration.GetValue<int>("MQTT:Port", 1883))
                    .WithCleanSession();

                // Only add credentials if both username and password are provided
                if (!string.IsNullOrEmpty(configuration["MQTT:Username"]) &&
                    !string.IsNullOrEmpty(configuration["MQTT:Password"]))
                {
                    optionsBuilder = optionsBuilder.WithCredentials(
                        configuration["MQTT:Username"],
                        configuration["MQTT:Password"]);
                }

                var options = optionsBuilder.Build();

                // Setup reconnection handler
                mqttClient.DisconnectedAsync += async (e) =>
                {
                    logger.LogWarning("Disconnected from MQTT broker. Trying to reconnect...");
                    await Task.Delay(TimeSpan.FromSeconds(5));

                    try
                    {
                        await mqttClient.ConnectAsync(options);
                        logger.LogInformation("Successfully reconnected to MQTT broker");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to reconnect to MQTT broker");
                    }
                };

                mqttClient.ConnectedAsync += (e) =>
                {
                    logger.LogInformation("Connected to MQTT broker successfully");
                    return Task.CompletedTask;
                };

                // Connect to MQTT broker
                try
                {
                    mqttClient.ConnectAsync(options).Wait();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to connect to MQTT broker on startup");
                }

                return mqttClient;
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



    }
}
