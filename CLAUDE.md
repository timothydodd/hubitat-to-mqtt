# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a .NET 9.0 ASP.NET Core application that bridges Hubitat Elevation home automation hub with MQTT brokers. It uses a hybrid approach with webhooks for real-time updates and periodic synchronization to ensure data consistency.

## Development Commands

**Build and Run:**
```bash
dotnet build src/HubitatMqtt/HubitatMqtt.csproj
dotnet run --project src/HubitatMqtt/HubitatMqtt.csproj
```

**Docker Build:**
```bash
docker build -t hubitat-mqtt -f src/HubitatMqtt/Dockerfile src/
```

## Architecture

### Core Components
- **WebhookController** (`Controllers/WebHookController.cs`): Handles real-time device events from Hubitat via HTTP webhooks at `/api/hook/device/event`
- **Worker** (`Services/Worker.cs`): Background service that performs periodic full synchronization of all devices
- **HubitatClient** (`Services/HubitatClient.cs`): HTTP client for communicating with Hubitat Maker API
- **MqttPublishService** (`Services/MqttPublishService.cs`): Handles publishing device data to MQTT topics
- **DeviceCache** (`Services/DeviceCache.cs`): In-memory cache for device data to minimize API calls
- **MqttSyncService** (`Services/MqttSyncService.cs`): Manages MQTT topic cleanup during synchronization

### Data Flow
1. Hubitat sends webhook events to WebhookController for real-time updates
2. Controller determines if full device refresh is needed based on event type and device type
3. Device data is published to MQTT with dual addressing (by name and ID)
4. Worker service performs periodic full synchronization based on `SyncPollIntervalHours` config
5. MqttCommandHandler listens for MQTT command topics to control Hubitat devices

### Key Configuration
Configuration is managed through `appsettings.json`:
- **Hubitat**: API connection settings, full refresh trigger events and device types
- **MQTT**: Broker connection and topic configuration  
- **SyncPollIntervalHours**: Periodic sync interval (set to 0 to disable)
- **ClearTopicOnSync**: Whether to clean up stale MQTT topics during sync

### MQTT Topic Structure
- Device data: `hubitat/{device_name}` and `hubitat/device/{device_id}`
- Device attributes: `hubitat/{device_name}/{attribute}` and `hubitat/device/{device_id}/{attribute}`
- Commands: `hubitat/{device_name}/command/{command}` and `hubitat/device/{device_id}/command/{command}`
- Events (fallback): `hubitat/device/{device_id}/events`

## Project Structure

- **Common/**: Shared utilities (AttributesConverter, Constants, MqttBuilder, MqttHealth)
- **Controllers/**: Web API controllers for webhooks
- **Services/**: Core business logic and background services
- **Program.cs**: Application startup, DI configuration, and service registration

## Development Notes

- Uses MQTTnet library for MQTT client functionality
- Implements health checks at `/health` endpoint
- Uses System.Text.Json for serialization with case-insensitive property matching
- Memory cache is used for device caching
- HTTP compression (Brotli, Gzip) is enabled
- Logging configured for console output with timestamps