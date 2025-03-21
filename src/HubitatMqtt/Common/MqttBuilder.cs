using MQTTnet;

namespace HubitatMqtt.Common;

public class MqttBuilder
{
    public static async Task<IMqttClient> CreateClient(ILogger logger, IConfiguration configuration, bool shortLived = true)
    {


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

        if (!shortLived)
        {
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
        }

        // Connect to MQTT broker
        try
        {
            await mqttClient.ConnectAsync(options);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to MQTT broker on startup");
        }

        return mqttClient;
    }
}
