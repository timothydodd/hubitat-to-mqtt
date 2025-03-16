## Features

- **Bi-directional Communication**: Send device states from Hubitat to MQTT and control devices via MQTT topics
- **Real-time Updates**: Receive instant device state changes via webhooks
- **Reliability**: Hybrid approach with webhooks and periodic synchronization
- **Flexible Topic Structure**: Clear, hierarchical MQTT topics for easy integration
- **Smart Refresh Strategy**: Configurable rules for when to fetch complete device data
- **Device Control**: Control Hubitat devices by publishing to MQTT topics
- **Dual Addressing**: Access devices by name or ID for maximum flexibility
- **In-Memory Caching**: Reduces API calls to Hubitat and improves performance
- **Optional Authentication**: Support for both secured and unsecured MQTT brokers# Hubitat to MQTT Bridge

This project creates a bridge between Hubitat Elevation home automation hub and MQTT, enabling integration with other smart home platforms and custom applications that support MQTT.

## Features

- **Bi-directional Communication**: Send device states from Hubitat to MQTT and control devices via MQTT topics
- **Real-time Updates**: Receive instant device state changes via webhooks
- **Reliability**: Hybrid approach with webhooks and periodic synchronization
- **Flexible Topic Structure**: Clear, hierarchical MQTT topics for easy integration
- **Smart Refresh Strategy**: Configurable rules for when to fetch complete device data
- **Device Control**: Control Hubitat devices by publishing to MQTT topics
- **Dual Addressing**: Access devices by name or ID for maximum flexibility
- **Optional Authentication**: Support for both secured and unsecured MQTT brokers

## Architecture

The project consists of two main components:

1. **WebAPI with Webhook Controller**: Receives real-time device events from Hubitat
2. **Background Worker Service**: Performs periodic synchronization to ensure data consistency

## Setup & Configuration

### Prerequisites

- .NET 6.0 SDK or newer
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
       "BaseTopic": "hubitat"
     },
     "SyncPollIntervalHours": 4  // Set to 0 to disable polling after initial sync
   }
   ```

3. Build and run the application:
   ```bash
   dotnet build
   dotnet run
   ```

### Hubitat Configuration

1. Install the Maker API app on your Hubitat hub
2. Select the devices you want to expose to the API
3. Note your Access Token and App ID
4. Configure a webhook in the Maker API to point to your server:
   - URL: `http://your-server-ip:port/api/hook/device/event`
   - Method: POST

## MQTT Topic Structure

The bridge publishes device states to MQTT topics using the following structure:

- **Full Device Data (by name)**: `hubitat/{device_name}`
- **Device Attributes (by name)**: `hubitat/{device_name}/{attribute_name}`
- **Full Device Data (by ID)**: `hubitat/device/{device_id}`
- **Device Attributes (by ID)**: `hubitat/device/{device_id}/{attribute_name}`
- **Event Data**: `hubitat/device/{id}/events` (fallback for unknown devices)

### Command Topics

To control devices, publish to the command topics:

- **Command by device ID**: `hubitat/device/{device_id}/command/{command_name}`
- **Command by device name**: `hubitat/{device_name}/command/{command_name}`

The payload of the message should be the command parameter value. For commands without parameters, the payload can be empty.

Example topics:
- `hubitat/living_room_light/switch` → "on"
- `hubitat/front_door_lock/lock` → "locked"
- `hubitat/kitchen_motion/motion` → "active"

Example command:
- Publish "on" to `hubitat/device/123/command/switch` to turn on device ID 123
- Publish "70" to `hubitat/living_room_thermostat/command/setTemperature` to set temperature

## Smart Refresh Strategy

The application uses a balanced approach to decide when to fetch complete device data:

1. **Configurable Event Types**: Events like mode changes, security alerts, or presence changes can be configured to always trigger a full device refresh.

2. **Configurable Device Types**: Complex devices like thermostats, locks, or security systems can be configured to always use full device data.

3. **Periodic Synchronization**: Scheduled full synchronization ensures data consistency (configurable interval, default: 4 hours).

## Project Structure

- **WebhookController.cs**: Handles incoming webhook events from Hubitat
- **Worker.cs**: Background service for periodic device synchronization
- **HubitatClient.cs**: Client for communicating with the Hubitat API
- **Program.cs**: Application startup and service configuration

## Performance Considerations

- **Webhook-First Approach**: Primary updates come from webhooks for efficiency
- **Selective Refresh**: Only fetches full device data when necessary
- **Device Type Caching**: Remembers device types to optimize decision-making
- **In-Memory Device Cache**: Stores device data to minimize API calls to Hubitat
- **Configurable Sync Interval**: Balance between efficiency and reliability

## Troubleshooting

- Check logs for connection errors to MQTT broker or Hubitat hub
- Verify your Hubitat Maker API is accessible from the server running this application
- Ensure proper webhook configuration in Hubitat
- Test MQTT connectivity using a client like MQTT Explorer

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the LICENSE file for details.