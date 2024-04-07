using System;

namespace EnergyExporter.Mqtt;

public class MqttDeviceInfoAttribute : Attribute
{
    public string? Manufacturer { get; }
    public string? Model { get; }
    public string? Name { get; }
    public string? SwVersion { get; }
    public string Id { get; }

    public MqttDeviceInfoAttribute(string id, string? manufacturer = null, string? model = null, string? name = null, string? sw_version = null)
    {
        Manufacturer = manufacturer;
        Model = model;
        Name = name;
        SwVersion = sw_version;
        Id = id;
    }
}