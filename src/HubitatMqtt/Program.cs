using HubitatMqtt.Common;
using HubitatMqtt.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;

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
            builder.Services.AddSingleton<MqttSyncService>();
            // Register MQTT client as a singleton so it can be shared
            builder.Services.AddSingleton(serviceProvider =>
            {
                return MqttBuilder.CreateClient(serviceProvider.GetRequiredService<ILogger<Program>>(), serviceProvider.GetRequiredService<IConfiguration>(), false).Result;

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
