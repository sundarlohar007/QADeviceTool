using System;
using System.Collections.Generic;
using LogPro.Models;

namespace LogPro.Tests.Helpers;

public class FakeDeviceConnection : IDisposable
{
    public DeviceInfo Device { get; set; } = new();
    public bool IsConnected { get; set; } = true;
    public List<string> CommandLog { get; } = new();
    public string? LogOutput { get; set; }

    public string? ExecuteCommand(string command)
    {
        CommandLog.Add(command);
        return LogOutput;
    }

    public void Disconnect()
    {
        IsConnected = false;
    }

    public void Dispose()
    {
        IsConnected = false;
    }
}

public static class FakeDevices
{
    public static DeviceInfo AndroidDevice => new()
    {
        Serial = "emulator-5554",
        Platform = DevicePlatform.Android,
        Model = "Pixel 7",
        Manufacturer = "Google",
        OsVersion = "14",
        ConnectionState = DeviceConnectionState.Online,
        Name = "Pixel 7"
    };

    public static DeviceInfo IosDevice => new()
    {
        Serial = "00001234-000A12345678",
        Platform = DevicePlatform.iOS,
        Model = "iPhone 15 Pro",
        Manufacturer = "Apple",
        ConnectionState = DeviceConnectionState.Online,
        Name = "iPhone 15 Pro"
    };

    public static DeviceInfo OfflineDevice => new()
    {
        Serial = "ABC123XYZ",
        Platform = DevicePlatform.Android,
        Model = "Samsung Galaxy S23",
        Manufacturer = "Samsung",
        ConnectionState = DeviceConnectionState.Offline,
        Name = "Samsung Galaxy S23"
    };
}