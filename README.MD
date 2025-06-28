# Hubitat to MQTT Bridge

This project creates a robust, production-ready bridge between Hubitat Elevation home automation hub and MQTT, enabling integration with other smart home platforms and custom applications that support MQTT.

## Features

- **Bi-directional Communication**: Send device states from Hubitat to MQTT and control devices via MQTT topics
- **Real-time Updates**: Receive instant device state changes via webhooks with race condition protection
- **Enhanced Reliability**: Hybrid approach with webhooks, periodic synchronization, and comprehensive error handling
- **Flexible Topic Structure**: Clear, hierarchical MQTT topics for easy integration
- **Smart Refresh Strategy**: Configurable rules for when to fetch complete device data
- **Device Control**: Control Hubitat devices by publishing to MQTT topics
- **Dual Addressing**: Access devices by name or ID for maximum flexibility
- **MQTT Authentication**: Support for both secured and unsecured MQTT brokers
- **Thread-Safe Operations**: Concurrent webhook processing with proper synchronization
- **Automatic Reconnection**: Intelligent MQTT reconnection with exponential backoff
- **Comprehensive Logging**: Detailed logging for troubleshooting and monitoring
- **Configuration Validation**: Startup validation prevents runtime configuration errors
- **Topic Cleanup**: Automatic cleanup of stale MQTT topics during synchronization

## Architecture

The application consists of several key components working together:

1. **WebAPI with Webhook Controller**: Receives real-time device events from Hubitat
2. **Background Worker Service**: Performs periodic synchronization to ensure data consistency
3. **Sync Coordinator**: Prevents race conditions between webhook updates and full sync operations
4. **Device Cache**: Thread-safe, high-performance caching of device data
5. **MQTT Services**: Robust publishing with retry logic and connection management
6. **Command Handler**: Processes MQTT commands to control Hubitat devices

## Setup & Configuration

### Prerequisites

- .NET 9.0 SDK or newer
- Hubitat Elevation hub with Maker API app installed
- MQTT broker (like Mosquitto, HiveMQ, or Eclipse MQTT)

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/yourusername/hubitat-mqtt.git
   cd hubitat-mqtt
   ```

2. Update the `appsettings.json` with your configuration:
   ```json
   {
     "Hubitat": {
       "BaseUrl": "http://your-hubitat-ip",
       "AccessToken": "your-maker-api-token",
       "DeviceId": "your-maker-api-app-id",
       "FullRefreshEvents": "mode,hsm,alarm,presence,away,home",
       "FullRefreshDeviceTypes": "thermostat,lock,security,doorControl,valve,waterSensor"
     },
     "MQTT": {
       "Server": "your-mqtt-broker-address",
       "Port": 1883,
       "Username": "",  // Optional - leave empty for no authentication
       "Password": "",  // Optional - leave empty for no authentication
       "BaseTopic": "hubitat",
       "MaxRetryAttempts": 3,        // Retry attempts for failed MQTT publishes
       "RetryDelayMs": 1000,         // Delay between retry attempts
       "SyncTimeoutMs": 10000        // Timeout for MQTT topic sync operations
     },
     "SyncPollIntervalHours": 4,     // Set to 0 to disable polling after initial sync
     "ClearTopicOnSync": true        // Clean up stale MQTT topics during sync
   }
   ```

3. Build and run the application:
   ```bash
   dotnet build
   dotnet run --project src/HubitatMqtt/HubitatMqtt.csproj
   ```

### Docker Deployment

Build and run using Docker:
```bash
docker build -t hubitat-mqtt -f src/HubitatMqtt/Dockerfile src/
docker run -d --name hubitat-mqtt -p 8080:8080 -v /path/to/appsettings.json:/app/appsettings.json hubitat-mqtt
```

### Hubitat Configuration

1. Install the Maker API app on your Hubitat hub
2. Select the devices you want to expose to the API
3. Note your Access Token and App ID
4. Configure a webhook in the Maker API to point to your server:
   - URL: `http://your-server-ip:8080/api/hook/device/event`
   - Method: POST

## MQTT Topic Structure

The bridge publishes device states to MQTT topics using the following structure:

- **Full Device Data (by ID)**: `hubitat/device/{device_id}`
- **Device Attributes (by ID)**: `hubitat/device/{device_id}/{attribute_name}`
- **Event Data**: `hubitat/device/{id}/events` (fallback for unknown devices)

### Command Topics

To control devices, publish to the command topics:

- **Command by device ID**: `hubitat/device/{device_id}/command/{command_name}`

The payload of the message should be the command parameter value. For commands without parameters, the payload can be empty.

**Example commands:**
- Publish `"on"` to `hubitat/device/123/command/switch` to turn on device ID 123
- Publish `"70"` to `hubitat/device/456/command/setTemperature` to set temperature to 70°

**Example attribute topics:**
- `hubitat/device/123/switch` → `"on"`
- `hubitat/device/456/temperature` → `"72"`
- `hubitat/device/789/battery` → `"85"`

## Smart Refresh Strategy

