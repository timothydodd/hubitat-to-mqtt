using MQTTnet;
using MQTTnet.Exceptions;

namespace HubitatMqtt.Common;

public class MqttBuilder
{
    private static int _reconnectAttempts = 0;
    private static readonly object _reconnectLock = new object();
    private static DateTime _lastReconnectAttempt = DateTime.MinValue;
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
            // Setup reconnection handler with exponential backoff
            mqttClient.DisconnectedAsync += async (e) =>
            {
                await HandleReconnectionAsync(mqttClient, options, logger, e);
            };

            mqttClient.ConnectedAsync += (e) =>
            {
                // Reset reconnection attempts on successful connection
                lock (_reconnectLock)
                {
                    _reconnectAttempts = 0;
                }
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

    private static async Task HandleReconnectionAsync(IMqttClient mqttClient, MqttClientOptions options, ILogger logger, MqttClientDisconnectedEventArgs e)
    {
        const int maxReconnectAttempts = 10;
        const int baseDelaySeconds = 2;
        const int maxDelaySeconds = 300; // 5 minutes

        // Don't attempt reconnection if it was a clean disconnect
        if (e.ClientWasConnected == false && e.Reason == MqttClientDisconnectReason.NormalDisconnection)
        {
            logger.LogInformation("MQTT client disconnected normally, not attempting reconnection");
            return;
        }

        int currentAttempt;
        lock (_reconnectLock)
        {
            _reconnectAttempts++;
            currentAttempt = _reconnectAttempts;
        }

        if (currentAttempt > maxReconnectAttempts)
        {
            logger.LogError("Maximum reconnection attempts ({MaxAttempts}) reached. Stopping reconnection attempts.", maxReconnectAttempts);
            return;
        }

        // Calculate exponential backoff delay with jitter
        var delaySeconds = Math.Min(baseDelaySeconds * Math.Pow(2, currentAttempt - 1), maxDelaySeconds);
        var jitter = new Random().NextDouble() * 0.1 * delaySeconds; // Add up to 10% jitter
        var totalDelay = TimeSpan.FromSeconds(delaySeconds + jitter);

        logger.LogWarning("MQTT disconnected (Reason: {Reason}). Attempt {Attempt}/{MaxAttempts} to reconnect in {Delay} seconds", 
            e.Reason, currentAttempt, maxReconnectAttempts, totalDelay.TotalSeconds);

        // Prevent too frequent reconnection attempts
        var timeSinceLastAttempt = DateTime.UtcNow - _lastReconnectAttempt;
        if (timeSinceLastAttempt < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        await Task.Delay(totalDelay);
        _lastReconnectAttempt = DateTime.UtcNow;

        try
        {
            if (!mqttClient.IsConnected)
            {
                logger.LogInformation("Attempting to reconnect to MQTT broker (attempt {Attempt})", currentAttempt);
                await mqttClient.ConnectAsync(options);
                logger.LogInformation("Successfully reconnected to MQTT broker on attempt {Attempt}", currentAttempt);
            }
            else
            {
                logger.LogDebug("MQTT client already connected, skipping reconnection attempt");
                lock (_reconnectLock)
                {
                    _reconnectAttempts = 0;
                }
            }
        }
        catch (MqttCommunicationException ex)
        {
            logger.LogWarning(ex, "MQTT communication error during reconnection attempt {Attempt}: {Message}", currentAttempt, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconnect to MQTT broker on attempt {Attempt}", currentAttempt);
        }
    }
}
