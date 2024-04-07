using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EnergyExporter.Devices;
using EnergyExporter.InfluxDb;
using EnergyExporter.Options;
using EnergyExporter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace EnergyExporter.Mqtt;

public class MqttExporter : IDisposable
{
    private readonly ILogger<MqttExporter> _logger;
    private readonly IOptions<ExportOptions> _exportOptions;
    private readonly DeviceService _deviceService;

    private readonly IMqttClient? _mqttClient;
    private readonly MqttClientOptions? _mqttClientOptions;

    public MqttExporter(
        ILogger<MqttExporter> logger,
        IOptions<ExportOptions> exportOptions,
        DeviceService deviceService)
    {
        _logger = logger;
        _exportOptions = exportOptions;
        _deviceService = deviceService;

        ExportOptions.MqttOptions? mqttExportOptions = exportOptions.Value.Mqtt;
        if (mqttExportOptions != null)
        {
            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttExportOptions.TcpServer, mqttExportOptions.Port)
                .WithClientId(mqttExportOptions.ClientId);

            if (mqttExportOptions is { User: not null, Password: not null })
            {
                mqttClientOptions =
                    mqttClientOptions.WithCredentials(mqttExportOptions.User, mqttExportOptions.Password);
            }

            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();
            _mqttClientOptions = mqttClientOptions.Build();

            _mqttClient.DisconnectedAsync += args =>
            {
                _logger.LogDebug("MQTT disconnected. {Reason}", args.ReasonString);
                return Task.CompletedTask;
            };
        }
    }

    public async Task PublishMetricsAsync()
    {
        if (_mqttClient == null || _mqttClientOptions == null)
            return;

        _logger.LogDebug("Publishing metrics on MQTT");

        if (!_mqttClient.IsConnected)
        {
            _logger.LogDebug("Connecting Client");
            await _mqttClient.ConnectAsync(_mqttClientOptions);
            _logger.LogDebug("Connected Client");
        }

        // Create list of data points
        var messages =
            _deviceService.Devices.SelectMany(device => GetDeviceMeasurement(device)).ToList();

        // and publish
        foreach (var mqttApplicationMessage in messages)
        {
            await _mqttClient.PublishAsync(mqttApplicationMessage);
        }
    }

    public async Task PublishHomeAssistantDiscoveryInformation()
    {
        if (_mqttClient == null || _mqttClientOptions == null)
            return;

        _logger.LogDebug("Publishing discovery information on MQTT");

        if (!_mqttClient.IsConnected)
        {
            _logger.LogDebug("Connecting Client");
            await _mqttClient.ConnectAsync(_mqttClientOptions);
            _logger.LogDebug("Connected Client");
        }

        // Create list of announcements
        var messages =
            _deviceService.Devices.SelectMany(GetDeviceAnnouncement).ToList();

        // and publish
        foreach (var mqttApplicationMessage in messages)
        {
            await _mqttClient.PublishAsync(mqttApplicationMessage);
        }
    }

    private IEnumerable<MqttApplicationMessage> GetDeviceMeasurement(IDevice device)
    {
        var measurementAttribute = device.GetType().GetCustomAttribute<InfluxDbMeasurementAttribute>();
        if (measurementAttribute == null)
            throw new Exception($"Device {device.GetType().Name} has no measurement attribute.");

        var measurementName = measurementAttribute.Name;
        var deviceId = device.DeviceIdentifier;

        foreach (PropertyInfo property in device.GetType().GetProperties())
        {
            var attribute = property.GetCustomAttribute<InfluxDbMetricAttribute>();
            if (attribute == null)
                continue;

            object? value = property.GetValue(device);
            if (value != null && value.GetType().IsEnum)
                value = value.ToString();
            else if (value is double doubleValue)
            {
                value = doubleValue.ToString(CultureInfo.InvariantCulture);
            }
            else if (value is float floatValue)
            {
                value = floatValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value != null)
            {
                yield return new MqttApplicationMessageBuilder()
                    .WithTopic(_exportOptions.Value.Mqtt!.Topic + "/" + measurementName + "/" + deviceId + "/" +
                               attribute.Field)
                    .WithPayload(value.ToString())
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(true)
                    .Build();
            }
        }
    }
    
    /// <summary>
    /// Creates auto discovery messages for HomeAssistant integration
    /// </summary>
    /// <remarks>
    /// https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery
    /// For this to work, the <see cref="ExportOptions.MqttOptions.HomeAssistantDiscoveryTopic"/>
    /// needs to be configured appropriately. Typically this will be "homeassistant"
    /// </remarks>
    /// <param name="device"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private IEnumerable<MqttApplicationMessage> GetDeviceAnnouncement(IDevice device)
    {
        var measurementAttribute = device.GetType().GetCustomAttribute<InfluxDbMeasurementAttribute>();
        if (measurementAttribute == null)
            throw new Exception($"Device {device.GetType().Name} has no measurement attribute.");

        var measurementName = measurementAttribute.Name;
        var deviceId = device.DeviceIdentifier;

        var deviceInfoAttribute = device.GetType().GetCustomAttribute<MqttDeviceInfoAttribute>();
        if (deviceInfoAttribute == null)
        {
           throw new Exception($"Device {device.GetType().Name} has no MqttDeviceInfo attribute.");
        }

        var id = (string) device.GetType().GetProperty(deviceInfoAttribute.Id)!.GetValue(device)!;
        var manufacturer = deviceInfoAttribute.Manufacturer != null ? device.GetType().GetProperty(deviceInfoAttribute.Manufacturer!)?.GetValue(device) as string ?? "" : "";
        var name = deviceInfoAttribute.Name != null ? device.GetType().GetProperty(deviceInfoAttribute.Name)?.GetValue(device) as string ?? "" : "";
        if (name.Length == 0)
        {
            name = measurementName;
        }
        var model = deviceInfoAttribute.Model != null ? device.GetType().GetProperty(deviceInfoAttribute.Model)?.GetValue(device) as string ?? "" : "";
        var version = deviceInfoAttribute.SwVersion != null ? device.GetType().GetProperty(deviceInfoAttribute.SwVersion)?.GetValue(device) as string ?? "" : "";

        foreach (PropertyInfo property in device.GetType().GetProperties())
        {
            var attribute = property.GetCustomAttribute<InfluxDbMetricAttribute>();
            var metricAttribute = property.GetCustomAttribute<MqttMetricAttribute>();
            if (attribute == null || metricAttribute == null)
                continue;
            
            // Create a MemoryStream to hold the JSON config data for HomeAssistant
            using var jsonStream = new MemoryStream();

            // Configure JsonWriterOptions for pretty-printing
            var options = new JsonWriterOptions
            {
                Indented = true
            };

            // Instantiate Utf8JsonWriter with the MemoryStream and options
            using var writer = new Utf8JsonWriter(jsonStream, options);

            // Start writing JSON
            writer.WriteStartObject();
            {
                writer.WriteStartObject("device");
                {
                    writer.WriteStartArray("identifiers");
                    {
                        writer.WriteStringValue(id + "_" + measurementName);
                    }
                    writer.WriteEndArray();
                    writer.WriteString("manufacturer", manufacturer);
                    writer.WriteString("model", model);
                    writer.WriteString("name", name);
                    writer.WriteString("sw_version", version);
                }
                writer.WriteEndObject();


                if (metricAttribute.DeviceClass != null)
                {
                    writer.WriteString("device_class", metricAttribute.DeviceClass);
                }
                writer.WriteBoolean("enabled_by_default", true);
                
                if (metricAttribute.StateClass != null)
                {
                    writer.WriteString("state_class", metricAttribute.StateClass);
                }
                writer.WriteString("state_topic", _exportOptions.Value.Mqtt!.Topic + "/" + measurementName + "/" + deviceId + "/" +
                                                  attribute.Field);
                
                writer.WriteString("name", attribute.Field);
                writer.WriteString("unique_id", deviceId + "_" + measurementName + "_" + attribute.Field + "_" + _exportOptions.Value.Mqtt!.Topic);
                
                if (metricAttribute.Unit != null)
                {
                    writer.WriteString("unit_of_measurement", metricAttribute.Unit);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            
            // Convert the MemoryStream to a byte array and decode it as a UTF-8 string
            string json = Encoding.UTF8.GetString(jsonStream.ToArray());

            yield return new MqttApplicationMessageBuilder()
                .WithTopic(_exportOptions.Value.Mqtt!.HomeAssistantDiscoveryTopic + "/sensor/" + measurementName + "_" + deviceId + "/" + attribute.Field + "/config")
                .WithPayload(json)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(true)
                .Build();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _mqttClient?.DisconnectAsync();
        _mqttClient?.Dispose();
    }
}