The application uses an intelligent approach to balance performance and reliability:

1. **Configurable Event Types**: Events like mode changes, security alerts, or presence changes trigger full device refresh
2. **Configurable Device Types**: Complex devices like thermostats, locks, or security systems always use full device data
3. **Periodic Synchronization**: Scheduled full synchronization ensures data consistency
4. **Race Condition Prevention**: Sync coordinator prevents conflicts between webhook updates and full sync

## Enhanced Logging

The application provides comprehensive logging for monitoring and troubleshooting:

### AlwaysRefreshDevice Logging
For devices configured in `FullRefreshDeviceTypes`, detailed change logging is available:

```
[INFO] AlwaysRefreshDevice Update - Device: 123 (Front Door Lock) Type: lock | Trigger: lock=locked
[INFO] AlwaysRefreshDevice Change - 123 (Front Door Lock) | lock: 'unlocked' → 'locked'
[INFO] AlwaysRefreshDevice Change - 123 (Front Door Lock) | battery: '85' → '84'
[INFO] AlwaysRefreshDevice Summary - 123 (Front Door Lock) | 2 attributes changed: lock, battery
```

### Error Handling and Recovery
- **Hubitat API Errors**: Automatic fallback to event-only updates
- **MQTT Connection Issues**: Automatic reconnection with exponential backoff
- **Publish Failures**: Retry logic with configurable attempts and delays
- **Configuration Errors**: Comprehensive validation at startup

## Performance Optimizations

- **Thread-Safe Device Cache**: High-performance concurrent device data storage
- **Webhook-First Approach**: Primary updates come from webhooks for efficiency
- **Selective Refresh**: Only fetches full device data when necessary
- **Connection Pooling**: Efficient HTTP client management for Hubitat API calls
- **Async Operations**: Non-blocking operations throughout the application
- **Memory Efficient**: Proper resource disposal and memory management

## Health Monitoring

The application exposes a health check endpoint at `/health` for monitoring:

```bash
curl http://localhost:8080/health
```

## Project Structure

- **Controllers/WebhookController.cs**: Handles incoming webhook events from Hubitat with sync coordination
- **Services/Worker.cs**: Background service for periodic device synchronization
- **Services/HubitatClient.cs**: Robust HTTP client for Hubitat API communication
- **Services/DeviceCache.cs**: Thread-safe device data caching
- **Services/MqttPublishService.cs**: MQTT publishing with retry logic and error handling
- **Services/SyncCoordinator.cs**: Prevents race conditions between webhook and sync operations
- **Services/MqttCommandHandler.cs**: Processes MQTT commands to control devices
- **Common/MqttBuilder.cs**: MQTT client factory with advanced reconnection logic
- **Program.cs**: Application startup, DI configuration, and validation

## Configuration Reference

### Required Settings
- `Hubitat:BaseUrl` - URL of your Hubitat hub
- `Hubitat:AccessToken` - Maker API access token
- `Hubitat:DeviceId` - Maker API app ID
- `MQTT:Server` - MQTT broker hostname/IP
- `MQTT:Port` - MQTT broker port

### Optional Settings
- `Hubitat:FullRefreshEvents` - Comma-separated list of events that trigger full refresh
- `Hubitat:FullRefreshDeviceTypes` - Comma-separated list of device types that always get full refresh
- `MQTT:Username/Password` - MQTT authentication credentials
- `MQTT:BaseTopic` - Base topic prefix (default: "hubitat")
- `MQTT:MaxRetryAttempts` - Retry attempts for failed publishes (default: 3)
- `MQTT:RetryDelayMs` - Delay between retries in milliseconds (default: 1000)
- `MQTT:SyncTimeoutMs` - Timeout for sync operations (default: 10000)
- `SyncPollIntervalHours` - Hours between full syncs (default: 4, 0 to disable)
- `ClearTopicOnSync` - Clean up stale topics during sync (default: true)

## Troubleshooting

### Common Issues

**Configuration Validation Errors**: Check logs during startup for specific validation failures

**MQTT Connection Issues**: 
- Verify broker accessibility and credentials
- Check logs for reconnection attempts and exponential backoff behavior

**Webhook Timeout Warnings**:
- `Timeout acquiring webhook lock` indicates full sync was in progress
- This is normal behavior that prevents race conditions

**Device Sync Issues**:
- Check Hubitat API accessibility and token validity
- Review logs for specific API error messages

### Log Analysis
- Use log prefixes like `AlwaysRefreshDevice` to filter specific device updates
- Monitor health check endpoint for overall system status
- Watch for `HubitatApiException` and `MqttPublishException` for service-specific issues

### Performance Tuning
- Adjust `SyncPollIntervalHours` based on your needs
- Configure `MaxRetryAttempts` and `RetryDelayMs` for your network conditions
- Monitor device cache performance in logs

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request. When contributing:

1. Follow the existing code style and patterns
2. Add appropriate logging for new features
3. Include error handling and validation
4. Update documentation as needed

## License

This project is licensed under the MIT License - see the LICENSE file for details.