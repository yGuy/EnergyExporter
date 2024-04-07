using System;

namespace EnergyExporter.Mqtt;

/// <summary>
/// Specifies that the value of this property should be exported to InfluxDb.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class MqttMetricAttribute : Attribute
{
    public string? Unit { get; }
    
    public string? DeviceClass { get; }
    
    public string? StateClass { get; }

    public MqttMetricAttribute(string? unit = null, string? deviceClass = null, string? stateClass = null)
    {
        Unit = unit;
        DeviceClass = deviceClass;
        StateClass = stateClass;
    }
}